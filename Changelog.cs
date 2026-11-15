using System;
using System.Collections.Generic;
using System.Linq;

namespace SlimPenHaptics;

// What changed in each version, newest first. The "What's new" window shows this after an
// update. Add an entry at the top with each release that has something worth telling users.
public static class Changelog
{
    public sealed record Item(string Title, string Detail);
    public sealed record Release(string Version, Item[] Items);

    public static readonly Release[] Releases =
    {
        new("2.3.0", new Item[]
        {
            new("Side button tools",
                "Click the pen's side button over an app to switch to another tool by its shortcut. " +
                "Add up to 10 tools with +; each click moves to the next, and after the last one it loops back to your pen tip tool. " +
                "Works while the pen hovers or draws."),
            new("Top button shortcuts",
                "Give the top button's click, double-click and hold each their own shortcut per app. " +
                "It works over Bluetooth, so the pen can be anywhere. While that app is in front, your shortcut replaces what Windows normally does."),
            new("Pen tip tool",
                "Tell each app the shortcut of the tool you normally draw with. The eraser and the side button both come back to it. " +
                "Your old \"Back to drawing\" shortcut has been moved here for you."),
            new("Tidier eraser shortcut",
                "The eraser card is now one box. Flipping back returns to the tool you were on, including one the side button chose."),
            new("What's new",
                "This window. It appears once after an update, and you can open it again from General settings."),
        }),
    };

    // The releases to show someone who last ran `seen` and now runs `current`: everything in
    // between, or just the current one when the last version is unknown.
    public static List<Release> Since(string? seen, string current)
    {
        if (!Version.TryParse(current, out var now)) return new();
        Version.TryParse(seen, out var before);

        var newer = Releases
            .Where(r => Version.TryParse(r.Version, out var v) && v <= now && (before == null || v > before))
            .ToList();
        return before == null ? newer.Take(1).ToList() : newer;
    }
}
