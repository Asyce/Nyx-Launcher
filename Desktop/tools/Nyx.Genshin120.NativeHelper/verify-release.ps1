[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$helperRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $helperRoot '..\..\..'))
$upstreamRoot = Join-Path $repoRoot '.verification-build\upstream-genshin-fps-v3.5.0'
$outputRoot = Join-Path $repoRoot '.verification-build\genshin120-native-helper'
$releaseRoot = Join-Path $outputRoot 'release'
$objectRoot = Join-Path $outputRoot 'obj'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
Assert-True (Test-Path -LiteralPath $vsWhere -PathType Leaf) "vswhere.exe was not found: $vsWhere"
$vsInstallPath = & $vsWhere -latest -products '*' -requires 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' -property installationPath
$vsWhereExitCode = $LASTEXITCODE
$vsInstallPath = ([string]$vsInstallPath).Trim()
Assert-True ($vsWhereExitCode -eq 0 -and -not [String]::IsNullOrWhiteSpace($vsInstallPath)) 'No Visual Studio installation with the required C++ tools was found.'
$vsInstallPath = [IO.Path]::GetFullPath($vsInstallPath)
Assert-True (Test-Path -LiteralPath $vsInstallPath -PathType Container) "Visual Studio installation was not found: $vsInstallPath"
$msvcRoot = Join-Path $vsInstallPath 'VC\Tools\MSVC'
$dumpbinCandidates = @(
    Get-ChildItem -LiteralPath $msvcRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(?:\.\d+){1,3}$' } |
        ForEach-Object {
            $candidate = Join-Path $_.FullName 'bin\Hostx64\x64\dumpbin.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                [pscustomobject]@{
                    Version = [version]$_.Name
                    Name = $_.Name
                    Path = [IO.Path]::GetFullPath($candidate)
                }
            }
        }
)
Assert-True ($dumpbinCandidates.Count -gt 0) "No x64 MSVC dumpbin.exe was found under: $msvcRoot"
$dumpbin = ($dumpbinCandidates | Sort-Object -Property @{Expression='Version';Descending=$true}, @{Expression='Name';Descending=$false} | Select-Object -First 1).Path
Assert-True (Test-Path -LiteralPath $dumpbin -PathType Leaf) "dumpbin.exe was not found: $dumpbin"

Assert-True (Test-Path -LiteralPath $upstreamRoot) 'Pinned upstream checkout is missing.'
$upstreamCommit = @(git -c core.longpaths=true -C $upstreamRoot rev-parse --verify 'HEAD^{commit}')
$upstreamCommitExitCode = $LASTEXITCODE
Assert-True ($upstreamCommitExitCode -eq 0 -and $upstreamCommit.Count -eq 1 -and $upstreamCommit[0] -eq '2b85d61dd06f6e11ad86fdd6bd90339f9abc58eb') 'Pinned upstream commit changed or is unreadable.'
$upstreamStatus = @(git -c core.longpaths=true -C $upstreamRoot status --short)
$upstreamStatusExitCode = $LASTEXITCODE
Assert-True ($upstreamStatusExitCode -eq 0 -and $upstreamStatus.Count -eq 0) 'Pinned upstream checkout is dirty or Git status failed.'
$upstreamHashes = @{
    'UnlockerStub\dllmain.cpp' = 'BE87F293E333BB7B931CADB4C3AEE15663190505B978C734400F0CA6755DF614'
    'UnlockerStub\Utils.cpp' = 'DB43539D87883686612CBC56E12C4D5E1CA4FCE981F56A234BC4B305095E2E7D'
    'UnlockerStub\Utils.h' = '59B416DDE357967C26760D6F3EA77BAB19F44931D74F254A15E9135850936AD6'
}
foreach ($entry in $upstreamHashes.GetEnumerator()) {
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $upstreamRoot $entry.Key) -Algorithm SHA256).Hash -eq $entry.Value) "Upstream hash changed: $($entry.Key)"
}
$upstreamLicense = (Get-Content -Raw (Join-Path $upstreamRoot 'LICENSE')).Replace("`r`n", "`n").Trim()
$localLicense = (Get-Content -Raw (Join-Path $helperRoot 'LICENSE-THIRD-PARTY.txt')).Replace("`r`n", "`n").Trim()
Assert-True ($upstreamLicense -ceq $localLicense) 'MIT licence copy differs from upstream.'

