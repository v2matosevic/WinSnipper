<#
.SYNOPSIS
    Run, supervise and rebuild the locally-installed WinSnipper.

.DESCRIPTION
    One entry point for the tray app on this machine. `setup` is the from-zero
    path: it builds if needed, wires autostart plus a keep-alive task, drops
    shortcuts, and starts the app.

    The keep-alive task launches the exe with --watchdog every 2 minutes. A
    live instance turns that into a no-op (single-instance mutex), a dead one
    gets replaced, and quitting from the tray writes a marker the watchdog
    honours -- so a crash comes back and a deliberate quit stays quit.

.EXAMPLE
    pwsh -File tools\winsnipper.ps1 setup
    pwsh -File tools\winsnipper.ps1 status
    pwsh -File tools\winsnipper.ps1 restart
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'start', 'stop', 'restart', 'status', 'build', 'logs', 'uninstall')]
    [string]$Command = 'status',

    # build: which flavor to publish. Defaults to whichever one is installed.
    [ValidateSet('ocr', 'lite', 'both')]
    [string]$Flavor,

    # logs: how many lines of each log to show.
    [int]$Tail = 25,

    # setup only: opt in to administrator-level hotkeys. Run from an elevated
    # shell; this replaces the running instance and uses the task at logon.
    [switch]$Elevated,

    # setup only: refresh both local app builds before configuring startup.
    [switch]$Rebuild,

    # setup/build: register WinSnipper in the owner's Windows Default apps list.
    # Windows still requires the owner to select the protected default.
    [switch]$RegisterCapture
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root      = Split-Path -Parent $PSScriptRoot
$DistDir   = Join-Path $Root 'dist'
# The installed copy lives on the SSD; dist is a junction to it (Use-SsdInstall).
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\WinSnipper'
$StateDir  = Join-Path $env:APPDATA 'WinSnipper'
$QuitFlag  = Join-Path $StateDir 'user-quit.flag'
$TaskName  = 'WinSnipper Keep-Alive'
$RunKey    = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$RunValue  = 'WinSnipper'
$CheckMins = 2

if ($Elevated -and $Command -ne 'setup') {
    throw '-Elevated is supported only with setup.'
}
if ($Rebuild -and $Command -ne 'setup') {
    throw '-Rebuild is supported only with setup.'
}
if ($RegisterCapture -and $Command -notin @('setup', 'build')) {
    throw '-RegisterCapture is supported only with setup or build.'
}

# ---------------------------------------------------------------- helpers

function Write-Step($msg) { Write-Host "  $msg" -ForegroundColor DarkGray }
function Write-Ok($msg)   { Write-Host "  $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  $msg" -ForegroundColor Yellow }

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ElevatedTask {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    return ($null -ne $task -and [string]$task.Principal.RunLevel -eq 'Highest')
}

# The OCR flavor is the one Marko runs; fall back to lite if that's all there is.
function Get-Exe {
    $ocr  = Join-Path $DistDir 'WinSnipper-OCR.exe'
    $lite = Join-Path $DistDir 'WinSnipper.exe'
    if (Test-Path $ocr)  { return $ocr }
    if (Test-Path $lite) { return $lite }
    return $null
}

