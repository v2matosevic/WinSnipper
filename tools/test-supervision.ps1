<# Isolated supervision checks. Never registers tasks, writes registry values,
   starts/stops processes, or opens windows. Tests definitions from the real script. #>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = Join-Path $PSScriptRoot 'winsnipper.ps1'
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($fn in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($fn.Extent.Text))
}

$TaskName = 'WinSnipper Test Only'
$DistDir = 'X:\NeverUsed'
$RunKey = 'TestOnly:\NeverUsed'
$RunValue = 'WinSnipper'
$CheckMins = 2
$QuitFlag = 'X:\NeverUsed\quit.flag'
$StateDir = 'X:\NeverUsed'
$Elevated = $false
$Rebuild = $false
$RegisterCapture = $false
$script:existingHigh = $false
$script:isAdmin = $false
$script:running = $false
$script:events = [Collections.Generic.List[string]]::new()
$script:registeredLevel = ''
$script:taskState = 'Ready'
$script:taskMatches = $false
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Test-ElevatedTask { return $script:existingHigh }
function Get-ScheduledTask { param($TaskName,$ErrorAction) return [pscustomobject]@{State=$script:taskState} }
function Test-Administrator { return $script:isAdmin }
function Assert-Exe { return 'X:\NeverUsed\WinSnipper.exe' }
function Get-AppProcess { if ($script:running) { return [pscustomobject]@{ Id = 123 } } }
function Get-AppTaskInstance { param($ProcessId) if ($script:taskMatches -and $ProcessId -eq 123) { return $script:fakeInstance } }
function Write-Step($msg) { }
function Write-Ok($msg) { }
function Write-Warn($msg) { }
$script:realShortcuts = ${function:Install-Shortcuts}
$script:realBuild = ${function:Invoke-Build}
$script:realSsd = ${function:Use-SsdInstall}
$InstallDir = 'X:\NeverUsedSsd'
function Use-SsdInstall { $script:events.Add('ssd') }

# Replace all mutation boundaries before invoking any extracted production function.
function Start-Process { throw 'Unexpected direct process launch' }
function Stop-Process { throw 'A real process must never be stopped' }
function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Settings,$Principal,[switch]$Force,$Description)
    $script:registeredLevel = [string]$Principal.RunLevel
    $script:events.Add('register')
    $script:existingHigh = $script:registeredLevel -eq 'Highest'
}
function New-ScheduledTaskAction { param($Execute,$Argument,$WorkingDirectory) return @{} }
function New-ScheduledTaskTrigger { param([switch]$AtLogOn,$User,[switch]$Once,$At,$RepetitionInterval,$RepetitionDuration) return @{} }
function New-ScheduledTaskSettingsSet { param([switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries,[switch]$StartWhenAvailable,$MultipleInstances,$ExecutionTimeLimit,$RestartCount,$RestartInterval) return @{} }
function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel) return [pscustomobject]@{ RunLevel=$RunLevel } }
function Remove-ItemProperty { param($Path,$Name,$ErrorAction) $script:events.Add('remove-run') }
function Set-ItemProperty { param($Path,$Name,$Value) $script:events.Add('set-run') }
function New-Item { param($Path,[switch]$Force) $script:events.Add('new-key') }
function Test-Path { param($Path) return $false }
function Set-Content { param($Path,$Value) $script:events.Add('quit-flag') }
function Remove-Item { param($Path,[switch]$Force,$ErrorAction) $script:events.Add('clear-quit') }
function Start-Sleep { param($Milliseconds) }
function Start-ScheduledTask { param($TaskName) $script:events.Add('task-start'); $script:running=$true }

# Test opt-in task principal and persistence when setup is later rerun without the flag.
$Elevated = $true
Install-KeepAlive
Assert ($script:registeredLevel -eq 'Highest') 'Opt-in did not create Highest principal'
$Elevated = $false
Install-KeepAlive
Assert ($script:registeredLevel -eq 'Highest') 'Existing elevated mode was silently downgraded'
$script:events.Clear()
Install-Autostart
Assert (($script:events -join ',') -eq 'remove-run') 'Elevated setup retained a competing medium Run launch'
Write-Output 'PASS: elevated task principal persists; competing Run launch removed'

$script:running = $false
$script:events.Clear()
Invoke-Start
Assert (($script:events -join ',') -eq 'clear-quit,task-start') 'Elevated start did not use scheduled task'
$script:events.Clear()
Invoke-Start
Assert ($script:events.Count -eq 0) 'Already-running app was launched again'
Write-Output 'PASS: elevated start uses task; existing process is preserved'

