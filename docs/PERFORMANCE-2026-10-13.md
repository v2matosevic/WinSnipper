# Heavy-load investigation, 2026-10-13

Investigation and authorized local implementation for the owner's report that WinSnipper becomes slow or unresponsive when the PC is heavily used. Source baseline: `5b6057c`; working tree was clean at the start. Installed app remains OCR 0.9.0.0, PID 54540, running since October 9. The fixes below are implemented and locally tested; installation and publication remain separate.

## Implementation after owner authorization

The owner authorized implementation with "go for it" on October 13. The screenshot path now:

- Keeps one hidden screenshot HWND and uses modal `ShowDialog` with `Hide` on acceptance or cancellation. The live recording selector retains its original close behavior. Reuse refreshes monitor bounds, selection, pick mode and hover caches; idle cleanup clears the overlay's screenshot reference.
- Captures on a dedicated STA worker with a bounded queue. Startup's native capture warmup uses this worker too. A slow capture no longer occupies the UI thread. A reservation prevents a recording selector opening while screenshot pixels are pending. Pending hotkeys and bounded priority scopes survive the asynchronous wait and release on failure/completion.
- Hands hotkeys to a dedicated worker rather than the shared thread pool, with at most one queued request per operation. Native callbacks never run subscribers.
- Adds hook age/handoff, capture queue/start/ready/UI-resume, HWND, Loaded, scene-ready and render markers, plus effective priority, render tier and GC counts/pause time. The retained window's per-show render fence is needed because WPF's `ContentRendered` follows `Window.Content` changes rather than every reshow. All per-show handlers detach. Render markers remain UI scene/dispatcher markers, not proof of DWM presentation or usable latency. [WPF Window source](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Window.cs).

Local verification uses `C:/Users/matos/AppData/Local/Temp/WinSnipper-load-verify-c24f3c6acd14460da6b4185f7d9abd10`. Lite and OCR builds have zero warnings/errors. `test-hotkey.ps1` passes 11 groups: blocked-subscriber isolation; repeat/release suppression; separate recording dispatch; priority expiry; pending request coalescing; async pending/boost lifetime; pixel-equivalent prepared overlays; three real offscreen modal shows using one HWND; per-show traces; clearing idle pixels and detached handlers; bounded/fault-recovering capture; collection of the idle worker's last bitmap; manager reservation/error cleanup; actual dedicated STA capture producing frozen UI-readable pixels at `-1920,0,5760,1080`. Some groups cover multiple assertions.

The harness matches the shipped DPI manifest, suppresses normal tray/hook startup, and shows its 320 x 180 modal window far offscreen starting at `-30000,-30000` without activation or input. It switches pick mode and changes the native window origin between shows, verifying reset state and actual `GetWindowRect` bounds. The per-show diagnostic example was 134.7 / 29.9 / 17.2 ms. This is an offscreen first/warm sequence, not an old/new comparison or a promised real-desktop speedup. Both build executables passed `--selftest` with process exit codes explicitly awaited. The 24 save/clipboard/recording reliability cases also passed against the final app source. Fixtures and error-state renders remain in the verification directory.

Both single-file, ReadyToRun `win-x64` candidates were then published into the private verification directory (`package-lite` and `package-ocr`), never `dist`. Each passed `--selftest`, explicitly awaiting its process exit code 0. These are deployment candidates, not installed or public releases. No version bump or GitHub release was made.

The capture benchmark was repeated: current median 67.078 ms (12 warm samples, 58.176–89.776 ms); unchanged legacy baseline 144.076 ms. Pixel, crop/PNG, opacity and resource checks passed; GDI handles 5/5, process handles 270/268 and private memory 10 MB after retention checks. This confirms the unchanged native capture path still passes; the new changes target blocking and window lifecycle, not raw blit throughput.

Confidence is high in the covered behavior and failure cleanup, moderate in reducing the reported stalls. No on-desktop keyboard/focus test under comparable load has been performed. The owner still has the October 3 executable running; no running app or service was stopped. Installing requires the exact command `pwsh -File tools/winsnipper.ps1 build -Flavor both`, which stops and restarts WinSnipper and therefore needs explicit process-stop approval under the supplied global instructions. No GPU recording refactor was made; that remains a separate optimization if recording measurements justify it.

