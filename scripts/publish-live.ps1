<#
.SYNOPSIS
    Publishes a ClaudeShell "live" install: the Blazor host, the BasicSidecar, and the
    session Launcher, side by side in one folder, plus a desktop shortcut to the Launcher.

.DESCRIPTION
    Produces this layout, which the Launcher recognises as an install root:

        <Destination>\
            host\      ClaudeWorkbench.Host.exe (the Blazor app)
            sidecar\   dist\index.js + production node_modules (the Claude Agent SDK driver)
            launcher\  ClaudeShell.Launcher.exe (multi-session manager)

    The Launcher finds the host at <root>\host and the sidecar at <root>\sidecar, so the
    install works wherever the folder is put. Sessions started on a launcher-created temp
    workspace are deleted from disk when they stop.

    ASCII only, on purpose: Windows PowerShell 5.1 reads this file as ANSI and mangles any
    non-ASCII punctuation into a parse error.

.PARAMETER Destination
    Where to publish. Defaults to C:\ClaudeShellLive.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER NoShortcut
    Skip creating the desktop shortcut (one is still written into the install folder).

.PARAMETER Clean
    Remove the host\, sidecar\ and launcher\ folders first.

.EXAMPLE
    .\scripts\publish-live.ps1
    .\scripts\publish-live.ps1 -Destination D:\ClaudeShell -Clean
#>
[CmdletBinding()]
param(
    [string]$Destination = 'C:\ClaudeShellLive',
    [string]$Configuration = 'Release',
    [switch]$NoShortcut,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repoRoot 'src\ClaudeWorkbench.Host\ClaudeWorkbench.Host.csproj'
$launcherProject = Join-Path $repoRoot 'samples\launcher\ClaudeShell.Launcher.csproj'
$basicSidecar = Join-Path $repoRoot 'sidecar\basic'

foreach ($required in @($hostProject, $launcherProject, $basicSidecar)) {
    if (-not (Test-Path $required)) {
        throw "Not a ClaudeShell checkout - missing $required"
    }
}

$hostOut = Join-Path $Destination 'host'
$sidecarOut = Join-Path $Destination 'sidecar'
$launcherOut = Join-Path $Destination 'launcher'

# A running install holds its exes open and publish fails partway through with an
# unhelpful MSBuild error. Say so up front instead.
$inUse = Get-Process -Name 'ClaudeWorkbench.Host', 'ClaudeShell.Launcher' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase) }
if ($inUse) {
    $names = ($inUse | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
    throw "Close the running install first - $names is using $Destination."
}

Write-Host "Publishing ClaudeShell ($Configuration) -> $Destination" -ForegroundColor Cyan

if ($Clean) {
    foreach ($stale in @($hostOut, $sidecarOut, $launcherOut)) {
        if (Test-Path $stale) {
            Write-Host "  cleaning $stale"
            Remove-Item $stale -Recurse -Force
        }
    }
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# --- 1. Blazor host ---------------------------------------------------------------------
Write-Host ''
Write-Host '[1/4] Publishing host (Blazor)...' -ForegroundColor Cyan
dotnet publish $hostProject -c $Configuration -o $hostOut --nologo
if ($LASTEXITCODE -ne 0) { throw "Host publish failed ($LASTEXITCODE)." }

# --- 2. BasicSidecar --------------------------------------------------------------------
Write-Host ''
Write-Host '[2/4] Building BasicSidecar...' -ForegroundColor Cyan
$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (-not $npm) { $npm = Get-Command npm -ErrorAction SilentlyContinue }
if (-not $npm) { throw 'npm was not found on PATH - needed to build the sidecar.' }

Push-Location $basicSidecar
try {
    if (-not (Test-Path (Join-Path $basicSidecar 'node_modules'))) {
        & $npm.Source install
        if ($LASTEXITCODE -ne 0) { throw "npm install failed ($LASTEXITCODE)." }
    }

    & $npm.Source run build
    if ($LASTEXITCODE -ne 0) { throw "BasicSidecar build failed ($LASTEXITCODE)." }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $sidecarOut | Out-Null
Copy-Item (Join-Path $basicSidecar 'dist') $sidecarOut -Recurse -Force
Copy-Item (Join-Path $basicSidecar 'package.json') $sidecarOut -Force
$lockFile = Join-Path $basicSidecar 'package-lock.json'
if (Test-Path $lockFile) { Copy-Item $lockFile $sidecarOut -Force }

# Runtime dependencies only (the Agent SDK + express). Falls back to copying the
# checkout's node_modules when npm cannot reach the registry.
Write-Host '  installing production dependencies...'
Push-Location $sidecarOut
try {
    if (Test-Path (Join-Path $sidecarOut 'package-lock.json')) {
        & $npm.Source ci --omit=dev
    } else {
        & $npm.Source install --omit=dev
    }
}
finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $sidecarOut 'node_modules'))) {
    Write-Warning 'npm install failed (offline?) - copying the checkout node_modules instead.'
    $checkoutNodeModules = Join-Path $basicSidecar 'node_modules'
    if (Test-Path $checkoutNodeModules) {
        Copy-Item $checkoutNodeModules $sidecarOut -Recurse -Force
    }
}

