# Umbra

<p align="center">
  <img src="store-assets/icon-128.png" alt="Umbra icon" width="96" height="96">
</p>

<p align="center">
  <strong>A native focus space for Windows.</strong><br>
  Focus sessions, distraction blocking, ambient soundscapes and useful statistics, without an account or a cloud service.
</p>

<p align="center">
  <a href="https://github.com/zixload/umbra/releases/latest"><img src="https://img.shields.io/github/v/release/zixload/umbra?style=flat-square&label=release&color=0A84FF" alt="Latest Umbra release"></a>
  <a href="https://github.com/zixload/umbra/actions/workflows/ci.yml"><img src="https://github.com/zixload/umbra/actions/workflows/ci.yml/badge.svg" alt="Build status"></a>
  <img src="https://img.shields.io/badge/Windows_10_%7C_11-x64-30363D?style=flat-square&logo=windows11&logoColor=white" alt="Windows 10 and 11 x64">
  <img src="https://img.shields.io/badge/data-local_only-30363D?style=flat-square&logo=shield&logoColor=white" alt="Local-only data">
</p>

<p align="center">
  <a href="https://github.com/zixload/umbra/releases/latest"><strong>Download Umbra for Windows</strong></a>
  &middot;
  <a href="https://github.com/zixload/umbra/issues">Support</a>
  &middot;
  <a href="PRIVACY.md">Privacy</a>
</p>

<p align="center">
  <a href="docs/screenshots/statistics-dark-water.png"><img src="docs/screenshots/statistics-dark-water.png" alt="Umbra statistics in the dark theme with a custom background" width="100%"></a>
</p>

Umbra brings Pomodoro and free-form focus sessions, recurring schedules,
application and website blocklists, ambient sounds, Windows media information,
a detachable timer and meaningful statistics into one customizable desktop app.

## Contents

