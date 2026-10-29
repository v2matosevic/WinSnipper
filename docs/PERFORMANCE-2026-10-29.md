# Cold-path lag diagnosis, 2026-10-29

Investigation only. No application code changed, nothing installed, the running
app (PID 55052, 0.9.2 OCR, up 46 h) was inspected and never stopped. Earlier
rounds: [September](PERFORMANCE.md), [October 13](PERFORMANCE-2026-10-13.md).

## Answer

WinSnipper is fast when it is already in memory and slow when Windows has pushed
it out. On this PC that happens after roughly 15 idle minutes. The machine is
overcommitted (64 GB committed on 48 GB of RAM), so an idle tray app is one of
the first things evicted. Getting it back means reading its memory back from disk,
and three things make that slow:

1. Every screenshot triggers a full .NET garbage collection, which touches the
   whole heap and drags it back from the pagefile at the moment of the hotkey.
   WPF itself triggers this for each new desktop-sized bitmap; our own
   `GC.AddMemoryPressure` call is not the only cause.
2. The installed exe runs from `B:`, a 3 TB hard drive that is saturated by the
   rest of `B:\Coding` (about 150 ms per I/O with 9 to 26 requests queued at
   inspection). Whatever Windows reloads from that file waits in that queue.
3. Screenshots save to `D:`, a hard drive the power plan spins down after 20
   idle minutes. After a selection, the UI thread checks for the file on `D:`
   before it starts the clipboard copy, and the editor waits for the save.

The earlier rounds optimised work done by a warm process (capture speed, window
reuse, threads, priority). That path is already fast. None of them addressed
residency, and every benchmark ran warm, which is why the fixes measured well
and the real experience did not change.

## Evidence

### Fast when warm, slow when cold

All `session.log` screenshots since the October 13 fix build (301), bucketed by
time since the previous snip or editor open in the same process:

| Idle before the snip | n | Median | p90 | Over 1 s |
|---|---:|---:|---:|---:|
| Under 2 min | 162 | 153 ms | 221 ms | 1% |
| 2 to 15 min | 42 | 162 ms | 830 ms | 10% |
| 15 to 60 min | 42 | 500 ms | 7,497 ms | 38% |
| Over 60 min | 42 | 615 ms | 3,216 ms | 29% |
| First after app start | 13 | 453 ms | 796 ms | 0% |

The startup warm-up works (first snip after start is fine); it cannot help an
hour later because the memory it warmed has been evicted.

Editor opens, by time since the previous editor open: under 15 min, median 86 ms
and p90 129 ms (n=51); 15 min to 3 h, p90 2,400 ms (n=25); over 3 h or first,
p90 7,662 ms (n=23). Worst today: 11,570 ms at 16:07. These exclude the save
wait described below, so real editor delays can be longer.

Worst snips: 66.9 s on Oct 29 02:35 (31.1 s of GC pause, hidden before it ever
rendered) and 36.0 s on Oct 25 02:39 (27.3 s before the app even saw the key).

### Every stage stalls, not one

The 33 snips over one second spent their 327 s like this, including the delay
between the key event and the application handler:

| Stage | Share |
|---|---:|
| Key event to handler (`input-age`) | 23% |
| UI thread picks up the request | 18% |
| Garbage collection pause | 23% |
| Screen copy itself, excluding GC | 13% |
| Window preparation | 5% |
| First render | 15% |
| Capture worker wake-up | 2% |

Code that normally takes microseconds (a thread handoff) took seconds. That
pattern means memory being read back from disk, not a slow algorithm.

### Every screenshot forces a full collection

291 of the 301 traced snips contain a gen2 collection. Isolated harness
(scratch project linking the real `src/ScreenCapture.cs`, no windows, no input,
nothing saved), in-process GC events, captures 1.2 s apart:

| Variant | Gen2 collections | Median capture |
|---|---:|---:|
| Shipped: new frozen desktop bitmap per capture | 6 of 6 | 60 ms |
| Same code with our `AddMemoryPressure` removed | 5 of 6 (9 of 10 in a second run) | 55 ms |
| One reused `InteropBitmap`, re-blit + `Invalidate()` | 0 of 6 | 57 ms |
| Reused bitmap + owned 1600x900 crop copy + PNG encode | 1 of 6 | 81 ms |

