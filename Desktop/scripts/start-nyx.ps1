#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch] $CheckOnly,
    [switch] $Restore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ExitEnvironment = 10
$script:ExitSdk = 11
$script:ExitProject = 12
$script:ExitRunSupport = 13
$script:ExitRestore = 14
$script:ExitRegistration = 15
$script:ExitRun = 20

function Stop-NyxStart {
    param(
        [Parameter(Mandatory)] [int] $Code,
        [Parameter(Mandatory)] [string] $Message
    )

    Write-Host "Nyx is not ready to start: $Message" -ForegroundColor Red
    exit $Code
}

function Read-BoundedText {
    param(
        [Parameter(Mandatory)] [string] $LiteralPath,
        [Parameter(Mandatory)] [long] $MaximumBytes
    )

    $item = Get-Item -LiteralPath $LiteralPath -ErrorAction Stop
    if ($item.Length -gt $MaximumBytes) {
        throw 'The required project file is unexpectedly large.'
    }

    return [System.IO.File]::ReadAllText($item.FullName)
}

function Read-SafeXml {
    param([Parameter(Mandatory)] [string] $LiteralPath)

    $item = Get-Item -LiteralPath $LiteralPath -ErrorAction Stop
    if ($item.Length -gt 1048576) {
        throw 'The required project XML is unexpectedly large.'
    }

    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = $null
    try {
        $reader = [System.Xml.XmlReader]::Create($item.FullName, $settings)
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
    }
}

if ($CheckOnly -and $Restore) {
    Stop-NyxStart -Code $script:ExitRestore -Message 'Check-only never restores. Remove -Restore and try again.'
}

$desktopRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$globalJsonPath = Join-Path $desktopRoot 'global.json'
$projectPath = Join-Path $desktopRoot 'src\Nyx.Desktop.App\Nyx.Desktop.App.csproj'
$assetsPath = Join-Path $desktopRoot 'src\Nyx.Desktop.App\obj\project.assets.json'

if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
    Stop-NyxStart -Code $script:ExitEnvironment -Message 'Nyx needs Windows 11.'
}

if ([Environment]::OSVersion.Version.Build -lt 22621) {
    Stop-NyxStart -Code $script:ExitEnvironment -Message 'Install Windows 11 version 22H2 or newer (build 22621+).'
}

if (-not [Environment]::Is64BitOperatingSystem -or
    [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    Stop-NyxStart -Code $script:ExitEnvironment -Message 'Use 64-bit PowerShell on an x64 Windows PC.'
}

foreach ($requiredPath in @($globalJsonPath, $projectPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        Stop-NyxStart -Code $script:ExitProject -Message 'The Desktop project is incomplete. Keep the scripts inside the Nyx repository.'
    }
}

try {
    $globalJson = Read-BoundedText -LiteralPath $globalJsonPath -MaximumBytes 65536 | ConvertFrom-Json
    $pinnedSdk = [string] $globalJson.sdk.version
}
catch {
    Stop-NyxStart -Code $script:ExitProject -Message 'Desktop\global.json is invalid.'
}

if ([string]::IsNullOrWhiteSpace($pinnedSdk) -or $pinnedSdk.Length -gt 32) {
    Stop-NyxStart -Code $script:ExitProject -Message 'Desktop\global.json does not contain a valid pinned SDK version.'
}

$dotnet = Get-Command 'dotnet.exe' -CommandType Application -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    Stop-NyxStart -Code $script:ExitSdk -Message "Install the .NET SDK $pinnedSdk (x64), then run this check again."
}

$oldTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$oldFirstRun = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$oldLogo = $env:DOTNET_NOLOGO
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $sdkLines = @(& $dotnet.Source --list-sdks 2>$null)
}
catch {
    Stop-NyxStart -Code $script:ExitSdk -Message "The .NET SDK could not be checked. Install SDK $pinnedSdk (x64)."
}
finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldTelemetry
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $oldFirstRun
    $env:DOTNET_NOLOGO = $oldLogo
}

$hasPinnedSdk = $false
foreach ($sdkLine in $sdkLines) {
    if ($sdkLine -match ('^' + [Regex]::Escape($pinnedSdk) + '\s+\[')) {
        $hasPinnedSdk = $true
        break
    }
}

if (-not $hasPinnedSdk) {
    Stop-NyxStart -Code $script:ExitSdk -Message "Install the pinned .NET SDK $pinnedSdk (x64)."
}

try {
    $projectXml = Read-SafeXml -LiteralPath $projectPath
    $windowsAppSdk = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference' and (@Include='Microsoft.WindowsAppSDK' or @Include='Microsoft.WindowsAppSDK.WinUI')]"))
    $packageTypes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='WindowsPackageType']"))
    $selfContainedValues = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='WindowsAppSDKSelfContained']"))
    $trimValues = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='PublishTrimmed']"))
    $targetFrameworkValues = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='TargetFramework']"))
}
catch {
    Stop-NyxStart -Code $script:ExitProject -Message 'The Nyx app project XML is invalid.'
}

