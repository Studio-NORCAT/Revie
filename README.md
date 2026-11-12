# Revie

[![License: CC BY 4.0](https://img.shields.io/badge/License-CC%20BY%204.0-lightgrey.svg)](https://creativecommons.org/licenses/by/4.0/)

Revie is a local review tool for teams working with visual content. It lets users draw on top of their screen, capture screenshots or video recordings with notes, and organize everything into review sessions so feedback, decisions, and collaboration work stay easy to track without relying on cloud storage.

[Development patch notes for November 12, 2026](PATCH_NOTES_2026-11-12.md)

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

Revie is a Windows 10/11 tray-first desktop app built with **C# / .NET 8 LTS / WPF (MVVM)**. Launching opens the main window and adds the tray icon; closing the window leaves Revie running in the tray. Opening Revie again brings the existing window forward.

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
  - `Views/SessionBrowserView.xaml(.cs)`: review sessions inside the main window
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

Revie runs one instance at a time. Exit the running app from the tray and respond to any session save prompt before launching a newly built copy. For a development build, share the entire output folder so its DLLs and bundled FFmpeg files stay beside `Revie.exe`.

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
- **Sessions** opens inside the main Revie window. The back button returns from an image to the session list, then to the dashboard. Switching views preserves the current window size and maximized state.
- Closing the main window keeps Revie in the tray. Choosing **Exit** from the tray first finalizes any active video recording and adds it to the session, then prompts to save the review session. Canceling or a failed save keeps Revie open.
- Save a session with a chosen folder name under `<StorageRoot>\Sessions\<chosen name>\`. If that folder exists, Revie adds `_01`, `_02`, and so on.
- New session packages place every screenshot directly in `screenshots\`, so they can be browsed together in Explorer. Videos are in `videos\`; small previews are in `thumbnails\`; capture metadata is in `captures\`.
- Right-click a session in the session browser to open its screenshots folder or the whole session folder. For older sessions, Open Screenshots Folder opens `captures\` instead.
- For image captures, use **Revised Upload** to add a screenshot of a fix. When there is no upload, that tab says **NO REVISED IMAGE YET** instead of showing the original again. Once images are uploaded, the tab shows their count and the capture thumbnail gets a revised-image marker. The original and uploaded images remain available for comparison. Choose a reviewer and request review; that user's Active User setting shows an orange pending icon on the session and capture until they choose **Mark Reviewed**.
- Older sessions with media directly in `captures\` or in one subfolder per capture remain readable.
- Session captures are not duplicated into the normal project capture bucket.
- Session browser supports:
  - list/open/delete sessions
  - filter by project and user
  - open captures, edit notes/tags
- linked note/user updates back to source capture metadata
- right-click state tagging per capture (`In Review` / `Completed`) with status dots
- malformed `session.json`/capture metadata is skipped or downgraded to safe fallback summaries so one bad session does not block opening the browser
- new-session indicators:
  - main toolbar `Sessions` button shows unread session count badge
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

- The main capture browser loads a local index manifest from `%LocalAppData%\Revie\indexes\` on launch, then checks storage in the background for added, changed, or deleted capture metadata. A missing or damaged manifest is rebuilt from the capture files.
- Capture refresh skips archived session and delete-queue folders during traversal and limits queued scan results, reducing storage work and memory spikes on large libraries.
- The session browser also shows cached session cards while it checks the archive in the background. Full resolution screenshot previews decode off the UI thread, and old previews are released when selection changes.
- Large session lists are applied to the UI in one collection update, avoiding repeated filter rebuilds for every capture.
- Pending main-window notes are saved when switching captures, hiding the window, or exiting from the tray. Metadata saves are serialized so quick edits cannot finish out of order or recreate a deleted capture.
- Session notes save shortly after editing and are flushed before navigating back or exiting Revie.
- Moving a capture between projects runs storage operations off the UI thread and coordinates with pending metadata saves.
- Sidebar previews are stored as small JPEGs beside captures (and in new session packages). Full resolution screenshots remain available in the main preview. Older captures get sidebar previews when they are first shown. Session cards prefer existing legacy `sidebar-preview.jpg` files and fall back to the screenshot if a thumbnail cannot be decoded.
- The WPF image loader reads previews from streams without URI cache options, so valid older JPEG thumbnails render correctly.
- Metadata writes now use atomic replace with retry logic to reduce partial writes and file-lock issues.
- Unknown fields in `capture.json` are preserved through updates via extension-data roundtripping.
- Destructive actions (capture/session delete flows) support backup snapshots under:
  - `%AppData%\Revie\backups\`
- Runtime logs are written to:
  - `%AppData%\Revie\logs\revie-YYYYMMDD.log`

## Team path notes (2026-11-12)

| What | Path | Notes |
| --- | --- | --- |
| Source checkout | Repository root (`Revie.sln`) | Build from here with `dotnet build .\Revie.sln -c Release`. |
| Current local build on this workstation | `Revie.App\bin\Release\net8.0-windows\` | Contains today's revised-upload UI changes, `Revie.exe`, and its adjacent dependencies. |
| Shared storage root configured on this workstation | `\\nt-pr-fs01\StudioReview\Revie\` | Each teammate can confirm their configured root in Settings. |
| Review session | `<StorageRoot>\Sessions\<session name>\` | `session.json` is the session manifest. |
| Screenshots to browse together | `<session>\screenshots\` | New session layout; one PNG per capture, with no individual screenshot subfolder. |
| Other session files | `<session>\videos\`, `<session>\thumbnails\`, `<session>\captures\` | Videos, compact previews, and capture JSON metadata respectively. |
| Uploaded revised images | `<session>\captures\attachments\<capture id>\user-uploads\` | A revised image stays associated with its original capture. |
| Older session screenshots | `<session>\captures\...\capture.png` | Legacy folder-per-capture and flat capture layouts remain readable. |
| Local indexes and staged sessions | `%LocalAppData%\Revie\indexes\`, `%LocalAppData%\Revie\session-staging\` | Indexes speed startup; active session media is staged before save. |
| Settings, logs, and backups | `%AppData%\Revie\settings.json`, `%AppData%\Revie\logs\`, `%AppData%\Revie\backups\` | Local to each Windows user. |

## License

This repository is licensed under the **Creative Commons Attribution 4.0 International (CC BY 4.0)** license.

- You may share and adapt the material for any purpose, even commercially.
- You must give appropriate credit, provide a link to the license, and indicate if changes were made.
- See the [LICENSE](LICENSE) file for the full license text.