if (-not (Test-Path (Join-Path $sidecarOut 'dist\index.js'))) {
    throw "BasicSidecar publish incomplete: $sidecarOut\dist\index.js is missing."
}

# --- 3. Launcher ------------------------------------------------------------------------
Write-Host ''
Write-Host '[3/4] Publishing the session Launcher...' -ForegroundColor Cyan
dotnet publish $launcherProject -c $Configuration -o $launcherOut --nologo
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed ($LASTEXITCODE)." }

$launcherExe = Join-Path $launcherOut 'ClaudeShell.Launcher.exe'
if (-not (Test-Path $launcherExe)) {
    throw "Launcher publish incomplete: $launcherExe is missing."
}

# Also ship the direct single-session script for shortcut-free use.
$scriptsOut = Join-Path $Destination 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsOut | Out-Null
Copy-Item (Join-Path $repoRoot 'scripts\launch-shell.ps1') $scriptsOut -Force

# --- 4. Shortcuts -----------------------------------------------------------------------
Write-Host ''
Write-Host '[4/4] Creating shortcuts...' -ForegroundColor Cyan

function New-LauncherShortcut {
    param([string]$LinkPath)
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($LinkPath)
        $shortcut.TargetPath = $launcherExe
        $shortcut.WorkingDirectory = $launcherOut
        $shortcut.Description = 'ClaudeShell Launcher - manage Claude sessions'
        $shortcut.Save()
        Write-Host "  $LinkPath"
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

New-LauncherShortcut -LinkPath (Join-Path $Destination 'ClaudeShell Launcher.lnk')
if (-not $NoShortcut) {
    New-LauncherShortcut -LinkPath (Join-Path ([Environment]::GetFolderPath('Desktop')) 'ClaudeShell Launcher.lnk')
}

Write-Host ''
Write-Host "Done. ClaudeShell install root: $Destination" -ForegroundColor Green
Write-Host "  host      $hostOut"
Write-Host "  sidecar   $sidecarOut"
Write-Host "  launcher  $launcherOut"
Write-Host ''
Write-Host 'Quick start:' -ForegroundColor Yellow
Write-Host '  Double-click "ClaudeShell Launcher" (desktop) -> New Session -> Open'
Write-Host '  Single session without the Launcher: scripts\launch-shell.ps1'
Write-Host ''
Write-Host 'Target machine requirements:' -ForegroundColor Yellow
Write-Host '  - .NET 10 runtime (or SDK)'
Write-Host '  - Node.js on PATH (the claude CLI ships inside the sidecar node_modules)'
Write-Host '  - a Claude login in ~\.claude'