if ($windowsAppSdk.Count -ne 1 -or
    $packageTypes.Count -ne 1 -or -not $packageTypes[0].InnerText.Trim().Equals('None', [StringComparison]::OrdinalIgnoreCase) -or
    $selfContainedValues.Count -ne 1 -or -not $selfContainedValues[0].InnerText.Trim().Equals('true', [StringComparison]::OrdinalIgnoreCase) -or
    $trimValues.Count -ne 1 -or -not $trimValues[0].InnerText.Trim().Equals('false', [StringComparison]::OrdinalIgnoreCase) -or
    $targetFrameworkValues.Count -ne 1) {
    Stop-NyxStart -Code $script:ExitRunSupport -Message 'The reviewed unpackaged x64 configuration is missing or ambiguous.'
}

$targetFramework = $targetFrameworkValues[0].InnerText.Trim()
if ($targetFramework.Length -gt 80 -or
    $targetFramework -notmatch '^net[0-9]+\.[0-9]+-windows10\.0\.[0-9]+\.[0-9]+$') {
    Stop-NyxStart -Code $script:ExitProject -Message 'The reviewed target framework is invalid.'
}

$isAdministrator = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdministrator) {
    Stop-NyxStart -Code $script:ExitRegistration -Message 'Close this administrator window and start Nyx normally. The launcher itself never needs elevation.'
}

function Test-RunAssets {
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        return $false
    }

    try {
        $assetsText = Read-BoundedText -LiteralPath $assetsPath -MaximumBytes 52428800
        $assets = $assetsText | ConvertFrom-Json
        $libraryNames = @($assets.libraries.PSObject.Properties.Name)
        return @($libraryNames | Where-Object {
            $_ -like 'Microsoft.WindowsAppSDK/*' -or
            $_ -like 'Microsoft.WindowsAppSDK.WinUI/*'
        }).Count -eq 1
    }
    catch {
        return $false
    }
}

$assetsReady = Test-RunAssets
if (-not $assetsReady -and $Restore -and -not $CheckOnly) {
    Write-Host 'Run files are missing. Restoring the reviewed Nyx projects now...'
    Push-Location -LiteralPath $desktopRoot
    try {
        & $dotnet.Source restore 'src\Nyx.Desktop.App\Nyx.Desktop.App.csproj' -r 'win-x64'
        if ($LASTEXITCODE -ne 0) {
            Stop-NyxStart -Code $script:ExitRestore -Message 'Restore failed. Check your connection and the .NET SDK, then retry.'
        }
    }
    finally {
        Pop-Location
    }
    $assetsReady = Test-RunAssets
}

if (-not $assetsReady) {
    Stop-NyxStart -Code $script:ExitRestore -Message 'Restore assets are missing. Run `dotnet restore Desktop\Nyx.Desktop.slnx`, or use -Restore for a real start.'
}

$outputRoot = Join-Path $desktopRoot "src\Nyx.Desktop.App\bin\x64\Release\$targetFramework\win-x64"
$executablePath = Join-Path $outputRoot 'Nyx.Desktop.App.exe'
$achievementHelperOutput = Join-Path $outputRoot 'Assets\Tools\pengo-achievements-launcher.exe'
$requiredOutputPaths = @(
    $executablePath,
    (Join-Path $outputRoot 'Nyx.Desktop.App.pri'),
    (Join-Path $outputRoot 'Microsoft.ui.xaml.dll'),
    (Join-Path $outputRoot 'Assets\Catalog\giicon.png'),
    (Join-Path $outputRoot 'Assets\Iris\nyx-logo.png'),
    (Join-Path $outputRoot 'Assets\Brand\kofi-logo.png'),
    (Join-Path $outputRoot 'Assets\Content\launcher-banners-v1.json')
)
if (-not $CheckOnly) {
    $requiredOutputPaths += $achievementHelperOutput
}

function Test-UnpackagedOutput {
    foreach ($path in $requiredOutputPaths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return $false
        }
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($item.Length -le 0) {
            return $false
        }
    }
    return $true
}

if ($CheckOnly) {
    if (-not (Test-UnpackagedOutput)) {
        Stop-NyxStart -Code $script:ExitRunSupport -Message 'The reviewed unpackaged x64 build output is incomplete. Build Nyx, then retry.'
    }
    Write-Host "Nyx developer start is ready (Windows x64, SDK $pinnedSdk, unpackaged self-contained app)." -ForegroundColor Green
    exit 0
}

