# Windows capture default, 0.9.2 candidate

Implemented and installed locally on 2026-10-24 after owner approval. The owner
selected WinSnipper in Windows Default apps; the effective choice and PrintScreen
route were verified. No protected UserChoice/hash was written by the app or agent,
and no Windows system executable or global capture privacy policy was changed.
No public 0.9.2 release has been published.

## Scope and flow

WinSnipper registers a per-user handler for `ms-screenclip` capture links. The
owner selects it in Windows Default apps. This makes it the default capture
interface, not a Microsoft system component. Windows capture APIs remain available
because WinSnipper's recording depends on them. This follows the association route
documented in the [Flameshot Windows guide](https://flameshot.org/docs/guide/windows-help/)
and Windows' [Default apps settings](https://support.microsoft.com/en-us/windows/apps/change-default-apps-in-windows).

The intended Windows shortcut route is then Win+Shift+S or the enabled PrintScreen
capture binding -> the selected default -> WinSnipper activation -> our selector.
The existing hook remains the fast direct path while resident. If closed, the
activation process starts the verified existing supervisor task on demand. An
explicit capture request clears the deliberate-quit marker; background watchdog
launches still honor it. Portable/disabled-supervision fallback starts at normal
integrity. Actual OS hotkey routing still needs acceptance after owner selection.

A message-only HWND forwards screenshot/recording requests into the resident
instance, including the configured elevated instance. Target executable and token
user SID must match. Only one registered message is allowed through UIPI. It carries
bounded integers for screenshot, recording or a no-capture probe, with a 64-entry
request-id cache to suppress retries. No path, script, callback URL or image data
crosses that boundary. The shell-launched client transfers foreground permission
to the verified resident PID. Startup and send waits are bounded.

URI parsing permits the screenclip scheme and known screenshot/video shapes.
Oversized, control-character, foreign-host, fragment and redirect-callback requests
are rejected. Supplied URLs are never opened. This is a default UI handler, not
the packaged Snipping Tool response-token API. Capture timing starts at request
receipt; it is not physical-key-to-overlay cold-start latency.

## PrintScreen and settings

The PrintScreen option uses Windows binding 0 when WinSnipper is not the selected
default, retaining direct hook handling while resident. Once selected, binding 1
allows Windows to launch WinSnipper on demand. Original and last-applied values
are persisted before mutation. Settings now use the existing atomic flushed
write helper. Legacy metadata is migrated without replacing its recorded original;
restoration preserves outside changes and pre-existing values the app never owned.

A read-only registry notification observes owner-selected default changes and
posts policy updates to WPF. A bounded keyboard-setting notification runs off
the UI after actual writes. The settings draft copies current backup metadata
before saving so returning from Windows Settings cannot overwrite a newer snapshot.
The settings panel scrolls within monitor work-area bounds.

## Verification

- Both Release flavors compile with zero warnings/errors.
- `tools/test-capture-integration.ps1`: 43 passing cases covering private registry
  registration/quoting/idempotence, untouched choice/hash, metadata ordering,
  failure/migration/restoration, native registry notification, URI validation,
  SID checks, real hidden native IPC, duplicate handling, command routing,
  no-capture probe and a hidden settings render. The actual Windows default
  remains unchanged.
- Existing `tools/test-hotkey.ps1` screenshot/priority/overlay/capture checks pass.
- All 40 recording-control and 24 reliability/finalization checks pass.
- Supervision tests pass under PowerShell 7 and Windows PowerShell 5.1, including
  registration after both publishes and preserving an originally stopped app.

Results and the actual XAML preview are in `artifacts/capture-integration`.
The integration test installs no hooks, sends no keyboard/mouse input, opens no
on-desktop test window or Windows Settings, and takes no screenshot. Existing
recording regressions use their documented private recording fixtures. Cross-integrity forwarding
and supervised cold startup subsequently passed on the installed process. The
owner's default choice is verified; physical shortcut testing after closing the
app remains the final acceptance step. No universal
Windows-build, game or servicing-reset guarantee follows from the isolated checks.
Direct launches of Windows system executables, Game Bar and other capture APIs
are outside this default-interface integration.

## Activation and reversal

### Installed verification on 2026-10-24

The approved supervisor build installed both 0.9.2 executables at 20:40, registered
their capabilities and restarted the existing Highest task. Each installed file's
SHA256 matches its published stage. A standard-user probe reached elevated PID
8500. The approved cold test then stopped that instance, verified the deliberate-
quit marker, and launched `--capture-probe`. It started elevated PID 9324, cleared
the marker and acknowledged without opening a selector. Native token/priority
queries verified High integrity (0x3000) and Normal priority (0x20). No new task
privilege or ACL was granted.

The owner selected WinSnipper on the Windows page opened by `--capture-defaults`.
Read-only verification at 20:46 found UserChoice ProgId `WinSnipper.ScreenClip`
and `PrintScreenKeyForSnippingTool=1`. The watcher preserved the original saved
value (-1/absent), with ownership captured and last-applied value 1. Exact payload
hashes and process/choice evidence are in `artifacts/capture-integration/install.json`.
Physical Win+Shift+S and PrintScreen tests after tray exit remain owner-run.

After approval for the running app's restart:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "B:\Coding\WinSnipper\tools\winsnipper.ps1" build -Flavor both -RegisterCapture
```

This publishes both candidates, registers only WinSnipper's own HKCU capabilities,
RegisteredApplications value and ProgID, and restarts through the existing task.
It never writes the protected default selection. In WinSnipper Settings click
**Set as Windows capture default**, then choose WinSnipper for **MS-SCREENCLIP**.
`--capture-defaults` opens the same owner-facing Windows page. `--capture-probe`
tests forwarding or supervised cold startup without opening a capture overlay.

To reverse, choose another handler in Windows Default apps. Untick PrintScreen
handling to restore its recorded key value. Choose another default before removing
the executable. The supervisor uninstall removes supervision, leaving app/files
and the Windows-owned default choice. No public 0.9.2 release has been made.
