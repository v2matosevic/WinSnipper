# Capture latency under load, 2026-09-20

This update addresses two reported symptoms: the Windows Snipping Tool opening
instead of WinSnipper, and captures being "close to impossible" while the
machine is busy. They share a cause.

## The hotkey path

Windows delivers a `WH_KEYBOARD_LL` callback on the thread that installed the
hook. WinSnipper installed it from the WPF UI thread, so every keystroke on the
machine queued behind whatever that thread was doing — building the editor,
laying out the overlay, collecting garbage. Once a callback exceeds
`LowLevelHooksTimeout` (300 ms by default) Windows stops waiting for it,
delivers the keystroke to the shell regardless, and after repeat offences
removes the hook. Delivering Win+Shift+S to the shell **is** the built-in
Snipping Tool opening, so the two symptoms are the same failure.

The hook now runs on a dedicated high-priority thread with its own message
pump and does nothing else. It also rejects any key that matches neither
configured hotkey before reading modifier state, which is most of the work it
used to do on every keystroke in the system.

This part is reasoned from the documented hook contract and is **not measured
here**: verifying it needs synthetic input and a loaded machine, and the
project does not drive the maintainer's desktop. `--selftest` covers that the
hook installs, re-arms and tears down cleanly.

## Capture preparation

The screen is now blitted through a `CreateDIBSection` bitmap into a
`CreateFileMapping` section, and WPF reads that same memory in place via
`Imaging.CreateBitmapSourceFromMemorySection`. The previous path blitted into a
GDI bitmap and then copied every pixel a second time into a fresh
large-object-heap array — about 24 MB per capture at this desktop size.

Run `pwsh -NoProfile -File tools/measure-capture.ps1` for the isolated .NET 8
benchmark. It compares the working-tree capture source against a baseline
revision, opens no windows, sends no input and saves no screenshots.
`-BaselineRevision HEAD` compares against what is committed.

Virtual desktop 5760 × 1080, origin (-1920, 0), WPF render tier 2. Twelve
interleaved warm samples per implementation:

| Capture preparation | Previous | Now |
| --- | ---: | ---: |
| Median | 64.3 ms | 46.1 ms |
| First capture of the run | 144.4 ms | 62.0 ms |

That is a 28% median reduction and a 57% reduction on the first capture. Two
earlier runs of the same harness the same day gave medians of 62.8 → 47.9 ms
and a comparable spread, so the shape is stable. These are capture-preparation
numbers, not total hotkey-to-selection latency.

The harness also verifies, every run: the returned image is frozen, its
dimensions match the reported bounds, its pixels are still readable after the
GDI objects are destroyed, every pixel is opaque, a crop survives a PNG
round-trip unchanged, and the GDI object count is unchanged (3 before and
after 26 captures).

## Memory and handles

Capture pixels now live in unmanaged memory, so the harness asserts they come
back. Across 336 captures in batches of 48, 96 and 192, each followed by a
forced collection and finalization:

| After | Process handles | Private memory |
| --- | ---: | ---: |
| baseline | 269 | — |
| 48 captures | 270 | 12 MB |
| 144 captures | 270 | 12 MB |
| 336 captures | 267 | 11 MB |

Flat. A capture that failed to release its section would show as one retained
handle and roughly 24 MB apiece.

## Selection dragging

The dimmed area outside the selection was a `CombinedGeometry` Exclude, rebuilt
from three fresh objects on every mouse move. It is now a single reused
`GeometryGroup` with `FillRule.EvenOdd` over [whole overlay, selection], in
which a drag only assigns the hole's rectangle. Even-odd over two rectangles
paints exactly what Exclude painted.

Verified offscreen by rendering both versions of the path at 1440 × 810 and
comparing every byte, with no selection and with a deliberately fractional one
(317.5, 128.25, 642.75, 401.5): **0 differing bytes** in both states. Cost of
the per-move work, 2000 moves:

| Per 2000 mouse moves | Previous | Now |
| --- | ---: | ---: |
| Time | 8.4 ms | 1.9 ms |
| Allocated | 1000 KB | 187 KB |

## Where the remaining time is

