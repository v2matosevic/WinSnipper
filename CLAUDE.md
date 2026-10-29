# WinSnipper

Tray app (.NET 8 WPF, no NuGet packages) that replaces Win+Shift+S and records
the screen. One project, two flavors: lite `WinSnipper.exe` and
`WinSnipper-OCR.exe` (`-p:EnableOcr=true`: WinRT for OCR and
Windows.Graphics.Capture).

Read first: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (component map and the
decisions behind it), then [CONTRIBUTING.md](CONTRIBUTING.md) (build, smoke
test, harnesses).

## Commands

| Do | Command |
|---|---|
| Dev build | `dotnet build -c Release` (`-p:EnableOcr=true` for the OCR flavor) |
| Build without touching bin/obj/dist | add `--artifacts-path <temp dir>` |
| Publish into `dist` and restart the running app | `pwsh -File tools\winsnipper.ps1 build -Flavor both` |
| Running? Which build? Native screenshot conflict? | `pwsh -File tools\winsnipper.ps1 status` |
| Administrator-window hotkeys (opt-in, admin shell, restarts app) | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\winsnipper.ps1 setup -Elevated -Rebuild` |
| Supervision regression checks (isolated) | `pwsh -NoProfile -File tools\test-supervision.ps1` |
| Windows capture integration checks | `pwsh -NoProfile -File tools\test-capture-integration.ps1` |
| Install/register capture default candidate (restarts app) | `pwsh -File tools\winsnipper.ps1 build -Flavor both -RegisterCapture` |
| Recording HUD, selector and safety shortcuts | `pwsh -NoProfile -File tools\test-recording-controls.ps1` |
| Smoke test (exit 0 = pass) | `(Start-Process <exe> --selftest -Wait -PassThru).ExitCode`, one exe at a time (GUI exe: `&` does not wait, and parallel runs collide on `_selftest.*`) |
| Render editor/trim UI to PNGs | `pwsh -NoProfile -File tools\ui-shots.ps1` (`-Readme` refreshes `docs\images`) |
| Capture benchmark | `pwsh -NoProfile -File tools\measure-capture.ps1` |
| Failure regression checks | `pwsh -NoProfile -File tools\test-reliability.ps1` |

Current installed state, final evidence and remaining acceptance/release work:
[docs/HANDOFF-2026-10-29.md](docs/HANDOFF-2026-10-29.md). Do not infer public
release availability from the assembly/source version.
Historical public 0.9.1 verification: [docs/RELEASE-0.9.1.md](docs/RELEASE-0.9.1.md).
Screenshot shortcut ownership and native app removal boundaries:
[docs/SCREENSHOT-OWNERSHIP.md](docs/SCREENSHOT-OWNERSHIP.md).
Windows capture default candidate, security boundary and owner selection:
[docs/WINDOWS-CAPTURE-DEFAULT.md](docs/WINDOWS-CAPTURE-DEFAULT.md).
Heavy-load responsiveness implementation (`88824f7`) and verification:
[docs/PERFORMANCE-2026-10-13.md](docs/PERFORMANCE-2026-10-13.md).
Read before any performance work: the lag was the cold path (first snip
after idle), not warm speed; warm benchmarks do not show it.
[docs/PERFORMANCE-2026-10-29.md](docs/PERFORMANCE-2026-10-29.md).
Optional ADE screenshot attachment requires a capture-protocol-v1 receiver;
see [docs/ADE-INTEGRATION.md](docs/ADE-INTEGRATION.md). Do not infer live
integration from the WinSnipper version alone.

## Invariants

- Never publish into `dist` by hand. The keep-alive task relaunches the exe
  every 2 minutes and a raw publish fails on the locked file;
  `winsnipper.ps1 build` stands the task down for the publish.
- `dist` is a junction to `%LOCALAPPDATA%\Programs\WinSnipper` on the SSD.
  Never replace it with a real folder: `B:` is a saturated hard drive and an
  idle app reloads evicted code from its exe. `winsnipper.ps1 build` keeps it.
- Startup must normalize memory and I/O priority, not only the CPU class:
  the keep-alive task launches with memory priority 2 and Low I/O priority.
- Never test UI by driving the maintainer's desktop: no synthetic input, no
  windows or popups on screen. Render offscreen with `tools\ui-shots.ps1`
  and leave the hands-on pass to the maintainer.
- No confirmation dialogs in the snip → annotate → paste loop. Closing the
  editor saves silently and refreshes the clipboard.
- Everything drawn in the editor goes on the `Ink` canvas through the shared
  undo stack and must survive `Composite()`, a 96-DPI render of `Surface`
  (1 DIP = 1 image pixel). Anything that must not reach the export lives
  outside `Surface`.
- `src/Theme.xaml` is merged in `App.xaml`, so a XAML error there (a duplicate
  key, say) crashes startup. Run `--selftest` after touching it.
- Icons are stroke geometries in `Theme.xaml`, never symbol-font glyphs; those
  rendered at mismatched sizes and depend on the fonts an install has.
- Windows excluded from capture render black to GDI-style capture. Border
  strips stay outside the region. The recording HUD must stay on a real monitor:
  outside the recording when possible, inside for fullscreen. Only WGC may
  exclude an embedded HUD. Clear affinity synchronously before a GDI fallback;
  compatibility mode may include controls in the video, never hide them offscreen.
- The keyboard hook runs on its own thread, never the UI thread. Windows
  delivers LL hook callbacks on the installing thread; behind a busy UI thread
  the callback exceeds `LowLevelHooksTimeout` and Windows hands the keystroke
  to the shell — the built-in Snipping Tool opening over us is exactly that.
- Selector pixels live in one reused section that WPF reads in place through
  one UI-thread `InteropBitmap` (re-blit, then `Invalidate()`). Never create a
  new desktop-sized WPF bitmap per snip: WPF answers each with a full GC. Never
  blit while a selector shows the surface. Anything that outlives the selector
  takes `ScreenCapture.CopyRegion`; a frozen `CroppedBitmap` over the surface
  would freeze the surface itself. Hidden selectors keep the surface as their
  `Source`: `Invalidate()` only reaches a bitmap some visual holds, and a
  detached one reshows its GPU copy of an older screen (the black-monitor bug).
  Off-screen windows are not rendered; `test-hotkey.ps1` checks this cloaked.
- In the snip path, start the clipboard copy before any disk access and keep
  disk I/O off the UI thread. The save folder can be a sleeping hard drive.
- Screenshot key routing may write
  `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingTool`. Record its old
  value before changing it; the checkbox and `winsnipper.ps1 uninstall` restore
  it. Startup separately owns the WinSnipper Run value and, for an opted-in
  elevated installation, its existing keep-alive task's enabled state.
- Capture registration writes only its own HKCU capabilities,
  RegisteredApplications entry and WinSnipper.ScreenClip ProgID. Never write
  UserChoice/hash or claim registration makes it default. The owner chooses in
  Windows Settings. Activation verifies executable/user SID, accepts bounded
  intents only and transfers no paths or images across elevation.
- Interop: vtable placeholders are named `ReservedN`, never `_VtblGap*` (the
  runtime reads that prefix as a gap directive). Verify GUIDs against the
  Windows SDK headers.
- Never block on WinRT async from the UI thread; await it.
- Keep screenshot capture on `CaptureWorker`, including startup warmup.
  The reusable screenshot selector finishes with `Hide`, never `DialogResult`
  (which destroys its HWND). Refresh selection/monitor state between shows,
  but keep the surface attached. `IsOpen` includes the pending capture reservation, not
  the idle hidden HWND. Recording uses the same opaque cached-selector lifecycle,
  showing the shared surface only while selecting. Coalesce pointer drawing to one frame.

## Release

1. Bump `<Version>` in `WinSnipper.csproj`; date the CHANGELOG section.
2. `pwsh -File tools\winsnipper.ps1 build -Flavor both`, then `--selftest`
   both `dist` exes.
3. `packaging/winget/*.yaml`: version, `InstallerUrl`, `InstallerSha256` of
   `dist\WinSnipper.exe`, `ReleaseNotesUrl`.
4. Commit and push, `git tag vX.Y.Z`, push the tag, then
   `gh release create vX.Y.Z dist\WinSnipper.exe dist\WinSnipper-OCR.exe`.
5. Download the release asset and check its SHA256 against the manifest.

The app's daily update check compares its assembly version with the latest
release tag, so a release reaches existing installs as a tray notice. Winget
community distribution is separate: check the live catalog before claiming
a version is available. Any Microsoft CLA signing is an owner-only step;
do not infer current account approval from an old closed PR's labels.
