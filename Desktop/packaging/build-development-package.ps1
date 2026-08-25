#Requires -Version 5.1

[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$')]
    [string] $Version = '1.4.0.0',
    [ValidateSet('development', 'stable')]
    [string] $Channel = 'development',
    [switch] $NoRestore,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packagingRoot = Split-Path -Parent $PSCommandPath
$desktopRoot = Split-Path -Parent $packagingRoot
$repositoryRoot = Split-Path -Parent $desktopRoot
$artifactsRoot = Join-Path $packagingRoot 'artifacts'
$workParent = Join-Path $packagingRoot '.work'
$fixedTimestamp = [DateTimeOffset]::Parse('2026-07-17T00:00:00Z')
$genshin120UpstreamUrl = 'https://github.com/34736384/genshin-fps-unlock.git'
$genshin120UpstreamTag = 'v3.5.0'
$genshin120UpstreamCommit = '2b85d61dd06f6e11ad86fdd6bd90339f9abc58eb'

function Test-ReparsePoint {
    param([Parameter(Mandatory)] [IO.FileSystemInfo] $Item)
    return ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
}

function Assert-SafePackagingRoot {
    $resolved = (Get-Item -LiteralPath $packagingRoot -Force).FullName.TrimEnd('\')
    if ($resolved -notmatch '^[A-Za-z]:\\' -or (Test-ReparsePoint (Get-Item -LiteralPath $resolved -Force))) {
        throw 'The packaging root must be a normal local-drive directory.'
    }
}

function Assert-NoPrivateBuildStrings {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string[]] $Needles
    )

    $privatePaths = @($Needles | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    foreach ($file in (Get-ChildItem -LiteralPath $Root -File -Recurse -Force |
        Where-Object { $_.Extension -in @('.dll', '.exe') })) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $strings = @(
            [regex]::Matches($latin1.GetString($bytes), '[ -~]{4,}') | ForEach-Object Value
            [regex]::Matches([Text.Encoding]::Unicode.GetString($bytes), '[ -~]{4,}') | ForEach-Object Value
        )
        foreach ($value in $strings) {
            if (@($privatePaths | Where-Object {
                    $value.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0
                }).Count -ne 0) {
                throw "A packaged binary contains private build-path text: $($file.Name)"
            }
        }
    }
}

function Remove-GeneratedDirectory {
    param([Parameter(Mandatory)] [string] $LiteralPath)

    $parent = [IO.Path]::GetFullPath($workParent).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($LiteralPath).TrimEnd('\')
    if (-not $target.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $target -PathType Container)) {
        throw 'Refusing to remove a path outside the packaging work directory.'
    }

    $item = Get-Item -LiteralPath $target -Force
    if (Test-ReparsePoint $item) {
        throw 'Refusing to remove a reparse-backed work directory.'
    }

    Remove-Item -LiteralPath $target -Recurse -Force
}

