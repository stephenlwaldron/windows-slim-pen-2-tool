using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimPenHaptics;

// User choices from the tray menu, kept in %AppData%\SlimPenHaptics\settings.json.
public class Settings
{
    public const byte DefaultIntensity = 80;

    public byte     Intensity  { get; set; } = DefaultIntensity;
    public Waveform Texture    { get; set; } = Waveform.PencilCont;    // Waveform.None = off
    public bool     TouchClick { get; set; }

    public bool StartupNotification { get; set; } = true;
    public bool WelcomeShown        { get; set; }   // the first-launch window has been seen

    // Choices offered in the tray menu and the settings window.
    public static readonly byte[] IntensityChoices = { 20, 40, 60, 80, 100 };

    public static readonly (string Label, Waveform Waveform)[] TextureChoices =
    {
        ("Off", Waveform.None), ("Ink", Waveform.InkCont), ("Pencil", Waveform.PencilCont),
        ("Marker", Waveform.MarkerCont), ("Brush", Waveform.BrushCont),
    };

    public static string IntensityLabel(byte value) =>
        value == DefaultIntensity ? $"{value}% (Default)" : $"{value}%";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlimPenHaptics", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    // A missing or unreadable file just means defaults.
    public static Settings Load()
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
            if (loaded == null) return new Settings();
            if (loaded.Intensity < 1 || loaded.Intensity > 100) loaded.Intensity = DefaultIntensity;
            if (!Enum.IsDefined(loaded.Texture)) loaded.Texture = Waveform.PencilCont;
            return loaded;
        }
        catch (Exception)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // Not being able to save shouldn't interrupt drawing.
        }
    }
}
