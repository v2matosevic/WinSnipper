# Changelog

## 0.8.0 — 2026-09-20

Nothing else should be able to answer the capture hotkey, and a busy machine
should not be the reason a screenshot takes seconds.

- **The hotkey hook moved off the UI thread.** Windows delivers a low-level
  keyboard callback on the thread that installed the hook, so WinSnipper's
  hook used to queue behind every window it was building and every garbage
  collection it was running. Past `LowLevelHooksTimeout` (300 ms by default)
  Windows stops waiting, hands the keystroke to the shell anyway — which is
  the Windows Snipping Tool opening on top of you — and unhooks after repeat
  offences. The hook now owns a dedicated high-priority thread that does
  nothing else, and rejects keys that are not ours before it looks at modifier
  state, which is most of the work it used to do on every keystroke in the
  system.
- **Replace the Windows Snipping Tool** (new setting, on by default).
  Win+Shift+S was already intercepted; Windows 11's other route into the same
  tool is bare PrintScreen, which is a setting rather than a hotkey. WinSnipper
  now captures on PrintScreen and turns that setting off, recording the
  previous value first — unticking the box, or `winsnipper.ps1 uninstall`,
  restores it exactly. Alt+PrintScreen is untouched.
- **Captures no longer copy the desktop twice.** The screen is blitted straight
  into a shared memory section that WPF reads in place. On the tested
  5760 × 1080 desktop, median capture preparation fell from 64.3 ms to 46.1 ms
  and the first capture of a run from 144.4 ms to 62.0 ms, and each snip stops
  allocating roughly 24 MB of large-object-heap garbage. See
  [performance results](docs/PERFORMANCE.md).
- **The capture path gets priority while it runs.** Pressing the hotkey lifts
  the process above whatever is making the machine feel slow, and drops it
  back as soon as the selection is done.
- **The first snip of a session no longer pays for the last one's warm-up.**
  The selection overlay, the crop/encode/render pipeline and the capture path
  are all exercised once at startup, at idle, with nothing shown on screen.
  Published builds are precompiled (ReadyToRun), so the first hotkey press is
  not also the first time the runtime compiles that code.
- **The PNG is written in the background.** The thumbnail appears as soon as
  the snip exists; anything that hands the file to someone else — dragging it
  out, the editor, Explorer — waits for the write first.
- **Dragging a selection is cheaper.** The dimmed area around the selection was
  a boolean geometry subtraction rebuilt from scratch on every mouse move
  across a 5760-pixel-wide overlay. It is now one reused even-odd geometry, in
  which only the selection rectangle moves: pixel-for-pixel identical output,
  4.4× less work and 5× less garbage per move.

## 0.7.0 — 2026-09-11

A redesigned editor and trim window, and a faster snip.

- **Redesigned screenshot editor.** One toolbar row with a consistent vector
  icon set (no more mixed symbol glyphs), the active tool in accent blue, and
  single-key tools: R rectangle, A arrow, O ellipse, P pen, T text, N step,
  B redact, C crop. Stroke size is four presets (`[` / `]` step through them).
  Colours and sizes sit inline on wide windows and fold behind a colour chip on
  narrow ones, so a small snip opens in a small window instead of a 1180 px
  one. The window is sized for the display's scaling, the snip gets a hairline
  edge so dark screenshots no longer vanish into the dark canvas, and the
  status bar has zoom controls (Ctrl+0 fit, Ctrl+1 actual pixels, Ctrl+wheel
  zooms at the cursor). "100%" now means pixel-for-pixel on any scaling.
- **Crop no longer shrinks with zoom.** On a big multi-monitor snip the Crop /
  Cancel pill and the selection outline used to scale down with the image
  until they were barely usable; they now stay a fixed size, the outline reads
  on light and dark snips, and the pill shows the crop size.
- **Redesigned trim window.** Play button beside the filmstrip, handles inside
  the kept range with no jump on grab, a time bubble above the strip while
  dragging, a "Keeping 0:02.4 – 0:08.7 · 6.3 s" readout, shortcut key hints,
  progress in the Save button, and trim errors shown inline instead of in a
  dialog.
- Both windows gained maximize, dark tooltips and scrollbars, and a maximized
  window now stays inside the screen instead of losing its edges.
