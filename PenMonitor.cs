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
    // Fired for every report, including while the pen only hovers.
    public event Action? PenSeen;
    public event Action? PenDown;
    public event Action? PenUp;

    // Fired when the pen is flipped: true = eraser end towards the screen, false = tip again.
    public event Action<bool>? EraserEnd;

    // Fired each time the side button is pressed, hovering or drawing.
    public event Action? SideButton;

    // Fired when the tip comes into range of the screen after being away from it.
    public event Action? PenArrived;

    // Fired for each report while the tip is down:
    // speed (screen px/s, smoothed), pressure (0..1), travel since pen-down (px).
    public event Action<double, double, double>? PenMove;

    private bool _tipDown;
    private bool _eraserEnd;
    private bool _disposed;

    private bool   _buttonDown;
    private double _buttonSeenAt;

    private const double ButtonMaxGapSeconds  = 0.25;   // reports stopped: the pen left the screen mid-press

    // The digitizer reports continuously while the pen is in range, so a gap this long means it
    // was taken away. Long enough that skimming the edge of hover range between strokes doesn't count.
    private const double ArrivalGapSeconds = 0.6;
    private double _lastReportAt = double.NegativeInfinity;

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

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
    private const ushort HID_USAGE_INVERT          = 0x003C;    // eraser end near the screen
    private const ushort HID_USAGE_ERASER          = 0x0045;    // eraser end touching
    private const ushort HID_USAGE_BARREL          = 0x0044;    // side button
    private const ushort HID_USAGE_BARREL_2        = 0x005A;    // second side button, on pens that have one
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

        // Work in 96-dpi units so drag speed feels the same at any display scaling.
        double scale = Math.Max(GetDpiForSystem(), 96u) / 96.0;
        _screenW = screen.Width  / scale;
        _screenH = screen.Height / scale;

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
            },
        };
        if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Write($"raw input registration failed (error {Marshal.GetLastWin32Error()})");
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

            PenSeen?.Invoke();

            // Check if Tip Switch (Usage 0x42) is in the active usages
            IntPtr reportPtr = (IntPtr)(pBuf + headerSize);
            var usages = new ushort[16];
            uint usageCount = (uint)usages.Length;
            int result = HidP_GetUsages(HidP_Input, HID_USAGE_PAGE_DIGITIZER, 0,
                                        usages, ref usageCount,
                                        dev.Ppd, reportPtr, ri.hid.dwSizeHid);

            bool tipNow = false, eraserNow = false, buttonNow = false;
            for (uint j = 0; j < usageCount; j++)
            {
                if (usages[j] == HID_USAGE_TIP_SWITCH) tipNow = true;
                if (usages[j] == HID_USAGE_INVERT || usages[j] == HID_USAGE_ERASER) eraserNow = true;
                if (usages[j] == HID_USAGE_BARREL || usages[j] == HID_USAGE_BARREL_2) buttonNow = true;
            }

            TrackButton(buttonNow, eraserNow);

            double reportAt = _clock.Elapsed.TotalSeconds;
            bool arrived = reportAt - _lastReportAt > ArrivalGapSeconds;
            _lastReportAt = reportAt;
            if (arrived && !eraserNow) PenArrived?.Invoke();

            if (eraserNow != _eraserEnd)
            {
                _eraserEnd = eraserNow;
                EraserEnd?.Invoke(eraserNow);
            }

            // Erasing is not drawing: no click or texture from the eraser end.
            if (eraserNow) tipNow = false;

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

    // Fires once per press, the moment the button goes down, whether the pen is hovering or
    // drawing. Not from the eraser end: some pens report their eraser button as side button + invert.
    private void TrackButton(bool buttonNow, bool eraser)
    {
        double now = _clock.Elapsed.TotalSeconds;

        // Reports stopped while it was held (the pen left the screen), so the release was never seen.
        if (_buttonDown && now - _buttonSeenAt > ButtonMaxGapSeconds) _buttonDown = false;
        _buttonSeenAt = now;

        if (buttonNow && !_buttonDown && !eraser) SideButton?.Invoke();
        _buttonDown = buttonNow;
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
