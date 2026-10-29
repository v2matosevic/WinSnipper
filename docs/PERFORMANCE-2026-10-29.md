# Cold-path lag: diagnosis and fix, 2026-10-29

## Delivery state

Implemented, verified locally and installed. WinSnipper 0.9.3 from `dad95ab`
runs from the SSD (`%LOCALAPPDATA%\Programs\WinSnipper`, with `dist` as a
junction). The owner approved the restarts ("do whatever you need to do").
Nothing was published; public stable remains 0.9.1. Installed hashes and
remaining checks: [handoff](HANDOFF-2026-10-29.md). Earlier rounds:
[September](PERFORMANCE.md), [October 13](PERFORMANCE-2026-10-13.md).

## What was wrong

WinSnipper was fast when it was already in memory and slow when Windows had
pushed it out, which on this PC happened after roughly 15 idle minutes. The
machine is overcommitted (64 GB committed on 48 GB of RAM), so idle processes
are trimmed. Five things made getting back slow; the last two were found
during the fix:

| Cause | Fix |
|---|---|
| Every snip created a new desktop-sized WPF bitmap, and WPF answers each with a full garbage collection. On a trimmed process that dragged the whole heap back from disk at the hotkey. Our own `GC.AddMemoryPressure` was not the only trigger. | One shared capture section and one `InteropBitmap` per session, re-blitted and invalidated; selections are copied out (`ScreenCapture.CopyRegion`). |
| The installed exe ran from `B:`, a 3 TB hard drive saturated by the rest of `B:\Coding` (92 to 175 ms per I/O, 9 to 26 queued). | Install on the SSD; `dist` is a junction, so the elevated task and Windows registration keep their paths. |
| Screenshots save to `D:`, a hard drive the power plan spun down after 20 idle minutes, and the UI thread checked `D:` before starting the clipboard copy. The editor waited for the save. | Clipboard first; no disk I/O on the UI thread; name clashes settled on the save worker; editor opens from memory. Disk spin-down on AC power set to never (owner-approved system setting). |
| The keep-alive task launches with Task Scheduler's default priority 7: memory priority 2 and Low I/O priority as well as Below Normal CPU. Startup reset only the CPU class, so Windows reclaimed the app's pages first and its page-ins queued behind all other disk traffic. | Startup normalizes memory priority to 5 and I/O priority to 2. Existing threads follow the process values (tested). |
| Nothing kept the warm path resident. | Hard minimum working set after warm-up (WS + 48 MB, 128 to 320 MB). |

The earlier rounds optimised work done by a warm process, which was already
fast, and every benchmark ran warm. That is why they measured well and the
experience did not change.

## Verification

Builds: both flavors, zero warnings. Suites: `test-hotkey.ps1` 14 groups (new:
repeat captures reuse one surface with no full collection; selection copies
keep their pixels; scheduled-task priorities normalized), `test-reliability.ps1`
29 (new: name clashes, editor waits for a pending first save and uses its name,
failed first save leaves the editor dirty), `test-capture-integration.ps1` 43,
`test-recording-controls.ps1` 40, `test-supervision.ps1` 12 in PS7 and PS5.1
(new: SSD move renames, never deletes, and refuses to merge). Both installed
exes pass `--selftest`. A standard-user `--capture-probe` reached the elevated
app through the junction in 267 ms.

Isolated GC harness (real `src/ScreenCapture.cs`, no windows), captures 1.2 s
apart, in-process GC events:

| Variant | Gen2 collections | Median capture |
|---|---:|---:|
| Old: new frozen desktop bitmap per capture | 6 of 6 | 60 ms |
| Old code with our `AddMemoryPressure` removed | 5 of 6 (9 of 10 in a second run) | 55 ms |
| One reused `InteropBitmap`, re-blit + `Invalidate()` | 0 of 6 | 57 ms |
| Reused bitmap + owned 1600x900 copy + PNG encode | 1 of 6 | 81 ms |

Offscreen soak, 30 full-size cycles (capture, 5760x1080 selector shown far
off screen, accept, copy, PNG): private memory flat at 70 to 93 MB and handles
flat on both old and new paths, so the snip path does not leak. Full
collections fell from 39 to 15; none remain between the hotkey and the
selector (the hotkey suite asserts it), and the rest follow the selection copy.

Live, PID 17444: memory priority 5, I/O priority 2, hard minimum 199 MB
(`flags=0x9`). Warm-up after start took 4 s, against 21 s for the same build an
hour earlier with the task's low priorities: one observation each, not a
benchmark. With low priorities the first build's working set fell from 151 to
55 MB within four minutes despite the 199 MB minimum; with normal priorities it
was 136 MB three minutes after start.