- **Faster screenshot preparation**: opaque desktop pixels are copied straight
  into WPF, without an intermediate bitmap and alpha conversion. On the tested
  three-monitor desktop, median preparation time fell from 142.8 ms to
  101.1 ms (29%). This measures capture preparation, not total opening time.
- Opening diagnostics: the bounded `session.log` now records keyboard-event
  age, dispatcher delay, capture preparation, window construction and first
  render for screenshot/recording selection and thumbnail-to-editor opening.
  Timing writes run in the background and contain no captured image content.
- For contributors: `tools\measure-capture.ps1` is a reproducible capture
  benchmark (pixel ownership, opacity, PNG round-trips, GDI handles; see
  [performance results](docs/PERFORMANCE.md)), and `tools\ui-shots.ps1` renders
  every editor and trim state to PNGs from an isolated build without opening
  anything on screen, which is also how the README screenshots are made.

## 0.6.3 — 2026-08-27

- **A keep-alive task brings WinSnipper back if it dies.** It is a tray app with
  no window, and the Run key only fires at logon — so anything that killed it
  mid-session (a crash, a rebuild) left it gone until the next reboot, with no
  quick way back. A scheduled task now launches the exe with `--watchdog` every
  2 minutes: the single-instance mutex makes that a no-op while the app is
  alive, and replaces it when it is not. Tray *Exit* writes a marker
  `--watchdog` honours, so a deliberate quit still stays quit
- **`WinSnipper.cmd` / `tools\winsnipper.ps1` — one entry point from zero.**
  `setup` builds if needed, enables autostart, installs the keep-alive task,
  creates desktop and Start Menu shortcuts and starts the app; `status`,
  `start`, `stop`, `restart`, `build`, `logs` and `uninstall` cover the rest
- **`session.log` makes a disappearance diagnosable.** Every run that claims the
  mutex logs a startup line and, on a clean shutdown, a matching exit line with
  its reason (tray exit, session ending, unhandled exception, exit code). A
  startup with no exit after it was killed or crashed hard

## 0.6.2 — 2026-08-25

- **Thumbnails appear on the screen you snipped**, not the primary one. The
  card docks to the bottom-right of the monitor containing the capture, stacks
  only against thumbs on that same monitor, and gets the placement right when
  displays run different scaling
- **Trimmed video is no longer sheared.** The trimmer handed decoded frames to
  the encoder with the media type's advertised stride, but decoders pad rows
  for alignment — so every frame came out as a diagonal smear whenever the
  recorded width wasn't a multiple of 16 (measured: 1124×628 and 1366×768
  broken, 1280×720 and 1920×1080 fine). Frames are now re-packed to a known
  stride first, and trims re-encode at 1.6× the source bitrate so a cut clip
  still looks like the take it came from
- **Filmstrip shows the video.** Tiles are sized to the clip's aspect ratio
  and as many are laid down as the timeline is wide, instead of 14 cells
  centre-cropped to whatever shape they landed in. Preview frames also decode
  forward to the requested timestamp rather than stopping at the previous
  keyframe, so neighbouring cells differ. The strip rebuilds on window resize
