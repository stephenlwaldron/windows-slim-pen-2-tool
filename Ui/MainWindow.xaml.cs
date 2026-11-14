using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SlimPenHaptics.Ui;

// One row in the sidebar: the default profile, or an app with its own.
public class NavItem
{
    public string       Title    { get; init; } = "";
    public string       Subtitle { get; init; } = "";
    public ImageSource? Icon     { get; init; }
    public string       Glyph    { get; init; } = "";    // icon-font symbol, shown when there is no Icon
    public AppEntry?    App      { get; init; }      // null = the default profile

    // What screen readers announce for the row.
    public override string ToString() => $"{Title}, {Subtitle}";
}

public partial class MainWindow : Window
{
    private readonly TrayApp  _app;
    private readonly Settings _settings;
    private readonly ObservableCollection<NavItem> _nav = new();
    private readonly DispatcherTimer _statusTimer;

    private AppEntry? _editingApp;      // null while the default profile is shown
    private bool      _loading;         // true while controls are being filled in, to ignore their events

    private const string DefaultGlyph = "";    // "all apps"
    private const string AppGlyph     = "";    // generic app, for exes whose icon can't be read

    private Profile Editing => _editingApp?.Profile ?? _settings.Default;

    public MainWindow(TrayApp app)
    {
        _app      = app;
        _settings = app.Settings;

        // Controls raise change events while they are being created (a slider snapping to its
        // minimum, for one); those must not be mistaken for the user changing a setting.
        _loading = true;
        InitializeComponent();
        _loading = false;

        NavList.ItemsSource = _nav;
        foreach (var (label, _) in Settings.TextureChoices) TextureBox.Items.Add(label);

        if (!_settings.WelcomeShown) WelcomeBanner.Visibility = Visibility.Visible;

        VersionText.Text = $"Version {_app.Updater.Version}";
        _app.Updater.Changed += OnUpdaterChanged;
        Closed += (_, _) => _app.Updater.Changed -= OnUpdaterChanged;

        // The pen can connect or drop while the window is open.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();

        Reload();
    }

    // Rebuild everything from settings, keeping the current selection where possible.
    public void Reload()
    {
        RebuildNav(_editingApp);
        LoadGeneral();
        RefreshStatus();
        RefreshUpdate();
    }

    // ── Sidebar ────────────────────────────────────────────────────────────

    private void RebuildNav(AppEntry? select)
    {
        _loading = true;
        _nav.Clear();
        _nav.Add(new NavItem
        {
            Title    = "Default",
            Subtitle = _settings.Default.IsSilent ? "Off, except your listed apps"
                                                  : "Every app without its own profile",
            Glyph    = DefaultGlyph,
        });
        foreach (var entry in _settings.Apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var icon = Icons.ForExe(entry.Path);
            _nav.Add(new NavItem
            {
                Title    = entry.Name,
                Subtitle = entry.Enabled ? Describe(entry.Profile) : "Haptics off",
                Icon     = icon,
                Glyph    = icon == null ? AppGlyph : "",
                App      = entry,
            });
        }
        _loading = false;

        if (GeneralButton.IsChecked == true) return;
        NavList.SelectedItem = _nav.FirstOrDefault(n => n.App == select) ?? _nav[0];
    }

