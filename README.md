# XBridge

A Windows system-tray app (.NET 8) that turns up to **four** wired DragonRise
gamepads (**USB VID 0079 / PID 0006**, sold as Speedlink Strike and similar) into
virtual **Xbox 360** controllers with working **rumble**, and hides the physical
pads from every other app via **HidHide** so games see only the Xbox pad.

It has no window — it lives in the notification area. The tray icon shows state:
**grey** = no controllers, **green** = N active, **red** = an error.

## What it does

- Detects each connected VID 0079 / PID 0006 pad and exposes one virtual Xbox 360
  controller for it (ViGEmBus).
- Routes rumble back to the correct physical pad, as raw HID — the gamepad itself
  needs no driver.
- Hides each physical pad from other applications (HidHide), so a game binds to
  the Xbox pad only and never sees a double input.
- Handles hot-plug: connect / disconnect / replug / a different USB port / sleep
  and resume, without disturbing the pads that stay connected.
- Runs as a standard user — no elevation, no UAC prompt. Only the installer needs
  admin (to install the drivers and write to Program Files).

## Requirements

- Windows 10 or 11, 64-bit.
- **ViGEmBus** and **HidHide** drivers — bundled in the installer, installed only
  if missing.
- **.NET 8 Desktop Runtime (x64)** — not bundled; the installer downloads and
  installs it if missing.

## Install

Run `XBridge-Setup-1.0.0.exe`. It installs the app to `%ProgramFiles%\XBridge`,
installs any missing driver / runtime, registers the app on HidHide's allow-list,
and sets it to start at sign-in. **Reboot when it asks** if it just installed a
driver — HidHide only starts hiding a pad after its device stack is rebuilt (a
reboot, or unplug/replug of the pad).

## Use

Start it from the Start menu (or it starts itself at next sign-in). The tray menu
has: re-scan controllers, open the log / config folder, exit.

Command-line options (framework-dependent build — `XBridge.exe <flag>`):

| flag | purpose |
|---|---|
| `--calibrate [--index N \| --guid <g>]` | print raw button / axis / POV indices while you press controls; fills `mapping.json` |
| `--hidhide-cleanup` | un-hide the pads, remove this exe from HidHide, disable the cloak, exit |
| `--help` | usage |

## Configuration — `%LocalAppData%\XBridge\`

`config.json` is created with defaults on first run. The keys most worth knowing:

| key | default | meaning |
|---|---|---|
| `PollIntervalMs` | 5 | per-pad poll target |
| `LeftStickDeadzone` / `RightStickDeadzone` | 0.12 | radial dead zone, fraction of full range |
| `TriggerDeadzone` | 0.06 | for `ZAxisSplit` trigger mode |
| `RequireCloak` | true | don't expose a virtual pad unless the physical one was hidden |
| `RequireHidPairing` | true | don't start a pad unless its rumble HID device was found |
| `DirectInputExclusive` | false | DirectInput cooperative level |
| `RumbleRepeat` | 3 | times the rumble packet is re-sent per change (this pad needs a few to latch) |
| `RumbleWeak0x0AQuirk` | true | send `0x0B` when the weak value would be `0x0A` (DragonRise quirk) |
| `LogMaxBytes` / `LogMaxFiles` | 1 MiB / 5 | rolling-log cap |

`mapping.json` is one shared button/axis profile (all pads are identical). Run
`XBridge.exe --calibrate`, press each control, and put the printed indices into
`mapping.json` (axis convention `0=X 1=Y 2=Z 3=RotationX 4=RotationY 5=RotationZ`;
`TriggerMode` is `"Buttons"` or `"ZAxisSplit"`). Restart the app to apply.

`logs\app.log` is a rolling, size-capped log — session lifecycle, driver
preflight, HidHide calls, poll-timing, warnings and errors.

## Build

Requires the .NET 8 SDK.

```
dotnet build src -c Release
dotnet run --project src            # dev run
```

Single-file exe (framework-dependent, win-x64):

```
installer\build.cmd
```

Output: `publish\XBridge.exe`. To build the installer, also put
`ViGEmBusSetup_x64.exe` and `HidHideSetup_x64.exe` in `installer\redist\` (see
that folder's `README.txt`) and have Inno Setup 6 on `PATH`; `build.cmd` then
compiles `installer\Output\XBridge-Setup-1.0.0.exe`.
