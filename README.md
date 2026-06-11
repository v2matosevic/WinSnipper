# WinSnipper

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)

Ultra-lightweight screenshot tool for Windows, built for fast feedback loops —
especially pasting annotated screenshots into AI coding chats. Replaces the
built-in **Win+Shift+S** snip with a tighter flow:

**snip → auto-save + clipboard → floating thumbnail → annotate → paste anywhere.**

Single-file exe. .NET 8 + WPF, no external packages. The core app is ~0.25 MB;
OCR ships as a separate flavor so the lightweight build stays lightweight.

## Install

Grab a build from [Releases](../../releases) and run it — it lives in the tray.
Two flavors:

| File | Size | What you get |
|---|---|---|
| `WinSnipper.exe` | ~0.25 MB | The full screenshot flow — snip, thumbnail, annotate, redact |
| `WinSnipper-OCR.exe` | ~25 MB | Everything above + **Copy Text** (Windows OCR) |

Both need the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
Tick *Start with Windows* in Settings if it earns a permanent spot.

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
   arrow, **text**, **numbered step badges** (1→2→3), **redact/pixelate**
   (hide API keys and secrets before sharing), crop, 7 colors, stroke width,
   undo/redo, Ctrl+wheel zoom. **Copy Text** runs Windows OCR over the snip and
   puts the recognized text on the clipboard. **Copy & Close** (Ctrl+Enter)
   puts the annotated image on the clipboard and exits — no confirmation
   dialogs, ever. Closing always saves silently and refreshes the clipboard,
   so what you paste is what you drew.

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

## OCR languages

OCR lives in the `WinSnipper-OCR.exe` flavor (the lite build hides all OCR UI).
"Copy Text" uses Windows' built-in OCR. It picks the best engine it can find:
Croatian → your profile languages → English → anything installed.

OCR language packs are Windows components, so they can't ship inside the app —
but WinSnipper's **Settings** shows your OCR status and offers a **one-click
install** of the pack for your language (a UAC prompt + ~1 minute download).
Equivalent manual command from an admin PowerShell:

```powershell
Add-WindowsCapability -Online -Name "Language.OCR~~~hr-HR~0.0.1.0"  # your tag here
```

Small snips are upscaled automatically before recognition, which significantly
improves accuracy on terminal-size text.

## Build

```powershell
dotnet build -c Release                     # dev build (lite)
dotnet publish -c Release -r win-x64 --self-contained false `
  /p:PublishSingleFile=true -o dist/lite    # lite single-file exe
dotnet publish -c Release -r win-x64 --self-contained false `
  /p:PublishSingleFile=true /p:EnableOcr=true -o dist/ocr   # OCR flavor
```

`tools/gen-icon.ps1` regenerates `assets/icon.ico`.

Per-Monitor-V2 DPI aware; all selection math is done in physical pixels, so
crops are pixel-exact on scaled and mixed-DPI displays.

## Roadmap

Ideas queued up — PRs welcome:

- **Color picker** — grab the hex of any pixel on screen
- **Snip history** — browse recent snips from the tray
- Highlighter pen, line tool
- Re-editable annotations (select / move / delete individual shapes)
- Optional Desktop Duplication capture path for exclusive-fullscreen games

## Docs

- [Architecture](docs/ARCHITECTURE.md) — component map and the decisions behind it
- [Contributing](CONTRIBUTING.md) — build, smoke test, ground rules
- [Changelog](CHANGELOG.md)

## Credits

Built by [Marko Matošević](https://github.com/v2matosevic) (Version2) together
with Claude (Anthropic) — designed, written, and shipped in a pair-programming
loop: human direction and testing, AI implementation.

## License

[MIT](LICENSE)
