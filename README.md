<p align="center">
  <img src="assets/juicebar-256.png" width="96" alt="Juice Bar icon">
</p>

<h1 align="center">Juice Bar</h1>

<p align="center">
  Battery levels of your wireless headset, keyboard, mouse, controller and earbuds, right on the Windows 11 taskbar.
</p>

<p align="center">
  <a href="https://github.com/AhmedAshrafM/JuiceBar/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/AhmedAshrafM/JuiceBar"></a>
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/github/license/AhmedAshrafM/JuiceBar"></a>
</p>

<p align="center">
  <img src="docs/images/bar.png" alt="Juice Bar on the taskbar: keyboard 55%, headset 45%, keyboard 50%, mouse 100%, controller 25%, audio switch">
</p>

Juice Bar is a small strip that sits on the empty left side of the Windows 11 taskbar. Each wireless device
gets a chip with its battery level. It works with Bluetooth devices and with many 2.4 GHz USB-dongle devices
that Windows itself can't show a battery for (Logitech, HyperX, Redragon/AULA, DualSense over USB). Click a chip
for controls, and let it remind you to charge things before they die.

It is a single exe, installs per user without admin rights, and idles at about 15 MB of private memory.

## Features

- **Battery at a glance.** One chip per device with a colour level bar. Hover for details; a bolt shows
  while charging. Devices reachable over two links (Bluetooth and a 2.4 GHz receiver) show as one chip.
- **Device popup.** Click a chip for:
  - a 7-day battery graph, time left (or time to full) estimated from your own usage, and when it was last seen;
  - battery health: how fast it usually drains, and a warning when this charge drains notably faster;
  - set as default audio output, volume and mic mute for headsets;
  - connect or disconnect without unpairing;
  - DualSense lightbar colour and *Turn off controller*.
- **Alerts.**
  - Low battery (per-device level) and *fully charged* notifications.
  - A daily **"Charge tonight"** reminder (default 21:00) listing every device under a level you choose (default 30%).
  - **Battery health** alert when a device drains 1.5× faster than its earlier charges (at most once per charge).
  - **Quiet time**: notifications are held while a fullscreen app or game is in front, and optionally during
    set hours, then shown together afterwards.
- **Audio switch.** One click flips the default output between your headset and speakers; right-click to pick any output.
- **Fits in.** Dark, compact, follows an auto-hiding taskbar, hides over fullscreen apps. Sits in free
  taskbar space, clear of the weather widget, Start, app icons and the tray, and moves aside as icons come
  and go; drag it along the taskbar or set *Move right by* in Settings. Compact mode shows only icon and
  number and can hide devices above a level.
- **Per-device settings.** Rename, reorder, hide, alert levels, lightbar colour.
- **Self-installing and self-updating.** Checks GitHub once a day, verifies the SHA-256 and swaps itself in place.

<p align="center">
  <img src="docs/images/settings-general.png" width="380" alt="Settings, General tab">
  <img src="docs/images/settings-alerts.png" width="380" alt="Settings, Alerts tab">
</p>

## Supported devices

| Device | Connection | Where the battery level comes from |
|---|---|---|
| Any Bluetooth device that reports battery to Windows (most headsets, earbuds, keyboards, mice) | Bluetooth / Bluetooth LE | The level Windows stores on the device (`DEVPKEY_Bluetooth_Battery`), filled from the BLE Battery Service (0x180F) or hands-free (HFP) reports. Read every 60 s and on device changes. |
| Sony DualSense / DualSense Edge | Bluetooth or USB cable | Status byte in the controller's input report. Lightbar and power-off through its output report. |
| Apple AirPods / Beats | Bluetooth | Windows doesn't show their battery, so Juice Bar listens briefly (at most 6 s every 2 min, only while they are connected) for the proximity broadcast they send. Shows left, right and case. Experimental. |
| HyperX Cloud III Wireless | 2.4 GHz dongle (`03F0:05B7`, `03F0:0C9D`) | Vendor HID reports (battery `66 89`, charging `66 8A`) plus the events the headset pushes. |
| Logitech devices on Unifying, Bolt or Lightspeed receivers | 2.4 GHz dongle | HID++ 2.0 battery features (`0x1004`, `0x1000`, `0x1001`) and the battery events devices push on their own. |
| Redragon, AULA, ATK, VXE and other Compx-based keyboards and mice | 2.4 GHz dongle (`3554:*`) | Vendor HID reports (keyboard `13 4A`, mouse `08 04`). Experimental. |
| UGREEN mice such as the M571P | 2.4 GHz dongle (`2B89:0043`) | The receiver has no battery channel. Pair the mouse over Bluetooth once; Juice Bar shows the Bluetooth reading and marks the mouse connected while you use it on the receiver. |

Your device isn't listed? Run the probe (see [Troubleshooting](#troubleshooting)) and
[open a device support request](https://github.com/AhmedAshrafM/JuiceBar/issues/new?template=device-support.yml),
or follow [docs/adding-a-device.md](docs/adding-a-device.md) to add it yourself (an AI coding agent can do most of it; see [AGENTS.md](AGENTS.md)).

## Install

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) if you don't have it.
   Windows also offers to download it the first time you start Juice Bar.