All were background gen2 GCs with reason "induced, not forced". Warm, their
pause is about a millisecond. With the heap paged out they paused 2 to 31 s in
real use, and the concurrent marking competes with the overlay for page-ins.
Removing our pressure line alone is not a fix, and it would let 25 MB sections
pile up until a natural collection. The fix is one reused capture surface plus
an owned copy of the selected region; that changes the "never return an
unfrozen capture" invariant in `CLAUDE.md` deliberately.

### The machine

Sampled 16:10 to 16:25: 47.9 GB RAM, 64.2 GB committed of an 85.8 GB limit,
6.5 GB available, pagefile on `C:` at 11.2 GB used (peak 16.6 GB), 320 to 490
pages read per second, CPU 33 to 59%. 128 `node` processes hold 9.0 GB private;
about ten `claude` processes hold roughly 0.8 GB each. SysMain is disabled.

| Drive | Hardware | Role | Measured |
|---|---|---|---|
| `C:` | Kingston A400 SATA SSD | OS, .NET/WPF runtime, pagefile | about 1 ms per I/O |
| `B:` | Toshiba HDWD130 3 TB HDD | `dist` exe and all of `B:\Coding` | 92 to 175 ms per I/O, queue 9 to 26 |
| `D:` | Seagate ST8000VN004 8 TB HDD | `SaveDir` = `D:\Pictures\WinSnipper` | idle, 0 transfers in 12 s |

Power plan: High performance, hard disks turned off after 1,200 s (20 min).

How much of the reload comes from the exe on `B:` versus the pagefile on `C:`
was not measured; that needs an elevated ETW trace or a run from the SSD.

### After the selection

`SnipManager` calls `NextSnipPath()` on the UI thread, which runs `File.Exists`
on `D:`, and only then starts `Util.SetClipboardAsync`. If `D:` has spun down,
the clipboard and thumbnail wait for spin-up, so an immediate paste can still
get the previous clipboard. `FloatingThumb.OpenEditor` awaits the PNG save (up
to 10 s) before creating the editor, although it already holds the image. None
of this is in the trace, which ends at the overlay's first render.

### Footprint

PID 55052 after 46 h, 54 snips, 17 editors and one recording: 443.5 MB private,
195.5 MB working set (peak 295.7 MB), 847 handles, 33 threads, 87.8 s total CPU.
Idle CPU is negligible; WinSnipper is not what slows the PC. Earlier
observations were 130 to 140 MB private after a few snips. What grew is unknown:
the app runs elevated, so a non-elevated shell cannot attach a dump or counters.

While the process is stalled, its low-level keyboard hook still sits in front of
every keystroke on the machine, so typing elsewhere can be held for up to the
system hook timeout per key. One trace shows the hook receiving a key 4.3 s late.
How often this was noticeable is unknown.

## Rewrite?

Not now. The language does not make the warm path slow; it is already 153 ms.
The cold path is slow because of how much memory has to come back and from
where. A native rewrite would shrink that and remove the collector, but it
would run from the same saturated `B:` drive and save to the same sleeping `D:`
drive, and it costs weeks while re-solving DPI, multi-monitor, recording and
capture-default work that already functions. Fix the identified causes first and
measure. If the cold bucket stays bad, rebuild only the always-running part
(hook, capture, selection, clipboard, thumbnail) natively and keep the editor,
recorder and trim window.

## Recommended order

1. Owner decisions, environment: run the installed copy from the SSD instead of
   `B:\Coding\WinSnipper\dist` (installer change, elevated task update, app
   restart), and either save to an SSD folder or stop hard disks sleeping
   (`powercfg /change disk-timeout-ac 0`, a system setting).
2. Code: reuse one capture surface so screenshots stop forcing collections;
   start the clipboard copy before any disk access and keep disk I/O off the UI
   thread; let the editor open from memory without waiting for the save; set a
   hard minimum working set sized from the measured hot set
   (`SetProcessWorkingSetSizeEx` with `QUOTA_LIMITS_HARDWS_MIN_ENABLE`); find
   the private-memory growth. Add post-selection clipboard/save markers.
3. Accept on real use, not warm benchmarks: the 15 to 60 minute bucket above
   should approach the under-2-minute bucket (p90 near 250 ms, at most a few
   percent over 1 s) over several days of normal work.
4. Only if step 3 fails: native always-running core.

## Unknowns

The split between `B:` reads and pagefile reads; whether pinning the working set
holds under this machine's peak pressure; GPU contention during first render;
what makes up the 443 MB; how often `D:` is actually asleep at a snip.
