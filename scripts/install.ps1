<#
.SYNOPSIS
    Builds AiUsageTray from source and installs it for the current user.

.DESCRIPTION
    One-step install:
      1. Publishes a Release build (framework-dependent by default).
      2. Stops any running AiUsageTray instance so files can be replaced.
      3. Copies the publish output to the install directory
         (default: %LOCALAPPDATA%\Programs\AiUsageTray).
      4. Registers a Scheduled Task that starts the app at user logon
         (replaces the older HKCU Run-key entry if one exists).
      5. Starts the app via the scheduled task.

    No administrator rights are required - everything is per-user.

.PARAMETER InstallDir
    Where the app is installed. Default: %LOCALAPPDATA%\Programs\AiUsageTray.

.PARAMETER SelfContained
    Publish self-contained single-file (no .NET 8 Desktop Runtime needed on
    the machine afterwards; larger output).

.PARAMETER NoScheduledTask
    Skip Scheduled Task registration (install files only).

.PARAMETER NoStart
    Do not start the app after installing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\AiUsageTray",
    [switch]$SelfContained,
    [switch]$NoScheduledTask,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$TaskName    = 'AiUsageTray'
$ProcessName = 'AiUsageTray'
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$Project     = Join-Path $RepoRoot 'src\AiUsageTray\AiUsageTray.csproj'
$StageDir    = Join-Path $RepoRoot 'publish\AiUsageTray'
$ExePath     = Join-Path $InstallDir 'AiUsageTray.exe'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# --- 0. Preconditions -------------------------------------------------------

if (-not (Test-Path $Project)) {
    throw "Project not found at '$Project'. Run this script from a clone of the repository."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is required to build. Install it from https://dotnet.microsoft.com/download/dotnet/8.0'
}

if (-not $SelfContained) {
    $desktopRuntimes = & dotnet --list-runtimes | Where-Object { $_ -like 'Microsoft.WindowsDesktop.App 8.*' }
    if (-not $desktopRuntimes) {
        Write-Warning ('.NET 8 Desktop Runtime not detected. The app may fail to start. ' +
            'Install it, or re-run with -SelfContained to bundle the runtime.')
    }
}

# --- 1. Build & publish (before stopping the app, to minimize downtime) -----

Write-Step "Publishing Release build to $StageDir"
if (Test-Path $StageDir) {
    Remove-Item $StageDir -Recurse -Force
}

$publishArgs = @(
    'publish', $Project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $StageDir
)
if ($SelfContained) {
    $publishArgs += @('--self-contained', 'true', '-p:PublishSingleFile=true')
} else {
    $publishArgs += @('--self-contained', 'false')
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# --- 2. Stop running instances ----------------------------------------------

$running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Step "Stopping $($running.Count) running AiUsageTray instance(s)"
    try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction Stop } catch { }
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    # Give Windows a moment to release file locks on the old executable.
    Start-Sleep -Milliseconds 500
}

# --- 3. Copy files into the install directory --------------------------------

Write-Step "Installing to $InstallDir"
if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
}
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $StageDir '*') -Destination $InstallDir -Recurse -Force

if (-not (Test-Path $ExePath)) {
    throw "Install verification failed: '$ExePath' does not exist."
}

# --- 4. Register the Scheduled Task (start at logon) -------------------------

if (-not $NoScheduledTask) {
    Write-Step "Registering Scheduled Task '$TaskName' (runs at logon)"

    $action  = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $InstallDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    # Small delay so the taskbar/notification area is ready before the tray icon registers.
    $trigger.Delay = 'PT10S'

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Seconds 0)

    $principal = New-ScheduledTaskPrincipal `
        -UserId "$env:USERDOMAIN\$env:USERNAME" `
        -LogonType Interactive `
        -RunLevel Limited

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal -Force | Out-Null

    # The scheduled task replaces the older "Start with Windows" Run-key entry;
    # remove it so the app is not started twice at logon.
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runEntry = Get-ItemProperty -Path $runKey -Name $TaskName -ErrorAction SilentlyContinue
    if ($runEntry) {
        Remove-ItemProperty -Path $runKey -Name $TaskName
        Write-Host "    Removed legacy Run-key startup entry (replaced by the scheduled task)."
    }
}

# --- 5. Start the app ---------------------------------------------------------

if (-not $NoStart) {
    Write-Step 'Starting AiUsageTray'
    if ($NoScheduledTask) {
        Start-Process -FilePath $ExePath -WorkingDirectory $InstallDir
    } else {
        Start-ScheduledTask -TaskName $TaskName
    }
}

Write-Host ''
Write-Host 'Install complete.' -ForegroundColor Green
Write-Host "  App:            $ExePath"
if (-not $NoScheduledTask) {
    Write-Host "  Scheduled task: $TaskName (starts at logon, 10s delay)"
}
Write-Host "  App data/logs:  $env:LOCALAPPDATA\AiUsageTray"
Write-Host "  Uninstall:      powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1"

exit 0