function Get-AppProcess {
    $exe = Get-Exe
    if (-not $exe) { return $null }
    $name = [IO.Path]::GetFileNameWithoutExtension($exe)
    Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Get-AppTaskInstance([int]$ProcessId) {
    try {
        $exe = Get-Exe
        if (-not $exe) { return $null }
        $service = New-Object -ComObject 'Schedule.Service'
        $service.Connect()
        $task = $service.GetFolder('\').GetTask($TaskName)
        $definition = $task.Definition
        $userId = [string]$definition.Principal.UserId
        $taskSid = if ($userId -like 'S-1-*') { $userId } else {
            (New-Object Security.Principal.NTAccount($userId)).Translate([Security.Principal.SecurityIdentifier]).Value
        }
        if ($taskSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { return $null }
        if ($definition.Actions.Count -ne 1) { return $null }
        $action = $definition.Actions.Item(1)
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($action.Path), [IO.Path]::GetFullPath($exe))) { return $null }
        if ([string]$action.Arguments -ne '--watchdog') { return $null }
        foreach ($instance in $task.GetInstances(0)) {
            if ($instance.EnginePID -eq $ProcessId -and [StringComparer]::OrdinalIgnoreCase.Equals([string]$instance.CurrentAction, $exe)) {
                return $instance
            }
        }
    } catch { return $null } # Unknown ownership must never permit stopping a different task.
    return $null
}

function Assert-AppControl {
    if ((Test-ElevatedTask) -and -not (Test-Administrator)) {
        $proc = Get-AppProcess
        if ($proc -and -not (Get-AppTaskInstance -ProcessId $proc.Id)) {
            throw 'The elevated app is not a verified instance of this user''s WinSnipper task. Use administrator PowerShell. No changes were made.'
        }
    }
}

function Assert-Exe {
    $exe = Get-Exe
    if (-not $exe) {
        Write-Warn 'No build in dist\ yet -- building one.'
        Invoke-Build
        $exe = Get-Exe
    }
    if (-not $exe) { throw "No WinSnipper executable in $DistDir and the build produced none." }
    return $exe
}

# B:\Coding is a busy hard drive, and Windows reloads an idle app's evicted
# code from its exe, so the installed copy lives on the SSD. dist stays the
# path every task, shortcut and registration names, as a directory junction:
# no elevation is needed and the elevated task keeps its exact action path.
# Call only while the app is stopped. Nothing is deleted: the old folder is
# renamed into the ignored artifacts folder as a rollback copy.
function Use-SsdInstall {
    $dist = Get-Item -LiteralPath $DistDir -Force -ErrorAction SilentlyContinue
    if ($dist -and [string]$dist.LinkType -eq 'Junction') { return }
    if ($dist -and -not $dist.PSIsContainer) { throw "$DistDir is a file, not a folder. No changes were made." }
    if (@(Get-ChildItem -LiteralPath $InstallDir -Force -ErrorAction SilentlyContinue).Count) {
        throw "$InstallDir already holds files; it is not this checkout's junction target. No changes were made."
    }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    if ($dist) {
        $backup = Join-Path $Root ('artifacts\dist-before-ssd-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null
        Move-Item -LiteralPath $DistDir -Destination $backup
        # The current build keeps running from the SSD even if the next publish fails.
        foreach ($file in @(Get-ChildItem -LiteralPath $backup -File -Force)) {
            Copy-Item -LiteralPath $file.FullName -Destination $InstallDir
        }
        Write-Step "Previous dist kept at $backup"
    }
    New-Item -ItemType Junction -Path $DistDir -Target $InstallDir | Out-Null
    Write-Ok "dist -> $InstallDir (SSD)"
}

function Register-CaptureDefault {
    $exe = Assert-Exe
    if ([version](Get-Item $exe).VersionInfo.FileVersion -lt [version]'0.9.2.0') {
        throw 'Build WinSnipper 0.9.2 or newer before registering native capture activation.'
    }
    & $exe --register-capture | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Capture registration failed; check the app crash log.' }
    $key = Get-Item 'HKCU:\Software\Classes\WinSnipper.ScreenClip\shell\open\command' -ErrorAction Stop
    $expected = "`"$exe`" --capture-uri `"%1`""
    if ($key.GetValue('') -ne $expected) { throw 'Capture registration did not resolve to the installed executable.' }
    Write-Ok 'WinSnipper registered in Windows Default apps. No protected default choice was changed.'
    Write-Step 'In WinSnipper Settings, click Set as Windows capture default, then choose WinSnipper for MS-SCREENCLIP.'
}

function Test-CaptureDefault {
    $choice = Get-Item 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\ms-screenclip\UserChoice' -ErrorAction SilentlyContinue
    return ($null -ne $choice -and $choice.GetValue('ProgId') -eq 'WinSnipper.ScreenClip')
}

# ---------------------------------------------------------------- actions

function Invoke-Start {
    $exe = Assert-Exe
    $proc = Get-AppProcess
    if ($proc) {
        Write-Ok "Already running (pid $($proc.Id))."
        return
    }
    Remove-Item $QuitFlag -Force -ErrorAction SilentlyContinue
    if (Test-ElevatedTask) {
        # Preserve the chosen integrity level, including after a rebuild or a
        # shortcut launch from Explorer. A direct launch would run at medium.
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        if ([string]$task.State -eq 'Disabled') {
            # A manual launch must not silently re-enable background startup.
            Start-Process -FilePath $exe -WorkingDirectory $DistDir -Verb RunAs
        } else {
            Start-ScheduledTask -TaskName $TaskName
        }
    } else {
        Start-Process -FilePath $exe -WorkingDirectory $DistDir
    }
    Start-Sleep -Milliseconds 2500
    $proc = Get-AppProcess
    if ($proc) { Write-Ok "Started $([IO.Path]::GetFileName($exe)) (pid $($proc.Id))." }
    else       { throw "Launched $exe but no process appeared -- check $StateDir\crash.log." }
}

function Invoke-Stop {
    param([switch]$Deliberate = $true)
    Assert-AppControl
    $proc = Get-AppProcess
    if (-not $proc) {
        Write-Step 'Not running.'
        if ($Deliberate) { New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
                           Set-Content -Path $QuitFlag -Value (Get-Date -Format o) }
        return
    }
    $instance = $null
    if ((Test-ElevatedTask) -and -not (Test-Administrator)) {
        $instance = Get-AppTaskInstance -ProcessId $proc.Id
        if ($null -eq $instance) { throw 'WinSnipper task ownership changed before stop. No changes were made.' }
    }
    $hadQuitFlag = Test-Path $QuitFlag
    # Written before the stop so the keep-alive task doesn't race us and
    # bring it straight back.
    if ($Deliberate) {
        New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
        Set-Content -Path $QuitFlag -Value (Get-Date -Format o)
    }
    try {
        if ($null -ne $instance) {
            # Task Scheduler permits its owner to stop their own elevated task.
            # This targets the exact verified action/PID and grants no new rights.
            $instance.Stop()
        } else { Stop-Process -Id $proc.Id -Force }
        # A task stop can take several seconds to terminate the process.
        for ($attempt = 0; $attempt -lt 150; $attempt++) {
            $remaining = Get-AppProcess
            if (-not $remaining) { break }
            if ($remaining.Id -ne $proc.Id) { throw 'A different WinSnipper instance started during stop.' }
            Start-Sleep -Milliseconds 100
        }
        if (Get-AppProcess) { throw 'WinSnipper did not exit after the stop request.' }
    } catch {
        if ($Deliberate -and -not $hadQuitFlag) { Remove-Item $QuitFlag -Force -ErrorAction SilentlyContinue }
        throw
    }
    Write-Ok "Stopped (pid $($proc.Id))."
}

function Invoke-Restart {
    Invoke-Stop # Suppress watchdog races until Invoke-Start clears the marker.
    Start-Sleep -Milliseconds 500
    Invoke-Start
}

function Invoke-Build {
    $which = if ($Flavor) { $Flavor } else {
        if (Test-Path (Join-Path $DistDir 'WinSnipper-OCR.exe')) { 'ocr' } else { 'lite' }
    }
    $wasRunning  = [bool](Get-AppProcess)
    $wasQuitFlag = Test-Path $QuitFlag

    # The keep-alive has to stand down for the whole publish, or a tick lands
    # mid-build, relaunches the app and locks the exe we're about to replace.
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    Set-Content -Path $QuitFlag -Value (Get-Date -Format o)
    try {
        if ($wasRunning) {
            Write-Step 'Stopping the running instance so the exe can be replaced.'
            Invoke-Stop -Deliberate:$false
        }
        Use-SsdInstall
        $csproj = Join-Path $Root 'WinSnipper.csproj'
        $targets = @()
        if ($which -in 'lite', 'both') { $targets += @{ Ocr = $false; Sub = 'lite'; Out = 'WinSnipper.exe' } }
        if ($which -in 'ocr',  'both') { $targets += @{ Ocr = $true;  Sub = 'ocr';  Out = 'WinSnipper-OCR.exe' } }

        foreach ($t in $targets) {
            $stage = Join-Path $DistDir $t.Sub
            Write-Step "Publishing $($t.Sub) flavor..."
            $args = @(
                'publish', $csproj,
                '-c', 'Release',
                '-r', 'win-x64',
                '--self-contained', 'false',
                '-p:PublishSingleFile=true',
                "-p:EnableOcr=$($t.Ocr.ToString().ToLower())",
                '-o', $stage,
                '-v', 'quiet', '--nologo'
            )
            & dotnet @args
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for the $($t.Sub) flavor." }
            Copy-Item (Join-Path $stage 'WinSnipper.exe') (Join-Path $DistDir $t.Out) -Force
            Write-Ok "dist\$($t.Out)"
        }
        if ($RegisterCapture) { Register-CaptureDefault }
    }
    finally {
        # Invoke-Start clears the flag itself; otherwise put it back as it was.
        if ($wasRunning) {
            # A failed stop may leave the original process alive. Do not leave
            # a newly-created quit marker disabling its future crash recovery.
            if (-not $wasQuitFlag) { Remove-Item $QuitFlag -Force -ErrorAction SilentlyContinue }
            Invoke-Start
        }
        elseif (-not $wasQuitFlag) { Remove-Item $QuitFlag -Force -ErrorAction SilentlyContinue }
    }
}

function Install-KeepAlive {
    $exe = Assert-Exe
    $runLevel = if ($Elevated -or (Test-ElevatedTask)) { 'Highest' } else { 'Limited' }

    # One action, two reasons to fire: at logon, and every couple of minutes
    # after. MultipleInstances=IgnoreNew means the ticks are free while the
    # app is alive -- Task Scheduler counts the running exe as the task.
    $action = New-ScheduledTaskAction -Execute $exe -Argument '--watchdog' -WorkingDirectory $DistDir

    # The pulse has to be its own trigger. Hanging a Repetition off the logon
    # trigger looks right and silently does nothing until the next logon.
    $atLogon = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $pulse   = New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
                 -RepetitionInterval (New-TimeSpan -Minutes $CheckMins) `
                 -RepetitionDuration ([TimeSpan]::FromDays(3650))
    $trigger = @($atLogon, $pulse)

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
        -LogonType Interactive -RunLevel $runLevel

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal -Force `
        -Description 'Restarts WinSnipper within a couple of minutes if it crashes or is killed. Honours a deliberate tray Exit.' | Out-Null

    Write-Ok "Keep-alive task registered (checks every $CheckMins min, $runLevel privileges)."
}

function Install-Autostart {
    if (Test-ElevatedTask) {
        # The logon task must win. A competing HKCU Run launch would claim the
        # single-instance mutex first at medium integrity and recreate the bug.
        Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
        Write-Ok 'Administrator startup is handled by the keep-alive logon task.'
        return
    }
    $exe = Assert-Exe
    New-Item -Path $RunKey -Force | Out-Null
    Set-ItemProperty -Path $RunKey -Name $RunValue -Value "`"$exe`""
    Write-Ok 'Autostart at logon enabled.'
}

function Install-Shortcuts {
    $exe = Assert-Exe
    $shell = New-Object -ComObject WScript.Shell
    $targets = @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinSnipper.lnk'),
        (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\WinSnipper.lnk')
    )
    foreach ($path in $targets) {
        New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
        $lnk = $shell.CreateShortcut($path)
        if (Test-ElevatedTask) {
            $lnk.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
            $lnk.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`" start"
        } else {
            $lnk.TargetPath = $exe
            $lnk.Arguments = ''
        }
        $lnk.WorkingDirectory = $DistDir
        $lnk.IconLocation     = $exe
        $lnk.Description      = 'WinSnipper - snip, annotate, OCR, record'
        $lnk.Save()
    }
    Write-Ok 'Desktop and Start Menu shortcuts created.'
}

