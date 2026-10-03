# Capture history implementation and verification

Implemented on 2026-10-02 after Marko selected improving WinSnipper itself before ecosystem integration. The October 3 stable release and installation are tracked in [the 0.9.0 delivery record](RELEASE-0.9.0.md). The original source verification follows.

## Behavior

Open Capture history in the tray menu or double-click the tray icon. The window lists PNG screenshots and MP4 recordings from the configured save folder and its Recordings subfolder, newest modification first. It includes renamed files in those folders and excludes self-test artifacts. It does not crawl unrelated subfolders or follow files saved to another destination.

Search filenames or dates, filter screenshots/recordings and select a capture for a preview. Open editor uses the existing annotation editor; Open trimmer uses the existing trim window. Copy image loads the full-resolution image; Copy file puts a recording on the file clipboard. Show file selects the capture in Explorer. A previously opened editor is reused within that history window, including restoration from a minimized state.

Enter opens the selection, Ctrl+C copies it, Ctrl+F focuses search, F5 refreshes and Escape closes history. Text editing keeps its normal Enter/Ctrl+C behavior. The existing snip/record shortcuts and automatic deletion preferences are unchanged.

Folder scans run on a worker. Rows are virtualized. Screenshot previews are bounded to 1400 pixels on their longest edge without enlarging small images; full-resolution loading is reserved for copy/edit. Video previews use the existing Media Foundation decoder on a worker. A revision prevents stale work from replacing the current selection, and a semaphore keeps preview decoding sequential. File changes trigger a debounced refresh that preserves the selected path. Activation and F5 also refresh.

Frozen OnLoad images release their source file before editing or cleanup needs it. Missing/corrupt files show inline errors rather than confirmation dialogs. A copy failure is reported instead of claiming success.

## Verification

- Lite Release build: 0 warnings, 0 errors, isolated artifacts at `C:/Users/matos/AppData/Local/Temp/WinSnipper-history-1790960870513/lite`. See the commands below for reproducing it with another temporary path.
- OCR Release build: 0 warnings, 0 errors through `tools/ui-shots.ps1`, using an isolated app/harness build.
- Both editions' `--selftest` completed with exit code 0. The lite DLL was run through `dotnet`; the OCR DLL was run from `C:/Users/matos/AppData/Local/Temp/WinSnipper-uishots-f92dca336051461cb6af8ceac9675dde/app/bin/WinSnipper/release/WinSnipper.dll`.
- Focused history regression/render command completed with exit code 0: `pwsh -NoProfile -File tools/ui-shots.ps1 -HistoryOnly -Out artifacts/history-review`.
- Synthetic checks cover save-folder scope, newest-first ordering, full image dimensions/frozen ownership, released file handles, bounded tall previews, type filters, name/date search, recording preview/action labels, empty/unreadable states and live file refresh.
- Reviewed renders for the normal view, recording view, minimum 720 by 480 window, filtered search, no matches, unreadable capture and empty folder. [The retained preview](images/history.png) contains synthetic content.
- `git diff --check` passed.

The offscreen harness explicitly sets WPF's dispatcher synchronization context because it pumps frames rather than calling Application.Run. Without that, programmatic filter/search changes resumed their asynchronous UI work on a worker and produced misleading preview failures. The corrected harness passed. Intermediate selection changes during a list replacement are also suppressed before decoding the final selection.

Hands-on interaction with the tray, clipboard, Explorer and editor/trimmer reopening remains Marko's check. No mouse/keyboard input was synthesized. The smoke tests capture locally for their existing checks; no captured content was uploaded or used in the documentation preview.

## Reproduce

```powershell
dotnet build -c Release --artifacts-path <temporary-build-directory> -m:1
pwsh -NoProfile -File tools\ui-shots.ps1 -HistoryOnly
pwsh -NoProfile -File tools\ui-shots.ps1
```

Run each edition's built DLL with `dotnet <built-WinSnipper.dll> --selftest`; the host supplies a reliable foreground process exit code for the WinExe. Run the editions sequentially because the smoke tests share output filenames.

Use `pwsh -File tools\winsnipper.ps1 build -Flavor both` for installation into dist. It stops/restarts the running app and manages the keep-alive task, so Marko's explicit process-stop approval applies. Never publish into dist by hand.

## Delivery and future integration

Source changes: App/TrayIcon wiring, CaptureHistory metadata/image loader, HistoryWindow, the offscreen harness and documentation. No dependency was added. This feature is included in 0.9.0.

The [ecosystem proposal](ECOSYSTEM-INTEGRATION.md) records future capture-to-ADE/Athena delivery. It reuses the existing Hermes local service registry and explicit destination/draft receipts. No cross-app bridge, server API or automatic upload was implemented in this pass.
