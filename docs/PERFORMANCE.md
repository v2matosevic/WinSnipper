# Capture latency, 2026-09-11

Marko reported seconds between the capture hotkey and usable selection, plus
a delay opening the annotation editor from a thumbnail.

## Implemented

Screenshot capture now uses an opaque 32-bit GDI bitmap and copies its locked
pixels directly into WPF. It no longer allocates an intermediate HBITMAP or
converts the opaque desktop through an alpha image. The returned image still
owns its pixels, is frozen, and uses physical screen coordinates.

Timing lines in `%APPDATA%\WinSnipper\session.log` measure:

- `input-age`: Windows keyboard-event age when the hook queues the action.
- `dispatched`: elapsed time until the UI dispatcher runs the action.
- `captured`: elapsed time after screenshot preparation.
- `constructed`: elapsed time after constructing the overlay or editor.
- `rendered`: elapsed time at the window's first `ContentRendered` event.

All elapsed stages start at hook handling or thumbnail opening. Input age is
separate. Logging happens on a worker after rendering, not in the keyboard hook.
The existing bounded session log is reused; no screenshot content is logged.
First-render timing does not prove when a person could first interact.

## Evidence

Run `pwsh -NoProfile -File tools/measure-capture.ps1` for an isolated .NET 8
benchmark using the app's PerMonitorV2 manifest. It compares current capture
source with baseline commit `423b3c2806113d2ace9de687486923b8ae4c80ab`.
It opens no windows, sends no input, and saves no screenshots.

On this machine, the virtual desktop was 5760 × 1080, origin (-1920, 0),
WPF render tier 2. Twelve interleaved warm samples per implementation gave:

| Capture preparation | Baseline | Updated |
| --- | ---: | ---: |
| Median | 142.8 ms | 101.1 ms |
| Range | 103.4–211.9 ms | 70.3–132.6 ms |

This is a 29% median reduction in capture preparation, not a measurement of
total hotkey latency. Initial PowerShell-hosted samples used a different DPI
context and are not the primary comparison.

The benchmark verified frozen pixel ownership after GDI disposal, capture
dimensions, opaque alpha, crop/PNG pixel round-trip, and unchanged GDI handle
count (5 before and after 24 captures). Both published flavors passed the
existing `--selftest` screenshot/OCR/recording/trim round-trip (exit 0).

Marko tested the installed build and reported "Noticeably faster now" for the
hotkey selection (2026-09-11). Its real interaction logs showed:

| Operation | Capture | Constructed | First render |
| --- | ---: | ---: | ---: |
| First snip after restart | 121.9 ms | 439.0 ms | 802.6 ms |
| Next snip | 111.3 ms | 115.4 ms | 234.6 ms |
| Next snip | 69.0 ms | 71.2 ms | 264.9 ms |
| Recording selection | n/a | 3.1 ms | 182.4 ms |

These samples queued on the dispatcher in 0.2–0.5 ms. They establish an
improvement in the tested session, not the cause of every earlier multi-second
stall. The first window still has a measurable initialization cost. Editor
timings are collected separately from the thumbnail click; the capture change
does not by itself establish an editor-opening improvement.

The concurrent editor-UI agent reported an isolated offscreen Release OCR
harness result with a 1280 × 760 image: first editor construction 152 ms and
first render 414 ms; next two constructions 14/7 ms and renders 77/62 ms.
This is peer-reported harness evidence, not a timing of Marko's thumbnail click.
It did not reproduce a multi-second editor delay. No speculative preloading or
publish-format change was added on that evidence.

After the final local build, Marko also confirmed "Editor opens quickly now".
The corresponding real thumbnail click logged construction at 43.4 ms and
first render at 123.5 ms. The screenshot preceding it logged zero input age,
1.0 ms dispatcher delay, and 812.0 ms first render after the restart. Both
flavors passed `--selftest` again after the final instrumentation changes.
The updated OCR executable is installed in the existing repo `dist` location
and running. No GitHub release was published. Confidence is high in the
measured capture improvement and these accepted interactions; the precise cause
of every earlier multi-second delay remains unknown.
