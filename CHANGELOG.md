# Changelog

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
