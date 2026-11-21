# Three weeks on 0.9.3: what failed and what was changed, 2026-11-21

## Delivery state

Implemented and verified offscreen as version 0.9.4 in source. **Not installed
and not published**: the running app is still 0.9.3 from October 29, and public
stable remains 0.9.1. Installing restarts the app, which needs the owner's
approval. Current state and remaining checks: [handoff](HANDOFF-2026-11-21.md).

The owner's report: "it lags, sometimes it doesn't work, sometimes it is slow
... video recording is terrible and lagging." Sources used: `session.log`
(411 snips, 196 editor opens since October 29), `crash.log`, `recorder.log`,
the Windows Application and System event logs, and new offscreen harnesses.

## Findings

### 1. The app broke after every .NET runtime update ("sometimes it doesn't work")

The exe is framework-dependent: it runs on the shared .NET 8 Desktop runtime.
Windows services that runtime in place. Each install creates the new patch
folder and empties the previous one, while a resident app is still running
from it. On this PC every folder older than the current one is empty:

| Runtime | Installed | What then failed in the running app |
|---|---|---|
| 8.0.33 | Oct 30, 04:01 | PID 32864 (started Oct 29 on 8.0.32): every tooltip threw `FileNotFoundException: Accessibility` on Oct 30, Nov 4, Nov 5 and Nov 12, until the Nov 12 reboot |
| 8.0.34 | Nov 15, 08:40 | PID 11492 (started Nov 14 on 8.0.33): at 15:53 the editor failed to open six times (`System.Windows.Controls.Ribbon` not found, then theme resources missing), then the process crashed (`e0434352`, `c000041d`); Windows could not even read its version (0.0.0.0) |

Assemblies already loaded keep working, so the app looks healthy until
something needs one that is not loaded yet.

Fix: `RuntimeHealth` checks once a minute that the framework files the process
runs from still exist. When they are gone, the app starts a replacement process
(`--restarted`, which waits for the single-instance mutex) and exits, as soon
as no capture, recording or window is open. Warm-up also builds one editor and
loads the automation assemblies behind tooltips and popups, so the common paths
keep working until that restart. A self-contained build would remove the
dependency entirely; it was not chosen because it multiplies the exe size and
changes the public artifacts (owner decision, see the handoff).

### 2. Memory only grew, and Windows paged it out ("it lags", "sometimes slow")

Private memory per process over time, from `session.log`:

| Process | Lifetime | Start | Plateau | Peak |
|---|---|---:|---:|---:|
| 32864 | Oct 29 to Nov 12 | 350 MB | 900 to 1,250 MB | 1,475 MB |
| 42696 | Nov 17 to Nov 20 | 375 MB | 730 to 830 MB | 921 MB |
| 11936 | Nov 20 to Nov 21 | 349 MB | 720 to 790 MB | 893 MB |

It rises in steps and never comes back; one editor open on Nov 21 added 272 MB
in a second. Windows flagged the process as leaking (`RADAR_PRE_LEAK_64`,
Nov 4). With a hard minimum working set near 200 MB, everything above it is
paged out on this overcommitted PC and read back around the next hotkey.

The editor itself does not leak: opening and closing it repeatedly in a
harness stays flat and every window is collected. The cost is in what a copy
allocates. Measured on a private clipboard, one full-desktop copy (5760 x 1080):

| Path | UI thread blocked | Managed allocations |
|---|---:|---:|
| WPF `Clipboard.SetImage` | 279 ms | 308 MB |
| New CF_DIB path | 0 ms | 0 MB |

WPF builds a BMP in a growing `MemoryStream`, a `System.Drawing` copy pushed
through `BinaryFormatter`, and a device bitmap, all on the calling thread. An
idle tray app then allocates too little for the collector to reclaim it.

Fix: `ClipboardImage` builds one CF_DIB (bottom-up, opaque alpha) on a worker
and places it through a message-only owner window; Windows derives CF_BITMAP
and CF_DIBV5. `MemoryTrim` runs a compacting collection four seconds after the
last capture window closes and after start-up, never while a capture, editor
or recording is in progress, and logs `memory-trim private=A->B`.

### 3. The screen copy stalled after idle

Snip latency by time since the previous use, 0.9.3, October 29 to November 21:

| Idle before the snip | n | Median | p90 | Max | Over 1 s |
|---|---:|---:|---:|---:|---:|
| Under 2 min | 252 | 128 ms | 182 ms | 290 ms | 0 |
| 2 to 15 min | 63 | 133 ms | 207 ms | 346 ms | 0 |
| 15 to 60 min | 39 | 145 ms | 924 ms | 1,844 ms | 4 |
| Over 60 min | 46 | 288 ms | 508 ms | 4,084 ms | 2 |
| First after start | 11 | 368 ms | 1,125 ms | 1,500 ms | 2 |

