# Changelog

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
