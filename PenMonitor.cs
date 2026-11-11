using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SlimPenHaptics;

// Hidden message-only window that uses Raw Input to detect pen tip state changes
// system-wide (RIDEV_INPUTSINK fires even when our window has no focus).
public class PenMonitor : NativeWindow, IDisposable
{
    public event Action? PenDown;
    public event Action? PenUp;

    private bool _tipDown;
    private bool _disposed;

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
    private static extern bool HidD_GetPreparsedData(IntPtr hObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

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
    private const int    HidP_Input                = 0;
    private const int    WM_INPUT                  = 0x00FF;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public PenMonitor()
    {
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

            // Get preparsed data for this device so we can call HidP_GetUsages
            uint ppdSize = 0;
            GetRawInputDeviceInfo(ri.header.hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppdSize);
            if (ppdSize == 0) return;

            var ppdBuf = Marshal.AllocHGlobal((int)ppdSize);
            try
            {
                GetRawInputDeviceInfo(ri.header.hDevice, RIDI_PREPARSEDDATA, ppdBuf, ref ppdSize);

                // Check if Tip Switch (Usage 0x42) is in the active usages
                fixed (byte* pReport = buf)
                {
                    IntPtr reportPtr = (IntPtr)(pReport + headerSize);
                    var usages = new ushort[16];
                    uint usageCount = (uint)usages.Length;
                    int result = HidP_GetUsages(HidP_Input, HID_USAGE_PAGE_DIGITIZER, 0,
                                                usages, ref usageCount,
                                                ppdBuf, reportPtr, ri.hid.dwSizeHid);

                    bool tipNow = false;
                    for (uint j = 0; j < usageCount; j++)
                    {
                        if (usages[j] == HID_USAGE_TIP_SWITCH) { tipNow = true; break; }
                    }

                    if (tipNow && !_tipDown)  { _tipDown = true;  PenDown?.Invoke(); }
                    if (!tipNow && _tipDown)  { _tipDown = false; PenUp?.Invoke();   }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ppdBuf);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyHandle();
    }
}
