# windows-slim-pen-haptics-unlocked

System-wide haptic feedback for the Surface Slim Pen 2 on Windows — in every app, not just the ones Microsoft blessed.

Microsoft restricted the haptics API to approved apps. This tool bypasses that by talking directly to the pen over HID and hooking into the Windows digitizer input stream globally. The result: you get the pen-on-paper click feel in Illustrator, Photoshop, Clip Studio, or anything else.

## How it works

- Reads the pen's HID waveform table at startup (feature report `0x42`)
- Registers for system-wide digitizer input via Raw Input (`RIDEV_INPUTSINK`)
- Sends haptic commands to the pen as `0x41` output reports
- While you drag, plays a continuous texture (ink, pencil, marker or brush) whose intensity follows stroke speed and pressure
- Runs as a system tray app, starts with Windows

Protocol reverse engineered from [zeroxoneafour/slimpend](https://github.com/zeroxoneafour/slimpend) (Linux).

## Requirements

- Surface Slim Pen 2 (VID `0x045E`, PID `0x0C0F`)
- Windows 10/11
- .NET 9 runtime (or use the self-contained release)

## Usage

Download `SlimPenHaptics.exe` from [Releases](../../releases). Run it. Green pen icon in the tray = pen found and haptics active.

Right-click the tray icon to adjust intensity (80% by default), pick the drag texture (or turn it off), turn on "Click on touch" for a click on every pen-down and pen-up (off by default), or enable run-at-startup.

## Build from source

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## License

MIT
