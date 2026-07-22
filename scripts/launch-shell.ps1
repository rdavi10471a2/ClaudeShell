<#
.SYNOPSIS
    Direct launcher for ClaudeShell: starts the Blazor host and BasicSidecar, opens the browser.
    No Launcher GUI — just spin up Claude and go.

.DESCRIPTION
    This script starts ClaudeShell components directly without the Launcher app:
    1. Starts the BasicSidecar (Node)
    2. Starts the Blazor host (.NET)
    3. Opens http://localhost:5000 in the browser
    4. Provides a simple stop command or Ctrl+C to shut down both

    If no workspace is specified, uses %TEMP%\ClaudeShell as a disposable workspace.

.PARAMETER ShellRoot
    Path to the ClaudeShell install root (default: current directory).

.PARAMETER Workspace
    Working directory for Claude. If omitted, uses %TEMP%\ClaudeShell.
    This is NOT watched/indexed — it's just the cwd for tool execution (Read, Bash, PowerShell, etc.).

.PARAMETER NoSidecar
    Skip starting the sidecar (assumes it's already running elsewhere).

.PARAMETER Port
    Host port (default: 5000).

.EXAMPLE
    .\launch-shell.ps1
    .\launch-shell.ps1 -ShellRoot C:\ClaudeShellLive
    .\launch-shell.ps1 -Workspace C:\MyProject
    .\launch-shell.ps1 -NoSidecar
#>
[CmdletBinding()]
param(
    [string]$ShellRoot = $PSScriptRoot,
    [string]$Workspace,
    [switch]$NoSidecar,
    [int]$Port = 5000
)

$ErrorActionPreference = 'Stop'

# Resolve paths
if (-not (Test-Path $ShellRoot)) {
    throw "ShellRoot not found: $ShellRoot"
}

$hostPath = Join-Path $ShellRoot 'host'
$sidecarPath = Join-Path $ShellRoot 'sidecar'
$hostExe = Join-Path $hostPath 'ClaudeWorkbench.Host.exe'

if (-not (Test-Path $hostExe)) {
    throw "Host exe not found at $hostExe. Is this a valid ClaudeShell install?"
}

# Workspace setup
if (-not $Workspace) {
    $Workspace = Join-Path $env:TEMP 'ClaudeShell'
    $tempWorkspace = $true
} else {
    $tempWorkspace = $false
}

if (-not (Test-Path $Workspace)) {
    Write-Host "Creating workspace: $Workspace" -ForegroundColor Gray
    New-Item -ItemType Directory -Path $Workspace -Force | Out-Null
}

Write-Host ''
Write-Host 'Starting ClaudeShell...' -ForegroundColor Cyan
Write-Host "  Workspace: $Workspace" -ForegroundColor Gray
if ($tempWorkspace) {
    Write-Host "  (using temp workspace; see Launcher GUI to pick a real project)" -ForegroundColor DarkGray
}

# Sidecar (if not skipped)
$sidecarJob = $null
if (-not $NoSidecar) {
    Write-Host '  → Starting BasicSidecar (Node)...' -ForegroundColor Gray
    $sidecarJs = Join-Path $sidecarPath 'dist\index.js'
    if (-not (Test-Path $sidecarJs)) {
        Write-Warning "Sidecar dist/index.js not found at $sidecarJs. Skipping sidecar."
    } else {
        try {
            $sidecarJob = Start-Process -FilePath node -ArgumentList $sidecarJs -WorkingDirectory $sidecarPath -PassThru -NoNewWindow
            Start-Sleep -Milliseconds 500
            Write-Host '  ✓ BasicSidecar started (PID: ' -NoNewline -ForegroundColor Green
            Write-Host $sidecarJob.Id -NoNewline
            Write-Host ')'
        } catch {
            Write-Warning "Failed to start sidecar: $_"
        }
    }
}

# Host
Write-Host '  → Starting Blazor host...' -ForegroundColor Gray
$env:ASPNETCORE_URLS = "http://localhost:$Port"
$env:WATCHED_SOLUTION_PATH = $Workspace
try {
    $hostJob = Start-Process -FilePath $hostExe -WorkingDirectory $hostPath -PassThru -NoNewWindow
    Write-Host '  ✓ Host started (PID: ' -NoNewline -ForegroundColor Green
    Write-Host $hostJob.Id -NoNewline
    Write-Host ')'
}
catch {
    Write-Error "Failed to start host: $_"
    if ($sidecarJob) { Stop-Process -Id $sidecarJob.Id -Force -ErrorAction SilentlyContinue }
    exit 1
}

# Wait for host to be ready
Write-Host "  → Waiting for host to be ready at http://localhost:$Port..." -ForegroundColor Gray
$maxRetries = 30
$retries = 0
while ($retries -lt $maxRetries) {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$Port" -TimeoutSec 1 -ErrorAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            Write-Host '  ✓ Host is ready' -ForegroundColor Green
            break
        }
    } catch {
        # Host not ready yet
    }
    $retries++
    Start-Sleep -Milliseconds 200
}

if ($retries -eq $maxRetries) {
    Write-Warning "Host did not respond after ${maxRetries}s. It may still be starting..."
}

# Open browser
Write-Host "  → Opening browser at http://localhost:$Port..." -ForegroundColor Gray
Start-Process "http://localhost:$Port"

Write-Host ''
Write-Host 'ClaudeShell is running.' -ForegroundColor Green
Write-Host "  Host:      http://localhost:$Port"
Write-Host "  Workspace: $Workspace"
Write-Host "  Sidecar:   http://localhost:6110" -ForegroundColor Dim
Write-Host ''
Write-Host 'Press Ctrl+C to stop, or type "exit" to close this window.' -ForegroundColor Yellow
Write-Host ''

# Wait for processes
try {
    if ($hostJob -and -not $hostJob.HasExited) {
        $hostJob.WaitForExit()
    }
}
catch {
    # Process ended
}
finally {
    # Cleanup
    if ($sidecarJob -and -not $sidecarJob.HasExited) {
        Write-Host ''
        Write-Host 'Stopping sidecar...' -ForegroundColor Gray
        Stop-Process -Id $sidecarJob.Id -Force -ErrorAction SilentlyContinue
    }
    if ($hostJob -and -not $hostJob.HasExited) {
        Write-Host 'Stopping host...' -ForegroundColor Gray
        Stop-Process -Id $hostJob.Id -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'Done.' -ForegroundColor Green
}
