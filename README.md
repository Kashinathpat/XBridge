# XBridge

A Windows system-tray app (.NET 8) that turns up to **four** gamepads into virtual
**Xbox 360** controllers, and hides the physical pads from every other app via
**HidHide** so games see only the Xbox pad. Which controllers it bridges is set by
a VID/PID list in `config.json`. Enabled by default: the wired DragonRise pad (USB
VID 0079 / PID 0006, sold as Speedlink Strike) and a phone acting as a Bluetooth
gamepad (the BluetoothPad app, VID 038F / PID 1200).

It has no window — it lives in the notification area. The tray icon shows state:
**grey** = no controllers, **green** = N active, **red** = an error.

## What it does

- Reads each connected pad in the `config.json` `Devices` list straight from its
  own HID reports and exposes one virtual Xbox 360 controller for it (ViGEmBus).
  Each report is forwarded the moment it arrives — no polling, near-zero CPU.
- Each pad layout is mapped via its own `mapping-<vid>-<pid>.json`, filled in once
  with `--calibrate`.
- Rumble: the DragonRise pad gets it via a raw-HID output report. A pad with no
  output report (like the BluetoothPad) runs input-only.
- Hides each physical pad from other applications (HidHide), so a game binds to
  the Xbox pad only and never sees a double input. The hide is permanent (it stays
  when XBridge exits) because Windows decides at connect time whether RawInput
  apps such as browsers can see a device — a pad that was connected before it was
  hidden stays visible to them until it is reconnected once; XBridge tells you when
  that is the case. Use `--hidhide-cleanup` to un-hide everything.
- Handles hot-plug: connect / disconnect / replug / a different USB port / sleep
  and resume, without disturbing the pads that stay connected.
- Runs as a standard user — no elevation, no UAC prompt. Only the installer needs
  admin (to install the drivers and write to Program Files).

## Requirements

Windows 10 or 11, 64-bit.

## Install

Run `XBridge-Setup-x.x.x.exe`. It installs the app to `%ProgramFiles%\XBridge`,
installs the ViGEmBus and HidHide drivers and the .NET 8 Desktop Runtime if they
are missing, registers the app on HidHide's allow-list, and sets it to start at
sign-in. The drivers are bundled; the .NET runtime is downloaded, so the first
install needs internet only if .NET 8 is not already on the PC.

**Reboot when it asks** if it just installed a driver — HidHide only starts hiding
a pad after its device stack is rebuilt (a reboot, or unplug/replug of the pad).

`XBridge.exe` on its own does not install any of this: it needs .NET 8 and both
drivers already present.

## Use

Start it from the Start menu (or it starts itself at next sign-in). The tray menu
has: open the config / log folder, exit.

Command-line options (framework-dependent build — `XBridge.exe <flag>`):

| flag | purpose |
|---|---|
| `--calibrate [--index N \| --guid <g>]` | print raw button / axis / POV indices while you press controls, to fill in a pad's mapping file |
| `--hidhide-cleanup` | un-hide the pads, remove this exe from HidHide, disable the cloak, exit (quit the tray app first) |
| `--help` | usage |

## Configuration — `%LocalAppData%\XBridge\`

`config.json` is created with defaults on first run. Changes take effect after you
quit XBridge from the tray icon and start it again. The keys most worth knowing:

| key | default | meaning |
|---|---|---|
| `Devices` | DragonRise `0079/0006`, BluetoothPad `038F/1200` | which controllers to bridge, by USB VID/PID (4 hex digits). Add an entry per new pad. |
| `LeftStickDeadzone` / `RightStickDeadzone` | 0.12 | radial dead zone, fraction of full range |
| `TriggerDeadzone` | 0.06 | analog-trigger dead zone |
| `RequireCloak` | true | don't expose a virtual pad unless the physical one was hidden |
| `RumbleRepeat` | 3 | times the DragonRise rumble packet is re-sent per change (it needs a few to latch) |
| `RumbleWeak0x0AQuirk` | true | send `0x0B` when the weak value would be `0x0A` (DragonRise quirk) |
| `LogMaxBytes` / `LogMaxFiles` | 1 MiB / 5 | rolling-log cap |

Each pad layout gets its own `mapping-<vid>-<pid>.json`: run `XBridge.exe
--calibrate` (add `--index N` or `--guid <g>` to pick which attached pad), press
each control, and put the printed indices into that file (axis convention `0=X
1=Y 2=Z 3=RotationX 4=RotationY 5=RotationZ`; `TriggerMode` is `"Buttons"` or
`"ZAxisSplit"`). Restart to apply.

`logs\app.log` is a rolling, size-capped log — session lifecycle, driver
preflight, HidHide calls, warnings and errors.

`hidden-devices.txt` lists every pad XBridge has hidden, so `--hidhide-cleanup` (and
the uninstaller) can un-hide them even after a pad is removed from `Devices`.

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
compiles `installer\Output\XBridge-Setup-x.x.x.exe`.
