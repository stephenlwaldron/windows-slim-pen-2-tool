using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    private bool _disposed;

    // ── Win32 ──────────────────────────────────────────────────────────────

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attrs);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buf, int len);

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
    private const uint FILE_FLAG_OVERLAPPED  = 0x40000000;

    // ── Public API ─────────────────────────────────────────────────────────

    public bool IsConnected => _device != null && !_device.IsInvalid;

    public bool Connect()
    {
        _device?.Dispose();
        _device = null;

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
                    IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);

                if (handle.IsInvalid) continue;

                var attrs = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
                if (!HidD_GetAttributes(handle, ref attrs) ||
                    attrs.VendorID != VendorId || attrs.ProductID != ProductId)
                {
                    handle.Dispose();
                    continue;
                }

                _device = handle;
                BuildWaveformTable();
                return true;
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }
        return false;
    }

    public void Buzz(Waveform waveform, byte intensity = 100)
    {
        if (_device == null || _device.IsInvalid) return;
        if (!_ordinals.TryGetValue((ushort)waveform, out byte ordinal)) return;

        var cmd = new byte[] { 0x41, 0x01, intensity, ordinal, 0x00, 0x00, 0x00 };

        // WriteFile via SafeFileHandle
        if (!WriteFile(_device, cmd, (uint)cmd.Length, out _, IntPtr.Zero))
        {
            // Device disconnected — mark as gone so next call tries to reconnect
            _device.Dispose();
            _device = null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    // ── Private helpers ────────────────────────────────────────────────────

    // Rust slimpend protocol: query feature report 0x42, parse big-endian u16 pairs.
    // First half = durations, second half = waveform IDs.
    // ordinal_base = waveform_ids[0]; Click ordinal = ordinal_base, etc.
    private void BuildWaveformTable()
    {
        if (_device == null) return;

        var buf = new byte[512];
        buf[0] = 0x42;
        if (!HidD_GetFeature(_device, buf, buf.Length)) return;

        // buf[0] = report id, buf[1..3] = unknown header; data starts at buf[4]
        var pairs = new List<ushort>();
        for (int pos = 4; pos + 1 < buf.Length; pos += 2)
        {
            ushort val = (ushort)((buf[pos] << 8) | buf[pos + 1]);
            if (val == 0 && pos > 10) break;    // end of table
            pairs.Add(val);
        }

        if (pairs.Count < 2) return;

        int half = pairs.Count / 2;
        byte ordinalBase = (byte)pairs[half];   // waveform_ids[0] cast to byte

        // Hardcoded entries for None/Stop
        _ordinals[(ushort)Waveform.None] = 1;
        _ordinals[(ushort)Waveform.Stop] = 2;

        for (int i = 0; i < half; i++)
        {
            ushort wfId = pairs[half + i];
            if (wfId < 0x1003) continue;
            byte ordinal = (byte)(i + ordinalBase);
            _ordinals[wfId]  = ordinal;
            _durations[wfId] = pairs[i];
        }
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
        _device?.Dispose();
    }
}
