# WinSnipper

Ultra-lightweight screenshot tool for Windows. Replaces the built-in **Win+Shift+S**
snip with a faster flow: snip → auto-save + clipboard → floating thumbnail pinned to
the bottom-right of the desktop → built-in annotation editor.

## How it works

- A `WH_KEYBOARD_LL` keyboard hook intercepts **Win+Shift+S** before the Windows
  Snipping Tool sees it (same mechanism AutoHotkey uses). No registry hacks —
  the moment WinSnipper isn't running, the stock Windows behavior is back.
- Snips are saved automatically to `Pictures\WinSnipper\` and copied to the clipboard.
- Each snip appears as a **floating thumbnail** at the bottom-right (new ones stack upward):
  - **drag it into any app** — the drag carries the PNG as a real file (Explorer,
    browsers, upload fields, chats, e-mail…), plus bitmap data for image-paste targets
  - **double-click** (or ✏) to open the editor
  - **⠿ grip** — reposition the thumbnail on screen
  - right-click for copy / save-as / open / show-in-folder / dismiss
- The **editor** has freehand draw, rectangle, ellipse, arrow, crop, 7 colors,
  stroke thickness, undo/redo (Ctrl+Z/Y), copy (Ctrl+C), save (Ctrl+S), save-as,
  and Ctrl+wheel zoom. Saving updates the PNG and the floating thumbnail.
- Tray icon: new snip, open snips folder, **Start with Windows** toggle, exit.

## Build & run

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\WinSnipper.exe
```

Single-file publish:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

.NET 8, WPF, zero external dependencies. Per-Monitor-V2 DPI aware; selection math is
done in physical pixels so crops are pixel-exact on scaled displays.
