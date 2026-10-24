# Screenshot shortcut ownership

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
other Windows screenshot routes such as Win+PrintScreen or Game Bar.

The Microsoft app can be reinstalled using Microsoft's instructions. Store or
Windows servicing may reinstall it later; `status` reports its current presence.

The documented `DisableSnippingTool` policy is separate from the PrintScreen
setting. Do not claim the policy blocks the installed Store app without checking
that behavior on that Windows/app version. It is not applied by WinSnipper.

Sources:

- [Microsoft: uninstall and reinstall Snipping Tool](https://support.microsoft.com/en-us/windows/uninstall-and-reinstall-paint-and-snipping-tool-d21261f8-1c3a-4776-9262-2d34928b1962)
- [Microsoft: Remove-AppxPackage, current-user scope](https://learn.microsoft.com/en-us/powershell/module/appx/remove-appxpackage)
- [Microsoft: DisableSnippingTool policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-tabletshell#disablesnippingtool_1)

## Inspection on 2026-10-24

Windows build 26200.9711 (25H2), Microsoft.ScreenSketch 11.2608.41.0,
WinSnipper-OCR 0.9.1 running from this repository's `dist` folder. The
PrintScreen binding was already zero and `ReplaceSnippingTool` was true.
Microsoft Snipping Tool remained registered, and a SnippingTool process was
observed. No synthetic input was sent. No claim of reproduced hook removal or
verified native overlay blocking follows from these observations.
