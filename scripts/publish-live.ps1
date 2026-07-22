<#
.SYNOPSIS
    Publishes a ClaudeShell "live" install: the Blazor host, the Node sidecar (BasicSidecar),
    and the Launcher, side by side in one folder, plus a shortcut to the Launcher.

.DESCRIPTION
    Produces this layout, which the Launcher recognises as a shell root:

        <Destination>\
            host\      ClaudeWorkbench.Host.exe (the Blazor app) + its config\
            sidecar\   dist\index.js + production node_modules
            launcher\  ClaudeWorkbench.Launcher.exe
            runtime\   created on first run: one folder per workspace

    The Launcher finds the host next to itself (<root>\host), the sidecar at <root>\sidecar,
    and provisions every instance into <root>\runtime\<workspace>. So this install works
    wherever it is put, and does not depend on the source checkout it was built from.

    runtime\ is never touched by this script: re-publishing over an existing install keeps
    the workspaces and indexes that are already there.

    ASCII only, on purpose: Windows PowerShell 5.1 reads this file as ANSI and mangles any
    non-ASCII punctuation into a parse error.

.PARAMETER Destination
    Where to publish. Defaults to C:\ClaudeShellLive.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER NoShortcut
    Skip creating the desktop shortcut (one is still written into the install folder).

.PARAMETER Clean
    Remove the host\, sidecar\ and launcher\ folders first. runtime\ is preserved.

.EXAMPLE
    .\scripts\publish-live.ps1
    .\scripts\publish-live.ps1 -Destination D:\Workbench -Clean
#>
[CmdletBinding()]
param(
    [string]$Destination = 'C:\ClaudeShellLive',
    [string]$Configuration = 'Release',
    [switch]$NoShortcut,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# Break the recursion: this script runs `dotnet publish -c Release`, which builds the same
# projects whose Release build triggers this script (see Directory.Build.targets). The child
# builds inherit this variable and skip the post-build publish target.
$env:CWB_SKIP_PUBLISH_LIVE = '1'

$repoRoot = Split-Path -Parent $PSScriptRoot
$hostProject = Join-Path $repoRoot 'src\ClaudeWorkbench.Host\ClaudeWorkbench.Host.csproj'
$sidecarSource = Join-Path $repoRoot 'sidecar'

foreach ($required in @($hostProject, $sidecarSource)) {
    if (-not (Test-Path $required)) {
        throw "Not a ClaudeShell checkout - missing $required"
    }
}

$hostOut = Join-Path $Destination 'host'
$sidecarOut = Join-Path $Destination 'sidecar'

# A running install holds its exes open and publish fails partway through with an unhelpful
# MSBuild error. Say so up front instead.
$inUse = Get-Process -Name 'ClaudeWorkbench.Host' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase) }
if ($inUse) {
    $names = ($inUse | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
    throw "Close the running install first - $names is using $Destination."
}

Write-Host "Publishing ClaudeShell ($Configuration) -> $Destination" -ForegroundColor Cyan

if ($Clean) {
    # Deliberately only the two build outputs: runtime\ holds the user's instance state.
    foreach ($stale in @($hostOut, $sidecarOut)) {
        if (Test-Path $stale) {
            Write-Host "  cleaning $stale"
            Remove-Item $stale -Recurse -Force
        }
    }
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# --- 1. Blazor host -------------------------------------------------------------------
Write-Host ''
Write-Host '[1/4] Publishing host (Blazor + MCP surface)...' -ForegroundColor Cyan
dotnet publish $hostProject -c $Configuration -o $hostOut --nologo
if ($LASTEXITCODE -ne 0) { throw "Host publish failed ($LASTEXITCODE)." }

# The mutable watched-solution config must not ship: each instance gets its own, written by
# the Launcher. Shipping one would point every fresh install at the build machine's workspace.
$strayConfig = Join-Path $hostOut 'config\appsettings.json'
if (Test-Path $strayConfig) {
    Remove-Item $strayConfig -Force
    Write-Host '  removed build-machine config\appsettings.json (instances get their own)'
}

# --- 2. Sidecar (BasicSidecar) -----------------------------------------------
Write-Host ''
Write-Host '[2/3] Building BasicSidecar...' -ForegroundColor Cyan
$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (-not $npm) { $npm = Get-Command npm -ErrorAction SilentlyContinue }
if (-not $npm) { throw 'npm was not found on PATH - needed to build the sidecar.' }

$basicSidecar = Join-Path $sidecarSource 'basic'
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

# Runtime dependencies only (the Agent SDK + express). Falls back to copying the checkout's
# node_modules when npm cannot reach the registry, so an offline publish still works.
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

# Copy launch scripts
Write-Host '  copying launch scripts...'
$scriptsOut = Join-Path $Destination 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsOut | Out-Null
Copy-Item (Join-Path $repoRoot 'scripts\launch-shell.ps1') $scriptsOut -Force
Copy-Item (Join-Path $repoRoot 'scripts\create-shortcuts.ps1') $scriptsOut -Force

# --- 3. Shortcuts -----------------------------------------------------------------------
Write-Host ''
Write-Host '[3/3] Creating shortcuts...' -ForegroundColor Cyan
$launchScript = Join-Path $scriptsOut 'launch-shell.ps1'
$pwshExe = (Get-Command powershell -ErrorAction SilentlyContinue).Source
if (-not $pwshExe) { $pwshExe = 'powershell.exe' }

function New-DirectLaunchShortcut {
    param([string]$LinkPath)
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($LinkPath)
        $shortcut.TargetPath = $pwshExe
        $shortcut.Arguments = "-NoExit -ExecutionPolicy Bypass -File `"$launchScript`""
        $shortcut.WorkingDirectory = $Destination
        $shortcut.Description = 'ClaudeShell — starts immediately with temp workspace'
        $shortcut.Save()
        Write-Host "  $LinkPath"
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

New-DirectLaunchShortcut -LinkPath (Join-Path $Destination 'ClaudeShell.lnk')
if (-not $NoShortcut) {
    $desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'ClaudeShell.lnk'
    New-DirectLaunchShortcut -LinkPath $desktopLink
}

Write-Host ''
Write-Host "Done. ClaudeShell install root: $Destination" -ForegroundColor Green
Write-Host "  host      $hostOut"
Write-Host "  sidecar   $sidecarOut  (BasicSidecar - no governance)"
Write-Host "  scripts   $scriptsOut   (launch-shell.ps1)"
Write-Host "  runtime   $(Join-Path $Destination 'runtime')  (per-workspace state, created on first Start)"
Write-Host ''
Write-Host 'Quick start:' -ForegroundColor Yellow
Write-Host '  → Click "ClaudeShell.lnk" to start (uses temp workspace by default)'
Write-Host '  → Or: powershell -NoExit -ExecutionPolicy Bypass -File "$launchScript" -Workspace C:\MyProject'
Write-Host ''
Write-Host 'To build a workspace manager (like the old Launcher):' -ForegroundColor Cyan
Write-Host '  See samples/ai-monitor-launcher/ for a complete example'
Write-Host ''
Write-Host 'Target machine requirements:' -ForegroundColor Yellow
Write-Host '  - .NET 10 SDK       (for the host and indexing via MSBuild/Roslyn)'
Write-Host '  - Node.js on PATH   (the claude CLI ships inside the sidecar)'
Write-Host '  - a Claude login in ~\.claude  (or ANTHROPIC_API_KEY for billing)'