& (Join-Path $helperRoot 'build.ps1')
$first = Get-Content -Raw (Join-Path $releaseRoot 'release-manifest.json') | ConvertFrom-Json
& (Join-Path $helperRoot 'build.ps1')
$manifest = Get-Content -Raw (Join-Path $releaseRoot 'release-manifest.json') | ConvertFrom-Json
Assert-True ($first.helperSha256 -eq $manifest.helperSha256 -and $first.payloadSha256 -eq $manifest.payloadSha256) 'Two clean builds were not deterministic.'

$helper = Join-Path $releaseRoot 'Nyx.Genshin120.Helper.exe'
$payload = Join-Path $objectRoot 'Nyx.Genshin120.Stub.dll'
Assert-True (Test-Path -LiteralPath $helper) 'Helper is missing.'
Assert-True (Test-Path -LiteralPath $payload) 'Build-time payload is missing.'
Assert-True (-not (Get-ChildItem -LiteralPath $releaseRoot -Filter '*.dll' -File)) 'Release folder contains a loose DLL.'
Assert-True ((Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash.ToLowerInvariant() -eq $manifest.helperSha256) 'Helper hash differs from manifest.'
Assert-True ((Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant() -eq $manifest.payloadSha256) 'Payload hash differs from manifest.'
Assert-True (((Get-Item $helper).Length + (Get-Item $payload).Length) -lt 15MB) 'Helper plus payload exceeds 15 MB.'

Assert-True (Test-Path -LiteralPath $dumpbin) 'dumpbin.exe is missing.'
$helperHeaders = (& $dumpbin /headers $helper) -join "`n"
$helperImports = (& $dumpbin /imports $helper) -join "`n"
$payloadHeaders = (& $dumpbin /headers $payload) -join "`n"
$payloadImports = (& $dumpbin /imports $payload) -join "`n"
$payloadExports = (& $dumpbin /exports $payload) -join "`n"
Assert-True ($helperHeaders -match '8664 machine \(x64\)' -and $payloadHeaders -match '8664 machine \(x64\)') 'A binary is not x64.'
Assert-True ($helperHeaders -match 'subsystem \(Windows GUI\)') 'Helper is not a GUI/no-console executable.'
$exportNames = @($payloadExports -split "`r?`n" | ForEach-Object {
    if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+(\S+)\s*$') { $Matches[1] }
})
Assert-True ($exportNames.Count -eq 1 -and $exportNames[0] -eq 'WndProc') 'Embedded payload export set is not exactly WndProc.'
Assert-True ($helperImports -notmatch '(?i)VCRUNTIME|MSVCP|ucrtbase|hostfxr|coreclr') 'Helper has a dynamic runtime dependency.'
Assert-True ($payloadImports -notmatch '(?i)VCRUNTIME|MSVCP|ucrtbase|hostfxr|coreclr') 'Payload has a dynamic runtime dependency.'
Assert-True ($payloadImports -match 'KERNEL32\.dll' -and $payloadImports -match 'USER32\.dll') 'Payload imports are unexpected.'

if (-not ('NyxNativeResourceReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class NyxNativeResourceReader {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LockResource(IntPtr resource);
  [DllImport("kernel32.dll", SetLastError=true)] static extern uint SizeofResource(IntPtr module, IntPtr resource);
  [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
  public static byte[] Read(string path, int id, int type) {
    IntPtr module=LoadLibraryEx(path, IntPtr.Zero, 0x22); if(module==IntPtr.Zero) throw new Win32Exception();
    try { IntPtr found=FindResource(module,(IntPtr)id,(IntPtr)type); if(found==IntPtr.Zero) throw new Win32Exception();
      uint size=SizeofResource(module,found); IntPtr loaded=LoadResource(module,found); IntPtr data=LockResource(loaded);
      byte[] result=new byte[size]; Marshal.Copy(data,result,0,(int)size); return result;
    } finally { FreeLibrary(module); }
  }
}
'@
}
$embeddedPayload = [NyxNativeResourceReader]::Read($helper, 101, 10)
$sha = [Security.Cryptography.SHA256]::Create()
try { $embeddedHash = ([BitConverter]::ToString($sha.ComputeHash($embeddedPayload))).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose() }
Assert-True ($embeddedHash -eq $manifest.payloadSha256) 'Embedded payload bytes do not match the pinned build payload.'
$embeddedManifest = [Text.Encoding]::UTF8.GetString([NyxNativeResourceReader]::Read($helper, 1, 24))
Assert-True ($embeddedManifest -match 'requestedExecutionLevel level="requireAdministrator"') 'Elevation manifest is missing.'

$binaryText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($helper)) + [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($helper))
foreach ($forbidden in @('Zydis','MobileUI','PowerSave','crashdump','hostfxr','coreclr','MessageBox','FPSTarget','custom resolution','plugin loader')) {
    Assert-True ($binaryText.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -lt 0) "Forbidden surface found in helper: $forbidden"
}
$helperSource = Get-Content -Raw (Join-Path $helperRoot 'src\Helper.cpp')
Assert-True ($helperSource -match 'ExpectedExecutableSha256' -and $helperSource -match 'HashFile\(executable\.Get\(\)') 'Pre/post-elevation executable hash check is missing.'
Assert-True ($helperSource -match 'HasExpectedCachedAuthenticodePublisher' -and $helperSource -match 'IsExpectedExecutableName') 'Elevated official-file identity check is missing.'
Assert-True ($helperSource -match 'CREATE_SUSPENDED' -and $helperSource -match 'QueryFullProcessImageNameW' -and $helperSource -match 'SameFile') 'Launched-image identity checks are missing.'
Assert-True ($helperSource -match 'IsWindowVisible' -and $helperSource -match 'RedrawWindow' -and $helperSource -match 'UpdateWindow' -and $helperSource -match 'GetDC' -and $helperSource -match 'ReleaseDC') 'Unity window drawing-readiness check is missing.'
Assert-True ($helperSource -notmatch 'ShellExecute|system\(|CreateRemoteThread|WriteProcessMemory') 'Forbidden launch or injection path is present.'
$authenticodeSource = Get-Content -Raw (Join-Path $helperRoot 'src\Authenticode.cpp')
Assert-True ($authenticodeSource -match 'WTD_CACHE_ONLY_URL_RETRIEVAL' -and $authenticodeSource -match 'file\.hFile = pinnedFile') 'Authenticode is not cached-only and pinned-handle-bound.'
$identityHeader = Get-Content -Raw (Join-Path $helperRoot 'src\Authenticode.h')
Assert-True ($identityHeader -match 'COGNOSPHERE PTE\. LTD\.' -and $identityHeader -match 'GenshinImpact\.exe') 'Expected publisher or executable name changed.'
$selfTestSource = Get-Content -Raw (Join-Path $helperRoot 'src\ProtocolSelfTest.cpp')
Assert-True ($selfTestSource -match 'NotGenshinImpact\.exe' -and $selfTestSource -match 'COGNOSPHERE PTE\. LTD\. FAKE' -and $selfTestSource -match '!nyx120::HasExpectedCachedAuthenticodePublisher') 'Unsigned, fake-publisher, or wrong-name rejection test is missing.'
Assert-True ($helperSource -match 'FILE_FLAG_OVERLAPPED' -and $helperSource -match 'CancelIoEx' -and $helperSource -notmatch 'FlushFileBuffers\(pipe\.Get\(\)\)') 'Pipe I/O is not bounded and cancellable.'

[ordered]@{
    helper = $helper
    helperSha256 = $manifest.helperSha256
    helperBytes = (Get-Item $helper).Length
    embeddedPayloadSha256 = $embeddedHash
    embeddedPayloadBytes = $embeddedPayload.Length
    combinedBytes = (Get-Item $helper).Length + $embeddedPayload.Length
    upstreamCommit = $manifest.upstreamCommit
    deterministicCleanBuilds = 2
    releaseDllCount = @(Get-ChildItem -LiteralPath $releaseRoot -Filter '*.dll' -File).Count
} | ConvertTo-Json
