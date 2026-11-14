# windows-slim-pen-haptics-unlocked

**Surface Slim Pen 2 haptics only work in a handful of apps. This makes them work everywhere on Windows.**

The Slim Pen 2 has a haptic motor that makes drawing feel like pen on paper, but Windows only lets approved apps use it. This tray app talks to the pen directly, so you get that feel in Clip Studio, Krita, OneNote, your browser, the desktop, or anything else.

<p align="center">
  <img src="assets/screenshots/app-profile.png" width="760" alt="The Slim Pen Haptics window showing a profile for Microsoft OneNote: haptics on, Ink texture with a Confirm button, texture intensity at 60%, an Advanced button, and click on touch enabled with its own intensity slider. The sidebar lists Default, Adobe Illustrator with haptics off, and Microsoft OneNote.">
</p>

## What you get

- **Drag texture:** a continuous vibration while the pen moves, stronger with stroke speed and pressure, quiet when the pen rests. Choose Ink, Pencil, Marker or Brush, and press **Confirm** to feel it on the pen without drawing.
- **Per-app profiles:** give any app its own texture and intensity, or turn haptics off in apps that already have their own (so the two don't stack). The app switches automatically based on where you're drawing.
- **Response tuning:** under Advanced, set how sensitive the texture is to stroke speed, how strong the slowest stroke feels, and how much pressure adds, with a live graph of the result. Each profile has its own.
- **Eraser shortcut:** for apps that ignore the pen's eraser end, flipping the pen presses that app's eraser shortcut for you, and flipping back presses the one for your drawing tool. Optional, set per app, and switched on with a checkbox.
- **Click on touch (optional):** a click when the tip touches down and lifts, with its own intensity slider. Off by default.
- **Intensity:** 0% to 100%, 80% by default, set separately for the texture and the click. Turn the Default profile down to 0% if you only want haptics in the apps you list.
- **Automatic updates** when installed, and it remembers your settings.
- A settings window in the Windows 11 style, with light and dark mode.

<p align="center">
  <img src="assets/screenshots/default-profile.png" width="760" alt="The Default profile: Pencil texture, intensity at 80%, click on touch off. It applies to every app without its own profile.">
</p>

<p align="center">
  <img src="assets/screenshots/eraser-shortcut.png" width="760" alt="The Adobe Illustrator profile with haptics off and the Eraser shortcut switched on: Eraser tool set to Shift+E and Back to drawing set to B.">
</p>

<p align="center">
  <img src="assets/screenshots/response-tuning.png" width="760" alt="The Advanced section open on the Default profile: sliders for Sensitivity, Minimum strength and Pressure effect, each at 50%, beside a graph of strength against stroke speed with one line for a firm press and one for a light press.">
</p>

## Download and run

Get the latest files from [Releases](../../releases/latest).

| File | For |
| --- | --- |
| `SlimPenHaptics-win-x64-Setup.exe` | **Recommended.** Installer for Intel and AMD Surfaces. Adds a Start menu entry and updates itself. |
| `SlimPenHaptics-win-arm64-Setup.exe` | Installer for Snapdragon (ARM) Surfaces. **Untested**: please [report](../../issues) how it goes. The x64 installer also runs on ARM through Windows' emulation. |
| `SlimPenHaptics-win-x64-Portable.zip`, `SlimPenHaptics-win-arm64-Portable.zip` | No install: unzip and run. Doesn't update itself. |

1. Run the installer. The settings window opens to confirm it's running and show whether the pen was found.
2. After that it lives in the system tray: a green pen icon means haptics are live, grey means the pen is asleep, docked or out of range. Click the icon, or open **Slim Pen Haptics** from the Start menu, to change settings.

**Windows will warn you the first time.** The files aren't code-signed, so SmartScreen shows "Windows protected your PC". Click **More info**, then **Run anyway**. The full source is in this repo if you'd rather build it yourself.

**Coming from v1.x?** Install v2, then delete the old `SlimPenHaptics.exe`. Your settings carry over, and "Start with Windows" is repointed to the installed copy automatically.

## Requirements

- Surface Slim Pen 2, paired over Bluetooth
- Windows 10 or 11, 64-bit

## Tested on

| Device | Windows | Works |
| --- | --- | --- |
| Surface Pro 9 | Windows 11 | Yes |
| Surface Laptop Studio (1st gen) | Windows 11 | Yes, [reported by a user](../../issues/1) |

If it works (or doesn't) on your device, please [open an issue](../../issues) so this list can grow.

## How it works

- Finds the pen's haptic HID collection (VID `0x045E`, PID `0x0C0F`, digitizer page) and reads its waveform table from feature report `0x42`
- Registers for system-wide digitizer input via Raw Input (`RIDEV_INPUTSINK`) to track tip contact, position and pressure in every app
- Sends haptic commands to the pen as `0x41` output reports, from a background thread so Bluetooth writes never delay input
- On each pen-down, looks up the app under the pen and applies its profile
- Installer and updates are built with [Velopack](https://velopack.io), which updates from this repo's GitHub releases

The pen's haptic commands were first worked out by [zeroxoneafour/slimpend](https://github.com/zeroxoneafour/slimpend), a Linux project for the same pen, which was the starting point for the protocol here.

## Build from source

You need Windows and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

Build and run it:

```
git clone https://github.com/stephenlwaldron/windows-slim-pen-haptics-unlocked
cd windows-slim-pen-haptics-unlocked
dotnet run -c Release -r win-x64
```

Use `-r win-arm64` on a Snapdragon Surface. The built program lands in `bin\Release\net9.0-windows\win-x64\SlimPenHaptics.exe`. Run this way it behaves like the portable version: everything works, but it doesn't update itself.

To build the installers, portable zips and update files for both architectures, as published on the Releases page:

```
dotnet tool install -g vpk
.\scripts\release.ps1 -Version 2.2.0
```

They are written to `artifacts\releases`. Adding `-Publish` also creates the GitHub release, which needs the `gh` command-line tool.

Two things that help when something isn't working:

- `SlimPenHaptics.exe --buzz` fires a single test click and exits. Exit code 0 means the pen accepted it, 1 means the pen wasn't found, 2 means the write failed.
- A small log of connection changes and eraser shortcuts sent is kept at `%AppData%\SlimPenHaptics\log.txt`.

## License

MIT
