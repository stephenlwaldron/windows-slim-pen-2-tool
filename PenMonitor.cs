using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SlimPenHaptics;

// Hidden message-only window that uses Raw Input to detect pen tip state changes
// system-wide (RIDEV_INPUTSINK fires even when our window has no focus).
public class PenMonitor : NativeWindow, IDisposable
{
    public event Action? PenDown;
    public event Action? PenUp;

    // Fired for each report while the tip is down:
    // speed (screen px/s, smoothed), pressure (0..1), travel since pen-down (px).
    public event Action<double, double, double>? PenMove;

    private bool _tipDown;
    private bool _disposed;

    // Per-device parse data, built on the first report from that device.
    private sealed class PenDevice
    {
        public IntPtr Ppd;
        public int MaxX, MaxY, MaxPressure;
    }

    private readonly Dictionary<IntPtr, PenDevice> _devices = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly double _screenW, _screenH;

    private bool   _hasLast;
    private double _lastX, _lastY, _lastT, _speed, _travel;

    private const double MinSampleSeconds = 0.004;  // batched reports arrive with ~0 dt
    private const double SpeedSmoothing   = 0.3;

    // ── Win32 ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData,
        ref uint pcbSize, uint cbSizeHeader);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(
        int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength,
        IntPtr preparsedData, IntPtr report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(
        int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, [Out] ushort[] caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(
        int reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("user32.dll")]
    private static extern IntPtr GetRawInputDeviceInfo(
        IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint   dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint   dwType;
        public uint   dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
        // variable-length data follows
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWINPUT
    {
        [FieldOffset(0)]  public RAWINPUTHEADER header;
        [FieldOffset(24)] public RAWHID         hid;    // 24 = size of header on x64
    }

    private const uint RIM_TYPEHID      = 2;
    private const uint RIDEV_INPUTSINK  = 0x00000100;
    private const uint RID_INPUT        = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const ushort HID_USAGE_PAGE_DIGITIZER = 0x000D;
    private const ushort HID_USAGE_DIGITIZER_PEN  = 0x0002;
    private const ushort HID_USAGE_TIP_SWITCH      = 0x0042;
    private const ushort HID_USAGE_TIP_PRESSURE    = 0x0030;
    private const ushort HID_USAGE_PAGE_GENERIC    = 0x0001;
    private const ushort HID_USAGE_GENERIC_X       = 0x0030;
    private const ushort HID_USAGE_GENERIC_Y       = 0x0031;
    private const int    HIDP_STATUS_SUCCESS       = 0x00110000;
    private const int    HidP_Input                = 0;
    private const int    WM_INPUT                  = 0x00FF;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public PenMonitor()
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1280);
        _screenW = screen.Width;
        _screenH = screen.Height;

        // Create a message-only window (parent = HWND_MESSAGE = -3)
        CreateHandle(new CreateParams
        {
            Parent = new IntPtr(-3),
            Style  = 0,
        });
        RegisterForPenInput();
    }

    private void RegisterForPenInput()
    {
        var rid = new RAWINPUTDEVICE[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_DIGITIZER,
                usUsage     = HID_USAGE_DIGITIZER_PEN,
                dwFlags     = RIDEV_INPUTSINK,
                hwndTarget  = Handle,
            }
        };
        RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            ProcessRawInput(m.LParam);
        }
        base.WndProc(ref m);
    }

    private unsafe void ProcessRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size,
                        (uint)Marshal.SizeOf<RAWINPUTHEADER>());
        if (size == 0) return;

        var buf = new byte[size];
        fixed (byte* pBuf = buf)
        {
            uint read = GetRawInputData(hRawInput, RID_INPUT, (IntPtr)pBuf, ref size,
                                        (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            if (read != size) return;

            var ri = Marshal.PtrToStructure<RAWINPUT>((IntPtr)pBuf);
            if (ri.header.dwType != RIM_TYPEHID) return;

            // The raw HID report data sits after the RAWINPUT header + RAWHID struct
            int headerSize = Marshal.SizeOf<RAWINPUTHEADER>() + Marshal.SizeOf<RAWHID>();
            if (buf.Length < headerSize + ri.hid.dwSizeHid) return;

            var dev = GetDevice(ri.header.hDevice);
            if (dev == null) return;

            // Check if Tip Switch (Usage 0x42) is in the active usages
            IntPtr reportPtr = (IntPtr)(pBuf + headerSize);
            var usages = new ushort[16];
            uint usageCount = (uint)usages.Length;
            int result = HidP_GetUsages(HidP_Input, HID_USAGE_PAGE_DIGITIZER, 0,
                                        usages, ref usageCount,
                                        dev.Ppd, reportPtr, ri.hid.dwSizeHid);

            bool tipNow = false;
            for (uint j = 0; j < usageCount; j++)
            {
                if (usages[j] == HID_USAGE_TIP_SWITCH) { tipNow = true; break; }
            }

            if (tipNow && !_tipDown)
            {
                _tipDown = true;
                _hasLast = false;
                _speed   = 0;
                _travel  = 0;
                PenDown?.Invoke();
            }
            if (tipNow) TrackMotion(dev, reportPtr, ri.hid.dwSizeHid);
            if (!tipNow && _tipDown)  { _tipDown = false; PenUp?.Invoke(); }
        }
    }

    private void TrackMotion(PenDevice dev, IntPtr report, uint reportLength)
    {
        if (PenMove == null || dev.MaxX <= 0 || dev.MaxY <= 0) return;

        if (HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_GENERIC, 0, HID_USAGE_GENERIC_X,
                               out uint rawX, dev.Ppd, report, reportLength) != HIDP_STATUS_SUCCESS ||
            HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_GENERIC, 0, HID_USAGE_GENERIC_Y,
                               out uint rawY, dev.Ppd, report, reportLength) != HIDP_STATUS_SUCCESS)
            return;

        double x = rawX / (double)dev.MaxX * _screenW;
        double y = rawY / (double)dev.MaxY * _screenH;
        double t = _clock.Elapsed.TotalSeconds;

        if (!_hasLast)
        {
            _hasLast = true;
            _lastX = x; _lastY = y; _lastT = t;
            return;
        }

        double dt = t - _lastT;
        if (dt < MinSampleSeconds) return;

        double dist = Math.Sqrt((x - _lastX) * (x - _lastX) + (y - _lastY) * (y - _lastY));
        _lastX = x; _lastY = y; _lastT = t;
        _travel += dist;
        _speed  += SpeedSmoothing * (dist / dt - _speed);

        double pressure = 1.0;
        if (dev.MaxPressure > 0 &&
            HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_DIGITIZER, 0, HID_USAGE_TIP_PRESSURE,
                               out uint rawP, dev.Ppd, report, reportLength) == HIDP_STATUS_SUCCESS)
            pressure = Math.Clamp(rawP / (double)dev.MaxPressure, 0.0, 1.0);

        PenMove?.Invoke(_speed, pressure, _travel);
    }

    private PenDevice? GetDevice(IntPtr hDevice)
    {
        if (_devices.TryGetValue(hDevice, out var known)) return known;

        uint ppdSize = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppdSize);
        if (ppdSize == 0) return null;

        var dev = new PenDevice { Ppd = Marshal.AllocHGlobal((int)ppdSize) };
        GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, dev.Ppd, ref ppdSize);
        ReadAxisRanges(dev);
        _devices[hDevice] = dev;
        return dev;
    }

    // Pull the logical maximum of X, Y and tip pressure out of the input value caps.
    private static void ReadAxisRanges(PenDevice dev)
    {
        const int capSize = 72;             // sizeof(HIDP_VALUE_CAPS)
        var caps = new ushort[32];          // HIDP_CAPS as 16-bit words
        if (HidP_GetCaps(dev.Ppd, caps) != HIDP_STATUS_SUCCESS) return;

        ushort count = caps[24];            // NumberInputValueCaps
        if (count == 0) return;

        var mem = Marshal.AllocHGlobal(capSize * count);
        try
        {
            if (HidP_GetValueCaps(HidP_Input, mem, ref count, dev.Ppd) != HIDP_STATUS_SUCCESS) return;

            for (int i = 0; i < count; i++)
            {
                IntPtr cap      = mem + capSize * i;
                ushort page     = (ushort)Marshal.ReadInt16(cap, 0);
                bool   isRange  = Marshal.ReadByte(cap, 12) != 0;
                int    max      = Marshal.ReadInt32(cap, 44);
                ushort usageMin = (ushort)Marshal.ReadInt16(cap, 56);
                ushort usageMax = isRange ? (ushort)Marshal.ReadInt16(cap, 58) : usageMin;

                bool Has(ushort usage) => usage >= usageMin && usage <= usageMax;

                if (page == HID_USAGE_PAGE_GENERIC)
                {
                    if (Has(HID_USAGE_GENERIC_X) && dev.MaxX == 0) dev.MaxX = max;
                    if (Has(HID_USAGE_GENERIC_Y) && dev.MaxY == 0) dev.MaxY = max;
                }
                else if (page == HID_USAGE_PAGE_DIGITIZER && Has(HID_USAGE_TIP_PRESSURE) && dev.MaxPressure == 0)
                {
                    dev.MaxPressure = max;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyHandle();
        foreach (var dev in _devices.Values) Marshal.FreeHGlobal(dev.Ppd);
        _devices.Clear();
    }
}
