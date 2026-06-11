# WinSnipper

Ultra-lightweight screenshot tool for Windows, built for fast feedback loops —
especially pasting annotated screenshots into AI coding chats. Replaces the
built-in **Win+Shift+S** snip with a tighter flow:

**snip → auto-save + clipboard → floating thumbnail → annotate → paste anywhere.**

Single ~200 KB exe. .NET 8 + WPF, zero external dependencies.

## Flow

1. Press **Win+Shift+S** (rebindable) — screen freezes, drag a region, done.
2. The snip is saved to your snips folder and copied to the clipboard.
3. A small **floating thumbnail** appears bottom-right for a few seconds:
   - **click** → opens the annotation editor (thumbnail disappears)
   - **drag** → carries the PNG out as a real file — drop it into Explorer,
     browser upload fields, chats, e-mail (plus bitmap data for image-paste targets)
   - right-click → pin / copy / save-as / open / show-in-folder
   - untouched → fades away on its own
4. The **editor** (frameless, dark, compact): rectangle, freehand, ellipse,
   arrow, crop, 7 colors, stroke width, undo/redo, Ctrl+wheel zoom.
   **Copy & Close** (Ctrl+Enter) puts the annotated image on the clipboard and
   exits — no confirmation dialogs, ever. Closing always saves silently and
   refreshes the clipboard, so what you paste is what you drew.

## Why the hotkey override works

A `WH_KEYBOARD_LL` hook sees Win+Shift+S before the Windows Snipping Tool does
and swallows it (the same mechanism AutoHotkey uses). No registry hacks — quit
WinSnipper and stock Windows behavior is back instantly.

## Settings

Tray icon → **Settings…**

- Rebind the capture hotkey (any modifier combo — recorded live, including Win-combos)
- Thumbnail auto-dismiss time (1–15 s)
- Snips folder
- Auto-copy to clipboard on/off
- Start with Windows

Stored in `%APPDATA%\WinSnipper\settings.json`.

## Build

```powershell
dotnet build -c Release                     # dev build
dotnet publish -c Release -r win-x64 --self-contained false `
  /p:PublishSingleFile=true -o dist         # single-file exe (needs .NET 8 runtime)
```

`tools/gen-icon.ps1` regenerates `assets/icon.ico`.

Per-Monitor-V2 DPI aware; all selection math is done in physical pixels, so
crops are pixel-exact on scaled and mixed-DPI displays.

## Roadmap

Ideas queued up — PRs welcome:

- **Redact / pixelate tool** — blur API keys and secrets before sharing a screenshot with an AI chat
- **Text annotations** and numbered step badges (1→2→3) for bug reports
- **OCR** — copy the *text* out of any snip (Windows.Media.Ocr)
- **Color picker** — grab the hex of any pixel on screen
- **Pin to screen** — promote a thumbnail to a permanent always-on-top reference image
- **Snip history** — browse recent snips from the tray
- Highlighter pen, line tool
- Optional Desktop Duplication capture path for exclusive-fullscreen games

## License

[MIT](LICENSE)