    private static string Describe(Profile profile)
    {
        string texture = Settings.TextureChoices.FirstOrDefault(c => c.Waveform == profile.Texture).Label ?? "Off";
        if (profile.IsSilent) return "Off";
        if (!profile.HasTexture) return $"Click only, {profile.ClickIntensity}%";
        return $"{texture}, {profile.Intensity}%";
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NavList.SelectedItem is not NavItem item) return;
        GeneralButton.IsChecked = false;
        ShowProfile(item);
    }

    // It behaves like one more row in the list: clicking it again must not switch it off.
    private void GeneralButton_Click(object sender, RoutedEventArgs e) => GeneralButton.IsChecked = true;

    private void GeneralButton_Checked(object sender, RoutedEventArgs e)
    {
        NavList.SelectedItem = null;
        ProfilePane.Visibility = Visibility.Collapsed;
        GeneralPane.Visibility = Visibility.Visible;
    }

    private void AddAppButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new AddAppWindow(_app, _settings) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedPath == null) return;

        string path = picker.SelectedPath;
        string exe  = Path.GetFileName(path);

        var entry = _settings.FindApp(exe);
        if (entry == null)
        {
            entry = new AppEntry
            {
                Exe     = exe,
                Path    = path,
                Name    = Icons.DisplayName(path),
                Profile = _settings.Default.Clone(),
            };
            _settings.Apps.Add(entry);
            _app.SettingsChanged();
        }

        GeneralButton.IsChecked = false;
        RebuildNav(entry);
    }

    // ── Profile pane ───────────────────────────────────────────────────────

    private void ShowProfile(NavItem item)
    {
        _loading    = true;
        _editingApp = item.App;

        GeneralPane.Visibility = Visibility.Collapsed;
        ProfilePane.Visibility = Visibility.Visible;

        HeaderIcon.Source = item.Icon;
        HeaderGlyph.Text  = item.Glyph;
        HeaderTitle.Text  = item.App?.Name ?? "Default";
        HeaderHint.Text   = item.App == null
            ? "How the pen feels in every app that doesn't have its own profile."
            : item.App.Exe;

        var isApp = item.App != null ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility   = isApp;
        AppEnabledCard.Visibility = isApp;
        AppEnabledBox.IsChecked   = item.App?.Enabled ?? true;
        ProfileCards.IsEnabled    = item.App?.Enabled ?? true;
        ProfileCards.Opacity      = ProfileCards.IsEnabled ? 1.0 : 0.45;

        var profile = Editing;
        TextureBox.SelectedIndex = Math.Max(0, Array.FindIndex(Settings.TextureChoices, c => c.Waveform == profile.Texture));
        IntensitySlider.Value    = SliderValue(profile.Intensity);
        TouchClickBox.IsChecked  = profile.TouchClick;
        ClickIntensitySlider.Value = SliderValue(profile.ClickIntensity);
        SensitivitySlider.Value     = SliderValue(profile.Sensitivity);
        MinimumStrengthSlider.Value = SliderValue(profile.MinimumStrength);
        PressureEffectSlider.Value  = SliderValue(profile.PressureEffect);
        RefreshResponse();
        RefreshIntensityText();
        RefreshClickControls();

        _loading = false;
    }

    // Sliders move in steps of 10.
    private static double SliderValue(byte percent) => Math.Clamp((int)Math.Round(percent / 10.0) * 10, 0, 100);

    private static string PercentLabel(double value) =>
        value == 0                         ? "0% (off)"
      : value == Settings.DefaultIntensity ? $"{value}% (default)"
                                           : $"{value}%";

    private void RefreshIntensityText()
    {
        IntensityText.Text      = PercentLabel(IntensitySlider.Value);
        ClickIntensityText.Text = PercentLabel(ClickIntensitySlider.Value);
    }

    // The slider and Confirm button for the click only mean something while the click is on.
    private void RefreshClickControls()
    {
        bool on = TouchClickBox.IsChecked == true;
        ClickIntensityCard.IsEnabled = on;
        ClickIntensityCard.Opacity   = on ? 1.0 : 0.45;
        ConfirmClickButton.IsEnabled = on;
    }

    // Save, then refresh the sidebar subtitles without losing the selection.
    private void Changed()
    {
        _app.SettingsChanged();
        RebuildNav(_editingApp);
    }

    private void AppEnabledBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _editingApp == null) return;
        _editingApp.Enabled = AppEnabledBox.IsChecked == true;
        Changed();
    }

    private void TextureBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TextureBox.SelectedIndex < 0) return;
        Editing.Texture = Settings.TextureChoices[TextureBox.SelectedIndex].Waveform;
        Changed();
    }

    private void IntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Editing.Intensity = (byte)IntensitySlider.Value;
        RefreshIntensityText();
        Changed();
    }

    private void ClickIntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Editing.ClickIntensity = (byte)ClickIntensitySlider.Value;
        RefreshIntensityText();
        Changed();
    }

    private void TouchClickBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Editing.TouchClick = TouchClickBox.IsChecked == true;
        RefreshClickControls();
        Changed();
    }

    private void ConfirmTextureButton_Click(object sender, RoutedEventArgs e) => _app.Preview(Editing);

    // ── Response (advanced) ────────────────────────────────────────────────

    private void ResponseSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        Editing.Sensitivity     = (byte)SensitivitySlider.Value;
        Editing.MinimumStrength = (byte)MinimumStrengthSlider.Value;
        Editing.PressureEffect  = (byte)PressureEffectSlider.Value;
        RefreshResponse();
        _app.SettingsChanged();
    }

    private void ResetResponseButton_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        SensitivitySlider.Value = MinimumStrengthSlider.Value = PressureEffectSlider.Value = Profile.DefaultResponse;
        _loading = false;
        ResponseSlider_ValueChanged(sender, null!);
    }

    private static string ResponseLabel(double value) =>
        value == Profile.DefaultResponse ? $"{value}% (default)" : $"{value}%";

    private void RefreshResponse()
    {
        SensitivityText.Text     = ResponseLabel(SensitivitySlider.Value);
        MinimumStrengthText.Text = ResponseLabel(MinimumStrengthSlider.Value);
        PressureEffectText.Text  = ResponseLabel(PressureEffectSlider.Value);
        DrawCurve();
    }

    private const double CurveMaxSpeed  = 1600;     // px/s at the right edge of the graph
    private const double LightPressure  = 0.2;
    private const int    CurveSamples   = 64;

    // Strength against stroke speed, from the same function the pen uses, for a firm and a light press.
    private void DrawCurve()
    {
        var profile = new Profile
        {
            Sensitivity     = (byte)SensitivitySlider.Value,
            MinimumStrength = (byte)MinimumStrengthSlider.Value,
            PressureEffect  = (byte)PressureEffectSlider.Value,
        };

        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        var light  = accent.Clone();
        light.Opacity = 0.4;
        FirmSwatch.Fill  = accent;
        LightSwatch.Fill = light;

        double w = CurveCanvas.Width, h = CurveCanvas.Height;
        CurveCanvas.Children.Clear();

        // faint guide lines at 50% and 100%
        foreach (double share in new[] { 0.5, 1.0 })
        {
            double y = h - 2 - share * (h - 4);
            CurveCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = w, Y1 = y, Y2 = y,
                Stroke = Foreground, Opacity = 0.12, StrokeThickness = 1,
            });
        }

        foreach (var (pressure, brush, thickness) in new[] { (LightPressure, light, 2.0), (1.0, accent, 2.5) })
        {
            var line = new System.Windows.Shapes.Polyline { Stroke = brush, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i <= CurveSamples; i++)
            {
                double speed = CurveMaxSpeed * i / CurveSamples;
                double level = profile.Response(speed, pressure);
                line.Points.Add(new Point(w * i / CurveSamples, h - 2 - level * (h - 4)));
            }
            CurveCanvas.Children.Add(line);
        }
    }

    private void ConfirmClickButton_Click(object sender, RoutedEventArgs e) => _app.PreviewClick(Editing);

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editingApp == null) return;
        _settings.Apps.Remove(_editingApp);
        _editingApp = null;
        _app.SettingsChanged();
        RebuildNav(null);
    }

    private void WelcomeDismiss_Click(object sender, RoutedEventArgs e)
    {
        WelcomeBanner.Visibility = Visibility.Collapsed;
        _settings.WelcomeShown = true;
        _app.SettingsChanged();
    }

    // ── General pane ───────────────────────────────────────────────────────

    private void LoadGeneral()
    {
        _loading = true;
        EnabledBox.IsChecked = _settings.Enabled;
        StartupBox.IsChecked = _app.IsStartupEnabled();
        NotifyBox.IsChecked  = _settings.StartupNotification;
        _loading = false;
    }

    private void EnabledBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Enabled = EnabledBox.IsChecked == true;
        _app.SettingsChanged();
        RefreshStatus();
    }

    private void StartupBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.SetStartupEnabled(StartupBox.IsChecked == true);
    }

    private void NotifyBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.StartupNotification = NotifyBox.IsChecked == true;
        _app.SettingsChanged();
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e) => await _app.Updater.CheckAsync();

    private void RestartUpdateButton_Click(object sender, RoutedEventArgs e) => _app.Updater.RestartToUpdate();

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(RefreshUpdate);

    private void RefreshUpdate()
    {
        UpdateText.Text = _app.Updater.Status;
        RestartUpdateButton.Visibility = _app.Updater.CanRestartToUpdate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LogFolder_Click(object sender, RoutedEventArgs e) =>
        Open(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlimPenHaptics"));

    private void GitHub_Click(object sender, RoutedEventArgs e) =>
        Open("https://github.com/stephenlwaldron/windows-slim-pen-haptics-unlocked");

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { /* nothing useful to do if the shell refuses */ }
    }

    // ── Pen status ─────────────────────────────────────────────────────────

    private void RefreshStatus()
    {
        bool connected = _app.IsPenConnected;
        if (!_settings.Enabled)
        {
            StatusText.Text = "Haptics are turned off";
            StatusDot.Fill  = Brushes.Gray;
        }
        else if (connected)
        {
            StatusText.Text = "Pen connected";
            StatusDot.Fill  = new SolidColorBrush(Color.FromRgb(0, 176, 80));
        }
        else
        {
            StatusText.Text = "Pen asleep or not found. It connects when you pick it up.";
            StatusDot.Fill  = new SolidColorBrush(Color.FromRgb(214, 138, 0));
        }
    }
}
