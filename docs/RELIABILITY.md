# Capture failure handling and verification

Implemented locally on 2026-10-03 after Marko authorized the proposed hardening pass. Nothing was installed or released in this pass. The running September 21 build remains unchanged. Confidence is high for the tested failure paths; native interaction and system sleep/resume remain unverified.

## Changed behavior

PNG encoding writes to a uniquely named temporary file in the destination folder. It flushes the file before committing the destination. An encode error, locked destination or refused replacement leaves the previous PNG intact. New captures use non-overwriting commits and reserve names while background writes may still be pending. No claim of protection against physical disk failure or arbitrary power loss is made.

Failed thumbnails retain their in-memory image and remain on screen, with inline status, Retry saving and Save as. File handoff waits asynchronously for a successful save. A ten-second timeout retains the original job, prevents premature handoff and observes its eventual success/failure. Screenshot Save as can recover to another folder. Video Save as copies through the same protected commit and retains the source copy in the thumbnail workflow.

Clipboard image/file/text requests retry asynchronously on their owning UI thread. Permanent failure stays visible rather than dismissing the capture. A newer app request or an external clipboard sequence change cancels a stale retry so it cannot overwrite newer content. Legacy synchronous helper names remain single-attempt boolean APIs; production capture/editor/recording flows use the asynchronous helpers.

Editor saves, copy actions and closing use guarded asynchronous operations. The editor stays open after a failed save or Copy & Close, and preserves its dirty annotations. Save as updates the editor's file path only after the new PNG is committed. Normal success still closes silently; no confirmation dialog was added. `ExportAsync()` exposes a final saved composite without copying or closing for the parallel integration work.

Recording finalization/capture errors return no success frame, report through the manager and retain partial files containing captured frames. Concurrent stop requests await one completion and produce one error. Tray Exit awaits recording completion instead of blocking the UI. The destination prompt uses protected streaming copy, and a preference-write failure cannot revert the returned path to a deleted source.

Isolated builds explicitly exclude `bin`, `obj` and `artifacts` under the checkout. This fixes generated WPF/assembly files from a concurrent normal build being compiled a second time.

## Verified checks

`tools/test-reliability.ps1` passed 24 checks covering:

- Partial write failure preserving the original PNG and cleaning its own scratch file.
- A real locked destination, new-capture overwrite refusal, PNG dimensions and pixel equality.
- One thousand unique rapid capture names before any background save completes.
- Fake clipboard retries yielding to UI heartbeats, retaining thread ownership, reporting permanent failure, and respecting newer app/external clipboard changes.
- An unfinished save remaining observable after the real ten-second timeout, later success, visible save/copy recovery, and retry saving the retained image.
- A real editor close attempt against a locked file preserving both its original PNG and dirty editor, followed by successful recovery to another path.
- The real capture/stop/finalization path with an injected finalization exception, shared concurrent stop completion, one manager error, no success frame, and retained partial file.

The injected finalization hook is internal and has no UI/configuration surface. Clipboard writers and sequence readers are fake in this harness; it does not modify the OS clipboard. Its native recording is a small private temporary fixture. Synthetic recovery PNGs and the exact passing case list are under `artifacts/reliability`.

The existing hotkey regression suite passed all five groups: worker dispatch, recording separation, boost expiry, request coalescing and prepared-overlay equivalence. The complete editor/trim/history offscreen render suite passed, including history regressions. Both build flavors compile without warnings/errors; final frozen-source smoke results are recorded in the completion handoff.

Reviewed recovery renders for failed thumbnail save/copy and editor close/save failure. The editor's action and error text remain readable. No visible desktop input or clipboard writes were used for these checks.

## Delivery and remaining limits

Source and tests are ready locally. Use `pwsh -File tools\winsnipper.ps1 build -Flavor both` for a local dist update only after Marko approves restarting the running app and the shared source is ready. The earlier restart question has no approval in this thread; it was not repeated. Never publish directly to dist.

Hard process termination can still lose edits that exist only in memory, and an incomplete MP4 is not promised playable or automatically repaired. Real sleep/resume, extended recording, mixed-monitor interaction and physical disk exhaustion remain hands-on/native acceptance cases; the injected faults and existing lifecycle harness do not prove those environments.

The Talkty/ADE integration is a separate peer task. Its additive XAML/partial classes were present during initial combined-tree checks and are excluded from this reliability commit except for this task's thumbnail recovery markup. No service, conversation or API was activated by this task.
