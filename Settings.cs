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