Real interaction logs from 0.7.0 on the 5760 × 1080 desktop looked like
`captured=70.9ms constructed=72.3ms rendered=199.6ms` — so with capture down to
around 46 ms, **first render is now the largest stage at roughly 130 ms**. That
is the cost of creating a 5760 × 1080 window and its render surface, paid once
per snip because the overlay is constructed fresh each time.

The remaining lever is keeping one overlay alive and showing and hiding it
instead of building a new one. That means giving up `ShowDialog` for `Show` plus
a completion callback, which changes activation, focus and modality for both the
snip and the recording paths. It was not taken here: it is a real refactor with
real regression surface, not a tweak.

## The rest of the change

Measured only as "builds, passes and does not regress the above":

- The process is lifted to High priority from the moment the hotkey fires
  until the selection is done, then dropped back. The boost expires on its own
  after 30 s so an overlay left open cannot pin it.
- The capture path, the selection overlay and the crop/encode/render pipeline
  are exercised once at startup, at idle, with nothing shown on screen and
  nothing written to disk. `--selftest` runs the same warm-up, so a failure in
  it fails the smoke test.
- Published builds are ReadyToRun. Single-file sizes: lite 0.61 MB (from
  0.37 MB), OCR flavor 26.0 MB.
- The snip's PNG is encoded and written on a worker; the thumbnail no longer
  waits for the disk. Everything that hands the path to another program waits
  for that write first.

### First real 0.8.0 sample

The first snip taken on the installed 0.8.0 build, 68 seconds after a restart
and while the machine was busy publishing this release, logged:

```
performance snip input-age=0ms dispatched=1.2ms captured=71.0ms constructed=93.5ms rendered=542.9ms
```

Read against the 0.7.0 samples above, that says the warm-up works on the stage
it can reach and not on the one it cannot. **Construction**, capture to
constructed, was 22.5 ms; on 0.7.0 the first snip after a restart spent 317 ms
there and a warm snip 1.4–4.4 ms, so the overlay was already warm on its first
use. **First render**, constructed to rendered, was 449 ms against roughly
130 ms for a warm 0.7.0 snip — untouched, because the warm-up builds and lays
out the overlay but never creates a window or a render surface.

This is one sample on a loaded machine, so it does not establish a steady-state
figure. It does confirm the split: the remaining cost is the window, and only
overlay reuse addresses it.

The effect of ReadyToRun on real first-snip timing is **not measured**
separately. `session.log` records `input-age`, `dispatched`, `captured`,
`constructed` and `rendered` for every capture, which is where cleaner
evidence would come from in normal use.

Both flavors passed `--selftest` (exit 0) after these changes, which shipped in
[v0.8.0](https://github.com/v2matosevic/WinSnipper/releases/tag/v0.8.0).

# Capture latency, 2026-09-11

This update addresses reported delays between the capture hotkey and usable
selection, and adds diagnostics for opening the annotation editor from a
thumbnail. It was tested in a local installation first and shipped in
[v0.7.0](https://github.com/v2matosevic/WinSnipper/releases/tag/v0.7.0).

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

On the test machine, the virtual desktop was 5760 × 1080, origin (-1920, 0),
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
count (5 before and after 24 captures). Both locally built flavors passed the
existing `--selftest` screenshot/OCR/recording/trim round-trip (exit 0).

The reporter tested the installed build and confirmed noticeably faster
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

An independent editor-UI investigation reported an isolated offscreen Release OCR
harness result with a 1280 × 760 image: first editor construction 152 ms and
first render 414 ms; next two constructions 14/7 ms and renders 77/62 ms.
This is a separately reported harness result, not a real thumbnail-click timing.
It did not reproduce a multi-second editor delay. No speculative preloading or
publish-format change was added on that evidence.

After the final local build, the reporter also confirmed the editor opened quickly.
The corresponding real thumbnail click logged construction at 43.4 ms and
first render at 123.5 ms. The screenshot preceding it logged zero input age,
1.0 ms dispatcher delay, and 812.0 ms first render after the restart. Both
flavors passed `--selftest` again after the final instrumentation changes.
The updated OCR executable was installed in the existing repo `dist` location
and running; no GitHub release existed at that point. Confidence is high in the
measured capture improvement and these accepted interactions; the precise cause
of every earlier multi-second delay remains unknown.
