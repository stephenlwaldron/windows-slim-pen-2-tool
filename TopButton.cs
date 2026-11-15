using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SlimPenHaptics;

public enum TopGesture { Click, DoubleClick, Hold }

// The pen's top button works over Bluetooth, so it reaches the PC from anywhere. The pen types
// Win+F20 for a click, Win+F19 for a double-click and Win+F18 for a hold, and Windows opens
// whatever is assigned to each. This watches for those keys and lets the app claim a press
// before Windows sees it; unclaimed presses carry on to Windows untouched.
public sealed class TopButton : IDisposable
{
    // Return true to claim the press. Called on the thread that created this object; it must be
    // quick, because all keyboard input waits on it.
    public Func<TopGesture, bool>? Pressed;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint   vkCode;
        public uint   scanCode;
        public uint   flags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    private const int  WH_KEYBOARD_LL = 13;
    private const int  WM_KEYUP       = 0x0101;
    private const int  WM_SYSKEYUP    = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint VK_F18 = 0x81, VK_F19 = 0x82, VK_F20 = 0x83;

    private readonly HookProc _proc;    // kept in a field so the delegate outlives the native hook
    private IntPtr _hook;
    private bool   _winDown;
    private uint   _claimedKey;         // the F-key whose press was claimed; its repeats and release are swallowed too

    public TopButton() => _proc = OnKey;

    // The hook is only installed while some app actually uses the top button.
    public bool Enabled
    {
        get => _hook != IntPtr.Zero;
        set
        {
            if (value == Enabled) return;
            if (value)
            {
                using var module = Process.GetCurrentProcess().MainModule;
                _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(module?.ModuleName), 0);
                if (_hook == IntPtr.Zero) Log.Write($"couldn't watch the pen's top button (error {Marshal.GetLastWin32Error()})");
            }
            else
            {
                UnhookWindowsHookEx(_hook);
                _hook       = IntPtr.Zero;
                _winDown    = false;
                _claimedKey = 0;
            }
        }
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);

        var  key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        bool up  = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

        // Keys this app (or any other) sends are never the pen.
        if ((key.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, code, wParam, lParam);

        if (key.vkCode is VK_LWIN or VK_RWIN)
        {
            _winDown = !up;
        }
        else if (key.vkCode is VK_F18 or VK_F19 or VK_F20)
        {
            if (key.vkCode == _claimedKey)
            {
                if (up) _claimedKey = 0;
                return 1;
            }

            if (!up && _winDown)
            {
                var gesture = key.vkCode == VK_F20 ? TopGesture.Click
                            : key.vkCode == VK_F19 ? TopGesture.DoubleClick
                                                   : TopGesture.Hold;
                bool claimed = false;
                try { claimed = Pressed?.Invoke(gesture) == true; }
                catch (Exception) { /* never let a failure here block the keyboard */ }

                if (claimed)
                {
                    _claimedKey = key.vkCode;
                    // Windows saw the Win key go down and will now see it come up with nothing in
                    // between, which opens the Start menu. A key that means nothing prevents that.
                    KeySender.TapMaskKey();
                    return 1;
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Enabled = false;
}
