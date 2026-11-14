# windows-slim-pen-haptics-unlocked

**Surface Slim Pen 2 haptics only work in a handful of apps. This makes them work everywhere on Windows.**

The Slim Pen 2 has a haptic motor that makes drawing feel like pen on paper, but Windows only lets approved apps use it. This tray app talks to the pen directly, so you get that feel in Clip Studio, Krita, OneNote, your browser, the desktop, or anything else.

<p align="center">
  <img src="assets/screenshots/app-profile.png" width="760" alt="The Slim Pen Haptics window showing a profile for Microsoft OneNote: haptics on, Ink texture with a Feel it button, intensity at 60%, and click on touch enabled. The sidebar lists Default, Adobe Illustrator with haptics off, and Microsoft OneNote.">
</p>

## What you get

- **Drag texture:** a continuous vibration while the pen moves, stronger with stroke speed and pressure, quiet when the pen rests. Choose Ink, Pencil, Marker or Brush, and press **Feel it** to try one without drawing.
- **Per-app profiles:** give any app its own texture and intensity, or turn haptics off in apps that already have their own (so the two don't stack). The app switches automatically based on where you're drawing.
- **Click on touch (optional):** a click when the tip touches down and lifts. Off by default.
- **Intensity:** 0% to 100%, 80% by default. Set the Default profile to 0% if you only want haptics in the apps you list.
- **Automatic updates** when installed, and it remembers your settings.
- A settings window in the Windows 11 style, with light and dark mode.

<p align="center">
  <img src="assets/screenshots/default-profile.png" width="760" alt="The Default profile: Pencil texture, intensity at 80%, click on touch off. It applies to every app without its own profile.">
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

If it works (or doesn't) on your device, please [open an issue](../../issues) so this list can grow.

## How it works

- Finds the pen's haptic HID collection (VID `0x045E`, PID `0x0C0F`, digitizer page) and reads its waveform table from feature report `0x42`
- Registers for system-wide digitizer input via Raw Input (`RIDEV_INPUTSINK`) to track tip contact, position and pressure in every app
- Sends haptic commands to the pen as `0x41` output reports, from a background thread so Bluetooth writes never delay input
- On each pen-down, looks up the app under the pen and applies its profile
- Installer and updates are built with [Velopack](https://velopack.io), which updates from this repo's GitHub releases

Inspired by [zeroxoneafour/slimpend](https://github.com/zeroxoneafour/slimpend), which does the same on Linux.

## Build from source

```
dotnet build -c Release -r win-x64
```

To build the installers, portable zips and update files for both architectures (needs `dotnet tool install -g vpk` once):

```
.\scripts\release.ps1 -Version 2.0.0
```

Run `SlimPenHaptics.exe --buzz` to fire a single test click; exit code 0 means the pen accepted it. A small log of connection changes is kept at `%AppData%\SlimPenHaptics\log.txt`.

## License

MIT
