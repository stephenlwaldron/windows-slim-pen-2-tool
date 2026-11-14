using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SlimPenHaptics.Ui;

public class AppChoice
{
    public string       Name   { get; init; } = "";
    public string       Detail { get; init; } = "";
    public string       Path   { get; init; } = "";
    public ImageSource? Icon   { get; init; }

    // What screen readers announce for the row.
    public override string ToString() => Name;
}

// Lets the user pick an app: ones they've just drawn in, ones that are open, or any exe.
public partial class AddAppWindow : Window
{
    public string? SelectedPath { get; private set; }

    // Windows' own shells; a profile for these is rarely what anyone means.
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe", "TextInputHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "SystemSettings.exe", "LockApp.exe",
    };

    public AddAppWindow(TrayApp app, Settings settings)
    {
        InitializeComponent();

        string own  = Path.GetFileName(Environment.ProcessPath ?? "");
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choices = new List<AppChoice>();

        void Offer(string? path, string detail)
        {
            if (string.IsNullOrEmpty(path)) return;
            string exe = Path.GetFileName(path);
            if (exe.Equals(own, StringComparison.OrdinalIgnoreCase) || Hidden.Contains(exe)) return;
            if (settings.FindApp(exe) != null || !seen.Add(exe)) return;
            choices.Add(new AppChoice
            {
                Name   = Icons.DisplayName(path),
                Detail = $"{exe}  ·  {detail}",
                Path   = path,
                Icon   = Icons.ForExe(path),
            });
        }

        foreach (string path in app.RecentApps) Offer(path, "drawn in recently");

        var open = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(process.MainWindowTitle)) continue;
                    string? path = AppResolver.ExePath((uint)process.Id);
                    if (path != null) open.Add(path);
                }
                catch (Exception)
                {
                    // Processes come and go, and some can't be inspected.
                }
            }
        }
        int recent = choices.Count;
        foreach (string path in open) Offer(path, "open now");
        choices.Sort(recent, choices.Count - recent,
            Comparer<AppChoice>.Create((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)));

        AppList.ItemsSource = choices;
        if (choices.Count == 0) EmptyText.Visibility = Visibility.Visible;
    }

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AddButton.IsEnabled = AppList.SelectedItem is AppChoice;

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AppList.SelectedItem is AppChoice) Add_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.SelectedItem is not AppChoice choice) return;
        SelectedPath = choice.Path;
        DialogResult = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Choose an app",
            Filter = "Apps (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) != true) return;
        SelectedPath = dialog.FileName;
        DialogResult = true;
    }
}
