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
            Subtitle = _settings.Default.Intensity == 0 ? "Off, except your listed apps"
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
        if (profile.Intensity == 0) return "Off (0%)";
        return profile.Texture == Waveform.None ? "No drag texture" : $"{texture}, {profile.Intensity}%";
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
        IntensitySlider.Value    = Math.Clamp((int)Math.Round(profile.Intensity / 10.0) * 10, 0, 100);
        TouchClickBox.IsChecked  = profile.TouchClick;
        RefreshIntensityText();

        _loading = false;
    }

    private void RefreshIntensityText()
    {
        int value = (int)IntensitySlider.Value;
        IntensityText.Text = value == 0                         ? "0% (off)"
                           : value == Settings.DefaultIntensity ? $"{value}% (default)"
                                                                : $"{value}%";
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
        if (_loading || IntensityText == null) return;
        Editing.Intensity = (byte)IntensitySlider.Value;
        RefreshIntensityText();
        Changed();
    }

    private void TouchClickBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Editing.TouchClick = TouchClickBox.IsChecked == true;
        Changed();
    }

    private void FeelButton_Click(object sender, RoutedEventArgs e) => _app.Preview(Editing);

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