The sections below preserve the evidence and recommendations from the earlier investigation in this task.

## Findings and confidence

**High confidence:** the delay is real, intermittent, and affects multiple stages. It is not explained solely by screenshot capture. The running process has 51 completed screenshot timing entries in the retained log: median 289.5 ms, four over one second, two over five seconds. These timings start when the application subscriber creates its trace and end at WPF `ContentRendered`; they do not measure exact visible or usable latency. Missing/dropped hotkeys and requests that never finish rendering are absent from this sample.

**Moderate confidence:** CPU scheduling and memory pressure contribute. Current counters demonstrate a busy machine, but were not collected at the historical stalls. The existing trace cannot distinguish WPF layout, graphics-driver waits, desktop composition, paging and scheduling within its last stage. It also cannot separate Windows hook delay from thread-pool handoff delay.

**Unknown:** the precise cause of every missed shortcut or freeze, and the speedup any proposed change would produce. No desktop input was injected and no visible selection was opened during this investigation.

## Actual interaction evidence

From `%APPDATA%/WinSnipper/session.log`. The columns below subtract consecutive cumulative stages; input age is separate and is not included in total.

| Local timestamp | UI dispatch | Capture | Construction | Construction to ContentRendered | Trace total |
| --- | ---: | ---: | ---: | ---: | ---: |
| Oct 9, 19:15:49, current process | 0.2 ms | 76.2 ms | 0.1 ms | 4,963.5 ms | 5,040.0 ms |
| Oct 10, 19:08:29, current process | 0.5 ms | 287.2 ms | 0.0 ms | 6,345.4 ms | 6,633.1 ms |
| Oct 9, 18:42:55, current process | 0.2 ms | 1,089.1 ms | 0.5 ms | 195.4 ms | 1,285.2 ms |
| Oct 8, 18:52:06, earlier 0.9.0 process | 90.2 ms | 2,201.1 ms | 79.3 ms | 28,381.4 ms | 30,752.0 ms |

All 122 October completed snips in the retained log have a median of 296.2 ms; 24 exceed one second and 10 exceed five seconds. These are an observed log sample, not a controlled workload or a prediction. The older October 5 sample also contains 429,485 ms of input age and 116,904 ms trace time. It cannot identify the source of that system-scale delay.

Editor opening can also stall: October 8 at 19:11:08 logged 18,045.6 ms construction and 18,354.5 ms total. A fast screenshot backend would not address that constructor stall.

## Current system and isolated capture check

Initial inspection: RTX 3060 Ti; 81% total CPU; 2,433 MB available RAM out of approximately 48 GB; 75% committed-memory use; 733 pages input/sec. WinSnipper itself was at Normal priority with approximately 208 MB working set and 130 MB private memory. This single process snapshot does not prove an app memory leak or its absence.

Five samples at 12:51:24–28 showed 70.8–84.0% total CPU and 2,198–2,382 MB available memory. Pages input/sec ranged from 250.4 to 34,918.9. This counter includes file-backed page reads as well as pagefile reads; it is not proof of pagefile thrashing. Samples overlapped the diagnostic harness, so they are not an uncontaminated baseline. A GPU engine snapshot showed the busiest individual process/engine counter at about 10%; this is neither aggregate GPU utilization nor evidence about earlier freezes.

`pwsh -NoProfile -File tools/measure-capture.ps1` completed with exit 0:

- Physical desktop: `-1920,0,5760,1080`; WPF render tier 2, indicating hardware acceleration capability in the harness.
- Current capture median: 58.534 ms across 12 warm captures, range 51.615–64.337 ms; first capture: 63.864 ms.
- Historical pre-optimization baseline median: 108.971 ms. This comparison confirms the already shipped optimization, not a new improvement from this investigation.
- Frozen pixel ownership, bounds, opacity and crop/PNG round-trip passed.
- GDI handles: 5 before/after; process handles after retention collection: 271 to 269. After 336 retention-check captures, private memory was 11 MB. These results concern the harness, not every allocation in the installed app.

The benchmark does not show the overlay or measure its creation, activation, presentation or interaction under load.

## Why a GPU fallback is not the whole fix

WPF already has a hardware rendering pipeline, with software fallback. This code does not force software rendering; the harness reports tier 2. That does not prove every installed window used GPU rendering during a stall. [Microsoft WPF hardware guidance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-taking-advantage-of-hardware).

