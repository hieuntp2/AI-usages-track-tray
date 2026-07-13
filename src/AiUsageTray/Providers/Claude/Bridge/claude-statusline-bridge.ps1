#Requires -Version 5.1
<#
    AI Usage Tray - Claude Code status-line bridge.

    Claude Code invokes this script as its statusLine command, sending session JSON on stdin.
    This script:
      1. Caches that JSON (wrapped with a local capture timestamp) for AI Usage Tray to read.
      2. Forwards the same JSON to the user's previously-configured status-line command, if any,
         and relays that command's stdout back to Claude Code - so the visible status line is
         completely unchanged from the user's point of view.

    This script never reads or transmits credentials, and never calls any network endpoint.
    Managed by AI Usage Tray - see "Repair integration" / "Remove integration" in Settings.
#>

$ErrorActionPreference = 'Stop'

$root = Join-Path $env:LOCALAPPDATA 'AiUsageTray'
$dataDir = Join-Path $root 'data'
$configDir = Join-Path $root 'config'
$logDir = Join-Path $root 'logs'
$cacheFile = Join-Path $dataDir 'claude-latest.json'
$metadataFile = Join-Path $configDir 'claude-bridge.json'
$logFile = Join-Path $logDir 'claude-bridge.log'

function Write-BridgeLog {
    param([string]$Message)
    try {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        $line = '{0:yyyy-MM-dd HH:mm:ss.fff} {1}' -f (Get-Date), $Message
        Add-Content -Path $logFile -Value $line -Encoding utf8
    } catch {
        # Logging must never break the status line.
    }
}

# 1. Read the complete stdin JSON.
$stdin = [Console]::In.ReadToEnd()

# 2-5. Validate, wrap with a capture timestamp, write to a temp file, atomically replace the cache.
try {
    if (-not [string]::IsNullOrWhiteSpace($stdin)) {
        $null = $stdin | ConvertFrom-Json -ErrorAction Stop

        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        $capturedAt = (Get-Date).ToUniversalTime().ToString('o')
        $envelope = '{"capturedAt":"' + $capturedAt + '","payload":' + $stdin + '}'

        $tempFile = Join-Path $dataDir (".claude-latest.$([guid]::NewGuid().ToString('N')).tmp")
        [System.IO.File]::WriteAllText($tempFile, $envelope, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -Path $tempFile -Destination $cacheFile -Force
    }
} catch {
    Write-BridgeLog "Failed to cache payload: $($_.Exception.Message)"
}

# 6/7. Forward the same stdin to the user's original status-line command, when one was configured.
$originalCommand = $null
try {
    if (Test-Path $metadataFile) {
        $metadataRaw = Get-Content -Path $metadataFile -Raw
        $metadata = $metadataRaw | ConvertFrom-Json -ErrorAction Stop
        if ($metadata.originalCommand) {
            $originalCommand = [string]$metadata.originalCommand
        }
    }
} catch {
    Write-BridgeLog "Failed to read bridge metadata: $($_.Exception.Message)"
}

if ($originalCommand) {
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'cmd.exe'
        $psi.Arguments = '/c ' + $originalCommand
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        $proc = [System.Diagnostics.Process]::Start($psi)
        $proc.StandardInput.Write($stdin)
        $proc.StandardInput.Close()
        $forwardedOutput = $proc.StandardOutput.ReadToEnd()
        $proc.WaitForExit(5000) | Out-Null

        # 8/9. Relay only the original command's own stdout - no bridge diagnostics on the status line.
        [Console]::Out.Write($forwardedOutput)
    } catch {
        Write-BridgeLog "Failed to forward to original status-line command: $($_.Exception.Message)"
    }
}

# 10. Return quickly: no network calls, no heavy work beyond the above.