- [Install](#install)
- [Get started](#get-started)
- [Features](#features)
- [Gallery](#gallery)
- [Browser extension](#browser-extension)
- [Updates](#updates)
- [Privacy and local data](#privacy-and-local-data)
- [Troubleshooting](#troubleshooting)
- [Build from source](#build-from-source)

## Install

### Requirements

- 64-bit Windows 10 version 2004 or newer, or Windows 11
- No separate .NET installation; the installer is self-contained
- A Chromium browser for website blocking: Chrome, Vivaldi, Edge or Brave

### Windows installer

1. Open the **[latest GitHub release](https://github.com/zixload/umbra/releases/latest)**.
2. Under **Assets**, download `Umbra-Setup-<version>-x64.exe`.
3. Run the installer.
4. Launch Umbra from the Start menu.

The first auto-update-capable version is **1.0.3**. It must be installed once
using the steps above. Future releases can then be installed directly from
**Settings > Updates**.

> [!IMPORTANT]
> Umbra is not Authenticode-signed yet. Windows SmartScreen may display an
> **Unknown publisher** warning. Download installers only from this repository.
> Every release includes `SHA256SUMS.txt` for integrity verification.

### Verify the download

Run this command in PowerShell from the folder containing the installer:

```powershell
$version = "1.0.3"
(Get-FileHash ".\Umbra-Setup-$version-x64.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
```

The result must match the line for the installer in the release's
`SHA256SUMS.txt`. The in-app updater performs this verification automatically
before it runs a downloaded installer.

## Get started

1. Open **Blocklist** and add distracting applications or websites. Ready-made
   lists are available for social media, streaming, games, news, shopping and
   messaging.
2. Install **Umbra Blocker** from **Settings > Umbra browser extension** if you
   want website blocking.
3. Open **Focus**, choose Pomodoro, Free or Schedules, select a blocklist and
   start the session.
4. Enable **Hard mode** only when you are comfortable letting the session run
   until its configured end.
5. Optionally mix up to three ambient sounds or keep Spotify/Windows media in
   the top playback bar.
6. Pop out the focus timer when you want a compact, resizable, always-on-top
   window.
7. Review focus time, streaks, activity and listening history in **Statistics**.

## Features

| Area | What Umbra provides |
| --- | --- |
| Focus | Pomodoro cycles, free timers, recurring schedules and named tasks |
| Protection | Application blocking, website blocking, reusable profiles, an always-on permanent blocklist independent of any session, and optional Hard mode |
| Sounds | Twenty-two built-in ambient soundscapes with independent volume controls and three-sound mixing |
| Media | Spotify and other Windows media titles, artists and artwork in the focus experience |
| Floating timer | Draggable, resizable and always-on-top, with image or MP4 backgrounds and blur |
| Statistics | Daily/weekly/monthly totals, streaks, longest session, activity heatmap, blocked attempts and top-played tracks |
| Appearance | Light and dark themes, custom backgrounds, blur and three background layouts |
| Reminders | Manual or habit-based session suggestions without automatic session starts |
| Updates | Automatic release checks, verified downloads, silent installation and restart |
| Languages | French and English |

Umbra does not require an account, sync browsing history, upload focus data or
run an advertising/analytics backend.

## Gallery

### Focus statistics and appearance layouts

The custom background can cover the entire window, the main workspace only,
or the navigation pane only. Each layout works with the light and dark themes.

<p align="center">
  <a href="docs/screenshots/statistics-rosy-solid-sidebar.png"><img src="docs/screenshots/statistics-rosy-solid-sidebar.png" alt="Main background with a solid sidebar" width="32%"></a>
  <a href="docs/screenshots/statistics-sand-sidebar.png"><img src="docs/screenshots/statistics-sand-sidebar.png" alt="Background sidebar with a solid workspace" width="32%"></a>
</p>

### Focus session and floating timer

An active session mixing ambient sounds, and the detachable floating timer with
a custom video background.


<p align="center">
  <img src="docs/screenshots/floating-timer-demo.gif" alt="The floating focus timer with a video background, showing an active schedule" width="44%">
</p>

### Blocklists, sounds and settings

<p align="center">
  <a href="store-assets/screenshots/02-blocklists.png"><img src="store-assets/screenshots/02-blocklists.png" alt="Ready-made and custom Umbra blocklists" width="49%"></a>
  <a href="store-assets/screenshots/04-sounds.png"><img src="store-assets/screenshots/04-sounds.png" alt="Umbra ambient sound catalogue" width="49%"></a>
</p>

<p align="center">
  <a href="store-assets/screenshots/05-settings.png"><img src="store-assets/screenshots/05-settings.png" alt="Umbra appearance and focus settings" width="49%"></a>
  <a href="store-assets/screenshots/01-blocked-site.png"><img src="store-assets/screenshots/01-blocked-site.png" alt="A distracting website blocked by Umbra" width="49%"></a>
</p>

## Browser extension

Website blocking uses the Manifest V3 **Umbra Blocker** extension and a local
native-messaging bridge installed with the Windows app. The extension is not a
standalone product: Umbra must be installed and running on the same computer.
Its toolbar popup shows whether Umbra is connected, the current session's
name, phase and countdown, and includes a Stop button (respecting Hard mode)
so a running session can be checked or ended without switching to the app.

The bridge is registered automatically for:

- Google Chrome
- Vivaldi
- Microsoft Edge
- Brave

### Chrome Web Store

Open **Settings > Umbra browser extension** or visit the
[Umbra Blocker listing](https://chromewebstore.google.com/detail/kihnnccjkhgjagaoljepcpghmfdpicoc).
If the listing is still under review or unavailable in your region, use the
manual method below.

### Manual extension installation

1. Download `Umbra-Extension-<version>.zip` from the
   [latest release](https://github.com/zixload/umbra/releases/latest).
2. Extract it to a permanent folder. Do not delete that folder afterward.
3. Open your browser's extension page:
   - Chrome: `chrome://extensions`
   - Vivaldi: `vivaldi://extensions`
   - Edge: `edge://extensions`
   - Brave: `brave://extensions`
4. Enable **Developer mode**.
5. Choose **Load unpacked** and select the extracted extension folder.
6. Restart Umbra once so its local bridge registration is refreshed.

The extension compares navigated domains with the active local blocklist. When
a domain matches, it displays a local blocked page and sends only that matched
domain to Umbra's local process so the blocked-attempt counter can be updated.
It does not send browsing history to an Umbra server. See
[PRIVACY.md](PRIVACY.md).

## Updates

Umbra checks the latest stable GitHub Release shortly after startup. You can
also check manually from **Settings > Updates**.

When an update is accepted, Umbra:

1. downloads the exact `Umbra-Setup-<version>-x64.exe` release asset;
2. downloads `SHA256SUMS.txt` from the same release;
3. verifies the installer with SHA-256;
4. waits until no focus session or schedule is active;
5. asks the background watchdog to clean up and stop;
6. installs the update silently and relaunches Umbra.

An update never starts during an active focus session or schedule. If a release
does not contain a verifiable installer, Umbra opens the release page instead
of executing an unverified file.

## Privacy and local data

Settings, blocklists, schedules, focus history, blocked-attempt counters and
music statistics are stored locally in:

```text
%APPDATA%\UmbraNative\data
```

Uninstalling Umbra preserves this directory so an update or reinstall does not
erase statistics. To remove it manually, quit Umbra from its notification-area
icon first, then delete the directory.

Umbra reads Spotify and other playback information through the Windows Global
System Media Transport Controls interface. It does not request or store Spotify
credentials. Full details are in the [privacy policy](PRIVACY.md).

## Troubleshooting

### Windows shows "Unknown publisher"

The installer is not code-signed yet. Confirm that it came from
`github.com/zixload/umbra/releases`, verify its SHA-256 checksum, then use
**More info > Run anyway** only if the values match.

### A website is not blocked

- Confirm the domain appears under **Blocklist > Blocked items**.
- Confirm Umbra Blocker is enabled in the browser.
- Restart Umbra to refresh the native-messaging registration.
- Keep the manually extracted extension folder in its original location.
- Start a focus session or ensure the configured schedule is currently active.

### An application is not blocked

Use the executable name shown by Umbra's running-app picker, such as
`Discord.exe`, and verify that the intended blocklist is selected in Focus.
Hard mode may request Windows administrator approval for the watchdog.

### An update is postponed

Finish the active focus session or scheduled period, then select
**Settings > Updates > Check now** again.

### Settings or statistics should be reset

Use **Settings > Privacy > Clear history** for focus history. For a complete
local reset, quit Umbra and remove `%APPDATA%\UmbraNative\data`.

For unresolved problems, open a
[GitHub issue](https://github.com/zixload/umbra/issues) with the Umbra version,
Windows version, browser and reproduction steps. Do not include private
browsing or account data.

## Build from source

### Requirements

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) for installer generation
- Git

### Clone, build and test

```powershell
git clone https://github.com/zixload/umbra.git
cd umbra
dotnet restore Umbra.slnx
dotnet build Umbra.slnx -c Release
dotnet test Umbra.Tests\Umbra.Tests.csproj -c Release -p:Platform=AnyCPU
```

### Create release artifacts

```powershell
.\scripts\build-release.ps1 -Version 1.0.3
```

Outputs are written to `artifacts/`:

- the self-contained Windows installer;
- the manual and Chrome Web Store extension packages;
- `SHA256SUMS.txt`.

### Repository layout

| Path | Purpose |
| --- | --- |
| `Umbra.App/` | WPF desktop interface, tray icon and Windows integrations |
| `Umbra.Core/` | Sessions, schedules, blocking, statistics, settings and updater logic |
| `Umbra.BrowserHost/` | Native-messaging bridge used by the browser extension |
| `Umbra.Tests/` | Core regression tests |
| `installer/` | Inno Setup definition |
| `scripts/` | Reproducible release build |
| `store-assets/` | Chrome Web Store listing graphics and copy |
| `docs/screenshots/` | README screenshots |

## Security and third-party media

Report vulnerabilities privately according to [SECURITY.md](SECURITY.md), not
in a public issue. Ambient-sound, image and dependency attributions are listed
in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