`EmptyWorkingSet` ignores a hard minimum (tested on a throwaway process: 354 MB
to 1 MB), while ordinary trimming left a pinned 354 MB working set alone for 90
s. No memory-cleaner process, service or task was found, and a two-minute
watch of every process saw no system-wide emptying.

## First real use, same evening

The owner used the installed build from 17:48 to 17:55 and reported "seems
much faster now". PID 17444's log for that session:

- 10 snips: 93, 146, 153, 245, 264, 330, 342, 570, 1,514 and 2,300 ms
  (median about 300 ms). None contained a full collection (before: 291 of 301);
  one gen1 paused 0.8 ms. The first, 19 minutes after start-up, took 570 ms.
- 5 editor opens: 248, 248, 255 and 287 ms, and one of 36,467 ms.
- The slow ones stalled outside memory and GC: the 2,300 ms snip in its first
  render (scene-ready at 2,204 ms), the 1,514 ms snip in the screen copy
  (1,110 ms, no GC), and the 36 s editor open inside window creation (built in
  7 ms, HWND at 36,122 ms). Those stages wait on the GPU, the compositor or
  other processes' window hooks. VALORANT was running at 17:57; whether it was
  in the foreground during these stalls is unknown. Next suspect, not proven.
- Footprint: each editor open added roughly 60 to 90 MB of private memory
  (432, 490, then 573 MB), partly released afterwards; the snip path does not
  grow it. Private memory was 424 MB and working set 407 MB at 17:57. The editor
  is where the old process's growth most likely came from.

Ten snips cannot show the long tail. The multi-day comparison below still decides.

## How to judge it in real use

The deciding number is the first snip after idle. In
`%APPDATA%\WinSnipper\session.log`, compare snips that come 15 to 60 minutes
after the previous snip or editor open against the table below (before the
fix). The target is close to the under-2-minute row. Each timed line now
also carries `ws=` and `private=`; the startup line carries the priorities and
the next line the residency result.

## Unknowns

Whether the cold bucket now matches the warm one over days of real work (only
real use can show it); whether anything on this PC empties working sets, which
would bypass the pin; why the editor grows private memory by tens of MB per
open; what caused the render, screen-copy and window-creation stalls in the
first real session (GPU or compositor contention with a running game is the
leading guess).

## Diagnosis evidence

### Fast when warm, slow when cold

All `session.log` screenshots from the October 13 fix build to this diagnosis
(301), bucketed by time since the previous snip or editor open in the same process:

| Idle before the snip | n | Median | p90 | Over 1 s |
|---|---:|---:|---:|---:|
| Under 2 min | 162 | 153 ms | 221 ms | 1% |
| 2 to 15 min | 42 | 162 ms | 830 ms | 10% |
| 15 to 60 min | 42 | 500 ms | 7,497 ms | 38% |
| Over 60 min | 42 | 615 ms | 3,216 ms | 29% |
| First after app start | 13 | 453 ms | 796 ms | 0% |

Editor opens by time since the previous editor open: under 15 min, p90 129 ms
(n=51); 15 min to 3 h, p90 2,400 ms (n=25); over 3 h or first, p90 7,662 ms
(n=23). These exclude the old save wait. Worst snips: 66.9 s on Oct 29 02:35
(31.1 s of GC pause, hidden before it rendered) and 36.0 s on Oct 25 02:39
(27.3 s before the app saw the key).

### Every stage stalled

The 33 snips over one second spent their 327 s (including key-to-handler
delay) like this: key event to handler 23%, UI thread pickup 18%, GC pause 23%,
screen copy excluding GC 13%, window preparation 5%, first render 15%, capture
worker wake-up 2%. Microsecond handoffs taking seconds means memory being read
back from disk, not a slow algorithm. 291 of the 301 snips contained a gen2
collection.

### The machine at diagnosis

47.9 GB RAM, 64.2 GB committed of an 85.8 GB limit, 6.5 GB available, pagefile
on `C:` at 11.2 GB used (peak 16.6 GB), 320 to 490 pages read per second, CPU 33
to 59%, 128 `node` processes holding 9.0 GB, about ten `claude` processes at
roughly 0.8 GB each, SysMain disabled. `C:` is a Kingston A400 SATA SSD (about
1 ms per I/O); `B:` a Toshiba HDWD130 3 TB HDD; `D:` a Seagate ST8000VN004 8 TB
HDD that was idle with the 20-minute spin-down. The old process (PID 55052, 46 h,
54 snips, 17 editors, one recording) held 443.5 MB private and 195.5 MB working
set with 87.8 s of total CPU: idle CPU was never the problem.

While the process stalls, its low-level keyboard hook still sits in front of
every keystroke on the machine, so typing elsewhere can be held for up to the
system hook timeout per key. One trace showed the hook receiving a key 4.3 s late.

`B:` remains saturated for everything else stored there; that is outside this
app's scope.