function Invoke-Setup {
    # Check before building, writing settings/tasks or stopping anything.
    if (($Elevated -or (Test-ElevatedTask)) -and -not (Test-Administrator)) {
        throw 'Administrator hotkey setup requires an elevated PowerShell window. No setup changes were made.'
    }
    Write-Host "`nWinSnipper setup" -ForegroundColor Cyan
    if ($Rebuild) {
        $Flavor = 'both'
        Invoke-Build
    }
    Assert-Exe | Out-Null
    Install-KeepAlive
    Install-Autostart
    Install-Shortcuts
    if ($RegisterCapture -and -not $Rebuild) { Register-CaptureDefault }
    if ($Elevated) { Invoke-Restart } else { Invoke-Start }
    Write-Host ''
    Invoke-Status
}

# WinSnipper switches Windows' own PrintScreen -> Snipping Tool binding off
# while it is the capture tool, and remembers what it found. Uninstalling has
# to put that back, or PrintScreen ends up doing nothing at all.
function Restore-PrintScreen {
    $kb = 'HKCU:\Control Panel\Keyboard'
    $name = 'PrintScreenKeyForSnippingTool'
    # Set-StrictMode makes a missing property fatal, so look before reading.
    $key = Get-ItemProperty -Path $kb -ErrorAction SilentlyContinue
    if (-not $key -or ($key.PSObject.Properties.Name -notcontains $name)) { return }
    $saved = -1
    $applied = 0
    $settings = Join-Path $StateDir 'settings.json'
    if (Test-Path $settings) {
        try {
            $data = Get-Content $settings -Raw | ConvertFrom-Json
            $saved = [int]$data.SavedPrintScreenBinding
            if ($data.PSObject.Properties.Name -contains 'PrintScreenBindingCaptured') {
                if (-not $data.PrintScreenBindingCaptured) { return }
                if ($null -ne $data.AppliedPrintScreenBinding) { $applied = [int]$data.AppliedPrintScreenBinding }
            }
        } catch { return } # Unknown ownership is not permission to modify Windows.
    }
    else { return }
    if ($key.$name -ne $applied) { return } # user changed it outside WinSnipper
    if ($saved -ge 0) { Set-ItemProperty -Path $kb -Name $name -Value $saved -Type DWord }
    else { Remove-ItemProperty -Path $kb -Name $name -ErrorAction SilentlyContinue }
    Write-Ok 'PrintScreen handed back to Windows.'
}

