# Revie development patch notes — November 12, 2026

These notes cover the work in the current local source tree and Release build. They are development notes for the team; no installer or Git commit was created as part of this work.

## Launch and loading

- Revie creates the tray icon and shows the main window before starting storage validation, taxonomy synchronization, session notification refresh, and cleanup work. This gets the interface on screen sooner when the shared storage root is slow.
- Launching a second copy signals the running instance to bring its existing window forward. Revie remains a single-instance app.
- The main capture browser reads a local index manifest from `%LocalAppData%\Revie\indexes\`, displays cached captures, then scans storage in the background for additions, changes, and deletions. The scan reuses unchanged metadata based on file timestamps and sizes; a missing or damaged manifest is rebuilt from capture files.
- Capture scans use a bounded queue, avoid archived Sessions and the delete queue, and skip hidden, system, and reparse-point directories. Large lists are applied to the UI in batches so imports and refreshes remain responsive.
- The session browser likewise displays cached session cards first and refreshes them in the background. Full-resolution previews load away from the UI thread and are released as selections change.
- Small JPEG sidebar previews are generated at up to 320 × 200 pixels. New session archives store previews in `thumbnails\`, while older captures can get `sidebar-preview.jpg` beside their original media.

## Session workflow and storage

- Review sessions open inside Revie's main window. Back navigation moves from a capture to the session list, then to the dashboard. Opening Sessions preserves the current window size and maximized state.
- Saving a session asks for a folder name. Revie writes the package into a temporary directory, then moves it to `<StorageRoot>\Sessions\<chosen name>\` after it is complete. A name collision gets `_01`, `_02`, and so on.
- In the new layout, all screenshot PNGs are together in `screenshots\`; videos are in `videos\`, compact previews in `thumbnails\`, capture metadata in `captures\`, and the package manifest is `session.json`. Session captures are staged under `%LocalAppData%\Revie\session-staging\` before save and are not duplicated into the normal Recent Captures bucket.
- Session context actions can open the whole session folder or its screenshots folder in Explorer. Older archives that keep media directly in `captures\` or in a folder per capture remain readable.
- The browser tolerates malformed session manifests or capture metadata so one damaged archive does not prevent the remaining sessions from loading.

## Saving, exiting, and recording

- Closing the main window keeps Revie in the tray. Choosing **Exit** with an unsaved active session shows **Yes / No / Cancel**: save and name the folder, discard the session, or keep Revie open. Canceling the name dialog or a failed save also keeps the app open.
- If a video is recording when Exit is requested, Revie now waits for any video startup or stop already in progress, finalizes the recording, adds it to the session, and only then shows the unsaved-session prompt. This fixes the case where choosing Yes previously returned to the app because saving was blocked by the still-running video. A failed video finalization keeps Revie open.
- Pending notes are flushed when switching captures, navigating between views, hiding the window, and exiting. Metadata saves are serialized so quick edits finish in order. Session note saves are also flushed before navigation or exit.
- Session package creation uses atomic metadata writes and a temporary folder, reducing the chance of a partially visible archive. Moving captures between projects performs storage work away from the UI thread.

## Revised images and review feedback

- An image in a review session can receive one or more revised screenshots through the file picker or drag and drop. Uploads are kept with that capture under `captures\attachments\<capture id>\user-uploads\`. The original and each revision remain selectable for comparison.
- A user can request review from a chosen teammate. Pending review state is tied to the recipient's Active User setting, appears on the session and capture, and clears when that user marks the revision reviewed.
- The **Revised Upload** tab now shows **NO REVISED IMAGE YET** when it is empty, instead of repeating the original screenshot. After an upload, a yellow count badge appears on the tab and a revised-image marker appears on the capture thumbnail. **Request Review** is disabled until a revised image exists.

## Thumbnails and visual polish

- Older session cards now prefer an existing `sidebar-preview.jpg` over a full-resolution `capture.png`. If a selected thumbnail cannot be decoded, the session card and capture list can fall back to the original screenshot.
- The WPF image loader no longer applies a URI cache option to stream-backed images. That option caused valid JPEG previews to fail decoding and appear blank.
- Subtle yellow accents now highlight important actions, navigation, selected states, and review indicators across the main window and session browser.

## Paths for the team

| Item | Path |
| --- | --- |
| Current local executable | `Revie.App\bin\Release\net8.0-windows\Revie.exe` |
| Shared storage root configured on this workstation | `\\nt-pr-fs01\StudioReview\Revie\` |
| New session package | `<StorageRoot>\Sessions\<session name>\` |
| Screenshots together | `<session>\screenshots\` |
| Session videos and thumbnails | `<session>\videos\`, `<session>\thumbnails\` |
| Revised uploads | `<session>\captures\attachments\<capture id>\user-uploads\` |
| Local capture and session indexes | `%LocalAppData%\Revie\indexes\` |
| Active session staging | `%LocalAppData%\Revie\session-staging\` |
| Settings, logs, and backups | `%AppData%\Revie\settings.json`, `%AppData%\Revie\logs\`, `%AppData%\Revie\backups\` |

Each teammate should confirm their storage root in Settings. A development build needs its whole output folder, including adjacent DLLs and FFmpeg files.

## Validation and current limits

- The latest Release build completed with zero warnings and zero errors. Revie launched from that build and was responding.
- All 38 automated tests passed. They cover the cache/index behavior, archive layout and moves, thumbnail generation and legacy preview decoding, revision-review state, and metadata-save behavior.
- A live video recording followed by Exit and session save has not yet been manually exercised. Startup and navigation performance improvements have not been benchmarked with before/after timings.
