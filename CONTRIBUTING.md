# Contributing

Thanks for the interest! WinSnipper is intentionally small — a single .NET 8
WPF project with no external packages. Please keep it that way: prefer in-box
APIs over NuGet dependencies, and simplicity over configurability.

## Build & run

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows10.0.19041.0\WinSnipper.exe
```

Single-file publish (what releases ship):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false `
  /p:PublishSingleFile=true -o dist
```

Smoke test (captures the screen, saves a PNG, runs OCR, exits):

```powershell
.\WinSnipper.exe --selftest
# writes _selftest.png / _selftest.txt to the snips folder
```

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
