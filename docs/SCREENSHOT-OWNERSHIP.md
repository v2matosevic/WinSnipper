# Screenshot shortcut ownership

The [0.9.2 default-capture integration](WINDOWS-CAPTURE-DEFAULT.md) is installed
locally and owner-selected in Windows Default apps. Elevated warm/cold activation
probes pass; the actual choice is `WinSnipper.ScreenClip` and PrintScreen routing
is enabled. Physical-key acceptance after tray exit remains to be checked. The
resident-only explanation below describes the preceding 0.9.1 behavior.

`pwsh -File tools\winsnipper.ps1 status` checks the running WinSnipper build,
supervision, Windows' PrintScreen binding, the current user's Microsoft Snipping
Tool registration and active native screenshot processes. Status changes nothing.

WinSnipper's "Replace the Windows Snipping Tool" checkbox takes bare PrintScreen
and sets `HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingTool` to zero.
It does not uninstall or block the Microsoft app. Win+Shift+S is intercepted by
the dedicated low-level keyboard hook. Windows can silently remove a hook after
a timeout, and the recovery timer replaces it every 15 seconds. During that gap,
Windows can receive the shortcut. This is a supported failure mechanism, not a
reproduction of every reported conflict.

## Administrator windows

The keyboard hook also depends on Windows integrity levels. A standard-user
WinSnipper cannot reliably take input from a higher-integrity administrator
window. A terminal-only failure is a reason to compare actual process tokens,
not to assume a timeout. Do not lower UAC or disable Windows capture privacy.

For an owner who needs captures over administrator windows, opt in from an
administrator PowerShell window:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "B:\Coding\WinSnipper\tools\winsnipper.ps1" setup -Elevated -Rebuild
```

This builds both app flavors using the supervised build path, restarts
WinSnipper, and configures its existing interactive keep-alive task with Highest
privileges. Save open edits first. The administrator shell check happens before
setup changes or process stops. Ordinary setup remains at Limited privileges;
rerunning setup on an elevated installation preserves the existing choice.

The elevated logon task replaces the competing HKCU Run launch. Desktop and
Start Menu shortcuts go through the script's `start` command, which uses that
task. The updated app's startup checkbox controls the elevated task and never
recreates a standard-user Run entry. Turning startup off disables its watchdog
triggers too; it does not stop the current process. A later manual launch uses
RunAs while preserving disabled background startup. Stop/restart/build can
control the elevated app through the existing current-user task without a new
elevated shell. The supervisor first matches the task owner SID, single executable
action, `--watchdog` arguments, running engine PID and current action. An
unverified elevated process still requires administrator PowerShell; rejection
happens before touching the quit marker. This uses existing task permissions,
grants no new rights, and does not waive the owner's process-stop approval rule.

This mode runs the whole app as administrator. Windows can restrict dragging
captures into standard-user apps. Clipboard capture/paste and real terminal
hotkeys still need hands-on acceptance. For broader distribution, signed UIAccess
installation in a protected location is the narrower alternative; that packaging
is not implemented here.

To revert the optional elevated setup, use an administrator PowerShell window
to stop WinSnipper with the supervisor, change its keep-alive task principal to
Limited, then rerun ordinary setup from a standard-user shell to restore normal
shortcuts and Run startup. Do not merely launch another copy: the existing
instance owns the mutex. Obtain owner approval before stopping it.

## Native app removal

For an owner who wants Microsoft Snipping Tool removed, Windows supports
uninstalling it for the current user:

```powershell
Get-AppxPackage -Name Microsoft.ScreenSketch | Remove-AppxPackage
```

This is an explicit owner action, never an automatic repair or startup action.
Get approval before removal or closing a running native screenshot app. Do not
use `-AllUsers`, remove Windows system components, change general capture privacy
policies or stop WinSnipper. Removal can discard the Microsoft app's settings
and unsaved work. Existing saved screenshot files are outside that app package.

After removal, check that `Get-AppxPackage -Name Microsoft.ScreenSketch` is empty
and that WinSnipper is still running. The owner should then try Win+Shift+S and
PrintScreen repeatedly, including after unlock and under their usual workload.
Do not simulate input or open screenshot overlays on the owner's desktop.
Package removal is not proof of shortcut responsiveness, nor does it disable
the separate inbox ScreenClippingHost fallback, Win+PrintScreen or Game Bar.
Do not recommend removal alone as a fix for administrator-window hook failures.

The Microsoft app can be reinstalled using Microsoft's instructions. Store or
Windows servicing may reinstall it later; `status` reports its current presence.

The documented `DisableSnippingTool` policy is separate from the PrintScreen
setting. Do not claim the policy blocks the installed Store app without checking
that behavior on that Windows/app version. It is not applied by WinSnipper.

Sources:

- [Microsoft: uninstall and reinstall Snipping Tool](https://support.microsoft.com/en-us/windows/uninstall-and-reinstall-paint-and-snipping-tool-d21261f8-1c3a-4776-9262-2d34928b1962)
- [Microsoft: Remove-AppxPackage, current-user scope](https://learn.microsoft.com/en-us/powershell/module/appx/remove-appxpackage)
- [Microsoft: DisableSnippingTool policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-tabletshell#disablesnippingtool_1)
- [Microsoft: UIPI and UIAccess](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/user-account-control-only-elevate-uiaccess-applications-that-are-installed-in-secure-locations)

## Inspection on 2026-10-24

Windows build 26200.9711 (25H2), Microsoft.ScreenSketch 11.2608.41.0,
WinSnipper-OCR 0.9.1 running from this repository's `dist` folder. The
PrintScreen binding was already zero and `ReplaceSnippingTool` was true.
Microsoft Snipping Tool remained registered, and a SnippingTool process was
observed. No synthetic input was sent. No claim of reproduced hook removal or
verified native overlay blocking follows from these observations.

The owner subsequently removed Microsoft.ScreenSketch: the registration count
was verified as zero while WinSnipper PID 32476 stayed running. A separate
`ScreenClippingHost.exe` from `MicrosoftWindows.Client.Core` was observed after
removal; the owner supplied a photograph of its overlay and reported that it
appeared in the terminal, while WinSnipper worked in ordinary apps. Token queries
then confirmed PowerShell PID 34152 at High integrity (0x3000), and WinSnipper
PID 32476 at Medium (0x2000). Its task remained Limited. This is high-confidence
evidence for a permission-level mismatch; elevated-mode acceptance remains pending.

Verification before installation: `tools/test-supervision.ps1` uses isolated
function definitions with fake task, registry and process boundaries. It runs
under PowerShell 7 and Windows PowerShell 5.1 without touching live tasks,
registry values or processes. Both app flavors must also compile. None of these
checks prove physical hotkey interception over an administrator window.

At 15:32 the owner ran `setup -Elevated -Rebuild` from administrator PowerShell.
Both flavors were published through the supervisor and WinSnipper was restarted.
Read-only verification at 15:35 confirmed one WinSnipper-OCR process (PID 19556)
at High integrity (0x3000), the keep-alive task at Highest with the correct
`dist\WinSnipper-OCR.exe --watchdog` action, and no direct HKCU Run entry.
Microsoft.ScreenSketch remained absent and PrintScreen's native binding remained
off. Session logs show screenshot hotkey dispatch and overlay rendering by that
new process at 15:35:06 and 15:35:08. These logs do not identify the foreground
app; explicit administrator-terminal acceptance and drag/drop behavior have not
been confirmed by the owner. The local installation is verified; no GitHub
release was published for this change.

The owner subsequently confirmed that the elevated screenshot shortcut fix
works well, then requested separate recording-control and selector improvements.
