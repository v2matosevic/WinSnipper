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
| Running? Which build? | `pwsh -File tools\winsnipper.ps1 status` |
| Smoke test (exit 0 = pass) | `<exe> --selftest` |
| Render editor/trim UI to PNGs | `pwsh -NoProfile -File tools\ui-shots.ps1` (`-Readme` refreshes `docs\images`) |
| Capture benchmark | `pwsh -NoProfile -File tools\measure-capture.ps1` |

## Invariants

- Never publish into `dist` by hand. The keep-alive task relaunches the exe
  every 2 minutes and a raw publish fails on the locked file;
  `winsnipper.ps1 build` stands the task down for the publish.
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
- Windows excluded from capture render black to GDI-style capture. HUD and
  border windows must never overlap the recorded region.
- The keyboard hook runs on its own thread, never the UI thread. Windows
  delivers LL hook callbacks on the installing thread; behind a busy UI thread
  the callback exceeds `LowLevelHooksTimeout` and Windows hands the keystroke
  to the shell — the built-in Snipping Tool opening over us is exactly that.
- A capture's pixels live in an unmanaged section WPF reads in place. Never
  return an unfrozen capture, and never let the section handle and its bitmap
  come apart — `ConditionalWeakTable` is what ties them.
- `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingTool` is the only
  thing the app writes outside its own folder. Record the old value before
  changing it; the settings checkbox and `winsnipper.ps1 uninstall` restore it.
- Interop: vtable placeholders are named `ReservedN`, never `_VtblGap*` (the
  runtime reads that prefix as a gap directive). Verify GUIDs against the
  Windows SDK headers.
- Never block on WinRT async from the UI thread; await it.

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
submission stays blocked until the maintainer signs the Microsoft CLA; that is
a human step.
