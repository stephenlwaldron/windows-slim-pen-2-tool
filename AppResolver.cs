using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SlimPenHaptics;

// Works out which app the pen is over, so per-app profiles can apply.
public static class AppResolver
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // Full path of the exe whose window is under the pen. The pointer follows the pen while it
    // hovers, so by touch-down it is already there. Asking for the window at that point (rather
    // than the foreground window) also finds Store apps, which draw inside a shared host window.
    public static string? ExeUnderPen()
    {
        IntPtr hwnd = GetCursorPos(out POINT point) ? WindowFromPoint(point) : IntPtr.Zero;
        if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == 0 ? null : ExePath(processId);
    }

    // Full path of the exe that has keyboard focus, which is where sent keys end up.
    public static string? ForegroundExe()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == 0 ? null : ExePath(processId);
    }

    public static string? ExePath(uint processId)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            uint size = (uint)name.Capacity;
            return QueryFullProcessImageNameW(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
