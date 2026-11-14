using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SlimPenHaptics;

// From: https://learn.microsoft.com/en-us/surface/surface-slim-pen2-haptics-dev-notes
public enum Waveform : ushort
{
    None           = 0x1001,
    Stop           = 0x1002,
    Click          = 0x1003,
    Press          = 0x1006,
    Release        = 0x1007,
    Hover          = 0x1008,
    Success        = 0x1009,
    Error          = 0x100A,
    InkCont        = 0x100B,
    PencilCont     = 0x100C,
    MarkerCont     = 0x100D,
    ChiselMarkerCont = 0x100E,
    BrushCont      = 0x100F,
    EraserCont     = 0x1010,
    SparkleCont    = 0x1011,
    Collide        = 0x1012,
    Align          = 0x1013,
    Step           = 0x1014,
    Grow           = 0x1015,
}

public class HapticEngine : IDisposable
{
    private const ushort VendorId  = 0x045E;
    private const ushort ProductId = 0x0C0F;

    private SafeFileHandle? _device;
    private readonly Dictionary<ushort, byte>   _ordinals  = new();
    private readonly Dictionary<ushort, ushort> _durations = new();
    private volatile bool _disposed;

    private readonly Thread         _writer;
    private readonly AutoResetEvent _wake      = new(false);
    private readonly object         _queueLock = new();
    private readonly object         _ioLock    = new();
    private readonly Queue<byte[]>  _oneShots  = new();
    private byte[]?                 _pendingTexture;

    private string?            _devicePath;         // last haptic collection that worked
    private int                _failures;           // consecutive failed writes
    private long               _firstFailureAt;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool      _lastWriteOk = true;

    // False while the most recent buzz didn't reach the pen.
    public bool LastWriteOk { get => _lastWriteOk; private set => _lastWriteOk = value; }