function Install-GeneratedFile {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $Destination
    )

    $artifactPrefix = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd('\') + '\'
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    $workPrefix = [IO.Path]::GetFullPath($workRoot).TrimEnd('\') + '\'
    $sourcePath = [IO.Path]::GetFullPath($Source)
    if (-not $destinationPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $sourcePath.StartsWith($workPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to replace a file outside generated packaging paths.'
    }

    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $backup = Join-Path $workRoot ('.previous-' + [guid]::NewGuid().ToString('N'))
        [IO.File]::Replace($Source, $Destination, $backup, $true)
        Remove-Item -LiteralPath $backup -Force
    }
    else {
        [IO.File]::Move($Source, $Destination)
    }
}

function Get-RelativeArchivePath {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Path
    )

    $rootUri = [Uri]::new(([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'))
    $pathUri = [Uri]::new([IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString())
}

function Get-PayloadFiles {
    param([Parameter(Mandatory)] [string] $Root)

    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force |
        Where-Object { $_.Extension -ne '.pdb' } |
        ForEach-Object {
            if (Test-ReparsePoint $_) {
                throw 'A package input is a reparse point.'
            }
            [pscustomobject]@{
                Item = $_
                Relative = Get-RelativeArchivePath -Root $Root -Path $_.FullName
            }
        })
    $comparison = [Comparison[object]] {
        param($left, $right)
        return [StringComparer]::Ordinal.Compare([string]$left.Relative, [string]$right.Relative)
    }
    [Array]::Sort($files, $comparison)
    return $files
}

function New-DeterministicZip {
    param(
        [Parameter(Mandatory)] [string] $SourceRoot,
        [Parameter(Mandatory)] [string] $Destination,
        [switch] $ExcludePdb
    )

    Add-Type -AssemblyName System.IO.Compression
    $files = if ($ExcludePdb) { Get-PayloadFiles -Root $SourceRoot } else {
        $archiveFiles = @(Get-ChildItem -LiteralPath $SourceRoot -File -Recurse -Force |
            ForEach-Object {
                if (Test-ReparsePoint $_) { throw 'A package input is a reparse point.' }
                [pscustomobject]@{
                    Item = $_
                    Relative = Get-RelativeArchivePath -Root $SourceRoot -Path $_.FullName
                }
            })
        $comparison = [Comparison[object]] {
            param($left, $right)
            return [StringComparer]::Ordinal.Compare([string]$left.Relative, [string]$right.Relative)
        }
        [Array]::Sort($archiveFiles, $comparison)
        $archiveFiles
    }

    $stream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $files) {
                $entry = $archive.CreateEntry($file.Relative, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp
                $input = [IO.File]::OpenRead($file.Item.FullName)
                $output = $entry.Open()
                try { $input.CopyTo($output) }
                finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

$Channel = $Channel.ToLowerInvariant()

function Get-StableReleaseIdentity {
    param(
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [Parameter(Mandatory)] [string] $GitPath,
        [string] $RequestedVersion
    )

    $status = @(& $GitPath -C $RepositoryRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the Git worktree.' }
    if ($status.Count -ne 0) { throw 'Stable packages require a clean Git worktree.' }

    $commit = (& $GitPath -C $RepositoryRoot rev-parse --verify 'HEAD^{commit}').Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Unable to resolve the stable Git commit.'
    }

    $tags = @(& $GitPath -C $RepositoryRoot tag --points-at HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect stable Git tags.' }
    if ($tags.Count -ne 1) { throw 'Stable packages require exactly one tag at HEAD.' }

    $tag = [string] $tags[0]
    $match = [regex]::Match(
        $tag,
        '^v(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})(?:\.(0|[1-9][0-9]{0,4}))?$')
    if (-not $match.Success) {
        throw 'The stable tag must be vMAJOR.MINOR or vMAJOR.MINOR.PATCH without leading zeros.'
    }

    $components = @(
        [uint32] $match.Groups[1].Value,
        [uint32] $match.Groups[2].Value,
        [uint32] $(if ($match.Groups[3].Success) { $match.Groups[3].Value } else { 0 })
    )
    if (@($components | Where-Object { $_ -gt [uint16]::MaxValue }).Count -ne 0) {
        throw 'Each stable tag component must be between 0 and 65535.'
    }

    $derivedVersion = '{0}.{1}.{2}.0' -f $components[0], $components[1], $components[2]
    if ($PSBoundParameters.ContainsKey('RequestedVersion') -and $RequestedVersion -cne $derivedVersion) {
        throw "The supplied version does not match stable tag $tag ($derivedVersion)."
    }

    return [pscustomobject]@{
        Tag = $tag
        Commit = $commit
        Version = $derivedVersion
    }
}

Assert-SafePackagingRoot
$git = (Get-Command git -ErrorAction Stop).Source
$stableIdentity = $null
if ($Channel -eq 'stable') {
    $identityArguments = @{
        RepositoryRoot = $repositoryRoot
        GitPath = $git
    }
    if ($PSBoundParameters.ContainsKey('Version')) {
        $identityArguments['RequestedVersion'] = $Version
    }
    $stableIdentity = Get-StableReleaseIdentity @identityArguments
    $Version = $stableIdentity.Version
}
foreach ($part in $Version.Split('.')) {
    if ([int]$part -gt 65535) { throw 'Each version component must be between 0 and 65535.' }
}

$artifactBase = "Nyx-Desktop-$Version-$Channel-win-x64"
$artifactPath = Join-Path $artifactsRoot "$artifactBase.zip"
$manifestArtifactPath = Join-Path $artifactsRoot "$artifactBase.release.json"
$hashPath = "$artifactPath.sha256"

[void] (New-Item -ItemType Directory -Path $artifactsRoot -Force)
[void] (New-Item -ItemType Directory -Path $workParent -Force)
if ((Test-Path -LiteralPath $artifactPath) -and -not $Force) {
    throw 'The output artifact already exists. Use -Force only to replace generated packaging output.'
}

$workRoot = Join-Path $workParent ([guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $workRoot 'app'
$toolRoot = Join-Path $workRoot 'tool'
$helperBuildRoot = Join-Path $workRoot 'achievement-helper-target'
$genshin120VerificationRoot = Join-Path $workRoot 'genshin120-verification'
$genshin120PrivateHelperRoot = Join-Path $genshin120VerificationRoot 'Desktop\tools\Nyx.Genshin120.NativeHelper'
$genshin120PrivateUpstreamRoot = Join-Path $genshin120VerificationRoot '.verification-build\upstream-genshin-fps-v3.5.0'
$bundleRoot = Join-Path $workRoot 'bundle'
$payloadRoot = Join-Path $bundleRoot 'payload'
$temporaryArtifactPath = Join-Path $workRoot "$artifactBase.zip"
$temporaryManifestPath = Join-Path $workRoot "$artifactBase.release.json"
$temporaryHashPath = Join-Path $workRoot "$artifactBase.zip.sha256"
$sourceAppManifest = Join-Path $desktopRoot 'src\Nyx.Desktop.App\app.manifest'
$generatedAppManifest = Join-Path $workRoot 'app.manifest'
[void] (New-Item -ItemType Directory -Path $publishRoot, $toolRoot, $payloadRoot -Force)

try {
    $sourceAppManifestText = [IO.File]::ReadAllText($sourceAppManifest)
    $appIdentityPattern = '<assemblyIdentity version="[^"]+" name="Nyx\.Desktop\.App\.app"\s*/>'
    if ([regex]::Matches($sourceAppManifestText, $appIdentityPattern).Count -ne 1) {
        throw 'The source application manifest identity is missing or ambiguous.'
    }
    $appIdentity = "<assemblyIdentity version=`"$Version`" name=`"Nyx.Desktop.App.app`"/>"
    $generatedAppManifestText = [regex]::Replace($sourceAppManifestText, $appIdentityPattern, $appIdentity)
    [IO.File]::WriteAllText($generatedAppManifest, $generatedAppManifestText, [Text.UTF8Encoding]::new($false))

    $cargo = (Get-Command cargo -ErrorAction Stop).Source
    $python = (Get-Command python -ErrorAction Stop).Source
    $helperRoot = Join-Path $repositoryRoot 'Extractor\Achievements'
    $previousCargoTarget = $env:CARGO_TARGET_DIR
    $previousRustFlags = $env:RUSTFLAGS
    $previousEncodedRustFlags = $env:CARGO_ENCODED_RUSTFLAGS
    $userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    $cargoHome = if ([string]::IsNullOrWhiteSpace($env:CARGO_HOME)) {
        Join-Path $userProfile '.cargo'
    }
    else {
        [IO.Path]::GetFullPath($env:CARGO_HOME)
    }
    if ($userProfile -notmatch '^[A-Za-z]:\\' -or
        $cargoHome -notmatch '^[A-Za-z]:\\') {
        throw 'Cargo and user-profile roots must be absolute paths.'
    }
    try {
        $env:CARGO_TARGET_DIR = $helperBuildRoot
        $env:RUSTFLAGS = $null
        $env:CARGO_ENCODED_RUSTFLAGS = @(
            '-C',
            'target-feature=+crt-static',
            '--remap-path-prefix',
            "$userProfile=C:\_home",
            '--remap-path-prefix',
            "$($userProfile.Replace('\', '/'))=C:/_home",
            '--remap-path-prefix',
            "$cargoHome=C:\_toolchain\cargo",
            '--remap-path-prefix',
            "$($cargoHome.Replace('\', '/'))=C:/_toolchain/cargo",
            '--remap-path-prefix',
            "$repositoryRoot=C:\_src\Nyx",
            '--remap-path-prefix',
            "$workRoot=C:\_build\package"
        ) -join [char] 0x1f
        Push-Location -LiteralPath $helperRoot
        try {
            & $cargo build --locked --release --target x86_64-pc-windows-msvc --bin pengo-achievements-launcher
            if ($LASTEXITCODE -ne 0) { throw 'Achievement launcher helper build failed.' }
        }
        finally { Pop-Location }
    }
    finally {
        $env:CARGO_TARGET_DIR = $previousCargoTarget
        $env:RUSTFLAGS = $previousRustFlags
        $env:CARGO_ENCODED_RUSTFLAGS = $previousEncodedRustFlags
    }
    $builtHelper = Join-Path $helperBuildRoot 'x86_64-pc-windows-msvc\release\pengo-achievements-launcher.exe'
    if (-not (Test-Path -LiteralPath $builtHelper -PathType Leaf)) {
        throw 'The reviewed achievement launcher helper artifact is missing.'
    }
    & $python (Join-Path $repositoryRoot 'Extractor\Achievements\tools\verify_release.py') $builtHelper
    if ($LASTEXITCODE -ne 0) { throw 'Achievement launcher helper verification failed.' }
    $helperSha256 = (Get-FileHash -LiteralPath $builtHelper -Algorithm SHA256).Hash.ToLowerInvariant()

    $genshin120SourceRoot = Join-Path $desktopRoot 'tools\Nyx.Genshin120.NativeHelper'
    foreach ($requiredSource in @(
        (Join-Path $genshin120SourceRoot 'build.ps1'),
        (Join-Path $genshin120SourceRoot 'verify-release.ps1'),
        (Join-Path $genshin120SourceRoot 'LICENSE-THIRD-PARTY.txt'),
        (Join-Path $genshin120SourceRoot 'PROVENANCE.md'),
        (Join-Path $genshin120SourceRoot 'src')
    )) {
        if (-not (Test-Path -LiteralPath $requiredSource)) {
            throw 'The pinned Genshin 120 FPS helper source is incomplete.'
        }
    }
    [void] (New-Item -ItemType Directory -Path (Split-Path -Parent $genshin120PrivateHelperRoot) -Force)
    Copy-Item -LiteralPath $genshin120SourceRoot -Destination $genshin120PrivateHelperRoot -Recurse
    [void] (New-Item -ItemType Directory -Path (Split-Path -Parent $genshin120PrivateUpstreamRoot) -Force)
    & $git -c core.longpaths=true clone --quiet --depth 1 --branch $genshin120UpstreamTag $genshin120UpstreamUrl $genshin120PrivateUpstreamRoot
    if ($LASTEXITCODE -ne 0) { throw 'Pinned Genshin FPS upstream checkout failed.' }
    $checkedOutCommit = (& $git -C $genshin120PrivateUpstreamRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $checkedOutCommit -cne $genshin120UpstreamCommit) {
        throw 'Pinned Genshin FPS upstream commit changed.'
    }
    & (Join-Path $genshin120PrivateHelperRoot 'verify-release.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Genshin 120 FPS helper verification failed.' }

    $genshin120ReleaseRoot = Join-Path $genshin120VerificationRoot '.verification-build\genshin120-native-helper\release'
    $genshin120ManifestPath = Join-Path $genshin120ReleaseRoot 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $genshin120ManifestPath -PathType Leaf)) {
        throw 'The verified Genshin 120 FPS helper manifest is missing.'
    }
    $genshin120Manifest = Get-Content -Raw -LiteralPath $genshin120ManifestPath | ConvertFrom-Json
    $genshin120Helper = Join-Path $genshin120ReleaseRoot 'Nyx.Genshin120.Helper.exe'
    $genshin120License = Join-Path $genshin120ReleaseRoot 'LICENSE-GENSHIN-FPS-UNLOCKER.txt'
    $genshin120Provenance = Join-Path $genshin120PrivateHelperRoot 'PROVENANCE.md'
    foreach ($requiredGenshinFile in @($genshin120Helper, $genshin120License, $genshin120Provenance)) {
        if (-not (Test-Path -LiteralPath $requiredGenshinFile -PathType Leaf)) {
            throw 'A required verified Genshin 120 FPS package input is missing.'
        }
    }
    if (Get-ChildItem -LiteralPath $genshin120ReleaseRoot -Filter '*.dll' -File) {
        throw 'The Genshin 120 FPS release contains a loose native payload.'
    }
    if ([string] $genshin120Manifest.upstreamTag -cne $genshin120UpstreamTag -or
        [string] $genshin120Manifest.upstreamCommit -cne $genshin120UpstreamCommit) {
        throw 'The Genshin 120 FPS release manifest does not match the pinned upstream source.'
    }
    $genshin120ProvenanceText = Get-Content -Raw -LiteralPath $genshin120Provenance
    foreach ($requiredProvenanceValue in @(
        $genshin120UpstreamUrl.Substring(0, $genshin120UpstreamUrl.Length - 4),
        $genshin120UpstreamTag,
        $genshin120UpstreamCommit,
        'Licence: MIT'
    )) {
        if ($genshin120ProvenanceText.IndexOf($requiredProvenanceValue, [StringComparison]::Ordinal) -lt 0) {
            throw 'The Genshin 120 FPS provenance record does not match the pinned source.'
        }
    }
    & (Join-Path $packagingRoot 'verify-genshin-provenance.ps1') `
        -UpstreamRoot $genshin120PrivateUpstreamRoot `
        -ProvenancePath $genshin120Provenance
    $genshin120HelperSha256 = (Get-FileHash -LiteralPath $genshin120Helper -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($genshin120HelperSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $genshin120HelperSha256 -cne [string] $genshin120Manifest.helperSha256) {
        throw 'The verified Genshin 120 FPS helper hash is invalid or mismatched.'
    }
    $genshin120LicenseSha256 = (Get-FileHash -LiteralPath $genshin120License -Algorithm SHA256).Hash.ToLowerInvariant()
    $genshin120ProvenanceSha256 = (Get-FileHash -LiteralPath $genshin120Provenance -Algorithm SHA256).Hash.ToLowerInvariant()

    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $restoreArgument = if ($NoRestore) { @('--no-restore') } else { @() }
    $appProject = Join-Path $desktopRoot 'src\Nyx.Desktop.App\Nyx.Desktop.App.csproj'
    $appArguments = @(
        'publish', $appProject,
        '-c', 'Release',
        '-r', 'win-x64',
        '-p:Platform=x64',
        '-p:WindowsPackageType=None',
        '-p:WindowsAppSDKSelfContained=true',
        '-p:SelfContained=true',
        '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:Deterministic=true',
        '-p:ContinuousIntegrationBuild=true',
        "-p:PathMap=$repositoryRoot=C:\_src\Nyx",
        "-p:Version=$Version",
        "-p:ApplicationManifest=$generatedAppManifest",
        "-p:AchievementHelperSource=$builtHelper",
        "-p:AchievementHelperSha256=$helperSha256",
        "-p:Genshin120HelperSource=$genshin120Helper",
        "-p:Genshin120HelperSha256=$genshin120HelperSha256",
        "-p:Genshin120LicenseSource=$genshin120License",
        "-p:Genshin120ProvenanceSource=$genshin120Provenance",
        "-p:PublishDir=$publishRoot"
    ) + $restoreArgument
    & $dotnet @appArguments
    if ($LASTEXITCODE -ne 0) { throw 'Nyx app publish failed.' }

    $toolProject = Join-Path $desktopRoot 'tools\Nyx.Desktop.Update\Nyx.Desktop.Update.csproj'
    $toolArguments = @(
        'publish', $toolProject,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:Deterministic=true',
        '-p:ContinuousIntegrationBuild=true',
        "-p:PathMap=$repositoryRoot=C:\_src\Nyx",
        "-p:Version=$Version",
        '-o', $toolRoot
    ) + $restoreArgument
    & $dotnet @toolArguments
    if ($LASTEXITCODE -ne 0) { throw 'Nyx updater publish failed.' }

    $entryPoint = Join-Path $publishRoot 'Nyx.Desktop.App.exe'
    $appAssembly = Join-Path $publishRoot 'Nyx.Desktop.App.dll'
    $achievementHelper = Join-Path $publishRoot 'Assets\Tools\pengo-achievements-launcher.exe'
    $packagedGenshin120Helper = Join-Path $publishRoot 'Assets\Tools\Nyx.Genshin120.Helper.exe'
    $packagedGenshin120License = Join-Path $publishRoot 'Assets\ThirdParty\genshin-fps-unlock\LICENSE.txt'
    $packagedGenshin120Provenance = Join-Path $publishRoot 'Assets\ThirdParty\genshin-fps-unlock\PROVENANCE.md'
    $updater = Join-Path $toolRoot 'Nyx.Desktop.Update.exe'
    foreach ($required in @(
        $entryPoint,
        $appAssembly,
        $achievementHelper,
        $packagedGenshin120Helper,
        $packagedGenshin120License,
        $packagedGenshin120Provenance,
        $updater
    )) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw 'A required distribution file is missing.'
        }
    }
    if ((Get-FileHash -LiteralPath $packagedGenshin120Helper -Algorithm SHA256).Hash.ToLowerInvariant() -cne $genshin120HelperSha256 -or
        (Get-FileHash -LiteralPath $packagedGenshin120License -Algorithm SHA256).Hash.ToLowerInvariant() -cne $genshin120LicenseSha256 -or
        (Get-FileHash -LiteralPath $packagedGenshin120Provenance -Algorithm SHA256).Hash.ToLowerInvariant() -cne $genshin120ProvenanceSha256) {
        throw 'A packaged Genshin 120 FPS helper file changed after verification.'
    }
    $appBinaryText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($entryPoint))
    $embeddedAppIdentities = [regex]::Matches(
        $appBinaryText,
        '<assemblyIdentity version="(?<version>[^"]+)" name="Nyx\.Desktop\.App\.app">')
    if ($embeddedAppIdentities.Count -ne 1 -or
        $embeddedAppIdentities[0].Groups['version'].Value -cne $Version) {
        throw 'The embedded application manifest version does not match the package version.'
    }
    if ($Channel -eq 'stable') {
        $appVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($entryPoint)
        $updaterVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($updater)
        $appAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($appAssembly).Version.ToString()
        $expectedProductVersion = "$Version+$($stableIdentity.Commit)"
        if ($appVersionInfo.FileVersion -cne $Version -or
            $appAssemblyVersion -cne $Version -or
            $updaterVersionInfo.FileVersion -cne $Version -or
            $appVersionInfo.ProductVersion -cne $expectedProductVersion -or
            $updaterVersionInfo.ProductVersion -cne $expectedProductVersion) {
            throw 'Stable app/updater versions or embedded commit do not match the tag-derived release.'
        }
    }
    if (Get-ChildItem -LiteralPath (Join-Path $publishRoot 'Assets\Tools') -Filter 'Nyx.Genshin120.*.dll' -File) {
        throw 'The packaged Genshin 120 FPS helper contains a forbidden loose payload.'
    }
    Assert-NoPrivateBuildStrings -Root $publishRoot -Needles @(
        $userProfile,
        $cargoHome,
        '.cargo',
        $repositoryRoot,
        $workRoot,
        (Split-Path -Leaf $workRoot)
    )

    $payloadFile = "Nyx-Desktop-$Version-win-x64.zip"
    $payloadPath = Join-Path $payloadRoot $payloadFile
    New-DeterministicZip -SourceRoot $publishRoot -Destination $payloadPath -ExcludePdb

    $fileEntries = @()
    foreach ($file in (Get-PayloadFiles -Root $publishRoot)) {
        $fileEntries += [ordered]@{
            path = $file.Relative
            size = [long]$file.Item.Length
            sha256 = (Get-FileHash -LiteralPath $file.Item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

    $payloadInfo = Get-Item -LiteralPath $payloadPath
    $packageUrl = if ($Channel -eq 'stable') {
        "https://pengo.gg/desktop/updates/stable/$payloadFile"
    }
    else {
        $null
    }
    $release = [ordered]@{
        schemaVersion = 1
        product = 'nyx-desktop'
        channel = $Channel
        version = $Version
        architecture = 'win-x64'
        packageFile = $payloadFile
        packageSize = [long]$payloadInfo.Length
        packageSha256 = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        entryPoint = 'Nyx.Desktop.App.exe'
        packageUrl = $packageUrl
        files = $fileEntries
    }
    $releaseJson = $release | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $bundleRoot 'release.json'), $releaseJson + "`n", [Text.UTF8Encoding]::new($false))
    & $updater verify --manifest (Join-Path $bundleRoot 'release.json') --package $payloadPath
    if ($LASTEXITCODE -ne 0) { throw 'Generated payload verification failed.' }
    Copy-Item -LiteralPath (Join-Path $packagingRoot 'scripts\Install-Nyx.ps1') -Destination $bundleRoot
    Copy-Item -LiteralPath (Join-Path $packagingRoot 'scripts\Uninstall-Nyx.ps1') -Destination $bundleRoot
    Copy-Item -LiteralPath (Join-Path $packagingRoot 'first-run-defaults.json') -Destination $bundleRoot
    Copy-Item -LiteralPath $updater -Destination $bundleRoot
    $notes = [IO.File]::ReadAllText((Join-Path $packagingRoot 'release-notes.md')).Replace('{{VERSION}}', $Version)
    [IO.File]::WriteAllText((Join-Path $bundleRoot 'release-notes.md'), $notes, [Text.UTF8Encoding]::new($false))

    New-DeterministicZip -SourceRoot $bundleRoot -Destination $temporaryArtifactPath
    [IO.File]::WriteAllText($temporaryManifestPath, $releaseJson + "`n", [Text.UTF8Encoding]::new($false))
    $artifactHash = (Get-FileHash -LiteralPath $temporaryArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($temporaryHashPath, "$artifactHash  $artifactBase.zip`n", [Text.UTF8Encoding]::new($false))
    $artifactBytes = (Get-Item -LiteralPath $temporaryArtifactPath).Length
    Install-GeneratedFile -Source $temporaryArtifactPath -Destination $artifactPath
    Install-GeneratedFile -Source $temporaryManifestPath -Destination $manifestArtifactPath
    Install-GeneratedFile -Source $temporaryHashPath -Destination $hashPath

    Write-Output "NYX_PACKAGE=CREATED"
    Write-Output "CHANNEL=$Channel"
    if ($Channel -eq 'stable') {
        Write-Output "TAG=$($stableIdentity.Tag)"
        Write-Output "COMMIT=$($stableIdentity.Commit)"
    }
    Write-Output "VERSION=$Version"
    Write-Output "ARTIFACT=$artifactPath"
    Write-Output "BYTES=$artifactBytes"
    Write-Output "SHA256=$artifactHash"
}
finally {
    if (Test-Path -LiteralPath $workRoot -PathType Container) {
        Remove-GeneratedDirectory -LiteralPath $workRoot
    }
}