Write-Host 'Building the reviewed unpackaged x64 Nyx app...'
$cargo = @(Get-Command 'cargo.exe' -CommandType Application -ErrorAction SilentlyContinue)[0]
$python = @(Get-Command 'python.exe' -CommandType Application -ErrorAction SilentlyContinue)[0]
if ($null -eq $cargo -or $null -eq $python) {
    Stop-NyxStart -Code $script:ExitRunSupport -Message 'Install Rust and Python so Nyx can build and verify the achievement helper.'
}
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $desktopRoot '..'))
$achievementHelperRoot = Join-Path $repositoryRoot 'Extractor\Achievements'
$achievementHelperBuildRoot = Join-Path $desktopRoot '.verification-build\achievement-helper'
$genshin120HelperRoot = Join-Path $desktopRoot 'tools\Nyx.Genshin120.NativeHelper'
$genshin120UpstreamRoot = Join-Path $repositoryRoot '.verification-build\upstream-genshin-fps-v3.5.0'
$genshin120ReleaseRoot = Join-Path $repositoryRoot '.verification-build\genshin120-native-helper\release'
$genshin120Helper = Join-Path $genshin120ReleaseRoot 'Nyx.Genshin120.Helper.exe'
$genshin120License = Join-Path $genshin120HelperRoot 'LICENSE-THIRD-PARTY.txt'
$genshin120Provenance = Join-Path $genshin120HelperRoot 'PROVENANCE.md'
$oldCargoTarget = $env:CARGO_TARGET_DIR
$oldRustFlags = $env:RUSTFLAGS
try {
    $env:CARGO_TARGET_DIR = $achievementHelperBuildRoot
    $env:RUSTFLAGS = '-C target-feature=+crt-static'
    Push-Location -LiteralPath $achievementHelperRoot
    try {
        & $cargo.Source build --locked --release --target x86_64-pc-windows-msvc --bin pengo-achievements-launcher
        if ($LASTEXITCODE -ne 0) {
            Stop-NyxStart -Code $script:ExitRun -Message 'The achievement helper build failed.'
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:CARGO_TARGET_DIR = $oldCargoTarget
    $env:RUSTFLAGS = $oldRustFlags
}
$builtAchievementHelper = Join-Path $achievementHelperBuildRoot 'x86_64-pc-windows-msvc\release\pengo-achievements-launcher.exe'
& $python.Source (Join-Path $achievementHelperRoot 'tools\verify_release.py') $builtAchievementHelper
if ($LASTEXITCODE -ne 0) {
    Stop-NyxStart -Code $script:ExitRun -Message 'The achievement helper did not pass verification.'
}
$achievementHelperSha256 = (Get-FileHash -LiteralPath $builtAchievementHelper -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not (Test-Path -LiteralPath $genshin120UpstreamRoot -PathType Container)) {
    $git = @(Get-Command 'git.exe' -CommandType Application -ErrorAction SilentlyContinue)[0]
    if ($null -eq $git) {
        Stop-NyxStart -Code $script:ExitRunSupport -Message 'Install Git so Nyx can verify the pinned Genshin 120 FPS helper source.'
    }
    [void] (New-Item -ItemType Directory -Path (Split-Path -Parent $genshin120UpstreamRoot) -Force)
    & $git.Source -c core.longpaths=true clone --quiet --depth 1 --branch v3.5.0 https://github.com/34736384/genshin-fps-unlock.git $genshin120UpstreamRoot
    if ($LASTEXITCODE -ne 0) {
        Stop-NyxStart -Code $script:ExitRun -Message 'The pinned Genshin 120 FPS source could not be retrieved.'
    }
}
$genshin120Commit = (& git -C $genshin120UpstreamRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $genshin120Commit -cne '2b85d61dd06f6e11ad86fdd6bd90339f9abc58eb') {
    Stop-NyxStart -Code $script:ExitRun -Message 'The pinned Genshin 120 FPS source changed.'
}
& (Join-Path $genshin120HelperRoot 'verify-release.ps1')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $genshin120Helper -PathType Leaf)) {
    Stop-NyxStart -Code $script:ExitRun -Message 'The Genshin 120 FPS helper did not pass verification.'
}
$genshin120HelperSha256 = (Get-FileHash -LiteralPath $genshin120Helper -Algorithm SHA256).Hash.ToLowerInvariant()
Push-Location -LiteralPath $desktopRoot
try {
    $buildArguments = @(
        'build',
        'src\Nyx.Desktop.App\Nyx.Desktop.App.csproj',
        '-c', 'Release',
        '-r', 'win-x64',
        '-p:Platform=x64',
        "-p:AchievementHelperSource=$builtAchievementHelper",
        "-p:AchievementHelperSha256=$achievementHelperSha256",
        "-p:Genshin120HelperSource=$genshin120Helper",
        "-p:Genshin120HelperSha256=$genshin120HelperSha256",
        "-p:Genshin120LicenseSource=$genshin120License",
        "-p:Genshin120ProvenanceSource=$genshin120Provenance",
        '--no-restore'
    )
    & $dotnet.Source @buildArguments
    $buildExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($buildExitCode -ne 0 -or -not (Test-UnpackagedOutput)) {
    Stop-NyxStart -Code $script:ExitRun -Message 'The reviewed unpackaged x64 build failed or produced incomplete output.'
}

Write-Host 'Starting Nyx as a normal-user unpackaged app...'
try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $executablePath
    $startInfo.WorkingDirectory = $outputRoot
    $startInfo.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'The Nyx process was not created.'
    }
    Start-Sleep -Milliseconds 750
    if ($process.HasExited -and $process.ExitCode -ne 0) {
        throw 'The Nyx process exited during startup.'
    }
}
catch {
    Stop-NyxStart -Code $script:ExitRun -Message 'The unpackaged Nyx app could not start. Run -CheckOnly, then retry.'
}

exit 0
