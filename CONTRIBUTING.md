# Contributing

Thanks for the interest! WinSnipper is intentionally small — a single .NET 8
WPF project with no external packages. Please keep it that way: prefer in-box
APIs over NuGet dependencies, and simplicity over configurability.

## Build & run

```powershell
dotnet build -c Release                      # lite flavor (net8.0-windows)
dotnet build -c Release /p:EnableOcr=true    # OCR/WinRT flavor (net8.0-windows10.0.22621.0)
.\bin\Release\net8.0-windows\WinSnipper.exe
```

Single-file publish (what releases ship):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false `
  /p:PublishSingleFile=true -o dist/lite
dotnet publish -c Release -r win-x64 --self-contained false `
  /p:PublishSingleFile=true /p:EnableOcr=true -o dist/ocr
```

Smoke test (screenshot + OCR + a 2 s screen recording + a trim round-trip;
exit code 0 = pass):

```powershell
.\WinSnipper.exe --selftest
# writes _selftest.png/.txt/.mp4/_selftest_trim.mp4 to the snips folder;
# on failure also _selftest_error.txt. Recording diagnostics land in
# %APPDATA%\WinSnipper\recorder.log.
```

Debug helpers: `--trim <file.mp4>` opens the trim editor directly;
`--thumbdump <file.mp4>` dumps filmstrip frames as PNGs next to the file.

## Code layout

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the component map.

## Ground rules

- One feature per PR, keep the diff tight.
- UI changes follow the existing design language: dark, frameless, rounded,
  one accent color, no dialogs that interrupt the flow.
- Anything that adds a confirmation prompt to the snip→annotate→paste loop
  will be rejected — frictionlessness is the product.
- New editor elements must go through the shared undo/redo stack and render
  correctly through `Composite()` (save/copy/crop all rely on it).
