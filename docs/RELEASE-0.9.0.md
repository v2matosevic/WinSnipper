# WinSnipper 0.9.0 release and installation

Installed on Olympus and [published as GitHub's latest stable release](https://github.com/v2matosevic/WinSnipper/releases/tag/v0.9.0) on 2026-10-03. Marko authorized the local update, public publication and final documentation in this conversation. Confidence is high in the verified delivery state; owner hands-on acceptance remains separate.

## Included changes

Capture history adds search, type filters, previews and reopening of screenshots/recordings. Protected PNG commits and asynchronous save/clipboard recovery keep failures visible and preserve existing captures. Recording completion errors suppress success output, and concurrent stop requests share completion. The optional ADE attachment client stages final screenshots in named agent drafts without submitting turns.

The optional integration requires a compatible capture-protocol-v1 Hephaestus receiver. It is absent from installed ADE 0.0.104 and the current public 0.0.108 package; those applications are not updated by this release. See [the public integration guide](ADE-INTEGRATION.md).

## Verification and delivery

The release tag `v0.9.0` resolves to `a8a78f4b0dc1850c408972a66a700ee6885208c4`, the source commit embedded in both executables' product version. The versioned source includes history `549e39a`, reliability `d802e85` and the peer's completed integration client `1e4b9e8`. The peer confirmed that commit was ready and had no unfinished WinSnipper work. Manifest/size documentation followed in `ed710d5`; it is not the binary tag target.

| Asset | Bytes | SHA256 |
| --- | ---: | --- |
| WinSnipper.exe | 865436 | `4E7EF4701119A5135E8B1A3BF95E9600F8BA68AD099220AF7977A75B655678AA` |
| WinSnipper-OCR.exe | 27506540 | `9ADA8C9D14774EA8F928657F4AB52180E67F5831D01DF5ED5BEB4DF476567AD3` |

Both exact `dist` executables passed `--selftest` with exit 0, sequentially, covering screenshot/hook startup, OCR where supported, recording and trim. The release-source reliability harness passed all 24 fault checks. The capture-picker offscreen render passed and was inspected. Earlier hotkey, editor/trim/history and real provider/receiver boundary evidence applies to unchanged relevant source, with provenance in [reliability](RELIABILITY.md), [history](CAPTURE-HISTORY.md) and [integration](ADE-INTEGRATION.md).

The supported supervisor build command replaced the previous PID18168 and started the OCR edition as PID11492. Status reports `0.9.0.0`, the expected `B:/Coding/WinSnipper/dist/WinSnipper-OCR.exe`, the keep-alive task in Ready state and the existing autostart path. Settings SHA256, the PrintScreen registry binding and autostart are unchanged. All 1503 pre-existing captures retain their byte length and modification time; self-test outputs are excluded. The preservation check compares UTC ticks because PowerShell converts ISO JSON timestamps into DateTime values.

The public release was published at `2026-10-03T14:48:19Z`. GitHub's latest-release API reports v0.9.0, draft=false and prerelease=false. Both uploaded assets were downloaded and matched the local hashes; both final public URLs were downloaded again without authentication and matched. The public GitHub page was inspected in the browser. The winget installer hash matches the lite asset, and `winget validate --manifest packaging/winget` succeeded; the external .NET runtime dependency is not checked by that validator.

Local verification records, rollback copies and the page screenshot remain under `artifacts/release-0.9.0` (gitignored). No private screenshot, outbox record, service token or settings file was published. No claim of native sleep/resume, extended recording or owner hands-on acceptance follows from the headless checks.

## Publication scope

Both standalone WinSnipper editions are published as GitHub release assets. Local installation uses `tools/winsnipper.ps1 build -Flavor both` and preserves the existing OCR flavor, autostart and keep-alive arrangement. Winget manifests are updated and validated; submission to the community repository remains a separate Microsoft CLA step.

The requested release work is complete. No source defect or required release gate remains open for this version. The unavailable ADE receiver is documented compatibility scope, not a failed standalone feature. No other app, agent terminal or background service was closed or updated. Existing peer-owned verification services remain outside this release task.