$script:existingHigh = $false
$script:running = $false
function Start-Process { param($FilePath,$WorkingDirectory,$Verb) $script:events.Add("direct-start:$Verb"); $script:running=$true }
$script:events.Clear()
Invoke-Start
Assert (($script:events -join ',') -eq 'clear-quit,direct-start:') 'Standard setup changed its launch path'
Install-KeepAlive
Assert ($script:registeredLevel -eq 'Limited') 'Default task unexpectedly elevated'
Write-Output 'PASS: default launch and task remain standard-user'

$script:existingHigh = $true
$script:taskState = 'Disabled'
$script:running = $false
$script:events.Clear()
Invoke-Start
Assert (($script:events -join ',') -eq 'clear-quit,direct-start:RunAs') 'Manual start changed disabled task or lost elevation'
$script:existingHigh = $false
$script:taskState = 'Ready'
Write-Output 'PASS: disabled elevated autostart stays disabled during manual RunAs launch'

$script:shortcutRows = [Collections.Generic.List[object]]::new()
$script:fakeShell = [pscustomobject]@{}
$script:fakeShell | Add-Member ScriptMethod CreateShortcut {
    param($path)
    $row = [pscustomobject]@{ TargetPath=''; Arguments=''; WorkingDirectory=''; IconLocation=''; Description='' }
    $row | Add-Member ScriptMethod Save { }
    $script:shortcutRows.Add($row)
    return $row
}
function New-Object { param($ComObject) Assert ($ComObject -eq 'WScript.Shell') 'Unexpected COM creation'; return $script:fakeShell }
$script:existingHigh = $true
& $script:realShortcuts
Assert ($script:shortcutRows.Count -eq 2) 'Expected Desktop and Start Menu shortcuts'
foreach ($row in $script:shortcutRows) {
    Assert ($row.TargetPath -like '*powershell.exe' -and $row.Arguments -like '*-WindowStyle Hidden* start') 'Elevated shortcut bypassed supervisor'
}
$script:shortcutRows.Clear()
$script:existingHigh = $false
& $script:realShortcuts
foreach ($row in $script:shortcutRows) {
    Assert ($row.TargetPath -eq 'X:\NeverUsed\WinSnipper.exe' -and $row.Arguments -eq '') 'Default shortcut changed'
}
Write-Output 'PASS: elevated shortcuts use hidden supervisor; standard shortcuts use app directly'

# Task delegation is allowed only after matching the current user's exact PID.
$script:fakeInstance = [pscustomobject]@{}
$script:stopFault=$false
$script:fakeInstance | Add-Member ScriptMethod Stop {
    $script:events.Add('task-stop')
    if ($script:stopFault) { throw 'Injected task stop failure' }
    $script:running=$false
}
$script:existingHigh=$true
$script:isAdmin=$false
$script:running=$true
$script:taskMatches=$false
$script:events.Clear()
$rejected=$false
try { Invoke-Stop } catch { $rejected=$_.Exception.Message -match 'not a verified instance' }
Assert ($rejected -and $script:events.Count -eq 0 -and $script:running) 'Unverified elevated stop caused side effects'
$script:taskMatches=$true
Invoke-Stop
Assert (($script:events -join ',') -eq 'new-key,quit-flag,task-stop' -and -not $script:running) 'Verified delegated stop did not use exact task instance'
Write-Output 'PASS: unverified elevated stop changes nothing; verified owner task stops without direct process access'
$script:running=$true
$script:stopFault=$true
$script:events.Clear()
$Flavor='both'
$rejected=$false
try { & $script:realBuild } catch { $rejected=$_.Exception.Message -match 'Injected task stop failure' }
Assert ($rejected -and $script:running -and ($script:events -join ',') -eq 'new-key,quit-flag,task-stop,clear-quit') 'Failed build stop left supervision disabled or continued publishing'
$script:stopFault=$false
Write-Output 'PASS: failed supervised stop preserves running app and restores watchdog marker before aborting build'

