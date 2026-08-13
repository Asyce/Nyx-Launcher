#Requires -Version 5.1

[CmdletBinding()]
param(
    [string] $DesktopRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$notReadyExitCode = 3
$maximumRootCharacters = 512
$maximumXmlBytes = 1048576
$allowedChannels = @('private-sideload', 'website', 'store')
$blockers = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)

function Write-NotReadyAndExit {
    param([Parameter(Mandatory)] [string] $Blocker)

    Write-Output 'NYX_PACKAGE_CONFIGURATION=NOT_READY'
    Write-Output "BLOCKER=$Blocker"
    exit $notReadyExitCode
}

function Add-Blocker {
    param([Parameter(Mandatory)] [string] $Name)
    [void] $blockers.Add($Name)
}

function Test-ReparsePoint {
    param([Parameter(Mandatory)] [System.IO.FileSystemInfo] $Item)
    return ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
}

function Get-SafeLocalRoot {
    param(
        [Parameter(Mandatory)] [string] $Candidate
    )

    if ($Candidate.Length -gt $maximumRootCharacters -or
        $Candidate -notmatch '^[A-Za-z]:\\' -or
        $Candidate -match '^[A-Za-z]:\\[?.]\\') {
        return $null
    }

    try {
        $driveRoot = $Candidate.Substring(0, 3)
        $drive = Get-Item -LiteralPath $driveRoot -Force -ErrorAction Stop
        if (-not $drive.PSIsContainer -or (Test-ReparsePoint -Item $drive)) {
            return $null
        }

        $segments = @($Candidate.Substring(3).TrimEnd('\') -split '\\' | Where-Object { $_.Length -gt 0 })
        $current = $driveRoot.TrimEnd('\')
        foreach ($segment in $segments) {
            if ($segment -in @('.', '..') -or $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
                return $null
            }

            $next = $current + '\' + $segment
            $item = Get-Item -LiteralPath $next -Force -ErrorAction Stop
            if (-not $item.PSIsContainer -or (Test-ReparsePoint -Item $item)) {
                return $null
            }
            $current = $item.FullName.TrimEnd('\')
        }

        return $current
    }
    catch {
        return $null
    }
}

function Get-ContainedInputState {
    param(
        [Parameter(Mandatory)] [string] $LiteralPath,
        [Parameter(Mandatory)] [string] $Root
    )

    try {
        if ($LiteralPath.Length -gt $maximumRootCharacters) {
            return 'Unsafe'
        }

        $rootPrefix = $Root.TrimEnd('\') + '\'
        if (-not $LiteralPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            return 'Unsafe'
        }

        $relative = $LiteralPath.Substring($rootPrefix.Length)
        $segments = @($relative -split '\\' | Where-Object { $_.Length -gt 0 })
        if ($segments.Count -eq 0) {
            return 'Unsafe'
        }

        $current = $Root
        for ($index = 0; $index -lt $segments.Count; $index++) {
            $segment = $segments[$index]
            if ($segment -in @('.', '..') -or $segment.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
                return 'Unsafe'
            }

            $next = $current.TrimEnd('\') + '\' + $segment
            try {
                $item = Get-Item -LiteralPath $next -Force -ErrorAction Stop
            }
            catch [System.Management.Automation.ItemNotFoundException] {
                return 'Missing'
            }

            if (Test-ReparsePoint -Item $item) {
                return 'Unsafe'
            }

            $isLast = $index -eq ($segments.Count - 1)
            if (-not $isLast -and -not $item.PSIsContainer) {
                return 'Unsafe'
            }
            $current = $item.FullName
        }

        return $(if ($item.PSIsContainer) { 'Container' } else { 'Leaf' })
    }
    catch {
        return 'Unsafe'
    }
}

function Read-SafeXml {
    param([Parameter(Mandatory)] [string] $LiteralPath)

    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if ($item.Length -gt $maximumXmlBytes -or (Test-ReparsePoint -Item $item)) {
        throw 'UnsafeXml'
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

function Get-UniqueUnconditionalValue {
    param(
        [Parameter(Mandatory)] [System.Xml.XmlDocument] $Document,
        [Parameter(Mandatory)] [string] $PropertyName
    )

    $nodes = @($Document.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$PropertyName']"))
    if ($nodes.Count -ne 1) {
        return $null
    }

    $node = $nodes[0]
    $nodeCondition = $node.Attributes['Condition']
    $groupCondition = $node.ParentNode.Attributes['Condition']
    if (($null -ne $nodeCondition -and -not [string]::IsNullOrWhiteSpace($nodeCondition.Value)) -or
        ($null -ne $groupCondition -and -not [string]::IsNullOrWhiteSpace($groupCondition.Value))) {
        return $null
    }

    return $node.InnerText.Trim()
}

function Test-SafeSigningKey {
    param(
        [Parameter(Mandatory)] [string] $Value,
        [Parameter(Mandatory)] [string] $AppRoot,
        [Parameter(Mandatory)] [string] $Root
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 240 -or
        [System.IO.Path]::IsPathRooted($Value) -or
        [System.IO.Path]::GetExtension($Value) -ne '.pfx' -or
        @($Value -split '[\\/]' | Where-Object { $_ -eq '..' }).Count -gt 0) {
        return $false
    }

    try {
        $candidate = $AppRoot.TrimEnd('\') + '\' + ($Value -replace '/', '\')
        if ((Get-ContainedInputState -LiteralPath $candidate -Root $Root) -ne 'Leaf') {
            return $false
        }

        $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
        return -not (Test-ReparsePoint -Item $item) -and
            $item.Length -gt 0 -and $item.Length -le $maximumXmlBytes
    }
    catch {
        return $false
    }
}

if ([string]::IsNullOrWhiteSpace($DesktopRoot)) {
    $DesktopRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
}

$root = Get-SafeLocalRoot -Candidate $DesktopRoot
if ($null -eq $root) {
    Write-NotReadyAndExit -Blocker 'RootInvalid'
}

$appRoot = Join-Path $root 'src\Nyx.Desktop.App'
$projectPath = Join-Path $appRoot 'Nyx.Desktop.App.csproj'
$manifestPath = Join-Path $appRoot 'Package.appxmanifest'
$profilesRoot = Join-Path $appRoot 'Properties\PublishProfiles'

$appRootState = Get-ContainedInputState -LiteralPath $appRoot -Root $root
if ($appRootState -notin @('Missing', 'Container')) {
    Write-NotReadyAndExit -Blocker 'RootInvalid'
}

$projectXml = $null
$projectState = Get-ContainedInputState -LiteralPath $projectPath -Root $root
if ($projectState -eq 'Missing') {
    Add-Blocker 'ProjectMissing'
}
elseif ($projectState -ne 'Leaf') {
    Write-NotReadyAndExit -Blocker 'RootInvalid'
}
else {
    try {
        $projectXml = Read-SafeXml -LiteralPath $projectPath
    }
    catch {
        Add-Blocker 'ProjectMalformed'
    }
}

$manifestXml = $null
$manifestState = Get-ContainedInputState -LiteralPath $manifestPath -Root $root
if ($manifestState -eq 'Missing') {
    Add-Blocker 'ManifestMissing'
}
elseif ($manifestState -ne 'Leaf') {
    Write-NotReadyAndExit -Blocker 'RootInvalid'
}
else {
    try {
        $manifestXml = Read-SafeXml -LiteralPath $manifestPath
    }
    catch {
        Add-Blocker 'ManifestMalformed'
    }
}

if ($null -ne $manifestXml) {
    $identities = @($manifestXml.SelectNodes("/*[local-name()='Package']/*[local-name()='Identity']"))
    $identity = if ($identities.Count -eq 1) { $identities[0] } else { $null }
    $publisherAttribute = if ($null -eq $identity) { $null } else { $identity.Attributes['Publisher'] }
    $publisher = if ($null -eq $publisherAttribute) { '' } else { $publisherAttribute.Value.Trim() }
    if ($identities.Count -ne 1) {
        Add-Blocker 'PublisherInvalid'
    }
    elseif ([string]::IsNullOrWhiteSpace($publisher) -or $publisher.Equals('CN=AppPublisher', [StringComparison]::OrdinalIgnoreCase)) {
        Add-Blocker 'PublisherPlaceholder'
    }
    elseif ($publisher.Length -gt 256 -or $publisher -notmatch '(?i)^CN=[^,=]{1,200}(?:,[A-Z]+=[^,=]{1,200})*$') {
        Add-Blocker 'PublisherInvalid'
    }
}

if ($null -ne $projectXml) {
    $channelValue = Get-UniqueUnconditionalValue -Document $projectXml -PropertyName 'NyxDistributionChannel'
    if ($null -eq $channelValue -or $allowedChannels -notcontains $channelValue.ToLowerInvariant()) {
        Add-Blocker 'DistributionChannelUnresolved'
    }

    $packageTypeNodes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='WindowsPackageType']"))
    if ($packageTypeNodes.Count -gt 0) {
        $projectPackageType = Get-UniqueUnconditionalValue -Document $projectXml -PropertyName 'WindowsPackageType'
        if ($null -eq $projectPackageType -or $projectPackageType.Equals('None', [StringComparison]::OrdinalIgnoreCase)) {
            Add-Blocker 'PackagedAppDisabled'
        }
    }

    $keyNodes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='PackageCertificateKeyFile']"))
    $thumbprintNodes = @($projectXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='PackageCertificateThumbprint']"))
    $signingNodeCount = $keyNodes.Count + $thumbprintNodes.Count
    if ($signingNodeCount -eq 0) {
        Add-Blocker 'SigningIdentityMissing'
    }
    elseif ($signingNodeCount -ne 1) {
        Add-Blocker 'SigningIdentityInvalid'
    }
    elseif ($thumbprintNodes.Count -eq 1) {
        $thumbprint = Get-UniqueUnconditionalValue -Document $projectXml -PropertyName 'PackageCertificateThumbprint'
        if ($null -eq $thumbprint -or $thumbprint -notmatch '^(?:[A-Fa-f0-9]{40}|[A-Fa-f0-9]{64})$') {
            Add-Blocker 'SigningIdentityInvalid'
        }
    }
    else {
        $keyFile = Get-UniqueUnconditionalValue -Document $projectXml -PropertyName 'PackageCertificateKeyFile'
        if ($null -eq $keyFile -or -not (Test-SafeSigningKey -Value $keyFile -AppRoot $appRoot -Root $root)) {
            Add-Blocker 'SigningIdentityInvalid'
        }
    }
}

$installableProfiles = 0
$invalidX64Profiles = 0
$profilesState = Get-ContainedInputState -LiteralPath $profilesRoot -Root $root
if ($profilesState -eq 'Missing') {
    Add-Blocker 'PublishProfileMissing'
}
elseif ($profilesState -ne 'Container') {
    Write-NotReadyAndExit -Blocker 'RootInvalid'
}
else {
    $profileFiles = @(Get-ChildItem -LiteralPath $profilesRoot -Filter '*.pubxml' -File -Force -ErrorAction SilentlyContinue | Select-Object -First 33)
    if ($profileFiles.Count -eq 0) {
        Add-Blocker 'PublishProfileMissing'
    }
    elseif ($profileFiles.Count -gt 32) {
        Add-Blocker 'PublishProfileSetInvalid'
    }
    else {
        foreach ($profileFile in $profileFiles) {
            if ((Get-ContainedInputState -LiteralPath $profileFile.FullName -Root $root) -ne 'Leaf') {
                Write-NotReadyAndExit -Blocker 'RootInvalid'
            }

            try {
                $profileXml = Read-SafeXml -LiteralPath $profileFile.FullName
                $platform = Get-UniqueUnconditionalValue -Document $profileXml -PropertyName 'Platform'
                $runtime = Get-UniqueUnconditionalValue -Document $profileXml -PropertyName 'RuntimeIdentifier'
                $generate = Get-UniqueUnconditionalValue -Document $profileXml -PropertyName 'GenerateAppxPackageOnBuild'
                $sign = Get-UniqueUnconditionalValue -Document $profileXml -PropertyName 'AppxPackageSigningEnabled'
                $profilePackageTypeNodes = @($profileXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='WindowsPackageType']"))
                $packageTypeSafe = $profilePackageTypeNodes.Count -eq 0
                if ($profilePackageTypeNodes.Count -eq 1) {
                    $profilePackageType = Get-UniqueUnconditionalValue -Document $profileXml -PropertyName 'WindowsPackageType'
                    $packageTypeSafe = $null -ne $profilePackageType -and
                        -not $profilePackageType.Equals('None', [StringComparison]::OrdinalIgnoreCase)
                }
                $isValidProfile = $null -ne $platform -and $platform.Equals('x64', [StringComparison]::OrdinalIgnoreCase) -and
                    $null -ne $runtime -and $runtime.Equals('win-x64', [StringComparison]::OrdinalIgnoreCase) -and
                    $null -ne $generate -and $generate.Equals('true', [StringComparison]::OrdinalIgnoreCase) -and
                    $null -ne $sign -and $sign.Equals('true', [StringComparison]::OrdinalIgnoreCase) -and
                    $packageTypeSafe
                if ($isValidProfile) {
                    $installableProfiles++
                }
                else {
                    $platformNodes = @($profileXml.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='Platform']"))
                    if (@($platformNodes | Where-Object { $_.InnerText.Trim().Equals('x64', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
                        $invalidX64Profiles++
                    }
                }
            }
            catch {
                Add-Blocker 'PublishProfileMalformed'
            }
        }
    }
}

if ($installableProfiles -ne 1 -or $invalidX64Profiles -gt 0) {
    Add-Blocker 'InstallablePackageProfileMissing'
}

if ($blockers.Count -gt 0) {
    Write-Output 'NYX_PACKAGE_CONFIGURATION=NOT_READY'
    foreach ($blocker in $blockers) {
        Write-Output "BLOCKER=$blocker"
    }
    exit $notReadyExitCode
}

Write-Output 'NYX_PACKAGE_CONFIGURATION=READY'
exit 0
