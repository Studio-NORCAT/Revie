# Revie patch notes — November 12, 2026

Baseline: the **1.0.6** release published May 19, 2026. Everything below is in the current source tree; no new GitHub release has been cut for it yet.

## Summary

- Startup now shows the window and tray icon before slower storage work, and the capture and session browsers display cached data first.
- Session saving is safer: named folders, atomic writes, staging, and an Exit flow that will not discard a session or an active recording.
- Revised images can be uploaded against a capture and routed to a reviewer, with clear pending and completed states.
- A GitHub Actions workflow now builds the app, runs the test suite, and produces the WiX installer (MSI and bootstrapper EXE) on every push and pull request to `main`.

## Launch and loading

- Revie creates the tray icon and shows the main window before storage validation, taxonomy synchronization, session notification refresh, and cleanup run, so the interface appears sooner when shared storage is slow.
- Launching a second copy signals the running instance to bring its existing window forward. Revie remains a single-instance app.
- The main capture browser reads a local index manifest from `%LocalAppData%\Revie\indexes\`, displays cached captures, then scans storage in the background for additions, changes, and deletions.
- Capture scans reuse unchanged metadata based on file timestamps and sizes, use a bounded queue, skip archived Sessions and the delete queue, and ignore hidden, system, and reparse-point directories.
- Large capture lists are applied to the UI in batches so imports and refreshes stay responsive.

## Capture and session browsing

- The session browser shows cached session cards first and refreshes them in the background.
- Full-resolution previews load away from the UI thread and are released as selections change.
- Small JPEG sidebar previews are generated at up to 320 x 200 pixels. New session archives store them under `thumbnails\`; older captures can get `sidebar-preview.jpg` beside their original media.
- Session cards prefer an existing `sidebar-preview.jpg` over a full-resolution `capture.png`, and fall back to the original screenshot if a preview cannot be decoded.
- Fixed a WPF image-loader issue where a URI cache option caused valid JPEG previews to decode as blank.

## Session workflow and storage

- Review sessions open inside Revie's main window. Back navigation moves from a capture to the session list, then to the dashboard, preserving the current window size and maximized state.
- Saving a session prompts for a folder name. Revie writes the package into a temporary directory and then moves it to `<StorageRoot>\Sessions\<chosen name>\`; name collisions get `_01`, `_02`, and so on.
- New session packages keep all screenshot PNGs together in `screenshots\`, videos in `videos\`, compact previews in `thumbnails\`, capture metadata in `captures\`, and the package manifest in `session.json`.
- Session captures are staged under `%LocalAppData%\Revie\session-staging\` before save and are not duplicated into the normal Recent Captures bucket.
- Session context actions can open the whole session folder or its screenshots folder in Explorer.
- Older archives that keep media directly in `captures\` or in a folder per capture remain readable.
- The browser tolerates malformed session manifests or capture metadata so one damaged archive does not prevent the remaining sessions from loading.

## Saving, exiting, and recording

- Closing the main window keeps Revie running in the tray.
- Choosing **Exit** with an unsaved active session shows **Yes / No / Cancel**: save and name the folder, discard the session, or keep Revie open. Canceling the name dialog or a failed save also keeps the app open.
- If a video is recording when Exit is requested, Revie waits for any video startup or stop already in progress, finalizes the recording, adds it to the session, and only then shows the unsaved-session prompt. A failed video finalization keeps Revie open.
- Pending notes are flushed when switching captures, navigating between views, hiding the window, and exiting. Metadata saves are serialized so quick edits finish in order.
- Session package creation uses atomic metadata writes and a temporary folder, reducing the chance of a partially visible archive.
- Moving captures between projects performs storage work away from the UI thread.

## Revised-image review

- An image in a review session can receive one or more revised screenshots through the file picker or drag and drop. Uploads are kept with that capture under `captures\attachments\<capture id>\user-uploads\`, and the original and each revision remain selectable for comparison.
- A user can request review from a chosen teammate. Pending review state is tied to the recipient's Active User setting, appears on the session and capture, and clears when that user marks the revision reviewed.
- The **Revised Upload** tab shows **NO REVISED IMAGE YET** when empty instead of repeating the original screenshot. After an upload, a count badge appears on the tab and a revised-image marker appears on the capture thumbnail.
- **Request Review** is disabled until a revised image exists.

## Visual polish

- Subtle yellow accents highlight important actions, navigation, selected states, and review indicators across the main window and session browser.

## Build, test, and installer

- Added a GitHub Actions workflow (`.github/workflows/dotnet-desktop.yml`) that, on pushes and pull requests to `main` or manual dispatch, restores and builds `Revie.sln` in Release, runs the test suite, publishes the self-contained `win-x64` app, builds the WiX MSI and bootstrapper EXE, and uploads the installer and portable build as artifacts.
- WiX is pinned to 6.0.2. WiX 7 and later require accepting the Open Source Maintenance Fee EULA, which fails unattended CI runs; 6.0.2 supports the v4 schema used in `packaging\`.
- The workflow uses current action majors (checkout v7, setup-dotnet v6, upload-artifact v7) and keeps installer artifacts for 14 days.
- The previous template workflow failed on its first run because its solution and WAP/MSIX placeholders were never filled in; the replacement builds the repo's actual WiX packaging path.
- Version metadata needs alignment before the next release: `Revie.App.csproj` still says 1.0.5, while the WiX files say 1.0.3 and the May 19 release is named 1.0.6.

## Validation

- Release build of the current source tree completed with zero warnings and zero errors.
- All 38 automated tests passed. They cover cache/index behavior, archive layout and moves, thumbnail generation and legacy preview decoding, revision-review state, and metadata-save behavior.
- The built MSI contains 467 files, including `Revie.exe`, `ffmpeg.exe`, and `ffprobe.exe`; the bootstrapper EXE embeds that MSI.
- The GitHub Actions workflow completed successfully on `main` in about 3.5 minutes, producing the installer and portable artifacts.

## Known limits

- A live video recording followed by Exit and session save has not been manually exercised.
- Startup and navigation performance improvements have not been benchmarked with before/after timings.
