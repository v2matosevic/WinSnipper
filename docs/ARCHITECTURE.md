# Architecture

One WPF process, one project, no external packages. Two flows share the same
front half; each component owns one stage.

```
                 ┌─► SnipManager ────► SnipOverlay ─► FloatingThumb ─► EditorWindow
                 │    (screenshot)      (pick region/    (pinned         (annotate)
KeyboardHook ────┤                       window/screen)   thumb)
 (two hotkeys)   │
                 └─► RecordingManager ► SnipOverlay ─► ScreenRecorder ─► FloatingThumb ─► TrimWindow
                      (recording)       (live mode)     (capture+encode    (video thumb)   (filmstrip trim)
                                                         + RecordingHud)
```

## Components (`src/`)

| File | Responsibility |
|---|---|
| `KeyboardHook.cs` | `WH_KEYBOARD_LL` hook. Sees the configured combos (snip + record) before the OS hotkey (that's how Win+Shift+S can be overridden), swallows them, sends a dummy key so the Start menu doesn't open on Win-up. Also exposes `CaptureInterceptor` so the settings window can record a new hotkey — including Win-combos. |
| `ScreenCapture.cs` | GDI BitBlt of the whole virtual screen (all monitors) into a frozen `BitmapSource`, in physical pixels. |
| `SnipManager.cs` | One snip end-to-end: hide thumbs → capture → overlay → crop → save + clipboard → spawn thumbnail. |
| `SnipOverlay.xaml` | Fullscreen selection UI with three pick modes: drag a **region**, click a **window** (`EnumWindows` + DWM frame bounds, cloaked windows filtered), click a **screen**. Frozen-screenshot mode for snips, live transparent mode for recordings (the desktop keeps moving). All selection math in physical pixels via `GetCursorPos`, so results are pixel-exact at any DPI scale. |
| `FloatingThumb.xaml` | Pinned thumbnail, bottom-right **of the monitor the capture came from**. Click = open editor (and self-dismiss), drag = `DoDragDrop` FileDrop, auto-fades after the configured timeout, hover pauses. Position is applied with `SetWindowPos` in physical pixels via `Monitors`, and stacking compares physical tops — `Window.Left/Top` converts DIPs using the *source* monitor's DPI and lands wrong across mixed-scaling displays. |
| `Monitors.cs` | Per-monitor work areas and DPI scale (`MonitorFromPoint` / `GetMonitorInfo` / `GetDpiForMonitor`). Exists because WPF only exposes `SystemParameters.WorkArea`, which is the primary monitor. |
| `EditorWindow.xaml` | Frameless annotation editor. The `Surface` grid is kept at 1 DIP == 1 px, so a 96-DPI `RenderTargetBitmap` of it (`Composite()`) is a pixel-exact output. Every tool adds an element to the `Ink` canvas and the shared undo stack; crop and pixelate bake from the live composite. |
| `SettingsWindow.xaml` | Hotkey recorder (both hotkeys) + preferences. |
| `Settings.cs` | JSON persistence (`%APPDATA%\WinSnipper\settings.json`), `Settings.Current` + `Changed` event. |
| `TrayIcon.cs` | WinForms `NotifyIcon`, menu, runtime-drawn fallback glyph. |
| `StartupManager.cs` | HKCU `Run` key toggle. |
| `Util.cs` | PNG save, clipboard retry wrappers (image / file / text), OCR (`Windows.Media.Ocr`, upscaled input, language fallback chain). |
| `AutoCleanup.cs` | Opt-in: recycles `Snip *.png` / `Recording *.mp4` older than N days (save dir + `Recordings\`), via `SHFileOperation` `FOF_ALLOWUNDO` — Recycle Bin, never a hard delete. |
| `RecordingHud.xaml` | Recording control pill (elapsed / pause / stop) + four border strips around the region. Everything is `WDA_EXCLUDEFROMCAPTURE`d and placed strictly **outside** the recorded pixels — GDI-style capture renders excluded windows as black, so nothing excluded may ever overlap the region. |
| `TrimWindow.xaml` | Filmstrip trim editor: draggable in/out handles, playhead, time bubble, selection-bounded playback. Drags never seek per pixel — visuals are instant, the `MediaElement` preview follows on a ~90 ms throttle. Filmstrip tiles are sized to the clip's aspect (from `MediaElement.NaturalVideoWidth/Height`) and tiled to fill the timeline, rebuilt on a debounced resize. |

## Recording pipeline (`src/Recording/`)

| File | Responsibility |
|---|---|
| `RecordingManager.cs` | One recording end-to-end: live overlay → even-sized region → `ScreenRecorder` + HUD → stop → clipboard file + video thumbnail. The hotkey toggles start/stop. 3 h runaway guard. |
| `ScreenRecorder.cs` | Capture thread + MF sink writer. Emits strict **constant frame rate** — the H.264 encoder MFT re-stamps timestamps at the declared fps, so the sample *count* is the timeline; late frames are filled with duplicates of the newest capture (encode to ~nothing). Forces a keyframe every second (`AVEncVideoForceKeyFrame`) because some hardware encoders accept `AVEncMPVGOPSize` and ignore it; falls back to the software encoder if hardware refuses. `timeBeginPeriod(1)` while recording (default 15.6 ms sleep quantum caps GDI loops near 20 fps). Diagnostics per session → `%APPDATA%\WinSnipper\recorder.log`. |
| `WgcCapture.cs` | Windows.Graphics.Capture backend (`WINRT` flavor only). The only API that captures hardware-overlay (MPO) planes — where browsers put playing video; BitBlt **and** Desktop Duplication render those black on modern Win11 drivers. Monitor item via `IGraphicsCaptureItemInterop`, free-threaded frame pool polled from the capture thread, cursor drawn manually. |
| `DesktopDuplication.cs` | DXGI Desktop Duplication backend + the shared `IRegionCapture` interface. Hand-rolled D3D11/DXGI vtables — placeholder methods are named `ReservedN`, **never** `_VtblGap*` (the runtime parses that prefix as a multi-slot gap directive and the vtable shifts). |
| `MediaFoundation.cs` | Minimal MF COM interop: sink writer, source reader, samples/buffers, `ICodecAPI`. Vtable order is load-bearing. All GUIDs verified against the Windows SDK headers — several online snippets circulate wrong ones. |
| `VideoTrimmer.cs` | Frame-accurate trim: source reader decodes to RGB32, sink writer re-encodes `[start, end]`. Re-encoding (not compressed-copy) keeps cuts exact regardless of GOP. Frames go through `VideoFrames.PackSample` first — passing the decoder's own buffer to the encoder sheared every frame whenever the width was not a multiple of 16. Re-encodes at 1.6× the source bitrate to limit generation loss. |
| `VideoFrames.cs` | The one place that turns a decoded RGB32 sample into packed top-down BGRA. Handles the coded-frame pitch trap: decoders may hand back the macroblock-aligned frame (e.g. 1136×640 for 1124×628) as a 1-D buffer while the type claims display stride — real pitch is derived from buffer length / coded height when `IMF2DBuffer` is absent. Copying at the wrong pitch produces diagonal smears. |
| `VideoThumbnails.cs` | Filmstrip frames via source reader, through `VideoFrames`. Decodes forward past the seek's keyframe to the requested timestamp, or neighbouring cells show the same picture. |

Capture ladder at runtime: **WGC → Desktop Duplication → GDI**, chosen per
recording, falling down a rung on any failure (spanning regions, rotated
displays, RDP, device loss mid-recording).

## Process lifecycle and supervision

WinSnipper has no main window, so a dead process looks exactly like a working
one until you press the hotkey. Three pieces make that state visible and
self-correcting.

| Piece | Where | What it does |
|---|---|---|
| Single-instance mutex | `App.OnStartup` | `WinSnipper_SingleInstance`. Only the instance that *claims* it may release it; a losing launch disposes the handle and exits before touching anything else. |
| `--watchdog` | `App.OnStartup` | Entry point for the keep-alive task. Exits immediately if `user-quit.flag` exists; otherwise falls through to the mutex, which turns the launch into a no-op or a relaunch. |
| `WinSnipper Keep-Alive` | Scheduled task, installed by `tools\winsnipper.ps1` | Runs the exe with `--watchdog` at logon and every 2 minutes. `MultipleInstances=IgnoreNew` + `ExecutionTimeLimit=0` means Task Scheduler counts the running exe as the task, so the ticks are free while the app is alive. |

The exit-reason ladder, most to least graceful, all recorded in
`%APPDATA%\WinSnipper\session.log`:

| Reason | Marker written? | Watchdog relaunches? |
|---|---|---|
| Tray *Exit* (`QuitFromTray`) | yes | no — deliberate quit stays quit |
| `winsnipper.ps1 stop` | yes | no |
| `SessionEnding` (logoff/shutdown) | **no** | n/a — nobody is logged on to run it |
| Unhandled background exception | no | yes |
| Killed, or a hard crash (`AccessViolation` out of COM interop, which no managed handler can catch) | no | yes, within 2 min |

Only a run that claimed the mutex writes a `startup` line, and only a clean
shutdown writes the matching `exit`. **A `startup` with no `exit` after it was
killed or crashed hard** — that asymmetry is the whole diagnostic.

`SessionEnding` deliberately does *not* write the quit marker. It looks
symmetric with the other deliberate exits and is wrong: the marker would
survive the reboot and suppress the next logon's start.

## Decisions worth knowing

- **Hotkey override**: `RegisterHotKey` cannot claim Win+Shift+S (the shell owns
  it). A low-level hook fires first and can swallow it. The override exists
  only while the app runs — no registry edits, nothing to undo.
- **DPI**: the process is Per-Monitor-V2 (`app.manifest`). Capture and
  selection run in physical pixels; WPF surfaces are mapped 1 DIP = 1 px and
  scaled visually, so output never depends on display scaling.
- **No dialogs**: closing the editor saves silently and refreshes the
  clipboard. This is a product decision, not an oversight.
- **OCR/WinRT & the TFM**: `net8.0-windows10.0.22621.0` (OCR flavor) pulls the
  Windows SDK projection (~24 MB in the single-file exe) for
  `Windows.Media.Ocr` and `Windows.Graphics.Capture`; 22621 specifically for
  `IsBorderRequired`. `SupportedOSPlatformVersion` stays 10.0.19041 — newer
  APIs are try/caught. The lite flavor stays on plain `net8.0-windows` and
  records via Desktop Duplication instead of WGC.
- **WinRT + STA**: never block on WinRT async from the UI thread
  (`.GetResult()` deadlocks). Await it.
- **Excluded windows are BLACK, not invisible, to GDI-style capture**: any
  `WDA_EXCLUDEFROMCAPTURE` window overlapping the recorded region blacks it
  out. That's why the recording border is four strips *outside* the region
  and the HUD hides itself when a full-screen recording leaves it no room.
- **Video timing**: the MP4 timeline comes from sample count × frame
  duration, not from the timestamps you write — the encoder MFT re-stamps
  them. CFR with duplicate-fill is the only reliable way to keep wall-clock
  time.
- **Supervision lives outside the process, not inside it.** An in-app watchdog
  cannot restart an app that took an `AccessViolationException` — the process
  is already gone. The relauncher has to be something the OS owns, hence the
  scheduled task; the app's only job is to honour the quit marker so the task
  doesn't fight a deliberate exit.
- **Verify interop GUIDs against the SDK headers** (`%ProgramFiles(x86)%\
  Windows Kits\10\Include\...\um\*.h`) — wrong GUIDs fail as `E_INVALIDARG`
  / `E_NOTIMPL` at runtime with no other clue.
