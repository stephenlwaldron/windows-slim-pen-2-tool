using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SlimPenHaptics;

public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon   _tray;
    private readonly HapticEngine _haptic;
    private PenMonitor?           _monitor;
    private bool                  _enabled = true;
    private byte                  _intensity = 100;
    private System.Windows.Forms.Timer _reconnectTimer;

    private const string StartupKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "SlimPenHaptics";

    public TrayApp()
    {
        _haptic = new HapticEngine();

        _tray = new NotifyIcon
        {
            Icon    = BuildIcon(connected: false),
            Visible = true,
            Text    = "Slim Pen Haptics",
        };

        _tray.ContextMenuStrip = BuildMenu();
        _tray.DoubleClick += (_, _) => ToggleEnabled();

        // Try connecting to the pen on startup
        TryConnect();

        // Reconnect timer — pen may pair after app starts
        _reconnectTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _reconnectTimer.Tick += (_, _) =>
        {
            if (!_haptic.IsConnected) TryConnect();
        };
        _reconnectTimer.Start();
    }

    private void TryConnect()
    {
        if (_haptic.Connect())
        {
            _monitor?.Dispose();
            _monitor = new PenMonitor();
            _monitor.PenDown += OnPenDown;
            _monitor.PenUp   += OnPenUp;
            _tray.Icon = BuildIcon(connected: true);
            _tray.Text = "Slim Pen Haptics — connected";
        }
        else
        {
            _tray.Icon = BuildIcon(connected: false);
            _tray.Text = "Slim Pen Haptics — pen not found";
        }
    }

    private void OnPenDown()
    {
        if (_enabled) _haptic.Buzz(Waveform.Click, _intensity);
    }

    private void OnPenUp()
    {
        if (_enabled) _haptic.Buzz(Waveform.Release, _intensity);
    }

    private void ToggleEnabled()
    {
        _enabled = !_enabled;
        RebuildMenu();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        PopulateMenu(menu);
        return menu;
    }

    private void RebuildMenu()
    {
        _tray.ContextMenuStrip?.Dispose();
        _tray.ContextMenuStrip = BuildMenu();
    }

    private void PopulateMenu(ContextMenuStrip menu)
    {
        var enableItem = new ToolStripMenuItem(_enabled ? "Enabled (click to disable)" : "Disabled (click to enable)");
        enableItem.Font = new Font(enableItem.Font, FontStyle.Bold);
        enableItem.Click += (_, _) => { ToggleEnabled(); };
        menu.Items.Add(enableItem);

        menu.Items.Add(new ToolStripSeparator());

        // Intensity submenu
        var intensityMenu = new ToolStripMenuItem("Intensity");
        foreach (var (label, val) in new[] { ("Low (40%)", (byte)40), ("Medium (70%)", (byte)70), ("High (100%)", (byte)100) })
        {
            byte captured = val;
            var item = new ToolStripMenuItem(label) { Checked = _intensity == val };
            item.Click += (_, _) => { _intensity = captured; RebuildMenu(); };
            intensityMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(intensityMenu);

        menu.Items.Add(new ToolStripSeparator());

        bool startsUp = IsStartupEnabled();
        var startupItem = new ToolStripMenuItem(startsUp ? "Run at startup (on)" : "Run at startup (off)");
        startupItem.Click += (_, _) => { ToggleStartup(); RebuildMenu(); };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);
    }

    private bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, false);
        return key?.GetValue(StartupName) != null;
    }

    private void ToggleStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
        if (key == null) return;
        if (IsStartupEnabled())
            key.DeleteValue(StartupName, false);
        else
            key.SetValue(StartupName, $"\"{Application.ExecutablePath}\"");
    }

    // 16x16 tray icon: pen at 45°, cap upper-left, tip lower-right.
    // Grey when pen not found, green when connected.
    private static Icon BuildIcon(bool connected)
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var bodyColor = connected ? Color.FromArgb(0, 200, 83)  : Color.FromArgb(140, 140, 140);
            var capColor  = connected ? Color.FromArgb(0, 130, 55)  : Color.FromArgb(85,  85,  85);
            var tipColor  = connected ? Color.FromArgb(190, 255, 190) : Color.FromArgb(205, 205, 205);

            // Work in local space (vertical pen centered at origin),
            // then rotate 45° CW so cap goes upper-left, tip lower-right.
            g.TranslateTransform(8f, 8f);
            g.RotateTransform(45f);

            // Cap / eraser end
            using (var b = new SolidBrush(capColor))
                g.FillRectangle(b, -1.5f, -6.5f, 3f, 2f);

            // Pen body
            using (var b = new SolidBrush(bodyColor))
                g.FillRectangle(b, -1.5f, -4.5f, 3f, 8f);

            // Nib / tip triangle
            var tip = new PointF[] { new(-1.5f, 3.5f), new(1.5f, 3.5f), new(0f, 6.5f) };
            using (var b = new SolidBrush(tipColor))
                g.FillPolygon(b, tip);

            g.ResetTransform();
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void ExitApp()
    {
        _reconnectTimer.Stop();
        _monitor?.Dispose();
        _haptic.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ExitApp();
        base.Dispose(disposing);
    }
}
