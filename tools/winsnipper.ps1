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
    [int]$Tail = 25
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root      = Split-Path -Parent $PSScriptRoot
$DistDir   = Join-Path $Root 'dist'
$StateDir  = Join-Path $env:APPDATA 'WinSnipper'
$QuitFlag  = Join-Path $StateDir 'user-quit.flag'
$TaskName  = 'WinSnipper Keep-Alive'
$RunKey    = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$RunValue  = 'WinSnipper'
$CheckMins = 2

# ---------------------------------------------------------------- helpers

function Write-Step($msg) { Write-Host "  $msg" -ForegroundColor DarkGray }
function Write-Ok($msg)   { Write-Host "  $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "  $msg" -ForegroundColor Yellow }

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

# ---------------------------------------------------------------- actions

function Invoke-Start {
    $exe = Assert-Exe
    $proc = Get-AppProcess
    if ($proc) {
        Write-Ok "Already running (pid $($proc.Id))."
        return
    }
    Remove-Item $QuitFlag -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $exe -WorkingDirectory $DistDir
    Start-Sleep -Milliseconds 2500
    $proc = Get-AppProcess
    if ($proc) { Write-Ok "Started $([IO.Path]::GetFileName($exe)) (pid $($proc.Id))." }
    else       { throw "Launched $exe but no process appeared -- check $StateDir\crash.log." }
}

function Invoke-Stop {
    param([switch]$Deliberate = $true)
    $proc = Get-AppProcess
    if (-not $proc) {
        Write-Step 'Not running.'
        if ($Deliberate) { New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
                           Set-Content -Path $QuitFlag -Value (Get-Date -Format o) }
        return
    }
    # Written before the kill so the keep-alive task doesn't race us and
    # bring it straight back.
    if ($Deliberate) {
        New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
        Set-Content -Path $QuitFlag -Value (Get-Date -Format o)
    }
    Stop-Process -Id $proc.Id -Force
    Start-Sleep -Milliseconds 800
    Write-Ok "Stopped (pid $($proc.Id))."
}

function Invoke-Restart {
    Invoke-Stop -Deliberate:$false
    Start-Sleep -Milliseconds 500
    Invoke-Start
}

function Invoke-Build {
    $which = if ($Flavor) { $Flavor } else {
        if (Test-Path (Join-Path $DistDir 'WinSnipper-OCR.exe')) { 'ocr' } else { 'lite' }
    }
    $wasRunning = [bool](Get-AppProcess)
    if ($wasRunning) {
        Write-Step 'Stopping the running instance so the exe can be replaced.'
        Invoke-Stop -Deliberate:$false
    }

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

    if ($wasRunning) { Invoke-Start }
}

function Install-KeepAlive {
    $exe = Assert-Exe

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
        -LogonType Interactive -RunLevel Limited

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal -Force `
        -Description 'Restarts WinSnipper within a couple of minutes if it crashes or is killed. Honours a deliberate tray Exit.' | Out-Null

    Write-Ok "Keep-alive task registered (checks every $CheckMins min)."
}

function Install-Autostart {
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
        $lnk.TargetPath       = $exe
        $lnk.WorkingDirectory = $DistDir
        $lnk.IconLocation     = $exe
        $lnk.Description      = 'WinSnipper - snip, annotate, OCR, record'
        $lnk.Save()
    }
    Write-Ok 'Desktop and Start Menu shortcuts created.'
}

function Invoke-Setup {
    Write-Host "`nWinSnipper setup" -ForegroundColor Cyan
    Assert-Exe | Out-Null
    Install-Autostart
    Install-KeepAlive
    Install-Shortcuts
    Invoke-Start
    Write-Host ''
    Invoke-Status
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
    }

    if ($proc) {
        $up = (Get-Date) - $proc.StartTime
        Write-Ok ("running  pid {0}, up {1:%d}d {1:%h}h {1:%m}m, {2} MB" -f `
            $proc.Id, $up, [math]::Round($proc.WorkingSet64 / 1MB, 1))
    } else {
        Write-Warn 'running  no'
    }

    if (Test-Path $QuitFlag) {
        Write-Warn "quit     you exited from the tray on $(Get-Content $QuitFlag) -- the keep-alive is standing down until you start it again"
    }

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        $info = Get-ScheduledTaskInfo -TaskName $TaskName
        Write-Ok "watchdog $($task.State), last ran $($info.LastRunTime), next $($info.NextRunTime)"
    } else {
        Write-Warn "watchdog not installed. Run: tools\winsnipper.ps1 setup"
    }

    $run = Get-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue
    if ($run) { Write-Ok "autostart $($run.$RunValue)" } else { Write-Warn 'autostart off' }
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
