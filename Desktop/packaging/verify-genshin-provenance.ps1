#Requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $UpstreamRoot,
    [Parameter(Mandatory)] [string] $ProvenancePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($UpstreamRoot).TrimEnd('\')
$rootPrefix = $root + '\'
$provenance = [IO.Path]::GetFullPath($ProvenancePath)
if (-not (Test-Path -LiteralPath $root -PathType Container) -or
    -not (Test-Path -LiteralPath $provenance -PathType Leaf)) {
    throw 'The provenance verifier input is missing.'
}
foreach ($item in @((Get-Item -LiteralPath $root -Force), (Get-Item -LiteralPath $provenance -Force))) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The provenance verifier does not accept reparse-backed inputs.'
    }
}

$lines = @(Get-Content -LiteralPath $provenance)
$header = 'Reviewed upstream source hashes (SHA-256):'
$headerIndexes = @(for ($index = 0; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -ceq $header) { $index }
})
if ($headerIndexes.Count -ne 1) {
    throw 'The provenance source-hash section is missing or duplicated.'
}

$hashes = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$started = $false
for ($index = $headerIndexes[0] + 1; $index -lt $lines.Count; $index++) {
    $line = $lines[$index]
    if ([string]::IsNullOrWhiteSpace($line)) {
        if ($started) { break }
        continue
    }
    $started = $true
    if ($line -cnotmatch '^- `([^`]+)`: `([0-9A-F]{64})`$') {
        throw 'A provenance source-hash line is malformed.'
    }
    $relative = $Matches[1]
    $expectedHash = $Matches[2]
    $parts = @($relative.Split('/'))
    if ([IO.Path]::IsPathRooted($relative) -or
        $parts.Count -eq 0 -or
        @($parts | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
        $hashes.ContainsKey($relative)) {
        throw 'A provenance source path is unsafe or duplicated.'
    }
    $hashes.Add($relative, $expectedHash)

    $source = [IO.Path]::GetFullPath((Join-Path $root ($relative.Replace('/', '\'))))
    if (-not $source.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw 'A provenance source path is missing or escaped the pinned checkout.'
    }
    $current = $root
    foreach ($part in $parts) {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A provenance source path crosses a reparse point.'
        }
    }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $expectedHash) {
        throw 'A provenance source hash does not match the pinned checkout.'
    }
}

$required = @(
    'UnlockerStub/dllmain.cpp',
    'UnlockerStub/Utils.cpp',
    'UnlockerStub/Utils.h'
)
if ($hashes.Count -ne $required.Count -or
    @($required | Where-Object { -not $hashes.ContainsKey($_) }).Count -ne 0) {
    throw 'The provenance source-hash set is incomplete or unexpected.'
}

Write-Output "GENSHIN_PROVENANCE=VERIFIED SOURCES=$($hashes.Count)"
