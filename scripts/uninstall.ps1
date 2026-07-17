<#
.SYNOPSIS
    Uninstalls AiUsageTray for the current user.

.DESCRIPTION
    Stops any running instance, removes the Scheduled Task and the legacy
    Run-key startup entry, and deletes the install directory. Application
    data (settings, logs, Claude bridge cache) under %LOCALAPPDATA%\AiUsageTray
    is kept unless -PurgeData is passed.

    Note: this does NOT remove the Claude status-line bridge from
    ~/.claude/settings.json - use Settings -> Providers -> Claude Code ->
    "Remove integration" inside the app before uninstalling if you want
    your original status line restored.

.PARAMETER InstallDir
    Install directory to remove. Default: %LOCALAPPDATA%\Programs\AiUsageTray.

.PARAMETER PurgeData
    Also delete %LOCALAPPDATA%\AiUsageTray (settings, logs, cached usage data).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\AiUsageTray",
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'

$TaskName    = 'AiUsageTray'
$ProcessName = 'AiUsageTray'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Stop running instances.
$running = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Step "Stopping $($running.Count) running AiUsageTray instance(s)"
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# Remove the scheduled task.
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    Write-Step "Removing Scheduled Task '$TaskName'"
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

# Remove the legacy Run-key startup entry, if present.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runEntry = Get-ItemProperty -Path $runKey -Name $TaskName -ErrorAction SilentlyContinue
if ($runEntry) {
    Write-Step 'Removing Run-key startup entry'
    Remove-ItemProperty -Path $runKey -Name $TaskName
}

# Remove installed files.
if (Test-Path $InstallDir) {
    Write-Step "Removing $InstallDir"
    Remove-Item $InstallDir -Recurse -Force
}

# Optionally remove app data.
$dataDir = Join-Path $env:LOCALAPPDATA 'AiUsageTray'
if ($PurgeData) {
    if (Test-Path $dataDir) {
        Write-Step "Removing app data at $dataDir"
        Remove-Item $dataDir -Recurse -Force
    }
} elseif (Test-Path $dataDir) {
    Write-Host "App data kept at $dataDir (re-run with -PurgeData to delete)."
}

Write-Host ''
Write-Host 'Uninstall complete.' -ForegroundColor Green

exit 0