function Invoke-Uninstall {
    Write-Host "`nRemoving WinSnipper's supervision (the app and its files stay)" -ForegroundColor Cyan
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Ok 'Keep-alive task removed.'
    Remove-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
    Write-Ok 'Autostart removed.'
    foreach ($p in @((Join-Path ([Environment]::GetFolderPath('Desktop')) 'WinSnipper.lnk'),
                     (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\WinSnipper.lnk'))) {
        Remove-Item $p -Force -ErrorAction SilentlyContinue
    }
    Write-Ok 'Shortcuts removed.'
    Restore-PrintScreen
    Write-Step "Still running? Stop it with: tools\winsnipper.ps1 stop"
}

function Invoke-Status {
    $exe  = Get-Exe
    $proc = Get-AppProcess

    Write-Host 'WinSnipper' -ForegroundColor Cyan
    if (-not $exe) {
        Write-Warn "No build in $DistDir. Run: tools\winsnipper.ps1 setup"
    } else {
        $ver = (Get-Item $exe).VersionInfo.FileVersion
        Write-Step "exe      $exe  (v$ver, built $((Get-Item $exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm')))"
        $dist = Get-Item -LiteralPath $DistDir -Force
        if ([string]$dist.LinkType -eq 'Junction') { Write-Ok "install  $(@($dist.Target)[0]) (dist is a junction)" }
        else { Write-Warn "install  $DistDir is a real folder; the next build moves the install to $InstallDir" }
    }

    if ($proc) {
        try {
            $up = (Get-Date) - $proc.StartTime
            Write-Ok ("running  pid {0}, up {1:%d}d {1:%h}h {1:%m}m, {2} MB" -f `
                $proc.Id, $up, [math]::Round($proc.WorkingSet64 / 1MB, 1))
        } catch {
            Write-Ok "running  pid $($proc.Id) (process details unavailable from this shell)"
        }
    } else {
        Write-Warn 'running  no'
    }

    if (Test-Path $QuitFlag) {
        Write-Warn "quit     you exited from the tray on $(Get-Content $QuitFlag) -- the keep-alive is standing down until you start it again"
    }

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        $info = Get-ScheduledTaskInfo -TaskName $TaskName
        Write-Ok "watchdog $($task.State), privileges $($task.Principal.RunLevel), last ran $($info.LastRunTime), next $($info.NextRunTime)"
    } else {
        Write-Warn "watchdog not installed. Run: tools\winsnipper.ps1 setup"
    }

    $run = Get-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
    if ($run) { Write-Ok "autostart $($run.$RunValue)" }
    elseif ((Test-ElevatedTask) -and [string]$task.State -ne 'Disabled') { Write-Ok 'autostart elevated logon task (no competing standard-user launch)' }
    else { Write-Warn 'autostart off' }

    # Process health alone does not prove exclusive ownership of the shortcuts.
    # These checks are read-only; never remove or stop the native app from status.
    $keyboard = Get-Item 'HKCU:\Control Panel\Keyboard' -ErrorAction SilentlyContinue
    $isCaptureDefault = Test-CaptureDefault
    $binding = if ($keyboard) { $keyboard.GetValue('PrintScreenKeyForSnippingTool') } else { $null }
    if ($null -ne $binding -and $binding -eq 0) {
        Write-Ok 'PrintScreen Windows Snipping Tool binding is off.'
    } elseif ($binding -eq 1 -and $isCaptureDefault) {
        Write-Ok 'PrintScreen Windows capture route is on and its default is WinSnipper.'
    } else {
        Write-Warn 'PrintScreen Windows Snipping Tool binding is on or uses the Windows default.'
    }
    if ($isCaptureDefault) { Write-Ok 'default  Windows capture links open WinSnipper, including on-demand launch.' }
    else { Write-Warn 'default  Windows capture still points elsewhere; interception requires WinSnipper to be running.' }

    try {
        $native = @(Get-AppxPackage -Name Microsoft.ScreenSketch -ErrorAction Stop)
        if ($native.Count) {
            Write-Warn "native   Microsoft Snipping Tool is installed (v$($native[0].Version))."
            if (-not $isCaptureDefault) {
                Write-Step '         Win+Shift+S still depends on the WinSnipper keyboard hook winning.'
            }
        } else {
            Write-Ok 'native   Microsoft Snipping Tool is not registered for this user.'
        }
    } catch {
        Write-Warn "native   Could not inspect Snipping Tool registration: $($_.Exception.Message)"
    }
    $nativeProcesses = @(Get-Process -Name SnippingTool,ScreenClippingHost -ErrorAction SilentlyContinue)
    if ($nativeProcesses.Count) {
        Write-Warn "native   Running: $(($nativeProcesses | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', ')."
    }
    Write-Step '         Screenshot conflict guidance: docs\SCREENSHOT-OWNERSHIP.md'
}

function Invoke-Logs {
    foreach ($name in 'session.log', 'crash.log', 'recorder.log') {
        $path = Join-Path $StateDir $name
        Write-Host "`n--- $name" -ForegroundColor Cyan
        if (Test-Path $path) { Get-Content $path -Tail $Tail }
        else { Write-Step '(empty)' }
    }
}

# ---------------------------------------------------------------- dispatch

# A limited shell may control only its exact, current-user supervised instance.
# Validate before changing the quit marker or publishing any files.
if ($Command -in @('stop', 'restart', 'build')) { Assert-AppControl }

switch ($Command) {
    'setup'     { Invoke-Setup }
    'start'     { Invoke-Start }
    'stop'      { Invoke-Stop }
    'restart'   { Invoke-Restart }
    'build'     { Invoke-Build }
    'status'    { Invoke-Status }
    'logs'      { Invoke-Logs }
    'uninstall' { Invoke-Uninstall }
}
