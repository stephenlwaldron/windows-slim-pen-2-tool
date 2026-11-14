# windows-slim-pen-haptics-unlocked

**Surface Slim Pen 2 haptics only work in a handful of apps. This makes them work everywhere on Windows.**

The Slim Pen 2 has a haptic motor that makes drawing feel like pen on paper, but Windows only lets approved apps use it. This tray app talks to the pen directly, so you get that feel in Clip Studio, Krita, OneNote, your browser, the desktop, or anything else.

## What you get

- **Drag texture:** a continuous vibration while the pen moves, stronger with stroke speed and pressure, quiet when the pen rests. Choose Ink, Pencil, Marker or Brush.
- **Click on touch (optional):** a click when the tip touches down and lifts. Off by default.
- **Intensity:** five steps from 20% to 100%, 80% by default.
- **Remembers your settings** between restarts.
- **One file, no installer.** Runs in the system tray and can start with Windows.

## Download and run

1. Download `SlimPenHaptics.exe` from [Releases](../../releases/latest).
2. Run it. A green pen icon in the tray means the pen was found; grey means it wasn't.
3. Right-click the tray icon to change intensity, texture, click on touch, or run-at-startup.

**Windows will warn you the first time.** The exe isn't code-signed, so SmartScreen shows "Windows protected your PC". Click **More info**, then **Run anyway**. The full source is in this repo if you'd rather build it yourself.

The download is about 113 MB because it bundles the .NET runtime, so there is nothing else to install.

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

Inspired by [zeroxoneafour/slimpend](https://github.com/zeroxoneafour/slimpend), which does the same on Linux.

## Build from source

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Run `SlimPenHaptics.exe --buzz` to fire a single test click; exit code 0 means the pen accepted it.

## License

MIT
