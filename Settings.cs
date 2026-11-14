using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimPenHaptics;

// How the pen should feel: used for the default profile and for each app with its own.
public class Profile
{
    public byte     Intensity  { get; set; } = Settings.DefaultIntensity;
    public Waveform Texture    { get; set; } = Waveform.PencilCont;    // Waveform.None = off
    public bool     TouchClick { get; set; }

    public Profile Clone() => (Profile)MemberwiseClone();
}

// An app the user has given its own behaviour. Matched by exe file name, so it survives app updates.
public class AppEntry
{
    public string  Exe     { get; set; } = "";      // e.g. "Illustrator.exe"
    public string  Path    { get; set; } = "";      // where it was when added; used for its icon
    public string  Name    { get; set; } = "";
    public bool    Enabled { get; set; } = true;    // false = no haptics from this app in it
    public Profile Profile { get; set; } = new();
}

// User choices, kept in %AppData%\SlimPenHaptics\settings.json.
public class Settings
{
    public const byte DefaultIntensity = 80;

    public bool           Enabled { get; set; } = true;
    public Profile        Default { get; set; } = new();
    public List<AppEntry> Apps    { get; set; } = new();

    public bool StartupNotification { get; set; } = true;
    public bool WelcomeShown        { get; set; }   // the first-launch banner has been seen

    // v1.x kept one flat profile; read it once so nobody loses their setup.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public byte?     Intensity  { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Waveform? Texture    { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool?     TouchClick { get; set; }

    public static readonly (string Label, Waveform Waveform)[] TextureChoices =
    {
        ("Off", Waveform.None), ("Ink", Waveform.InkCont), ("Pencil", Waveform.PencilCont),
        ("Marker", Waveform.MarkerCont), ("Brush", Waveform.BrushCont),
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlimPenHaptics", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    public AppEntry? FindApp(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        return Apps.Find(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase));
    }

    // The profile to use for a given exe, or null when haptics are off there.
    public Profile? ProfileFor(string? exe)
    {
        var app = FindApp(exe);
        if (app == null) return Default;
        return app.Enabled ? app.Profile : null;
    }

    // A missing or unreadable file just means defaults.
    public static Settings Load()
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
            if (loaded == null) return new Settings();

            loaded.Default ??= new Profile();
            loaded.Apps    ??= new List<AppEntry>();

            if (loaded.Intensity != null || loaded.Texture != null || loaded.TouchClick != null)
            {
                loaded.Default.Intensity  = loaded.Intensity  ?? loaded.Default.Intensity;
                loaded.Default.Texture    = loaded.Texture    ?? loaded.Default.Texture;
                loaded.Default.TouchClick = loaded.TouchClick ?? loaded.Default.TouchClick;
                loaded.Intensity = null; loaded.Texture = null; loaded.TouchClick = null;
            }

            Validate(loaded.Default);
            loaded.Apps.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Exe));
            foreach (var app in loaded.Apps)
            {
                app.Profile ??= new Profile();
                Validate(app.Profile);
            }
            return loaded;
        }
        catch (Exception)
        {
            return new Settings();
        }
    }

    private static void Validate(Profile profile)
    {
        if (profile.Intensity < 1 || profile.Intensity > 100) profile.Intensity = DefaultIntensity;
        if (!Enum.IsDefined(profile.Texture)) profile.Texture = Waveform.PencilCont;
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