    // ── Win32 ──────────────────────────────────────────────────────────────

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attrs);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buf, int len);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps caps);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid,
        uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        ref SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData,
        uint DeviceInterfaceDetailDataSize, out uint RequiredSize,
        IntPtr DeviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int    Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)]
        public ushort[] Reserved;   // reserved words + cap counts, unused here
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint   cbSize;
        public Guid   InterfaceClassGuid;
        public uint   Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public uint   cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string DevicePath;
    }

    private static readonly Guid HidGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const uint DIGCF_PRESENT         = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ          = 0x80000000;
    private const uint GENERIC_WRITE         = 0x40000000;
    private const uint FILE_SHARE_READ       = 0x01;
    private const uint FILE_SHARE_WRITE      = 0x02;
    private const uint OPEN_EXISTING         = 3;

    private const ushort HID_USAGE_PAGE_DIGITIZER = 0x000D;
    private const byte   WaveformListReportId     = 0x42;
    private const byte   TriggerReportId          = 0x41;
    private const int    TriggerReportLength      = 7;
    private const int    FirstWaveformOrdinal     = 3;   // 1 = None, 2 = Stop (HID haptics spec)

    // ── Public API ─────────────────────────────────────────────────────────

    public bool IsConnected => _device != null && !_device.IsInvalid;

    public HapticEngine()
    {
        // Bluetooth writes can block for a connection interval; keep them off the input thread.
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "HapticWriter" };
        _writer.Start();
    }

    public bool Connect()
    {
        lock (_ioLock)
        {
            _device?.Dispose();
            _device = null;
        }

        var devInfo = SetupDiGetClassDevs(ref Unsafe_HidGuid(), IntPtr.Zero, IntPtr.Zero,
                                          DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1)) return false;

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref Unsafe_HidGuid(), i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);

                var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                detail.cbSize = (uint)(IntPtr.Size == 8 ? 8 : 5);
                if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, ref detail, needed, out _, IntPtr.Zero))
                    continue;

                var handle = CreateFile(detail.DevicePath,
                    GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (handle.IsInvalid) continue;

                var attrs = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                if (!HidD_GetAttributes(handle, ref attrs) ||
                    attrs.VendorID != VendorId || attrs.ProductID != ProductId)
                {
                    handle.Dispose();
                    continue;
                }

                // The pen exposes several HID collections under the same VID/PID;
                // only the digitizer one carries the haptic reports.
                if (!IsHapticCollection(handle, out int featureLength) ||
                    !BuildWaveformTable(handle, featureLength))
                {
                    handle.Dispose();
                    continue;
                }

                lock (_ioLock)
                {
                    _device     = handle;
                    _devicePath = detail.DevicePath;
                }
                LastWriteOk = true;
                Log.Write("pen haptics connected");
                return true;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
        return false;
    }

    // One-shot feedback (click, release, stop). Sent in order; drops any pending texture update.
    public void Buzz(Waveform waveform, byte intensity = 100)
    {
        var cmd = BuildTrigger(waveform, intensity);
        if (cmd == null) return;
        lock (_queueLock)
        {
            _pendingTexture = null;
            _oneShots.Enqueue(cmd);
        }
        _wake.Set();
    }

    // Texture update while dragging. Only the newest one is kept, so a slow link
    // never builds up a backlog of stale intensities.
    public void BuzzTexture(Waveform waveform, byte intensity)
    {
        var cmd = BuildTrigger(waveform, intensity);
        if (cmd == null) return;
        lock (_queueLock) _pendingTexture = cmd;
        _wake.Set();
    }

    // True once a haptic collection has been found, even if the link is currently down.
    public bool HasDevice => _devicePath != null;

    // Send a harmless Stop to find out whether the link is up; LastWriteOk reports the result.
    public void Probe()
    {
        LastWriteOk = false;
        Buzz(Waveform.Stop);
    }

    // How long the pen plays this waveform before it needs retriggering (0 if unknown).
    public int DurationMs(Waveform waveform) =>
        _durations.TryGetValue((ushort)waveform, out ushort ms) ? ms : 0;

    private byte[]? BuildTrigger(Waveform waveform, byte intensity)
    {
        // No IsConnected check: after a failed write the writer reopens the device itself.
        if (!_ordinals.TryGetValue((ushort)waveform, out byte ordinal)) return null;

        // report id, repeat count, intensity, waveform ordinal, cutoff time, retrigger period (u16)
        var cmd = new byte[TriggerReportLength];
        cmd[0] = TriggerReportId;
        cmd[1] = 0x01;
        cmd[2] = Math.Max(intensity, (byte)1);
        cmd[3] = ordinal;
        return cmd;
    }

    private void WriterLoop()
    {
        while (!_disposed)
        {
            _wake.WaitOne();
            while (true)
            {
                byte[]? cmd;
                lock (_queueLock)
                {
                    if (_oneShots.Count > 0) cmd = _oneShots.Dequeue();
                    else { cmd = _pendingTexture; _pendingTexture = null; }
                }
                if (cmd == null) break;

                lock (_ioLock) LastWriteOk = TryWrite(cmd);
            }
        }
    }

    // The pen drops its Bluetooth link when it sleeps, so a write can fail even though
    // the pen is in the user's hand. Reopen the same device straight away instead of
    // waiting for the slow reconnect timer; callers retry while LastWriteOk is false.
    private bool TryWrite(byte[] cmd)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (_device == null || _device.IsInvalid)
            {
                if (_devicePath == null) return false;
                var reopened = CreateFile(_devicePath,
                    GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (reopened.IsInvalid)
                {
                    reopened.Dispose();
                    _device = null;
                    NoteFailure($"reopen failed (error {Marshal.GetLastWin32Error()})", 0);
                    return false;
                }
                _device = reopened;
            }

            long started = _clock.ElapsedMilliseconds;
            if (WriteFile(_device, cmd, (uint)cmd.Length, out _, IntPtr.Zero))
            {
                if (_failures > 0)
                {
                    Log.Write($"haptics back after {_clock.ElapsedMilliseconds - _firstFailureAt} ms, {_failures} failed writes");
                    _failures = 0;
                }
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            NoteFailure($"write failed (error {error})", _clock.ElapsedMilliseconds - started);
            _device.Dispose();
            _device = null;
        }
        return false;
    }

    private void NoteFailure(string what, long tookMs)
    {
        if (_failures == 0) _firstFailureAt = _clock.ElapsedMilliseconds;
        _failures++;
        // The first few show how long a dead link takes to report; after that it's noise.
        if (_failures <= 5) Log.Write($"{what}, took {tookMs} ms");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    // ── Private helpers ────────────────────────────────────────────────────

    private static bool IsHapticCollection(SafeFileHandle device, out int featureLength)
    {
        featureLength = 0;
        if (!HidD_GetPreparsedData(device, out IntPtr ppd)) return false;
        try
        {
            HidP_GetCaps(ppd, out HidpCaps caps);
            featureLength = caps.FeatureReportByteLength;
            return caps.UsagePage == HID_USAGE_PAGE_DIGITIZER &&
                   caps.OutputReportByteLength == TriggerReportLength;
        }
        finally
        {
            HidD_FreePreparsedData(ppd);
        }
    }

    // Feature report 0x42 (HID haptics waveform list), little-endian:
    //   [0] report id, [1..4] waveform vendor id,
    //   then N u16 durations (ms), then N u16 waveform ids.
    // Slot i is waveform ordinal i + 3; ordinals 1 and 2 are always None and Stop.
    private bool BuildWaveformTable(SafeFileHandle device, int featureLength)
    {
        const int tableStart = 5;
        int slots = (featureLength - tableStart) / 4;
        if (slots < 1) return false;

        var buf = new byte[featureLength];
        buf[0] = WaveformListReportId;
        if (!HidD_GetFeature(device, buf, buf.Length)) return false;

        _ordinals.Clear();
        _durations.Clear();
        _ordinals[(ushort)Waveform.None] = 1;
        _ordinals[(ushort)Waveform.Stop] = 2;

        for (int i = 0; i < slots; i++)
        {
            ushort duration = BitConverter.ToUInt16(buf, tableStart + i * 2);
            ushort wfId     = BitConverter.ToUInt16(buf, tableStart + (slots + i) * 2);
            if (wfId < (ushort)Waveform.Click) continue;    // empty slot
            _ordinals[wfId]  = (byte)(FirstWaveformOrdinal + i);
            _durations[wfId] = duration;
        }

        return _ordinals.ContainsKey((ushort)Waveform.Click);
    }

    // Helper to pass ref to static readonly Guid
    private static ref Guid Unsafe_HidGuid()
    {
        // This is intentional — SetupDi needs a ref but won't modify it
        return ref System.Runtime.CompilerServices.Unsafe.AsRef(in HidGuid);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        lock (_ioLock) _device?.Dispose();
    }
}
