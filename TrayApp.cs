using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SlimPenHaptics;

// The app itself: tray icon, pen input, and turning strokes into haptics.
// The settings window (Ui.MainWindow) is a view over this.
public sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon   _tray;
    private readonly HapticEngine _haptic = new();
    private readonly PenMonitor   _monitor;
    private Ui.MainWindow?        _window;

    private readonly System.Windows.Forms.Timer _reconnectTimer;
    private readonly System.Windows.Forms.Timer _previewTimer;
    private bool _iconActive;
    private bool _disposed;

    public Settings Settings { get; } = Settings.Load();
    public Updater  Updater  { get; } = new();

    // The app under the pen and the profile that applies there (null = haptics off in it)
    private string?  _activeExe;
    private string?  _eraserSentTo;     // app whose eraser shortcut was pressed on the last flip
    private bool     _eraserEndLogged;
    private Profile? _profile;
    private readonly List<string> _recentApps = new();
    private static readonly string OwnExe = Path.GetFileName(Environment.ProcessPath ?? "SlimPenHaptics.exe");

    // Waking the pen's Bluetooth link after it has been idle
    private long _penSeenAt;
    private long _wakeUntil;
    private long _wakeProbeAt;
    private bool _wakeProbed;

    private const int WakeIdleMs     = 2000;    // no pen input for this long counts as "put away"
    private const int WakeWindowMs   = 8000;    // how long to keep trying after it reappears
    private const int WakeProbeMs    = 400;     // gap between tries
    private const int TextureRetryMs = 150;     // resend a texture that didn't reach the pen
    private const int PreviewMs      = 1200;    // length of a "Feel it" preview
    private const int RecentAppCount = 8;

    // Drag texture state
    private bool     _texturePlaying;
    private byte     _textureIntensity;
    private long     _textureSentAt;
    private readonly Stopwatch _textureClock = Stopwatch.StartNew();

    private const double TextureStartTravel   = 6;      // px moved before a touch counts as a drag
    private const double TextureMinSpeed      = 20;     // px/s; slower than this is "resting"
    private const int    TextureMinStep       = 20;     // intensity change (of 255) worth resending
    private const int    TextureMinIntervalMs = 100;    // retriggering restarts the waveform

    private const string StartupKey     = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName    = "SlimPenHaptics";
    public  const string BackgroundFlag = "--background";

    // background: started by Windows at sign-in, so stay out of the way.
    public TrayApp(bool background)
    {
        _profile = Settings.Default;

        _tray = new NotifyIcon
        {
            Icon    = BuildIcon(active: false),
            Visible = true,
            Text    = "Slim Pen Haptics",
        };
        _tray.ContextMenuStrip = BuildMenu();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        // Pen input comes through the screen, so it can be watched before the pen's
        // Bluetooth side (the haptics) is reachable.
        _monitor = new PenMonitor();
        _monitor.PenSeen += OnPenSeen;
        _monitor.PenDown += OnPenDown;
        _monitor.PenUp   += OnPenUp;
        _monitor.PenMove += OnPenMove;
        _monitor.EraserEnd += OnEraserEnd;

        TryConnect();
        Log.Write($"started v{Updater.Version}" + (_haptic.IsConnected ? "" : "; pen not reachable yet (asleep or not paired)"));

        RepairStartupEntry();

        // A tray app starts invisibly, so say so: the window when the user launched it,
        // a notification when Windows did.
        if (!background || !Settings.WelcomeShown)
        {
            ShowWindow();
        }
        else if (Settings.StartupNotification)
        {
            _tray.ShowBalloonTip(4000, "Slim Pen Haptics is running",
                _haptic.IsConnected
                    ? "Pen connected. Click the pen icon in the tray for settings."
                    : "Pen not found yet. It will connect when the pen wakes up.",
                ToolTipIcon.Info);
        }

        _reconnectTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _reconnectTimer.Tick += (_, _) =>
        {
            // Look for the pen until it has been found once; after that only re-scan if the
            // link is up but buzzes still fail (for example after re-pairing).
            if (!_haptic.HasDevice || (_haptic.IsConnected && !_haptic.LastWriteOk)) TryConnect();
            UpdateIcon();
        };
        _reconnectTimer.Start();

        _previewTimer = new System.Windows.Forms.Timer { Interval = PreviewMs };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            _haptic.Buzz(Waveform.Stop);
        };

        _ = Updater.CheckAsync();
    }

    public bool IsPenConnected => _haptic.IsConnected;

    // Recently drawn-in apps, newest first, as full exe paths.
    public IReadOnlyList<string> RecentApps => _recentApps;

    // Called by the settings window after it changes anything.
    public void SettingsChanged()
    {
        StopTexture();
        Settings.Save();
        _profile = Settings.ProfileFor(_activeExe);
        RebuildMenu();
        UpdateIcon();
    }

    // "Feel it": play a profile's texture briefly, without drawing.
    public void Preview(Profile profile)
    {
        _previewTimer.Stop();
        if (!profile.HasTexture) return;
        _haptic.BuzzTexture(profile.Texture, Level(profile.Intensity, 1.0));
        _previewTimer.Start();
    }

    // Play the touch click once at a profile's click intensity.
    public void PreviewClick(Profile profile)
    {
        if (profile.ClickIntensity > 0) _haptic.Buzz(Waveform.Click, Level(profile.ClickIntensity, 1.0));
    }

    public void ShowWindow()
    {
        if (_window == null)
        {
            _window = new Ui.MainWindow(this);
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == System.Windows.WindowState.Minimized)
            _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    public void Exit()
    {
        Dispose();
        System.Windows.Application.Current?.Shutdown();
    }

    // ── Pen link ───────────────────────────────────────────────────────────

    private void TryConnect()
    {
        _haptic.Connect();
        UpdateIcon();
    }

    private void UpdateIcon()
    {
        bool connected = _haptic.IsConnected;
        bool active    = connected && Settings.Enabled;
        _tray.Text = !Settings.Enabled ? "Slim Pen Haptics — turned off"
                   : connected         ? "Slim Pen Haptics — connected"
                                       : "Slim Pen Haptics — pen asleep or not found";
        if (active == _iconActive) return;
        _iconActive = active;
        if (Settings.Enabled) Log.Write(connected ? "pen link up" : "pen link down (asleep, docked or out of range)");
        _tray.Icon = BuildIcon(active);
    }

    // The pen drops its Bluetooth link when it sleeps, while its tip keeps working through
    // the screen. So when the pen shows up after a pause, poke the link straight away and
    // keep poking until a buzz gets through, instead of finding out on the first stroke.
    private void OnPenSeen()
    {
        long now  = _textureClock.ElapsedMilliseconds;
        bool woke = now - _penSeenAt > WakeIdleMs;
        _penSeenAt = now;
        if (!Settings.Enabled) return;

        if (woke)
        {
            _wakeUntil   = now + WakeWindowMs;
            _wakeProbed  = false;
            _wakeProbeAt = now - WakeProbeMs;
        }

        if (now >= _wakeUntil || now - _wakeProbeAt < WakeProbeMs) return;

        if (_wakeProbed && _haptic.IsConnected && _haptic.LastWriteOk)
        {
            _wakeUntil = 0;
            UpdateIcon();
            return;
        }

        _wakeProbeAt = now;
        _wakeProbed  = true;
        if (_haptic.HasDevice) _haptic.Probe();
        else                   TryConnect();
    }

    // ── Strokes ────────────────────────────────────────────────────────────

    private void OnPenDown()
    {
        _texturePlaying = false;
        ResolveProfile();
        if (Settings.Enabled && _profile is { HasClick: true })
            _haptic.Buzz(Waveform.Click, Level(_profile.ClickIntensity, 1.0));
    }

    private void OnPenUp()
    {
        StopTexture();
        if (Settings.Enabled && _profile is { HasClick: true })
            _haptic.Buzz(Waveform.Release, Level(_profile.ClickIntensity, 1.0));
    }

    // Flipping the pen presses the app's own eraser shortcut, for apps that ignore the eraser end.
    // Keys go to the window with keyboard focus, so only act when that is the app under the pen.
    private void OnEraserEnd(bool eraser)
    {
        string? under = AppResolver.ExeUnderPen();
        string? exe   = under == null ? null : Path.GetFileName(under);
        var app = Settings.FindApp(exe);

        if (eraser)
        {
            _eraserSentTo = null;
            if (!_eraserEndLogged)
            {
                // Once per run, so a bug report shows whether this pen reports its eraser end at all.
                _eraserEndLogged = true;
                Log.Write("pen eraser end detected");
            }
            if (app == null || string.IsNullOrEmpty(app.EraserShortcut)) return;
            if (!IsForeground(exe)) return;
            if (KeySender.Send(app.EraserShortcut)) _eraserSentTo = app.Exe;
            Log.Write($"eraser end over {exe}: sent {KeySender.Display(app.EraserShortcut)}");
        }
        else
        {
            // Only switch back where this app switched to the eraser in the first place.
            string? sentTo = _eraserSentTo;
            _eraserSentTo = null;
            if (app == null || sentTo == null || string.IsNullOrEmpty(app.DrawShortcut)) return;
            if (!string.Equals(sentTo, app.Exe, StringComparison.OrdinalIgnoreCase) || !IsForeground(exe)) return;
            KeySender.Send(app.DrawShortcut);
            Log.Write($"tip over {exe}: sent {KeySender.Display(app.DrawShortcut)}");
        }
    }

    private static bool IsForeground(string? exe)
    {
        string? focused = AppResolver.ForegroundExe();
        return focused != null && string.Equals(Path.GetFileName(focused), exe, StringComparison.OrdinalIgnoreCase);
    }

    // Pick the profile for whichever app the pen just touched.
    private void ResolveProfile()
    {
        string? path = AppResolver.ExeUnderPen();
        string? exe  = path == null ? null : Path.GetFileName(path);

        _profile = Settings.ProfileFor(exe);

        if (!string.Equals(exe, _activeExe, StringComparison.OrdinalIgnoreCase))
        {
            _activeExe = exe;
            var app = Settings.FindApp(exe);
            if (app != null) Log.Write($"pen in {exe}: {(app.Enabled ? "its own profile" : "haptics off")}");
        }

        if (path != null && !string.Equals(exe, OwnExe, StringComparison.OrdinalIgnoreCase))
        {
            _recentApps.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _recentApps.Insert(0, path);
            if (_recentApps.Count > RecentAppCount) _recentApps.RemoveAt(RecentAppCount);
        }
    }

    // The pen's intensity field runs 1..255; the sliders are a percentage of that.
    private static byte Level(byte percent, double fraction) =>
        (byte)Math.Clamp(Math.Round(255 * percent / 100.0 * fraction), 1, 255);

    // Drag feedback: play the chosen texture while the tip moves, scaling its
    // intensity with stroke speed and pressure, and go quiet when the pen rests.
    private void OnPenMove(double speed, double pressure, double travel)
    {
        var profile = _profile;
        if (!Settings.Enabled || profile == null || !profile.HasTexture) return;

        if (travel < TextureStartTravel || speed < TextureMinSpeed)
        {
            StopTexture();
            return;
        }

        // Needs twice the resting speed to start, so it doesn't flutter at the threshold.
        if (!_texturePlaying && speed < TextureMinSpeed * 2) return;

        double level        = profile.Response(speed, pressure);
        byte   target       = Level(profile.Intensity, level);

        long now     = _textureClock.ElapsedMilliseconds;
        long elapsed = now - _textureSentAt;

        // The pen plays a texture for a fixed time, so retrigger before it runs out.
        int  duration  = _haptic.DurationMs(profile.Texture);
        long keepAlive = duration > 0 ? duration * 3 / 4 : 1000;

        bool changed = Math.Abs(target - _textureIntensity) >= TextureMinStep &&
                       elapsed >= TextureMinIntervalMs;
        bool retry   = !_haptic.LastWriteOk && elapsed >= TextureRetryMs;

        if (!_texturePlaying || changed || retry || elapsed >= keepAlive)
        {
            _previewTimer.Stop();
            _haptic.BuzzTexture(profile.Texture, target);
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

    // ── Tray menu ──────────────────────────────────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open Slim Pen Haptics");
        openItem.Font = new Font(openItem.Font, FontStyle.Bold);
        openItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(openItem);

        var enableItem = new ToolStripMenuItem("Haptics on") { Checked = Settings.Enabled };
        enableItem.Click += (_, _) =>
        {
            Settings.Enabled = !Settings.Enabled;
            SettingsChanged();
            _window?.Reload();
        };
        menu.Items.Add(enableItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Exit();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void RebuildMenu()
    {
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    // ── Start with Windows ─────────────────────────────────────────────────

    private static string StartupCommand => $"\"{Environment.ProcessPath}\" {BackgroundFlag}";

    public bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, false);
        return key?.GetValue(StartupName) != null;
    }

    public void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
        if (key == null) return;
        if (enabled) key.SetValue(StartupName, StartupCommand);
        else         key.DeleteValue(StartupName, false);
    }

    // Uninstalling shouldn't leave Windows trying to start an app that is gone.
    public static void RemoveStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
            key?.DeleteValue(StartupName, false);
        }
        catch (Exception)
        {
            // Nothing more to do during an uninstall.
        }
    }

    // Keep an existing entry pointing at this copy of the app, started quietly. Covers people
    // moving from the loose v1 exe to the installed app, and the exe being moved.
    private void RepairStartupEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
            if (key?.GetValue(StartupName) is string current && current != StartupCommand)
                key.SetValue(StartupName, StartupCommand);
        }
        catch (Exception)
        {
            // Startup is a convenience; never fail to launch over it.
        }
    }

    // ── Tray icon ──────────────────────────────────────────────────────────

    // Pen at 45°: green when haptics are live, grey when the pen is away or haptics are off.
    private static Icon BuildIcon(bool active)
    {
        int size = SystemInformation.SmallIconSize.Width;
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var bodyColor = active ? Color.FromArgb(0, 200, 83)    : Color.FromArgb(140, 140, 140);
            var capColor  = active ? Color.FromArgb(0, 130, 55)    : Color.FromArgb(85,  85,  85);
            var tipColor  = active ? Color.FromArgb(190, 255, 190) : Color.FromArgb(205, 205, 205);

            // Drawn on a 16-unit grid (vertical pen centred at the origin), then rotated.
            g.TranslateTransform(size / 2f, size / 2f);
            g.RotateTransform(45f);
            g.ScaleTransform(size / 16f, size / 16f);

            using (var b = new SolidBrush(capColor))
                g.FillRectangle(b, -1.5f, -6.5f, 3f, 2f);

            using (var b = new SolidBrush(bodyColor))
                g.FillRectangle(b, -1.5f, -4.5f, 3f, 8f);

            var tip = new PointF[] { new(-1.5f, 3.5f), new(1.5f, 3.5f), new(0f, 6.5f) };
            using (var b = new SolidBrush(tipColor))
                g.FillPolygon(b, tip);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reconnectTimer.Stop();
        _previewTimer.Stop();
        _window?.Close();
        _monitor.Dispose();
        _haptic.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
