# Recording controls and responsive selection

Implemented and verified locally on 2026-10-24. These changes have not yet been
installed into the running WinSnipper or published as a release. Existing local
elevated startup remains configured; installation must preserve it.

## Changes

- Controls stay within one real monitor's work area, rather than the combined
  bounding rectangle of all displays. This avoids monitor gaps and offscreen
  placement. Fullscreen uses a bar inside the viewport instead of hiding it.
  Native dragging clamps the bar to an actual monitor. Display/DPI changes
  reposition it. The bar uses Windows' non-activating window style.
- Pause, Resume and Stop have readable labels and scalable geometry icons.
  The elapsed time excludes pauses. The paused state is explicit. The timer
  updates only when the displayed time changes; the continuous dot animation
  and drop-shadow effect have been removed.
- Ctrl+Shift+F10 pauses/resumes and Ctrl+Shift+F12 stops while recording. These
  do not require the Windows key, and run through an independent bounded worker
  outside the native keyboard callback. Repeats/releases are swallowed. The
  configured recording shortcut still toggles start/stop.
- The tray changes New recording to Stop and save recording and adds
  Pause/Resume and Show recording controls. The last action restores a bar the
  user moved, without restarting or losing the take.
- Recording selection now uses an opaque frozen preview and retains one hidden
  selector HWND, as screenshots already did. Native capture stays on the
  dedicated worker. Both selectors are prepared during startup warmup.
  Mouse motion stores the latest coordinates and draws once at the next
  compositor frame, instead of relaying every pointer event into layout.
  Badge measurement is skipped when its text has not changed. The toolbar
  anchors to the cursor's real monitor. Idle selectors release their pixels and
  pending render callback.
- Media Foundation startup now runs on the recorder thread. Readiness resolves
  even on encoder startup failure, so the UI cannot wait forever for controls.
  Resume publishes the updated paused-time total before making capture active.

## Capture exclusion

The recorder identifies its backend before showing controls. WGC uses native
window exclusion for an embedded bar. Compatibility capture such as GDI can
turn protected windows black. When an external placement fits, the bar stays
outside the recording and can remain excluded. For fullscreen compatibility
capture, the bar stays visible without protected affinity, so it may appear in
the video instead of producing a black rectangle. The app reports this tradeoff.

If WGC fails during recording, its worker clears native bar affinity directly
before taking the first GDI frame. It posts the UI status update separately and
never waits for the UI dispatcher. A shared native-affinity gate and a current
backend flag prevent a stale UI update from re-protecting the embedded bar.
This also avoids waiting on the UI while shutdown is waiting on the encoder. The recording continues
with the existing fallback behavior. Border strips always stay outside the
recorded pixels. No Windows privacy policy or system screenshot component is
modified by this change.

## Verification

Both Release flavors compile with zero warnings and zero errors using isolated
artifact directories. The following checks passed without installed hooks,
synthetic input, visible on-desktop app windows or clipboard changes:

| Check | Evidence |
|---|---|
| Recording controls | `tools/test-recording-controls.ps1`: 40 passing checks; `artifacts/recording-controls/checks.json` records each case. |
| Placement | Seven layouts cover fullscreen, negative coordinates, stacked monitors, portrait, mixed DPI, desktop gaps and small displays. Placement and dragged rectangles remain within the supplied real work area. `layouts.json` records exact rectangles. |
| Native HWND | A hidden HUD for the full virtual desktop fits one actual work area. Its four strips are strictly outside the whole desktop bounds. Native NOACTIVATE and display affinity are verified. WGC sets 0x11; embedded compatibility mode clears it to zero. |
| Fallback and stop race | The recorder worker clears native affinity while the UI thread is blocked. A deliberately stale UI update cannot restore embedded protection after backend fallback. No dispatcher wait is needed before GDI capture. |
| UI | `recording.png`, `paused.png` and `recording-selection.png` were rendered from real XAML with synthetic content and visually inspected. |
| Selector work | 1,000 queued coordinate updates cause no synchronous selection layout; one rendering callback uses the final coordinates and detaches. Pixel/selection cleanup and pending reservation are checked. |
| Safety shortcuts | Exact active-only matching, worker separation, repeat/release suppression, and queued stop delivery behind a blocked pause subscriber. |
| Recording start | A missing recording destination injects encoder failure; readiness and stop both complete. An accepted 4x4 selection verifies coordinates are copied before cached selection cleanup and reports the size error. |
| Recording round-trip | A short private real capture pauses, resumes and stops without a HUD. Its MP4 finalizes and decodes; duration matches active time, excluding the pause, and the returned thumbnail is frozen. |
| Hotkey/capture regressions | `tools/test-hotkey.ps1` passes dispatch, queue, boost, offscreen modal reuse, pixel-equivalence, lifetime, capture-failure and real capture-worker checks. |
| Save/finalization | `tools/test-reliability.ps1`: all 24 checks pass, including concurrent stop, real encoder-finalization failure, retained partial file and suppressed success thumbnail. |

The existing screenshot capture benchmark measured a 69.6 ms warm median across
12 samples on the 5760x1080 desktop (first sample 93.4 ms). GDI handles remained
5 before/after; frozen pixels, opacity, crop and PNG round-trip passed. This
benchmark exercises the existing native capture code, not a new improvement
made here. Its historical baseline is not the immediately preceding install.
The cached-selector test measured 8.8 ms to attach pixels after a 546.8 ms
one-time preparation in that harness run. These are scoped measurements, not
an end-to-end game latency guarantee. A peer announced background GPU rendering
during this investigation; no quiet-window performance claim is made.

Native affinity flags are verified, but the bar's absence from an encoded WGC
video and focus behavior inside a specific fullscreen game have not been
visually verified. No game was opened or controlled. The owner still needs to
check fullscreen visibility, dragging, pause/resume, stop/save and smooth area
selection in the affected game after installation. Exclusive fullscreen and
game input policies can limit desktop overlays; the no-Windows-key shortcuts
and tray controls provide independent routes.

## Local installation

Save open edits and finish any active recording. From administrator PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "B:\Coding\WinSnipper\tools\winsnipper.ps1" build -Flavor both
```

The existing supervisor publishes both flavors, stands the watchdog down during
replacement, and restarts the installed elevated task. Approval for that restart
belongs to the owner under the process-closure rule. No extra startup setup or
native Windows app removal is needed.
