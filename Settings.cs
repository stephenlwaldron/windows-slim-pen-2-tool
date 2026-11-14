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
    public byte     ClickIntensity { get; set; } = Settings.DefaultIntensity;

    // How the texture responds to the stroke, each 0-100. The defaults are the original feel.
    public byte Sensitivity     { get; set; } = DefaultResponse;   // higher = full strength at slower strokes
    public byte MinimumStrength { get; set; } = DefaultResponse;   // share of intensity at the slowest drag
    public byte PressureEffect  { get; set; } = DefaultResponse;   // how much pressing harder adds

    public const byte DefaultResponse = 50;

    private const double BaseFullSpeed = 800;   // px/s that reaches full intensity at default sensitivity

    // Stroke speed (px/s) at which the texture reaches full strength: 3200 at 0, 800 at 50, 200 at 100.
    [JsonIgnore] public double FullSpeed => BaseFullSpeed * Math.Pow(2, (DefaultResponse - Sensitivity) / 25.0);

    // Share of the texture intensity (0..1) for a stroke at this speed and pressure (0..1).
    // The settings window draws its graph from this same function.
    public double Response(double speed, double pressure)
    {
        double floor        = MinimumStrength / 100.0;
        double effect       = PressureEffect / 100.0;
        double speedPart    = Math.Sqrt(Math.Clamp(speed / FullSpeed, 0.0, 1.0));
        double pressurePart = (1.0 - effect) + effect * Math.Clamp(pressure, 0.0, 1.0);
        return floor + (1.0 - floor) * speedPart * pressurePart;
    }

    // What the profile actually produces. 0% on a slider switches that part off.
    [JsonIgnore] public bool HasTexture => Texture != Waveform.None && Intensity > 0;
    [JsonIgnore] public bool HasClick   => TouchClick && ClickIntensity > 0;
    [JsonIgnore] public bool IsSilent   => !HasTexture && !HasClick;

    public Profile Clone() => (Profile)MemberwiseClone();
}

// An app the user has given its own behaviour. Matched by exe file name, so it survives app updates.
public class AppEntry
{
    public string  Exe     { get; set; } = "";      // e.g. "Illustrator.exe"
    public string  Path    { get; set; } = "";      // where it was when added; used for its icon
    public string  Name    { get; set; } = "";
    public bool    Enabled { get; set; } = true;    // false = no haptics from this app in it

    // Keyboard shortcuts pressed for the user when the pen is flipped over this app, in
    // KeySender's stored form ("Shift+E"). Empty = do nothing.
    public bool   EraserEnabled  { get; set; }          // off until the user opts in
    public string EraserShortcut { get; set; } = "";    // on flipping to the eraser end
    public string DrawShortcut   { get; set; } = "";    // on flipping back to the tip
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
        if (app is { Enabled: false }) return null;
        return app?.Profile ?? Default;
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
        // 0 is valid on both: it means off
        if (profile.Intensity > 100)      profile.Intensity      = DefaultIntensity;
        if (profile.ClickIntensity > 100) profile.ClickIntensity = DefaultIntensity;
        if (profile.Sensitivity > 100)     profile.Sensitivity     = Profile.DefaultResponse;
        if (profile.MinimumStrength > 100) profile.MinimumStrength = Profile.DefaultResponse;
        if (profile.PressureEffect > 100)  profile.PressureEffect  = Profile.DefaultResponse;
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
