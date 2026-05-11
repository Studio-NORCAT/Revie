# Revie

[![License: CC BY 4.0](https://img.shields.io/badge/License-CC%20BY%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by/4.0/)

Revie is a local review tool for teams working with visual content. It lets users draw on top of their screen, capture screenshots or video recordings with notes, and organize everything into review sessions so feedback, decisions, and collaboration work stay easy to track without relying on cloud storage.

<p align="center">
  <a href="https://studio.norcat.org/github/revie_program.jpg" target="_blank" rel="noopener noreferrer">
    <img src="https://studio.norcat.org/github/revie_program.jpg" alt="GENESIS Engine Header" width="100%" />
  </a>
</p>

<p align="center">
  <a href="https://studio.norcat.org/github/revie_program_review.jpg" target="_blank" rel="noopener noreferrer">
    <img src="https://studio.norcat.org/github/revie_program_review.jpg" alt="GENESIS Engine Header" width="100%" />
  </a>
</p>

Revie is a Windows 10/11 tray-first desktop app built with **C# / .NET 8 LTS / WPF (MVVM)**.

It provides:
- Transparent always-on-top drawing overlay.
- Responsive drawing toolbar that clamps/wraps for different resolutions and multi-monitor desktops.
- Global hotkeys for draw toggle, clear, screenshot, open window, and start/stop video.
- Screenshot capture with doodles composited into PNG.
- Video segment capture to MP4 (with microphone audio when available).
- Dark-mode browser for captures, sessions, notes, project and user tagging/filtering.
- Session-aware storage routing: captures made during an active review session are staged under `%LocalAppData%\Revie\session-staging\...` and saved only into the final session package.

## Solution Layout

- `Revie.sln`
- `Revie.App/`
  - `App.xaml.cs`: tray lifecycle, hotkeys, capture/session orchestration
  - `Views/OverlayWindow.xaml`: drawing overlay window
  - `MainWindow.xaml` + `ViewModels/MainViewModel.cs`: main capture browser
  - `Views/SessionBrowserWindow.xaml(.cs)`: review session browser
  - `Views/SettingsWindow.xaml(.cs)`: storage, hotkeys, projects, users
  - `Services/CaptureService.cs`: screenshot + metadata save
  - `Services/VideoCaptureService.cs`: ffmpeg-based video recording
  - `Services/CaptureIndexService.cs`: capture scan/index
  - `Services/SessionArchiveService.cs`: session packaging
  - `Services/SessionBrowseService.cs`: session discovery/open
  - `Services/SettingsService.cs`: AppData settings + shared taxonomy sync

## Prerequisites

- Windows 10/11 (x64)
- .NET SDK 8.0.x
- Optional (installer builds only): WiX CLI (`wix`)

## Installation

### Option 1: Installer (recommended for end users)

1. Build the installer (see `Package Installer` below), which outputs:
   - `dist\Revie-Installer.exe`
2. Run `dist\Revie-Installer.exe`.

### Option 2: Run from published folder (portable/dev)

1. Publish:
   ```powershell
   dotnet publish .\Revie.App\Revie.App.csproj -c Release -r win-x64 --self-contained true -o .\dist\publish\installer-win-x64
   ```
2. Start the app:
   ```powershell
   .\dist\publish\installer-win-x64\Revie.exe
   ```

## Build and Run (From Source)

1. Open PowerShell at repo root.
2. Restore/build:
   ```powershell
   dotnet build .\Revie.sln -c Release
   ```
3. Run:
   ```powershell
   dotnet run --project .\Revie.App\Revie.App.csproj -c Release
   ```

## Test

```powershell
dotnet test .\Revie.sln -c Release --no-build
```

## Package Installer (WiX)

If the WiX bootstrapper extension is missing:

```powershell
wix extension add WixToolset.BootstrapperApplications.wixext
```

Build MSI + EXE bundle:

```powershell
$publishDir = (Resolve-Path .\dist\publish\installer-win-x64).Path
wix build .\packaging\RevieInstaller.wxs -d SourceDir="$publishDir" -o .\dist\Revie-Installer.msi

$msiPath = (Resolve-Path .\dist\Revie-Installer.msi).Path
wix build .\packaging\RevieBundle.wxs -d MsiPath="$msiPath" -ext WixToolset.BootstrapperApplications.wixext -o .\dist\Revie-Installer.exe
```

## First Run Setup

On first launch, Settings opens.

- Configure `Storage Root`:
  - local path (example: `C:\Captures\Revie`)
  - UNC path (example: `\\server\share\Revie`)
- Write access is validated before save.
- Projects/users are also synced to a shared file at:
  - `<StorageRoot>\Revie\taxonomy.json`
  - This lets other machines using the same storage root load the same project/user lists.
- Settings now shows the running app build version in the top-right.
  - Build revision is stamped automatically at compile time (`1.0.5+build.yyyyMMdd-HHmmss`).

## Hotkeys (Default)

- Toggle drawing mode: `Ctrl+Shift+D`
- Clear overlay: `Ctrl+Shift+C`
- Capture screenshot: `Ctrl+Shift+S`
- Open main UI: `Ctrl+Shift+O`
- Start video segment: `Ctrl+Shift+V`
- Stop video segment: `Ctrl+Shift+B`

Hotkeys are editable in Settings.

## Video Capture Notes

Revie uses `ffmpeg` for video capture.

- Revie now bundles `ffmpeg.exe` (and `ffprobe.exe`) next to `Revie.exe` in build/publish output.
- On a normal install/portable run, no manual FFmpeg path setup is required.
- `FFmpeg Path` and `Microphone Device` are in Settings -> General.
- Settings includes:
  - `Browse...` and `Auto Detect` for ffmpeg.exe
  - `Refresh Inputs` to detect Windows microphones (DirectShow + WinMM fallback)
- If you override `FFmpeg Path`, that explicit path is used.
- If the configured microphone device cannot be opened, Revie falls back to video-only capture and reports that in status.
- Enter `none` in Microphone Device to intentionally record without mic audio.
- Video encoding falls back from `libx264 (H.264)` to `mpeg4` for broader ffmpeg compatibility.

## Storage Format

Captures are saved as:

`<StorageRoot>\[Projects\<ProjectName>\]YYYY\MM\DD\HHmmss_fff\`

Files:
- `capture.png` + `capture.json` for image captures
- `capture.mp4` + `thumbnail.png` + `capture.json` for video captures

`capture.json` includes:
- `MetadataVersion` (schema version for forward-safe updates)
- Capture type (`image` or `video`)
- Review state (`ReviewState`: `InReview`, `Completed`, or empty)
- Timestamps (local + UTC)
- Project
- User tags (`AssignedUsers`)
- Screen/monitor metadata
- Active window/process (when available)
- Note text

## Sessions

- Start session in main UI or tray.
- Save session to package current captures under:
  - `<StorageRoot>\Sessions\<timestamp_sessionName>\`
- Session captures are not duplicated into the normal project capture bucket.
- Session browser supports:
  - list/open/delete sessions
  - filter by project and user
  - open captures, edit notes/tags
- linked note/user updates back to source capture metadata
- right-click state tagging per capture (`In Review` / `Completed`) with status dots
- malformed `session.json`/capture metadata is skipped or downgraded to safe fallback summaries so one bad session does not block opening the browser
- new-session indicators:
  - main toolbar `Open Sessions` button shows unread session count badge
  - each session card shows `N new` when it has captures not yet opened in the session browser

## Recent Capture Cleanup

- `Purge Recent` button removes all items from the main recent-captures bucket.
- This does not delete packaged session archives under `<StorageRoot>\Sessions\...`.

## Multi-User Tagging

- Settings -> Users: manage available users and active default user.
- Notes panel allows:
  - assign one user quickly from dropdown
  - edit multi-user tags with comma-separated input
- Filters support project + user + user text search.

## Reliability Notes

- Metadata writes now use atomic replace with retry logic to reduce partial writes and file-lock issues.
- Unknown fields in `capture.json` are preserved through updates via extension-data roundtripping.
- Destructive actions (capture/session delete flows) support backup snapshots under:
  - `%AppData%\Revie\backups\`
- Runtime logs are written to:
  - `%AppData%\Revie\logs\revie-YYYYMMDD.log`

## License

This repository is licensed under the **Creative Commons Attribution 4.0 International (CC BY 4.0)** license.

- You may share and adapt the material for any purpose, even commercially.
- You must give appropriate credit, provide a link to the license, and indicate if changes were made.
- See the [LICENSE](LICENSE) file for the full license text.