# Setup validation must happen before any install/restart boundary.
function Install-Shortcuts { $script:events.Add('shortcuts') }
function Invoke-Status { }
function Invoke-Restart { $script:events.Add('restart') }
function Invoke-Build { $script:events.Add('build') }
$Elevated = $true
$script:isAdmin = $false
$script:events.Clear()
$rejected = $false
try { Invoke-Setup } catch { $rejected = $_.Exception.Message -match 'requires an elevated' }
Assert $rejected 'Non-admin elevated setup was not rejected'
Assert ($script:events.Count -eq 0) 'Rejected setup caused side effects'
$script:isAdmin = $true
Invoke-Setup
Assert (($script:events -join ',') -eq 'register,remove-run,shortcuts,restart') 'Opt-in setup order or restart is wrong'
Write-Output 'PASS: non-admin setup has no effects; approved elevated setup registers before restarting'
$Rebuild = $true
$script:events.Clear()
Invoke-Setup
Assert (($script:events -join ',') -eq 'build,register,remove-run,shortcuts,restart') 'Rebuild/setup order is wrong'
Write-Output 'PASS: requested app rebuild precedes elevated setup'

$Rebuild=$false
$RegisterCapture=$true
$script:events.Clear()
function Register-CaptureDefault { $script:events.Add('capture-register') }
Invoke-Setup
Assert (($script:events -join ',') -eq 'register,remove-run,shortcuts,capture-register,restart') 'Capture registration was skipped or ran before startup setup'
Write-Output 'PASS: opt-in capture registration runs before approved setup restart'

# Exercise the real build ordering with fake publishers and file copies only.
$script:running=$false
$script:existingHigh=$false
$script:events.Clear()
$Root=$PSScriptRoot
$DistDir=Join-Path $env:TEMP 'WinSnipper-supervision-not-created'
function dotnet { $script:events.Add('publish'); $global:LASTEXITCODE=0 }
function Copy-Item { param($Path,$Destination,[switch]$Force) $script:events.Add('copy') }
& $script:realBuild
Assert (($script:events -join ',') -eq 'new-key,quit-flag,ssd,publish,copy,publish,copy,capture-register,clear-quit') 'Build must move to the SSD install, publish both flavors before capture registration and preserve stopped state'
Write-Output 'PASS: build moves to the SSD install, registers capture only after both payloads publish and leaves an originally stopped app stopped'

# The real SSD move, with every file operation faked: rename (never delete)
# the old folder into artifacts, copy its exes, then add the junction.
$Root = Join-Path $env:TEMP 'WinSnipper-ssd-check-not-created'
$DistDir = Join-Path $Root 'dist'
$script:distItem = [pscustomobject]@{ LinkType = $null; PSIsContainer = $true }
$script:installFiles = @()
function Get-Item { param($LiteralPath,[switch]$Force,$ErrorAction) if ($LiteralPath -eq $DistDir) { return $script:distItem } }
function Get-ChildItem { param($LiteralPath,[switch]$File,[switch]$Force,$ErrorAction)
    if ($LiteralPath -eq $InstallDir) { return $script:installFiles }
    return @([pscustomobject]@{ FullName = "$LiteralPath\WinSnipper-OCR.exe" }, [pscustomobject]@{ FullName = "$LiteralPath\WinSnipper.exe" })
}
function New-Item { param($ItemType,$Path,$Target,[switch]$Force)
    $script:events.Add($(if ($ItemType -eq 'Junction') { "junction:$Path->$Target" } else { "mkdir:$Path" })) }
function Move-Item { param($LiteralPath,$Destination) $script:events.Add("move:$LiteralPath->$Destination") }
function Copy-Item { param($LiteralPath,$Destination) $script:events.Add("copy:$(Split-Path -Leaf $LiteralPath)->$Destination") }
function Get-Date { param($Format) return '20261029-170000' }
$script:events.Clear()
& $script:realSsd
$backup = Join-Path $Root 'artifacts\dist-before-ssd-20261029-170000'
Assert (($script:events -join ',') -eq "mkdir:$InstallDir,mkdir:$(Join-Path $Root 'artifacts'),move:$DistDir->$backup,copy:WinSnipper-OCR.exe->$InstallDir,copy:WinSnipper.exe->$InstallDir,junction:$DistDir->$InstallDir") 'SSD move must rename the old dist, copy its exes, then add the junction'
$script:events.Clear()
$script:distItem = [pscustomobject]@{ LinkType = 'Junction'; PSIsContainer = $true }
& $script:realSsd
Assert ($script:events.Count -eq 0) 'An existing junction was moved again'
$script:distItem = [pscustomobject]@{ LinkType = $null; PSIsContainer = $true }
$script:installFiles = @([pscustomobject]@{ FullName = "$InstallDir\other.exe" })
$rejected = $false
try { & $script:realSsd } catch { $rejected = $_.Exception.Message -match 'already holds files' }
Assert ($rejected -and $script:events.Count -eq 0) 'SSD move merged into an occupied install folder'
Write-Output 'PASS: SSD move renames (never deletes) the old dist, copies the running build, adds the junction once and refuses to merge'
