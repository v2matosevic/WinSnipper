# Changelog

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
