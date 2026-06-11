# Architecture

One WPF process, one project, no external packages. The flow is a straight
pipeline; each component owns one stage.

```
KeyboardHook ──► SnipManager ──► SnipOverlay ──► FloatingThumb ──► EditorWindow
 (hotkey)        (orchestrates)   (region select)  (pinned thumb)    (annotate)
                      │
                      └─► save PNG + clipboard
```

## Components (`src/`)

| File | Responsibility |
|---|---|
| `KeyboardHook.cs` | `WH_KEYBOARD_LL` hook. Sees the configured combo before the OS hotkey (that's how Win+Shift+S can be overridden), swallows it, sends a dummy key so the Start menu doesn't open on Win-up. Also exposes `CaptureInterceptor` so the settings window can record a new hotkey — including Win-combos. |
| `ScreenCapture.cs` | GDI BitBlt of the whole virtual screen (all monitors) into a frozen `BitmapSource`, in physical pixels. |
| `SnipManager.cs` | One snip end-to-end: hide thumbs → capture → overlay → crop → save + clipboard → spawn thumbnail. |
| `SnipOverlay.xaml` | Fullscreen frozen-frame selection UI. All selection math in physical pixels via `GetCursorPos`, so crops are pixel-exact at any DPI scale. |
| `FloatingThumb.xaml` | Bottom-right pinned thumbnail. Click = open editor (and self-dismiss), drag = `DoDragDrop` FileDrop, auto-fades after the configured timeout, hover pauses. |
| `EditorWindow.xaml` | Frameless annotation editor. The `Surface` grid is kept at 1 DIP == 1 px, so a 96-DPI `RenderTargetBitmap` of it (`Composite()`) is a pixel-exact output. Every tool adds an element to the `Ink` canvas and the shared undo stack; crop and pixelate bake from the live composite. |
| `SettingsWindow.xaml` | Hotkey recorder + preferences. |
| `Settings.cs` | JSON persistence (`%APPDATA%\WinSnipper\settings.json`), `Settings.Current` + `Changed` event. |
| `TrayIcon.cs` | WinForms `NotifyIcon`, menu, runtime-drawn fallback glyph. |
| `StartupManager.cs` | HKCU `Run` key toggle. |
| `Util.cs` | PNG save, clipboard retry wrappers, OCR (`Windows.Media.Ocr`, upscaled input, language fallback chain). |

## Decisions worth knowing

- **Hotkey override**: `RegisterHotKey` cannot claim Win+Shift+S (the shell owns
  it). A low-level hook fires first and can swallow it. The override exists
  only while the app runs — no registry edits, nothing to undo.
- **DPI**: the process is Per-Monitor-V2 (`app.manifest`). Capture and
  selection run in physical pixels; WPF surfaces are mapped 1 DIP = 1 px and
  scaled visually, so output never depends on display scaling.
- **No dialogs**: closing the editor saves silently and refreshes the
  clipboard. This is a product decision, not an oversight.
- **OCR & the TFM**: `net8.0-windows10.0.19041.0` pulls the Windows SDK
  projection (~24 MB in the single-file exe) purely for `Windows.Media.Ocr`.
  Dropping OCR and reverting the TFM gets a ~200 KB exe back.
- **WinRT + STA**: never block on WinRT async from the UI thread
  (`.GetResult()` deadlocks). Await it.