2. Download `JuiceBar.exe` from the [latest release](https://github.com/AhmedAshrafM/JuiceBar/releases/latest) and run it.
3. Choose **Yes** to install. It copies itself to `%LOCALAPPDATA%\Programs\JuiceBar`, adds a Start menu
   shortcut, starts with Windows and appears in *Settings > Apps > Installed apps*. No admin rights needed.
   Choose **No** to just run it once from where it is.

Windows SmartScreen may warn about an unrecognised app because the exe isn't code-signed yet; choose
*More info > Run anyway*. Each release lists the exe's SHA-256 next to it if you want to check the download.

Command line:

| Command | What it does |
|---|---|
| `JuiceBar.exe --install [--quiet]` | Install for the current user (quiet: no prompts). |
| `JuiceBar.exe --uninstall [--quiet]` | Remove the app, its startup entry and its shortcut (your settings stay). |
| `JuiceBar.exe --portable` | Run without offering to install. |
| `JuiceBar.exe --probe` | Print what every battery source sees and save it to `%TEMP%\juicebar-probe.txt`. |
| `JuiceBar.exe --listen VID SECONDS` | Print raw input reports from a vendor's HID collections (diagnostics; sends nothing). |

Starting `JuiceBar.exe` while it is already running opens its settings.

## Uninstall

*Settings > Apps > Installed apps > Juice Bar > Uninstall*, or run `JuiceBar.exe --uninstall`.
Your settings and history stay in `%APPDATA%\JuiceBar`; delete that folder to remove them too.

## Resource footprint

Juice Bar is designed to be left running all day:

- **Memory:** about 15 MB private memory at idle (working set 15 to 30 MB depending on what Windows has trimmed).
- **CPU:** one short poll a minute (Bluetooth), USB dongles every 5 minutes by default, and otherwise only
  reacting to device events. Measured idle: 0.05 to 0.25 s of CPU time per minute, well under 1% of one core.
- **No background services or extra processes.** The settings window and popups are created only while open.
- **Disk:** the exe is about 25 MB, almost all of it the Windows SDK projection used by the Bluetooth LE
  scanner, loaded only when needed. Data is a few small JSON files.

Data lives in `%APPDATA%\JuiceBar`: `settings.json` (settings, per-device preferences, last-known levels),
`history.json` (14 days of battery changes, only recorded when a level changes), `logitech-slots.json`
(Logitech receiver pairing cache) and `juicebar.log` (errors). Nothing is sent anywhere; the only network
request is the daily update check to GitHub, which you can turn off in Settings.

## Troubleshooting

**A device shows no battery or isn't listed.**
Run `JuiceBar.exe --probe` from a terminal (or from the install folder). It lists every device each battery
source sees, every vendor-specific HID collection, and which Bluetooth LE devices offer a battery service.
Bluetooth addresses and serials are shortened so you can share it; check the device names before posting.
Then [open a device support request](https://github.com/AhmedAshrafM/JuiceBar/issues/new?template=device-support.yml) with the output.

**A Bluetooth device shows a battery in Windows Settings but not in Juice Bar** (or the other way round).
Juice Bar reads the same value Windows does. Many devices only report it while connected and after a few
minutes; hover the chip to see when it last updated.

**The DualSense lightbar doesn't change.** Steam Input overrides the lightbar while Steam is running. Turn
off *PlayStation Configuration Support* for the controller in Steam, or set the colour in Steam instead.

**AirPods show "experimental" or jump around.** The reading comes from Apple's proximity broadcast, which
is rounded to 10% steps and isn't always sent, so it can lag behind what your iPhone shows.

**The bar is in the wrong place or behind widgets.** Drag it sideways, or use *Settings > General > Reset bar position*.

**No notifications appear.** Check *Settings > Alerts > Quiet time* (fullscreen apps hold notifications) and
Windows *Focus / Do not disturb*.

**SmartScreen blocks the download or the exe.** See [Install](#install); the exe isn't code-signed yet.

## Build from source

Requires Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/AhmedAshrafM/JuiceBar
cd JuiceBar
dotnet build src\BFM\BFM.csproj -c Release   # dev build in src\BFM\bin, runs without the install prompt
tools\publish.ps1                            # single-file dist\JuiceBar.exe + JuiceBar.exe.sha256
dist\JuiceBar.exe --install                  # install your build
```

The project folder is still called `src/BFM` (the app's original name). `tools\make-icon.ps1` regenerates
`src\BFM\JuiceBar.ico` from `assets\juicebar.svg` and `assets\juicebar-small.svg` (needs Microsoft Edge).

Releases: bump `<Version>` in `src\BFM\BFM.csproj`, commit, and push a tag `vX.Y.Z`. The
[release workflow](.github/workflows/release.yml) builds the exe and checksum and attaches them to a GitHub
release, which installed copies pick up within a day. `packaging/winget` has the winget manifest templates.

## Code signing policy

Release builds are signed through the SignPath Foundation once the project is approved; until then
releases are unsigned (see [Install](#install)).

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [the repository's maintainers](https://github.com/AhmedAshrafM/JuiceBar/graphs/contributors) with write access, currently [@AhmedAshrafM](https://github.com/AhmedAshrafM). Pull requests from anyone else are reviewed by a maintainer before merging.
- Approvers: [@AhmedAshrafM](https://github.com/AhmedAshrafM) (repository owner) approves every signing request.
- Only binaries built by this repository's [release workflow](.github/workflows/release.yml) on GitHub-hosted runners from tagged source are signed.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by
the user or the person installing or operating it, with one exception you can turn off: once a day the installed
app asks GitHub (`api.github.com`) whether a newer release exists, sending only its version in the request
header, and downloads the update from GitHub if there is one. Turn this off in *Settings > General > Install
updates automatically*. Battery readings, device names and history stay on your PC in `%APPDATA%\JuiceBar`.
Reading AirPods battery only listens to Bluetooth broadcasts nearby; it sends nothing.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Device support is the most useful contribution: the
[guide to adding a device](docs/adding-a-device.md) walks through diagnosing the protocol and writing a provider.

## License

[MIT](LICENSE) © Ahmed Ashraf. Juice Bar is not affiliated with Logitech, Sony, Apple, HP/HyperX, UGREEN or any other device maker.
Protocol notes are credited in the source of each provider.
