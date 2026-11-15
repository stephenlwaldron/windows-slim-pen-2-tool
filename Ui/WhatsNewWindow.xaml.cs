using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SlimPenHaptics.Ui;

// Shown once after an update, and on request from General settings: what changed, from Changelog.
public partial class WhatsNewWindow : Window
{
    private readonly Action? _openSettings;

    // openSettings: null when the settings window is already what opened this one.
    public WhatsNewWindow(IReadOnlyList<Changelog.Release> releases, Action? openSettings)
    {
        InitializeComponent();
        _openSettings = openSettings;
        if (openSettings == null) OpenButton.Visibility = Visibility.Collapsed;

        Heading.Text = releases.Count > 0 ? $"What's new in {releases[0].Version}" : "What's new";

        foreach (var release in releases)
        {
            // Only say which version a group belongs to when there is more than one.
            if (releases.Count > 1)
            {
                Items.Children.Add(new TextBlock
                {
                    Text = $"Version {release.Version}",
                    FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.65,
                    Margin = new Thickness(0, 6, 0, 8),
                });
            }

            foreach (var item in release.Items)
            {
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = item.Title, FontSize = 14, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock
                {
                    Text = item.Detail, FontSize = 12, Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                });

                Items.Children.Add(new Border
                {
                    Child = text,
                    Padding = new Thickness(16, 12, 16, 12),
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Background = TryFindResource("CardBackgroundFillColorDefaultBrush") as Brush,
                    BorderBrush = TryFindResource("CardStrokeColorDefaultBrush") as Brush,
                });
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _openSettings?.Invoke();
    }
}