- **Choose where recordings go** — "Save as…" on a video thumbnail (moves the
  MP4 and re-points the card), "Save as…" in the trim window, and an
  *Ask where to save each recording when it stops* option in Settings.
  Cancelling any of these keeps the file safely in `Recordings\`
- Launching a second instance no longer dies with an unhandled
  ApplicationException — the losing instance was releasing a mutex it never
  owned
- Trim window footer no longer overlaps its own buttons at narrow widths

## 0.6.1 — 2026-07-02

- **Recording over playing video no longer black** (OCR/full flavor) — capture
  now uses Windows.Graphics.Capture, the only API that sees hardware-overlay
  (MPO) video planes; browsers put playing video there and both GDI and DXGI
  Desktop Duplication record it black on modern Windows 11 drivers. Capture
  ladder: Windows.Graphics.Capture → Desktop Duplication → GDI (the lite
  flavor starts at Desktop Duplication)
- **Trim window rebuilt** — filmstrip timeline with draggable in/out handles
  and a playhead, QuickTime-style: dimmed outside the selection, drag the blue
  handles to trim, `[` / `]` set edges at the playhead, ←/→ step frames,
  Space plays the selection, playback stops at the trim end so you preview
  exactly what gets exported. Scrub visuals follow the mouse instantly; the
  video preview follows on a throttle
- Version is now visible: tray tooltip and the Settings footer show it
- `--trim` handles unquoted paths with spaces
- Recordings save to a `Recordings` subfolder of the save location; the tray
  menu gets "Open recordings folder", and a recording's thumbnail menu gets
  "Copy video (paste as file)". Auto-delete covers the subfolder too

## 0.6.0 — 2026-07-01

- **Screen recording** — Win+Shift+D (configurable) selects a region with the
  same overlay as snips and records it to H.264 MP4 via Media Foundation
  (hardware encoder when available, zero new dependencies, works in the lite
  build). Press the hotkey again or use the floating HUD (elapsed / pause /
  stop) to finish; the HUD and region border are excluded from capture so
  they never appear in the video. Cursor on/off and 15/30/60 fps in Settings
- **Trim editor** — click a recording's floating thumbnail (or `--trim
  <file>`) to open a player with Set start / Set end markers; saves a
  frame-accurate "(trimmed)" copy or replaces the original
- **Auto-delete** — opt-in Settings toggle recycles snips and recordings
  older than 7/14/30/60/90 days (Recycle Bin, never a hard delete; only
  files WinSnipper created are touched)
- Recordings land on the clipboard as a pasteable file, and the thumbnail
  drags out into any app just like snips
- `--selftest` now covers a record → trim round-trip;
  `%APPDATA%\WinSnipper\recorder.log` traces each recording session

## 0.5.1 — 2026-06-12

- Removed the redundant Copy button from the editor toolbar — Ctrl+C still
  copies, and Copy & Close remains the primary action

## 0.5.0 — 2026-06-12

- **Hook watchdog** — the keyboard hook re-arms every 5 minutes and on
  wake/unlock, fixing the classic "hotkey silently stops working" failure
  (Windows drops LL hooks after a slow callback)
- **Crash resilience** — unhandled exceptions are logged to
  `%APPDATA%\WinSnipper\crash.log`; UI-thread errors no longer kill the app
- **Update check** — once a day the app compares itself against the latest
  GitHub release and shows a tray balloon linking to it

## 0.4.0 — 2026-06-12

- **Two build flavors**: lite `WinSnipper.exe` (~0.25 MB, the full screenshot
  flow) and `WinSnipper-OCR.exe` (~25 MB, adds Copy Text). One codebase,
  `/p:EnableOcr=true` switches; the lite build hides all OCR UI
- Settings shows OCR engine status + one-click elevated install of the
  user-language OCR pack (OCR flavor only)
- Copy Text closes the editor (copy → save → close, like Copy & Close)
- Save / Save As are distinct icon buttons

## 0.3.0 — 2026-06-12

- **Redact/pixelate tool** — drag a region to hide API keys/secrets before sharing
- **Text annotations** — click to type, drop-shadowed for contrast
- **Numbered step badges** — click to drop ① ② ③, contrast-aware
- **OCR ("Copy Text")** — Windows OCR over the snip, in the editor and the
  thumbnail menu; prefers Croatian → profile languages → en-US; snips are
  upscaled before recognition for noticeably better accuracy
- Settings window: rebindable hotkey (recorded live through the hook),
  dismiss time, snips folder, clipboard toggle, start with Windows
- Pin option on thumbnails; tray shows the live hotkey

## 0.2.0 — 2026-06-11

- Frameless Mac-style editor (custom chrome, rounded corners, segmented tools)
- Compact editor window — full toolbar always visible
- Closing the editor saves silently and refreshes the clipboard (no dialogs)
- Thumbnail: click = edit (and instantly dismisses), drag = file drag-out,
  auto-dismiss with hover/interaction awareness, fade+slide entrance
- Embedded multi-size app icon; single-file `dist/` publish

## 0.1.0 — 2026-06-11

- Initial release: Win+Shift+S override via low-level keyboard hook,
  freeze-frame region selection, auto-save + clipboard, floating thumbnail,
  annotation editor (pen/rect/ellipse/arrow/crop, colors, undo/redo, zoom),
  tray icon with start-with-Windows toggle
