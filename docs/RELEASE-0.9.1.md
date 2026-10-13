# WinSnipper 0.9.1

Installed locally and [published as GitHub's latest stable release](https://github.com/v2matosevic/WinSnipper/releases/tag/v0.9.1)
on October 13, 2026, following the owner's authorization. The preceding local
installation used version 0.9.0 with the same responsiveness changes; 0.9.1
gives those changes a distinct public version. Confidence is high in the
verified delivery state. [Implementation evidence](PERFORMANCE-2026-10-13.md).

## Source and exact artifacts

The annotated `v0.9.1` tag resolves to `af05437c2fee5f53e2c80d79b15323b551fa211d`,
the source revision embedded in both executables' product version. Manifest and
size documentation followed in `0c931f7`; that commit is not the binary tag target.
The implementation is `88824f7`, with initial local-install evidence in `db24e02`.

| Asset | Bytes | SHA256 |
| --- | ---: | --- |
| WinSnipper.exe | 898204 | `B2D83F0D5284070AF42ADDC71C1236D2C63F3F15D05F8719ED2B0668BEEE8D2B` |
| WinSnipper-OCR.exe | 27535212 | `A47515D2C3093364A219C6A7A9A7B50906F46129E80E24E6C630869D376193CF` |

The public release was published at `2026-10-13T17:11:55Z`. GitHub's latest-release
API reports `v0.9.1`, draft=false and prerelease=false. Both assets' API digests
match the built files. Both final public download URLs were downloaded without
authentication and matched the hashes above. The source branch and source tag
were pushed after agent-coord's review found only this session's completed work.

## Local installation

The supported `tools/winsnipper.ps1 build -Flavor both` command rebuilt both
single-file ReadyToRun editions, stopped the earlier OCR instance and started
PID 62336. Status confirms version 0.9.1.0, the October 13 19:06 build at
`B:/Coding/WinSnipper/dist/WinSnipper-OCR.exe`, the existing autostart target
and a Ready keep-alive task. The session's explicit WinSnipper restart approval
was already present; no other app or service was stopped.

Settings SHA256, the PrintScreen registry value and autostart target are unchanged
across the release build. Before the release smoke tests, 1600 existing PNG/MP4
capture files were inventoried; all retained their sizes and UTC modification
ticks after the tests. Self-test outputs are excluded. These metadata checks do
not claim a full content-hash audit of every capture.

Private verification fixtures, rollback files and downloaded public assets are
under `artifacts/release-0.9.1` (gitignored). Only the two standalone executables
were uploaded as release assets; no settings, captures or diagnostic logs were
published as assets.

## Changes

Screenshot selection keeps one native window between captures, clears idle
pixels and resets selection mode, cached window targets and monitor bounds.
Native capture and its startup warmup run on a dedicated bounded worker;
selection is reserved while pixels are pending. Hotkey handoff has a separate
bounded worker rather than sharing the thread pool. Pending-request and
priority scopes span asynchronous capture and recover on failure.

Per-show timing covers reused windows and separates hook handoff, native
capture work and UI resume. Completed handlers detach, and the idle capture
worker releases its last bitmap.

Both standalone editions retain the existing history, editor, recording and
trim workflow. The OCR edition adds Windows OCR and Windows.Graphics.Capture.
Windows 10/11 and the .NET 8 Desktop Runtime are required.

## Verification scope

The October 13 implementation passed 11 shortcut/capture test groups and 24
reliability checks, both-flavor builds, sequential exact-binary smoke tests
and the capture pixel/resource benchmark. The source, dependencies and
configuration relevant to those checks are unchanged apart from the release
version. Both exact versioned `dist` executables passed sequential `--selftest`
with process exit codes explicitly awaited (0). The 11 shortcut/capture test
groups and all 24 reliability cases were rerun for 0.9.1 and passed. The unchanged
native capture benchmark's October 13 result is reused: median 67.078 ms,
validated frozen pixels/crop/PNG/opacity, GDI handles 5/5, process handles 270/268,
and 10 MB private memory after retention checks. That is capture preparation,
not an end-to-end hotkey measurement. No unrelated editor/trim UI runtime audit
was repeated because their relevant sources are unchanged.

The modal-reuse test shows a small nonactivated window far offscreen, sends no
input and checks acceptance/cancellation, state reset, physical bounds, idle
pixel clearing and per-show tracing. The real background-capture check uses
the shipped DPI manifest and produces frozen UI-readable desktop pixels.

One real shortcut in the prior local fix build logged 286.1 ms to its render
marker at 19:03:09 on October 13; its editor opened to the render marker in
181.2 ms. Workload was not measured alongside that sample, so it is not a
controlled heavy-load result or an old/new comparison.

These checks do not prove visible shortcut latency under every heavy workload,
native focus/sleep/resume behavior or the absence of OS-wide stalls. No GPU
recording pipeline refactor is included, and no universal speed guarantee is
made. Optional ADE screenshot attachments still require a compatible
capture-protocol-v1 receiver; see [requirements](ADE-INTEGRATION.md).

## Publication scope

Both single-file Windows x64 editions are available in the stable GitHub release.
The README, changelog, architecture/contributing guidance and performance record
document the delivered changes. Winget manifests target 0.9.1, the public lite
URL and its exact hash; `winget validate --manifest packaging/winget` passed.
The validator does not check the external .NET runtime dependency. Community
winget submission remains separate: the existing Microsoft CLA requirement
has not been completed by this release, so WinSnipper is not claimed to be
available from the winget community repository.
