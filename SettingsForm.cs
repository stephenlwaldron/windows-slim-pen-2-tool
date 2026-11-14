using System;
using System.Drawing;
using System.Windows.Forms;

namespace SlimPenHaptics;

// Small window shown on first launch and when the tray icon is double-clicked.
// Says the app is running, where it lives, and offers the same settings as the tray menu.
public class SettingsForm : Form
{
    private readonly Func<bool> _isConnected;
    private readonly Label      _status;
    private readonly System.Windows.Forms.Timer _statusTimer;

    public SettingsForm(Settings settings, Icon icon, Func<bool> isConnected,
                        Func<bool> isStartupEnabled, Action<bool> setStartupEnabled, Action changed)
    {
        _isConnected = isConnected;

        Text            = "Slim Pen Haptics";
        Icon            = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode   = AutoScaleMode.Dpi;
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        Padding         = new Padding(16);

        var table = new TableLayoutPanel
        {
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount  = 2,
            Dock         = DockStyle.Fill,
        };

        var heading = new Label
        {
            Text     = "Slim Pen Haptics is running",
            Font     = new Font(Font.FontFamily, Font.Size * 1.4f, FontStyle.Bold),
            AutoSize = true,
            Margin   = new Padding(3, 0, 3, 8),
        };

        _status = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 8) };

        var info = new Label
        {
            Text = "It lives in the system tray, next to the clock. If you can't see the pen icon, " +
                   "click the ^ arrow there. Right-click the icon for these settings, or double-click " +
                   "it to open this window again.",
            AutoSize    = true,
            MaximumSize = new Size(LogicalToDeviceUnits(440), 0),
            Margin      = new Padding(3, 0, 3, 14),
        };

        var intensity = NewCombo();
        foreach (byte value in Settings.IntensityChoices) intensity.Items.Add(Settings.IntensityLabel(value));
        intensity.SelectedIndex = Math.Max(0, Array.IndexOf(Settings.IntensityChoices, settings.Intensity));
        intensity.SelectedIndexChanged += (_, _) =>
        {
            settings.Intensity = Settings.IntensityChoices[intensity.SelectedIndex];
            changed();
        };

        var texture = NewCombo();
        foreach (var (label, _) in Settings.TextureChoices) texture.Items.Add(label);
        texture.SelectedIndex = Math.Max(0, Array.FindIndex(Settings.TextureChoices, c => c.Waveform == settings.Texture));
        texture.SelectedIndexChanged += (_, _) =>
        {
            settings.Texture = Settings.TextureChoices[texture.SelectedIndex].Waveform;
            changed();
        };

        var touchClick = NewCheck("Click when the pen touches down and lifts", settings.TouchClick);
        touchClick.CheckedChanged += (_, _) => { settings.TouchClick = touchClick.Checked; changed(); };

        var startup = NewCheck("Start with Windows", isStartupEnabled());
        startup.CheckedChanged += (_, _) => { setStartupEnabled(startup.Checked); changed(); };

        var notify = NewCheck("Show a notification when it starts", settings.StartupNotification);
        notify.CheckedChanged += (_, _) => { settings.StartupNotification = notify.Checked; changed(); };

        var close = new Button
        {
            Text     = "Got it",
            AutoSize = true,
            Anchor   = AnchorStyles.Right,
            Padding  = new Padding(12, 2, 12, 2),
            Margin   = new Padding(3, 14, 3, 0),
        };
        close.Click += (_, _) => Close();
        AcceptButton = close;

        AddWide(table, heading);
        AddWide(table, _status);
        AddWide(table, info);
        AddRow(table, "Intensity", intensity);
        AddRow(table, "Drag texture", texture);
        AddWide(table, touchClick);
        AddWide(table, startup);
        AddWide(table, notify);
        AddWide(table, close);
        Controls.Add(table);

        // The pen can connect or drop while the window is open.
        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        bool connected = _isConnected();
        _status.Text = connected
            ? "●  Pen connected. Haptics are active in every app."
            : "●  Pen not found yet. Wake it with a tap; it connects within a few seconds.";
        _status.ForeColor = connected ? Color.FromArgb(0, 130, 55) : Color.FromArgb(160, 90, 0);
    }

    private ComboBox NewCombo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width         = LogicalToDeviceUnits(190),
        Margin        = new Padding(3, 3, 3, 6),
    };

    private static CheckBox NewCheck(string text, bool isChecked) => new()
    {
        Text     = text,
        Checked  = isChecked,
        AutoSize = true,
        Margin   = new Padding(3, 4, 3, 2),
    };

    private static void AddWide(TableLayoutPanel table, Control control)
    {
        table.Controls.Add(control);
        table.SetColumnSpan(control, 2);
    }

    private static void AddRow(TableLayoutPanel table, string caption, Control control)
    {
        table.Controls.Add(new Label
        {
            Text     = caption,
            AutoSize = true,
            Anchor   = AnchorStyles.Left,
            Margin   = new Padding(3, 0, 12, 3),
        });
        table.Controls.Add(control);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _statusTimer.Dispose();
        base.Dispose(disposing);
    }
}
