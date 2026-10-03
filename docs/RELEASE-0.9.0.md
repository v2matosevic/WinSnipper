# WinSnipper 0.9.0 release and installation

Release preparation, 2026-10-03. Marko authorized the stable local installation, public GitHub release and final documentation. This record will be completed with exact source, build, install and download verification after those actions succeed.

## Included changes

Capture history adds search, type filters, previews and reopening of screenshots/recordings. Protected PNG commits and asynchronous save/clipboard recovery keep failures visible and preserve existing captures. Recording completion errors suppress success output, and concurrent stop requests share completion. The optional ADE attachment client stages final screenshots in named agent drafts without submitting turns.

The optional integration requires a compatible capture-protocol-v1 Hephaestus receiver. It is absent from installed ADE 0.0.104 and the current public 0.0.108 package; those applications are not updated by this release. See [the public integration guide](ADE-INTEGRATION.md).

## Verification and delivery

Source, artifact hashes, build/smoke/regression results, installed process/watchdog status and GitHub asset download verification will be recorded here. No claim of native sleep/resume, extended recording or owner hands-on acceptance follows from the headless checks.

## Publication scope

Both standalone WinSnipper editions are published as GitHub release assets. Local installation uses `tools/winsnipper.ps1 build -Flavor both` and preserves the existing OCR flavor, autostart and keep-alive arrangement. Winget manifests are updated and validated; submission to the community repository remains a separate Microsoft CLA step.
