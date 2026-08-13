#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch] $RemoveUserData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (Test-IsAdministrator) {
    Write-Error 'Nyx uninstalls from the current Windows user. Run this without administrator approval.'
    exit 10
}

$installedUpdater = Join-Path $env:LOCALAPPDATA 'Programs\Pengo Nyx\control\Nyx.Desktop.Update.exe'
if (-not (Test-Path -LiteralPath $installedUpdater -PathType Leaf)) {
    Write-Error 'The Nyx uninstaller is missing.'
    exit 11
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('Pengo\NyxUninstall\' + [guid]::NewGuid().ToString('N'))
[void] (New-Item -ItemType Directory -Path $temporaryRoot)
$temporaryUpdater = Join-Path $temporaryRoot 'Nyx.Desktop.Update.exe'
Copy-Item -LiteralPath $installedUpdater -Destination $temporaryUpdater

try {
    if ($RemoveUserData) {
        & $temporaryUpdater uninstall --remove-user-data
    }
    else {
        & $temporaryUpdater uninstall
    }

    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($exitCode -ne 0) {
    exit $exitCode
}

if ($RemoveUserData) {
    Write-Output 'Nyx Desktop and its current and legacy per-user data were removed.'
}
else {
    Write-Output 'Nyx Desktop was removed. Your per-user data was kept.'
}
exit 0
