<#
.SYNOPSIS
    Direct single-session launcher for a published ClaudeShell install: starts the
    Blazor host (which starts and supervises the sidecar itself) and opens the browser.

.PARAMETER ShellRoot
    The install root (the folder holding host\ and sidecar\). Defaults to the parent
    of this script's folder, which is correct inside a publish-live.ps1 install.

.PARAMETER Workspace
    Working directory for Claude. If omitted, the host uses %TEMP%\ClaudeShell.

.PARAMETER Port
    Host UI port (default 5000).

.PARAMETER SidecarPort
    Sidecar port (default 6110). Change both ports + workspace to run a second session,
    or just use the Launcher (launcher\ClaudeShell.Launcher.exe).

.EXAMPLE
    .\launch-shell.ps1
    .\launch-shell.ps1 -Workspace C:\MyProject -Port 5001 -SidecarPort 6111
#>
[CmdletBinding()]
param(
    [string]$ShellRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Workspace,
    [int]$Port = 5000,
    [int]$SidecarPort = 6110
)

$ErrorActionPreference = 'Stop'

$hostExe = Join-Path $ShellRoot 'host\ClaudeWorkbench.Host.exe'
if (-not (Test-Path $hostExe)) {
    throw "Host exe not found at $hostExe. Run scripts\publish-live.ps1 first (or pass -ShellRoot)."
}

$url = "http://localhost:$Port"

Write-Host ''
Write-Host 'Starting ClaudeShell...' -ForegroundColor Cyan
if ($Workspace) {
    New-Item -ItemType Directory -Force -Path $Workspace | Out-Null
    Write-Host "  Workspace: $Workspace" -ForegroundColor Gray
} else {
    Write-Host "  Workspace: %TEMP%\ClaudeShell (default)" -ForegroundColor Gray
}

# The host launches and supervises the sidecar itself (from <root>\sidecar).
$env:ASPNETCORE_URLS = $url
$env:Sidecar__Port = "$SidecarPort"
if ($Workspace) { $env:WORKSPACE = $Workspace } else { Remove-Item Env:WORKSPACE -ErrorAction SilentlyContinue }

$hostProc = Start-Process -FilePath $hostExe -WorkingDirectory (Split-Path -Parent $hostExe) -PassThru -NoNewWindow

# Wait for the host, then open the browser.
Write-Host "  Waiting for $url..." -ForegroundColor Gray
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        $response = Invoke-WebRequest -Uri "$url/health" -TimeoutSec 1 -UseBasicParsing -ErrorAction Stop
        if ($response.StatusCode -eq 200) { $ready = $true; break }
    } catch { Start-Sleep -Milliseconds 500 }
}

if (-not $ready) {
    Write-Warning 'Host did not respond in 30s; it may still be starting.'
}

Start-Process $url
Write-Host ''
Write-Host "ClaudeShell is running at $url (host pid $($hostProc.Id))." -ForegroundColor Green
Write-Host 'Press Ctrl+C or close this window to stop it.' -ForegroundColor Yellow

try {
    $hostProc.WaitForExit()
}
finally {
    if (-not $hostProc.HasExited) {
        Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue
    }
    Write-Host 'Done.' -ForegroundColor Green
}
