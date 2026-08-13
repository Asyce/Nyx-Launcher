[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$helperRoot = $PSScriptRoot
$sourceRoot = Join-Path $helperRoot 'src'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $helperRoot '..\..\..'))
$outputRoot = Join-Path $repoRoot '.verification-build\genshin120-native-helper'
$objectRoot = Join-Path $outputRoot 'obj'
$releaseRoot = Join-Path $outputRoot 'release'
$vsDevCmd = 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\Common7\Tools\VsDevCmd.bat'

if (-not (Test-Path -LiteralPath $vsDevCmd)) { throw "VS2019 Build Tools were not found." }
if (Test-Path -LiteralPath $outputRoot) {
    $resolvedOutput = [IO.Path]::GetFullPath($outputRoot)
    $resolvedVerification = [IO.Path]::GetFullPath((Join-Path $repoRoot '.verification-build')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedOutput.StartsWith($resolvedVerification, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe output path.' }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $objectRoot, $releaseRoot | Out-Null

function Invoke-VsBuild([string]$Command) {
    $commandFile = Join-Path $objectRoot ('build-' + [guid]::NewGuid().ToString('N') + '.cmd')
    [IO.File]::WriteAllText($commandFile, "@call `"$vsDevCmd`" -no_logo -arch=x64 -host_arch=x64`r`n$Command`r`n", [Text.UTF8Encoding]::new($false))
    try {
        & $env:ComSpec /d /c $commandFile
        if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
    }
    finally { Remove-Item -LiteralPath $commandFile -Force -ErrorAction SilentlyContinue }
}

$stub = Join-Path $objectRoot 'Nyx.Genshin120.Stub.dll'
$stubObject = Join-Path $objectRoot 'Stub.obj'
Invoke-VsBuild "cl.exe /nologo /std:c++17 /O2 /GL /MT /GS /guard:cf /sdl /W4 /WX /DUNICODE /D_UNICODE /DNDEBUG /DWIN32_LEAN_AND_MEAN /LD `"$sourceRoot\Stub.cpp`" /Fo`"$stubObject`" /link /NOLOGO /LTCG /BREPRO /DLL /MACHINE:X64 /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /OUT:`"$stub`" kernel32.lib user32.lib"

$payloadHash = (Get-FileHash -LiteralPath $stub -Algorithm SHA256).Hash.ToLowerInvariant()
$hashBytes = for ($index = 0; $index -lt 64; $index += 2) { '0x' + $payloadHash.Substring($index, 2) }
$generatedHeader = @"
#pragma once
#include <cstdint>
namespace nyx120 {
constexpr std::uint8_t PayloadSha256[32] = { $($hashBytes -join ', ') };
constexpr wchar_t PayloadSha256Hex[] = L"$payloadHash";
}
"@
[IO.File]::WriteAllText((Join-Path $objectRoot 'PayloadHash.generated.h'), $generatedHeader, [Text.UTF8Encoding]::new($false))

$escapedStub = $stub.Replace('\', '\\')
$escapedManifest = (Join-Path $sourceRoot 'helper.manifest').Replace('\', '\\')
$resourceScript = "1 24 `"$escapedManifest`"`r`n101 RCDATA `"$escapedStub`"`r`n"
$resourcePath = Join-Path $objectRoot 'Payload.generated.rc'
[IO.File]::WriteAllText($resourcePath, $resourceScript, [Text.ASCIIEncoding]::new())

$resourceObject = Join-Path $objectRoot 'Payload.res'
Invoke-VsBuild "rc.exe /nologo /fo `"$resourceObject`" `"$resourcePath`""

$helper = Join-Path $releaseRoot 'Nyx.Genshin120.Helper.exe'
Invoke-VsBuild "cl.exe /nologo /std:c++17 /EHsc /O2 /GL /MT /GS /guard:cf /sdl /W4 /WX /DUNICODE /D_UNICODE /DNDEBUG /DWIN32_LEAN_AND_MEAN /I`"$objectRoot`" /I`"$sourceRoot`" `"$sourceRoot\Helper.cpp`" `"$sourceRoot\Authenticode.cpp`" `"$resourceObject`" /Fo`"$objectRoot\\`" /link /NOLOGO /LTCG /BREPRO /MACHINE:X64 /SUBSYSTEM:WINDOWS /MANIFEST:NO /DYNAMICBASE /NXCOMPAT /GUARD:CF /OPT:REF /OPT:ICF /OUT:`"$helper`" kernel32.lib user32.lib bcrypt.lib shell32.lib ole32.lib wintrust.lib crypt32.lib"

$selfTest = Join-Path $objectRoot 'ProtocolSelfTest.exe'
Invoke-VsBuild "cl.exe /nologo /std:c++17 /EHsc /O2 /MT /W4 /WX /I`"$sourceRoot`" `"$sourceRoot\ProtocolSelfTest.cpp`" `"$sourceRoot\Authenticode.cpp`" /Fo`"$objectRoot\\`" /link /NOLOGO /BREPRO /MACHINE:X64 /OUT:`"$selfTest`" wintrust.lib crypt32.lib"
& $selfTest
if ($LASTEXITCODE -ne 0) { throw "Protocol self-test failed with exit code $LASTEXITCODE." }

$manifest = [ordered]@{
    upstreamTag = 'v3.5.0'
    upstreamCommit = '2b85d61dd06f6e11ad86fdd6bd90339f9abc58eb'
    helperSha256 = (Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash.ToLowerInvariant()
    payloadSha256 = $payloadHash
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $helperRoot 'LICENSE-THIRD-PARTY.txt') -Destination (Join-Path $releaseRoot 'LICENSE-GENSHIN-FPS-UNLOCKER.txt') -Force

Write-Host "Built $helper"
