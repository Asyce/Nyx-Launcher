#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$')]
    [string] $Version,
    [switch] $NoRestore,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$arguments = @{
    Channel = 'stable'
    NoRestore = $NoRestore
    Force = $Force
}
if ($PSBoundParameters.ContainsKey('Version')) {
    $arguments['Version'] = $Version
}

& (Join-Path $PSScriptRoot 'build-development-package.ps1') @arguments
