using System;
using System.Diagnostics;
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
    private readonly Settings     _settings = Settings.Load();
    private SettingsForm?         _window;

    private System.Windows.Forms.Timer _reconnectTimer;

    // Drag texture state
    private bool     _texturePlaying;
    private byte     _textureIntensity;
    private long     _textureSentAt;
    private readonly Stopwatch _textureClock = Stopwatch.StartNew();

    private const double TextureStartTravel   = 6;      // px moved before a touch counts as a drag
    private const double TextureMinSpeed      = 20;     // px/s; slower than this is "resting"
    private const double TextureFullSpeed     = 800;    // px/s that reaches full intensity
    private const double TextureFloor         = 0.5;    // share of intensity at the slowest drag
    private const int    TextureMinStep       = 20;     // intensity change (of 255) worth resending
    private const int    TextureMinIntervalMs = 100;    // retriggering restarts the waveform

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
        _tray.DoubleClick += (_, _) => ShowWindow();

        // Try connecting to the pen on startup
        TryConnect();

        // A tray app starts invisibly, so say so: a window the first time, a notification after.
        if (!_settings.WelcomeShown)
        {
            _settings.WelcomeShown = true;
            _settings.Save();
            ShowWindow();
        }
        else if (_settings.StartupNotification)
        {
            _tray.ShowBalloonTip(4000, "Slim Pen Haptics is running",
                _haptic.IsConnected
                    ? "Pen connected. Right-click the pen icon in the tray for settings."
                    : "Pen not found yet. It will connect when the pen wakes up.",
                ToolTipIcon.Info);
        }

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
            _monitor.PenMove += OnPenMove;
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
        _texturePlaying = false;
        if (_enabled && _settings.TouchClick) _haptic.Buzz(Waveform.Click, Level(1.0));
    }

    private void OnPenUp()
    {
        StopTexture();
        if (_enabled && _settings.TouchClick) _haptic.Buzz(Waveform.Release, Level(1.0));
    }

    // The pen's intensity field runs 1..255; _settings.Intensity is the menu percentage of that.
    private byte Level(double fraction) =>
        (byte)Math.Clamp(Math.Round(255 * _settings.Intensity / 100.0 * fraction), 1, 255);

    // Drag feedback: play the chosen texture while the tip moves, scaling its
    // intensity with stroke speed and pressure, and go quiet when the pen rests.
    private void OnPenMove(double speed, double pressure, double travel)
    {
        if (!_enabled || _settings.Texture == Waveform.None) return;

        if (travel < TextureStartTravel || speed < TextureMinSpeed)
        {
            StopTexture();
            return;
        }

        // Needs twice the resting speed to start, so it doesn't flutter at the threshold.
        if (!_texturePlaying && speed < TextureMinSpeed * 2) return;

        double speedPart    = Math.Sqrt(Math.Min(speed / TextureFullSpeed, 1.0));
        double pressurePart = 0.5 + 0.5 * pressure;
        double level        = TextureFloor + (1.0 - TextureFloor) * speedPart * pressurePart;
        byte   target       = Level(level);

        long now     = _textureClock.ElapsedMilliseconds;
        long elapsed = now - _textureSentAt;

        // The pen plays a texture for a fixed time, so retrigger before it runs out.
        int  duration  = _haptic.DurationMs(_settings.Texture);
        long keepAlive = duration > 0 ? duration * 3 / 4 : 1000;

        bool changed = Math.Abs(target - _textureIntensity) >= TextureMinStep &&
                       elapsed >= TextureMinIntervalMs;

        if (!_texturePlaying || changed || elapsed >= keepAlive)
        {
            _haptic.BuzzTexture(_settings.Texture, target);
            _texturePlaying   = true;
            _textureIntensity = target;
            _textureSentAt    = now;
        }
    }

    private void StopTexture()
    {
        if (!_texturePlaying) return;
        _texturePlaying = false;
        _haptic.Buzz(Waveform.Stop);
    }

    private void ToggleEnabled()
    {
        _enabled = !_enabled;
        RebuildMenu();
    }

    private void ShowWindow()
    {
        if (_window == null || _window.IsDisposed)
        {
            _window = new SettingsForm(_settings, BuildIcon(connected: true),
                () => _haptic.IsConnected, IsStartupEnabled,
                on => { if (on != IsStartupEnabled()) ToggleStartup(); },
                () => { StopTexture(); _settings.Save(); RebuildMenu(); });
        }
        _window.Show();
        _window.Activate();
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

        var windowItem = new ToolStripMenuItem("Open settings window");
        windowItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(windowItem);

        menu.Items.Add(new ToolStripSeparator());

        // Intensity submenu
        var intensityMenu = new ToolStripMenuItem("Intensity");
        foreach (byte val in Settings.IntensityChoices)
        {
            string label = Settings.IntensityLabel(val);
            byte captured = val;
            var item = new ToolStripMenuItem(label) { Checked = _settings.Intensity == val };
            item.Click += (_, _) => { _settings.Intensity = captured; _settings.Save(); RebuildMenu(); };
            intensityMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(intensityMenu);

        var touchItem = new ToolStripMenuItem("Click on touch") { Checked = _settings.TouchClick };
        touchItem.Click += (_, _) => { _settings.TouchClick = !_settings.TouchClick; _settings.Save(); RebuildMenu(); };
        menu.Items.Add(touchItem);

        // Drag texture submenu
        var textureMenu = new ToolStripMenuItem("Drag texture");
        foreach (var (label, wf) in Settings.TextureChoices)
        {
            Waveform captured = wf;
            var item = new ToolStripMenuItem(label) { Checked = _settings.Texture == wf };
            item.Click += (_, _) => { StopTexture(); _settings.Texture = captured; _settings.Save(); RebuildMenu(); };
            textureMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(textureMenu);

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