The recorder already tries WGC, then Desktop Duplication, then GDI. Its sink writer enables hardware encoders when available. That flag permits hardware transforms; existing logs do not establish which encoder actually ran. [Microsoft hardware-transform attribute](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-readwrite-enable-hardware-transforms).

There is a real remaining GPU opportunity for recording: `WgcCapture` and `DesktopDuplication` copy GPU textures into staging memory and synchronously map them for CPU readback. `ScreenRecorder` then copies the CPU bitmap again and packs each frame into an MF memory buffer. A texture-to-encoder pipeline could remove those transfers. Readback can itself synchronize the CPU and GPU, so changing API names alone does not ensure lower latency. [Microsoft resource-copy guidance](https://learn.microsoft.com/en-us/windows/uwp/graphics-concepts/copying-and-accessing-resource-data).

## Recommended implementation order

1. **Measure the missing stages, then reuse the screenshot overlay's actual window.** Add hook-receipt/handoff timestamps without file IO in the callback, plus HWND initialization, Loaded, first render and a dispatcher heartbeat. Log render capability, effective priority, process GC deltas and contemporaneous memory pressure for slow operations. The current prepared shell saves XAML construction but retains no HWND; `ShowDialog` still creates a window/render surface on every capture, and cleanup closes it. Retaining and hiding one screenshot window targets a recurring cost in the slowest recent samples. The benefit is plausible but unmeasured. Release its screenshot references on completion, reset selection/mode/hover state, refresh monitor/DPI bounds, and derive `IsOpen` from active selection rather than HWND existence. Test focus, cancellation, recording mutual exclusion and multi-monitor changes. Do not retain full desktop pixels while idle.

2. **Keep capture waits off the UI thread and use a bounded interactive handoff.** `SnipManager.StartSnip` currently calls `CaptureVirtualScreen` synchronously on WPF. Run one capture at a time on a dedicated worker, passing the existing frozen bitmap back to the UI and restoring thumbnails on failure/cancellation. A worker prevents a slow native capture from blocking every other app window; it does not make that native capture faster. The hook currently uses the general thread pool before the priority boost is applied. A small bounded dedicated dispatcher can avoid competition with unrelated app tasks. Preserve repeat suppression and per-operation coalescing. Do not move work back inside the native callback, use RealTime priority, or permanently boost the entire process. Existing dedicated hook/recovery code is already present; repeating those old fixes is not a new solution.

3. **Optimize GPU transfer paths where measurements justify it.** For recording, verify the actual selected encoder and move towards D3D texture samples/device-manager integration with software fallback. For screenshots, benchmark WGC/DXGI against existing GDI under the owner's workload before switching defaults; keep multi-monitor, RDP, device-loss and black-content coverage. A GPU pipeline cannot prevent CPU scheduling, paging or an OS-wide stall. If recording is the reported failure, also inspect duplicate-frame catch-up: its inner loop currently ignores `_stop` until all due slots have encoded. Any fix must preserve duration and frame-rate behavior rather than silently truncate the timeline.

Acceptance should measure the slow end of real hotkey-to-usable-selection timings under comparable busy workloads, not just average capture throughput. Use offscreen project harnesses first; the repository forbids automated input or visible UI on the owner's desktop. The final real shortcut/focus check remains a hands-on owner check. Required gates depend on the chosen change: capture benchmark; `test-hotkey.ps1` for dispatch/overlay; `test-reliability.ps1` for saving/completion; both-flavor builds and smoke tests for release. Installing via `winsnipper.ps1 build` stops/restarts the running app and requires the owner's explicit process-stop approval under the current global rules.

Microsoft documents that timed-out low-level keyboard hooks can be silently removed. That is a credible failure mechanism, not proof it caused this report. The app already has a dedicated hook thread and native recovery timer. [Microsoft hook contract](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc).

## Delivery and instruction sources

The initial investigation changed no application code; the subsequent authorized implementation is described above. No installs, releases, synthetic input, clipboard writes or process stops were performed. Repository `AGENTS.md` is absent; the owner-supplied AGENTS instructions and repository `CLAUDE.md` apply. Shared memory and relevant project notes were read; Athena's performance-memory search returned no notes. The missing explicit ADE logo was copied from the existing app icon, without generating a new identity.
