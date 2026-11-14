using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace SlimPenHaptics;

// Keyboard shortcuts as text ("Shift+E"), and pressing them on the user's behalf.
// Used to switch an app to its eraser tool when the pen is flipped.
public static class KeySender
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    // INPUT is a tagged union; the keyboard member is padded to the size of the largest (mouse).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint       type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private const uint INPUT_KEYBOARD  = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private static readonly Dictionary<Key, string> Friendly = new()
    {
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4",
        [Key.D5] = "5", [Key.D6] = "6", [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
        [Key.OemOpenBrackets] = "[", [Key.OemCloseBrackets] = "]", [Key.OemComma] = ",", [Key.OemPeriod] = ".",
        [Key.OemMinus] = "-", [Key.OemPlus] = "=", [Key.OemQuestion] = "/", [Key.OemSemicolon] = ";",
        [Key.OemQuotes] = "'", [Key.OemPipe] = "\\", [Key.OemTilde] = "`",
    };

    public static bool IsModifier(Key key) =>
        key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    // Stored form, e.g. "Ctrl+Shift+E". The key part is the WPF Key name so it parses back exactly.
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    // What the user sees: "Shift+E", "Ctrl+[".
    public static string Display(string? shortcut)
    {
        if (!TryParse(shortcut, out var modifiers, out var key)) return "";
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        parts.Add(Friendly.TryGetValue(key, out string? name) ? name : key.ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? shortcut, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;

        string[] parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i])
            {
                case "Ctrl":  modifiers |= ModifierKeys.Control; break;
                case "Alt":   modifiers |= ModifierKeys.Alt;     break;
                case "Shift": modifiers |= ModifierKeys.Shift;   break;
                default: return false;
            }
        }
        return parts.Length > 0 && Enum.TryParse(parts[^1], out key) && key != Key.None && !IsModifier(key);
    }

    // Press and release the shortcut. It goes to whichever window has keyboard focus.
    public static bool Send(string? shortcut)
    {
        if (!TryParse(shortcut, out var modifiers, out var key)) return false;

        var down = new List<ushort>();
        if (modifiers.HasFlag(ModifierKeys.Control)) down.Add(VK_CONTROL);
        if (modifiers.HasFlag(ModifierKeys.Alt))     down.Add(VK_MENU);
        if (modifiers.HasFlag(ModifierKeys.Shift))   down.Add(VK_SHIFT);
        down.Add((ushort)KeyInterop.VirtualKeyFromKey(key));

        var inputs = new List<INPUT>();
        foreach (ushort vk in down) inputs.Add(Make(vk, up: false));
        for (int i = down.Count - 1; i >= 0; i--) inputs.Add(Make(down[i], up: true));

        return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) == inputs.Count;
    }

    private static INPUT Make(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk     = vk,
            wScan   = (ushort)MapVirtualKeyW(vk, 0),
            dwFlags = up ? KEYEVENTF_KEYUP : 0,
        },
    };
}