0.9.3 fixed the medians (the October table had 500 to 615 ms in the cold
buckets). Every remaining slow snip spent its time between `capture-started`
and `capture-ready`: the screen copy, 0.7 to 3.9 s, with no collection. The
copy writes a desktop-sized buffer (24 MB) that nothing touches between
hotkeys; once paged out, every page is read back from the page file only to be
overwritten. One trace shows a 89 MB working set under a 197 MB "hard"
minimum, so something on this PC empties working sets, which a hard minimum
does not survive.

Fix: the capture surface is mapped once and locked with `VirtualLock` after the
minimum working set is applied. A locked range stays in physical memory through
trimming and through `EmptyWorkingSet` (tested). If the lock is refused, the
old contents are discarded with `MEM_RESET` before each copy.

### 4. "Not responding" three times in a week

Windows closed the app as hung on Nov 17 14:11, Nov 20 12:54 and Nov 21 19:22
(Application Hang 1002), each a few minutes after a snip or editor open. The
hang reports carry no stack and the app wrote nothing, so the cause is
**unknown**. The clipboard path above is the leading suspect (it blocks the UI
thread in proportion to the image, retries with sleeps, and runs right after a
selection and at editor close); unproven.

Fix for the evidence gap: `UiWatchdog` writes `ui-stall` lines to `session.log`
when the UI thread stops answering for 1.5 s, with the stage in progress, and
writes at 5 s and 30 s while a stall is still going on, so a hang that ends in
the process being closed still leaves a line. `process-stall` marks the whole
process (or PC) standing still, which is a different finding.

### 5. Recording

`recorder.log` held no quality data, so the report could not be confirmed from
history. Measured now, 1920 x 1080, real screen, RTX 3060 Ti (the NVIDIA
hardware encoder is in use; the colour conversion in front of it runs on the CPU):

| | Old | New |
|---|---:|---:|
| Process CPU, 20 s at 30 fps | 68% of one core | 59% |
| Capture loop per frame | not logged | 3.7 ms of a 33.3 ms budget |
| Late frames (filled with a repeat) | not logged | 0 |
| Age of each recorded frame | two frames (about 66 ms) | newest available |

Changes: the WGC frame pool is drained to the newest frame; the capture thread
runs above normal priority under the multimedia scheduler ("Capture"); the
frame is a DIB section the cursor is drawn into directly (the GDI+ bitmap
copied the whole frame in and out for each cursor draw); an unchanged screen
and every repeated slot reuse one buffer; the save folder is prepared off the
UI thread. Each recording now logs `stats backend= unique= late-slots=
avg-work= max-work= budget=`.

Not changed: the default of 30 fps (Settings offers 60, which is what makes
motion look smooth; measured cost at 1080p60 is about 113% of one core
including start-up), and the CPU colour conversion (a GPU-only path needs a
D3D device manager and texture samples; not attempted).

Whether real recordings were choppy because of late frames is **unknown**; the
new stats line answers it on the next recording.

## Also found

- The PC itself restarted without a clean shutdown five times in nine days
  (Kernel-Power 41: Nov 13, Nov 14 twice, Nov 20, Nov 21). That is why most
  `startup` lines have no `exit`. Not caused by this app as far as the logs show.
- `tools\ui-shots.ps1` had been broken since October 29 (the editor
  constructor gained an optional parameter). Fixed.

## Verification

All offscreen: no input sent, nothing shown, the interactive clipboard untouched
(clipboard checks run on a private window station, which has its own clipboard).

- `tools\test-resilience.ps1`: 18 checks (locked surface survives
  `EmptyWorkingSet` while a control page is evicted; runtime removal raised and
  re-raised; memory trim waits while busy and returned 113 MB to 14 MB; a 2.75 s
  stall recorded with its stage; six editors collected; warm-up leaves no
  window) and 6 clipboard checks (five image shapes round-trip pixel-exact and
  opaque, Windows derives CF_BITMAP and CF_DIBV5, a WPF reader gets the image;
  later copy wins).
- Recorder benchmark above: files decode, sampled frames are not black.
- Existing suites and both `--selftest` runs: see the handoff for the results
  of this session.

Not verified: anything that needs the installed app and real use, which is the
actual test of findings 1 to 4. A real paste into each target application was
not tried; the format is the standard CF_DIB that Windows itself converts.
