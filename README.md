# WinSnipper

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)

Ultra-lightweight screenshot **and screen recording** tool for Windows, built
for fast feedback loops — especially pasting annotated screenshots into AI
coding chats. Replaces the built-in **Win+Shift+S** snip with a tighter flow:

**snip → auto-save + clipboard → floating thumbnail → annotate → paste anywhere.**

And the same flow for video: **Win+Shift+D → pick a region/window/screen →
MP4 → trim → paste as a file.**

Single-file exe. .NET 8 + WPF, no external packages — video encoding is
hand-rolled Media Foundation interop, capture is Windows.Graphics.Capture /
DXGI Desktop Duplication. The core app is ~0.3 MB; OCR ships as a separate
flavor so the lightweight build stays lightweight.

## Install

Grab a build from [Releases](../../releases) and run it — it lives in the tray.
Two flavors:

| File | Size | What you get |
|---|---|---|
| `WinSnipper.exe` | ~0.3 MB | The full flow — snip, record, trim, thumbnail, annotate, redact |
| `WinSnipper-OCR.exe` | ~25 MB | Everything above + **Copy Text** (Windows OCR) + Windows.Graphics.Capture recording (records hardware-overlay video — browser video playback — that other capture paths show as black) |

Both need the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
Tick *Start with Windows* in Settings if it earns a permanent spot.

Or via winget (submission pending review):

```powershell
winget install winsnipper
```

### 🤖 Install via an AI agent

Using Claude Code, Copilot, or any agent with shell access? Paste this:

> Install WinSnipper for me: download the latest release from
> https://github.com/v2matosevic/WinSnipper/releases/latest — the asset is
> `WinSnipper.exe` (lite) or `WinSnipper-OCR.exe` (adds OCR text copy; I'll tell
> you which I want) — into `%LOCALAPPDATA%\WinSnipper\`, make sure the .NET 8
> Desktop Runtime is installed (`winget install Microsoft.DotNet.DesktopRuntime.8`),
> then run the exe. It lives in the tray; Win+Shift+S takes a screenshot.
> If I ask for autostart, enable "Start with Windows" is in its tray Settings.

Equivalent script:

```powershell
$dir = "$env:LOCALAPPDATA\WinSnipper"
New-Item -ItemType Directory -Force $dir | Out-Null
Invoke-WebRequest "https://github.com/v2matosevic/WinSnipper/releases/latest/download/WinSnipper.exe" -OutFile "$dir\WinSnipper.exe"
& "$dir\WinSnipper.exe"
```

## Flow

1. Press **Win+Shift+S** (rebindable) — screen freezes, drag a region, done.
2. The snip is saved to your snips folder and copied to the clipboard.
3. A small **floating thumbnail** appears bottom-right **of the display you
   snipped on** — not the primary one — for a few seconds. Thumbnails stack
   upward per monitor, and placement is correct across displays running
   different scaling:
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

## Screen recording

1. Press **Win+Shift+D** (rebindable) — a live overlay opens with three pick
   modes: **Region** (drag), **Window** (click one), **Screen** (click a
   monitor). Keys `R`/`W`/`S` or `1`/`2`/`3` switch modes; Esc cancels.
2. Recording starts immediately: a red border marks the region and a small
   HUD pill shows elapsed time with **pause** and **stop**. Neither appears
   in the video. Press the hotkey again (or the HUD stop) to finish.
3. The MP4 is saved to `<snips folder>\Recordings\`, lands on the clipboard
   as a pasteable **file**, and a floating thumbnail appears — click it to
   open the **trim editor**.
4. Trim: a filmstrip timeline with draggable in/out handles, playhead,
   time bubble while dragging. Space plays the selection, `[` / `]` set the
   edges at the playhead, ←/→ step frames. **Save trimmed** writes a
   frame-accurate `(trimmed)` copy next to the original, **Save as…** asks
   where to put it, or tick *Replace original*.

**Choosing where a recording lands.** Recordings file themselves under
`Recordings\` so a take is never lost to a dismissed dialog. To put one
somewhere else, use **Save as…** on its thumbnail — it moves the MP4 and
re-points the card, so dragging it out afterwards drags the file you chose.
If you would rather be asked every time, tick *Ask where to save each
recording when it stops* in Settings; cancelling that dialog still leaves the
file in `Recordings\`.

Details that matter:

- **Capture ladder**: Windows.Graphics.Capture (OCR flavor — the only API
  that sees hardware-overlay/MPO video planes, i.e. browser video playback)
  → DXGI Desktop Duplication → GDI. `%APPDATA%\WinSnipper\recorder.log`
  records which path each recording used.
- **Encoding**: H.264 MP4 via a Media Foundation sink writer (hardware
  encoder when it cooperates), constant frame rate with wall-clock-correct
  timing, a keyframe every second so seeking/scrubbing stays instant.
  15 / 30 / 60 fps and cursor on/off in Settings.
- DRM-protected content (Netflix and friends) records black — the OS
  enforces that for every capture tool.
- `WinSnipper.exe --trim <file.mp4>` opens the trim editor directly.

## Auto-delete

Optional (Settings): recycle snips and recordings older than 7–90 days.
Files go to the Recycle Bin, never hard-deleted, and only files WinSnipper
itself created are touched.

## Why the hotkey override works

A `WH_KEYBOARD_LL` hook sees Win+Shift+S before the Windows Snipping Tool does
and swallows it (the same mechanism AutoHotkey uses). No registry hacks — quit
WinSnipper and stock Windows behavior is back instantly.

## Settings

Tray icon → **Settings…**

- Rebind the snip and recording hotkeys (any modifier combo — recorded live,
  including Win-combos)
- Recording frame rate (15/30/60) and cursor visibility
- Ask where to save each recording when it stops (off by default)
- Thumbnail auto-dismiss time (1–15 s)
- Snips folder (recordings go to its `Recordings` subfolder)
- Auto-copy to clipboard on/off
- Auto-delete old captures (off by default; Recycle Bin only)
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

- **Audio in recordings** — mic and/or system loopback (WASAPI)
- **GIF export** — for the places MP4 can't go
- **Color picker** — grab the hex of any pixel on screen
- **Snip history** — browse recent snips from the tray
- Highlighter pen, line tool
- Re-editable annotations (select / move / delete individual shapes)

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
