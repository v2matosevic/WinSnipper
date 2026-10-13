# WinSnipper 0.9.1

Release preparation authorized by the owner on October 13, 2026. This record
will be completed with exact source, artifact and public download evidence
after publication. The preceding local installation used version 0.9.0 with
the same responsiveness changes; 0.9.1 gives those changes a distinct public
version. [Implementation evidence](PERFORMANCE-2026-10-13.md).

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
version. Release binaries will be smoke-tested again after building.

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

Publish both single-file Windows x64 editions as a stable GitHub release,
update the repository documentation and winget manifest version/URL/hash, and
verify unauthenticated public downloads against the exact built assets.
Winget community submission remains separate from preparing the manifests;
the existing Microsoft CLA requirement has not been completed by this release.
