<#
.SYNOPSIS
    Creates desktop shortcuts for ClaudeShell launch options.

.DESCRIPTION
    Creates two shortcuts:
    1. "ClaudeShell Launcher" → opens the GUI Launcher app (workspace picker, config)
    2. "ClaudeShell Direct" → runs launch-shell.ps1 directly (starts immediately with temp workspace)

    Use whichever workflow you prefer.

.PARAMETER ShellRoot
    Path to the ClaudeShell install root (default: current directory).

.PARAMETER Desktop
    Create desktop shortcuts (default: true). Set to false to skip.

.EXAMPLE
    .\create-shortcuts.ps1
    .\create-shortcuts.ps1 -ShellRoot C:\ClaudeShellLive
#>
[CmdletBinding()]
param(
    [string]$ShellRoot = $PSScriptRoot,
    [bool]$Desktop = $true
)

$ErrorActionPreference = 'Stop'

# Resolve paths
if (-not (Test-Path $ShellRoot)) {
    throw "ShellRoot not found: $ShellRoot"
}

$launcherExe = Join-Path $ShellRoot 'launcher\ClaudeWorkbench.Launcher.exe'
$launchScript = Join-Path $ShellRoot 'scripts\launch-shell.ps1'

if (-not (Test-Path $launcherExe)) {
    throw "Launcher exe not found at $launcherExe"
}

if (-not (Test-Path $launchScript)) {
    throw "Launch script not found at $launchScript"
}

Write-Host 'Creating shortcuts...' -ForegroundColor Cyan
Write-Host ''

function New-Shortcut {
    param([string]$LinkPath, [string]$Target, [string]$Arguments, [string]$WorkingDir, [string]$Description)

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($LinkPath)
        $shortcut.TargetPath = $Target
        if ($Arguments) { $shortcut.Arguments = $Arguments }
        if ($WorkingDir) { $shortcut.WorkingDirectory = $WorkingDir }
        $shortcut.Description = $Description
        $shortcut.Save()
        Write-Host "✓ Created: $(Split-Path -Leaf $LinkPath)" -ForegroundColor Green
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

# Install-folder shortcut
$installLink = Join-Path $ShellRoot 'ClaudeShell Launcher (GUI).lnk'
New-Shortcut -LinkPath $installLink `
    -Target $launcherExe `
    -WorkingDirectory (Split-Path $launcherExe) `
    -Description 'ClaudeShell Launcher — GUI for folder selection and config'

# Direct-launch shortcut (runs PowerShell with the launch script)
$directLink = Join-Path $ShellRoot 'ClaudeShell Direct.lnk'
$pwshExe = (Get-Command powershell).Source
New-Shortcut -LinkPath $directLink `
    -Target $pwshExe `
    -Arguments "-NoExit -ExecutionPolicy Bypass -File `"$launchScript`"" `
    -WorkingDirectory $ShellRoot `
    -Description 'ClaudeShell Direct — starts host and sidecar immediately'

if ($Desktop) {
    $desktopPath = [Environment]::GetFolderPath('Desktop')
    Write-Host ''
    Write-Host 'Creating desktop shortcuts...' -ForegroundColor Gray

    $launcherDesktop = Join-Path $desktopPath 'ClaudeShell Launcher (GUI).lnk'
    New-Shortcut -LinkPath $launcherDesktop `
        -Target $launcherExe `
        -WorkingDirectory (Split-Path $launcherExe) `
        -Description 'ClaudeShell Launcher — GUI for folder selection and config'

    $directDesktop = Join-Path $desktopPath 'ClaudeShell Direct.lnk'
    New-Shortcut -LinkPath $directDesktop `
        -Target $pwshExe `
        -Arguments "-NoExit -ExecutionPolicy Bypass -File `"$launchScript`"" `
        -WorkingDirectory $ShellRoot `
        -Description 'ClaudeShell Direct — starts host and sidecar immediately'
}

Write-Host ''
Write-Host 'Done!' -ForegroundColor Green
Write-Host ''
Write-Host '  GUI Launcher  → For picking workspaces, configuring options' -ForegroundColor Dim
Write-Host '  Direct        → Fast start: runs host + sidecar immediately' -ForegroundColor Dim
