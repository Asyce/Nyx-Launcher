#Requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (Test-IsAdministrator) {
    Write-Error 'Nyx installs for the current Windows user. Run this installer without administrator approval.'
    exit 10
}

$updater = Join-Path $PSScriptRoot 'Nyx.Desktop.Update.exe'
$manifest = Join-Path $PSScriptRoot 'release.json'
if (-not (Test-Path -LiteralPath $updater -PathType Leaf) -or
    -not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    Write-Error 'The Nyx installation bundle is incomplete.'
    exit 11
}

& $updater install --bundle $PSScriptRoot
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\Pengo Nyx'
$app = Join-Path $installRoot 'app\Nyx.Desktop.App.exe'
$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Pengo'
$shortcutPath = Join-Path $startMenuDirectory 'Nyx Desktop.lnk'
try {
    if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
        throw 'Installed app entry point is missing.'
    }

    if (Test-Path -LiteralPath $startMenuDirectory) {
        $startMenuItem = Get-Item -LiteralPath $startMenuDirectory -Force
        if (($startMenuItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The Start menu destination is unsafe.'
        }
    }
    else {
        [void] (New-Item -ItemType Directory -Path $startMenuDirectory)
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $app
    $shortcut.WorkingDirectory = Split-Path -Parent $app
    $shortcut.IconLocation = "$app,0"
    $shortcut.Description = 'Nyx Desktop'
    $shortcut.Save()

    & $updater confirm --manifest $manifest
    if ($LASTEXITCODE -ne 0) {
        throw 'The installed release could not be confirmed.'
    }
}
catch {
    & $updater uninstall | Out-Null
    Write-Error 'Nyx installation was rolled back because the Start menu entry could not be created.'
    exit 12
}

Write-Output 'Nyx Desktop is installed for this Windows user.'
Write-Output 'Your Nyx data is stored separately and is kept by default during uninstall.'
exit 0
