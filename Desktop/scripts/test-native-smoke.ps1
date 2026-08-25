#Requires -Version 5.1

<#
.SYNOPSIS
Runs the native Windows UI smoke test against a Nyx launcher package on PENGO.

.DESCRIPTION
Verifies the outer ZIP with its SHA256 sidecar, verifies and extracts the inner
payload, isolates both Nyx data folders, and drives only safe launcher controls.

.PARAMETER StateWorker
Full path to the prebuilt Release StateWorker DLL. Build it before running with:
dotnet build Desktop/tests/Nyx.Desktop.StateWorker/Nyx.Desktop.StateWorker.csproj --configuration Release

.EXAMPLE
.\test-native-smoke.ps1 `
  -PackageZip D:\NyxSmoke\Nyx-Desktop.zip `
  -Sha256Sidecar D:\NyxSmoke\Nyx-Desktop.zip.sha256 `
  -EvidenceDirectory D:\NyxSmoke\evidence `
  -StateWorker D:\Nyx\Desktop\tests\Nyx.Desktop.StateWorker\bin\Release\net10.0\Nyx.Desktop.StateWorker.dll
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageZip,
    [Parameter(Mandatory)]
    [string] $Sha256Sidecar,
    [Parameter(Mandatory)]
    [string] $EvidenceDirectory,
    [Parameter(Mandatory)]
    [string] $StateWorker
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runId = [guid]::NewGuid().ToString('N')
$temporaryRoot = $null
$appProcess = $null
$evidenceCreated = $false
$failureCode = $null
$restoreFailed = $false
$restoreFailureCode = $null
$pengoParentCleanupFailureCode = $null
$screenshotEvidence = @()
$retryControlObserved = $false
$publisherAccountFixture = 'not-seeded'
$uiDeadlineSeconds = 180
$uiRuntime = $null
$uiChecks = [ordered]@{
    cachedResourceVisibleWithinOneSecondOfGameSwitch = $false
    cachedResourceGameSwitchMilliseconds = $null
    cachedResourceSelectionMilliseconds = $null
    cachedResourceLastObservedState = 'not-observed'
    shellControlsFound = $false
    secondInstanceSuppressed = $false
    gamesSelected = @()
    sideEffectControlsInspected = 0
    retryStateCheckedForGames = 0
    retryStates = @()
    bannersCollapsedAndExpanded = $false
    accountControlObserved = $false
    accountCollapsedAndExpanded = $false
    mainTabAndShiftTabContained = $false
    modalTabAndShiftTabContained = $false
    settingsCanceledWithEnter = $false
    settingsCanceledWithEscape = $false
}
$processNames = @('Nyx', 'Nyx.Desktop', 'Nyx.Desktop.App', 'Nyx.Desktop.Update')
$gameNames = @(
    'Genshin Impact',
    'Honkai: Star Rail',
    'Zenless Zone Zero',
    'Wuthering Waves',
    'Arknights: Endfield'
)

function Throw-SmokeFailure {
    param([Parameter(Mandatory)] [string] $Code)
    throw $Code
}

function Get-FailureCode {
    param([Parameter(Mandatory)] [System.Management.Automation.ErrorRecord] $ErrorRecord)
    $message = [string] $ErrorRecord.Exception.Message
    if ($message -cmatch '^[A-Z0-9_]{3,64}$') { return $message }
    return 'UNEXPECTED_FAILURE'
}

function Test-ReparsePoint {
    param([Parameter(Mandatory)] [IO.FileSystemInfo] $Item)
    return ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
}

function Assert-ExistingNormalFile {
    param([Parameter(Mandatory)] [string] $LiteralPath)
    if (-not [IO.Path]::IsPathRooted($LiteralPath) -or $LiteralPath -notmatch '^[A-Za-z]:\\') {
        Throw-SmokeFailure 'INPUT_PATH_INVALID'
    }
    Assert-NoReparseComponents -LiteralPath $LiteralPath
    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        Throw-SmokeFailure 'INPUT_MISSING'
    }
    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    if ($item.PSIsContainer -or (Test-ReparsePoint $item)) {
        Throw-SmokeFailure 'INPUT_PATH_INVALID'
    }
    return $item.FullName
}

function Assert-NoReparseComponents {
    param([Parameter(Mandatory)] [string] $LiteralPath)
    $full = [IO.Path]::GetFullPath($LiteralPath)
    if ($full -notmatch '^[A-Za-z]:\\') { Throw-SmokeFailure 'PATH_INVALID' }
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root.TrimEnd('\')
    foreach ($segment in $full.Substring($root.Length).Split('\')) {
        if ([string]::IsNullOrWhiteSpace($segment)) { continue }
        $current = $current + '\' + $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (Test-ReparsePoint $item) { Throw-SmokeFailure 'REPARSE_PATH_BLOCKED' }
        }
    }
}

function Test-PathsOverlap {
    param(
        [Parameter(Mandatory)] [string] $Left,
        [Parameter(Mandatory)] [string] $Right
    )
    $leftFull = [IO.Path]::GetFullPath($Left).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $rightFull = [IO.Path]::GetFullPath($Right).TrimEnd([IO.Path]::DirectorySeparatorChar)
    return [string]::Equals($leftFull, $rightFull, [StringComparison]::OrdinalIgnoreCase) -or
        $leftFull.StartsWith($rightFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $rightFull.StartsWith($leftFull + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Test-SafeRelativeFile {
    param([Parameter(Mandatory)] [string] $RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        $RelativePath.Length -gt 512 -or
        $RelativePath.StartsWith('/') -or
        $RelativePath.Contains('\') -or
        $RelativePath.Contains(':') -or
        $RelativePath.EndsWith('/')) {
        return $false
    }
    $segments = @($RelativePath.Split('/'))
    return $segments.Count -gt 0 -and
        @($segments | Where-Object { $_ -in @('', '.', '..') }).Count -eq 0
}

function Remove-SafeTree {
    param([Parameter(Mandatory)] [string] $LiteralPath)
    if (-not (Test-Path -LiteralPath $LiteralPath)) { return }
    $item = Get-Item -LiteralPath $LiteralPath -Force
    if (-not $item.PSIsContainer) {
        [IO.File]::Delete($item.FullName)
        return
    }
    if (Test-ReparsePoint $item) {
        [IO.Directory]::Delete($item.FullName, $false)
        return
    }
    foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Force)) {
        Remove-SafeTree -LiteralPath $child.FullName
    }
    [IO.Directory]::Delete($item.FullName, $false)
}

function Assert-NoNyxProcesses {
    if (@(Get-Process -Name $processNames -ErrorAction SilentlyContinue).Count -ne 0) {
        Throw-SmokeFailure 'NYX_PROCESS_RUNNING'
    }
}

function Stop-SmokeProcesses {
    if ($null -ne $script:appProcess) {
        try {
            if (-not $script:appProcess.HasExited) {
                [void] $script:appProcess.CloseMainWindow()
                if (-not $script:appProcess.WaitForExit(5000)) { $script:appProcess.Kill() }
            }
        }
        catch { }
    }
    foreach ($process in @(Get-Process -Name $processNames -ErrorAction SilentlyContinue)) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
        catch { }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (@(Get-Process -Name $processNames -ErrorAction SilentlyContinue).Count -ne 0 -and
        [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (@(Get-Process -Name $processNames -ErrorAction SilentlyContinue).Count -ne 0) {
        Throw-SmokeFailure 'PROCESS_STOP_FAILED'
    }
}

function Assert-UiDeadline {
    if ($null -ne $script:uiRuntime -and
        $script:uiRuntime.Elapsed.TotalSeconds -ge $uiDeadlineSeconds) {
        Throw-SmokeFailure 'UI_DEADLINE_EXCEEDED'
    }
}

function Expand-SafeOuterPackage {
    param(
        [Parameter(Mandatory)] [string] $ArchivePath,
        [Parameter(Mandatory)] [string] $Destination
    )
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            if ($archive.Entries.Count -le 0 -or $archive.Entries.Count -gt 32) {
                Throw-SmokeFailure 'OUTER_ENTRY_SET_INVALID'
            }
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            [long] $total = 0
            foreach ($entry in $archive.Entries) {
                if (-not (Test-SafeRelativeFile $entry.FullName) -or
                    -not $seen.Add($entry.FullName) -or
                    (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                    (([IO.FileAttributes] $entry.ExternalAttributes) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    Throw-SmokeFailure 'OUTER_ENTRY_INVALID'
                }
                $total += [long] $entry.Length
                if ($total -gt 6GB) { Throw-SmokeFailure 'OUTER_SIZE_INVALID' }
                $output = Join-Path $Destination ($entry.FullName.Replace('/', '\'))
                $parent = Split-Path -Parent $output
                [void] [IO.Directory]::CreateDirectory($parent)
                $input = $entry.Open()
                $target = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                [long] $written = 0
                $buffer = New-Object byte[] 131072
                try {
                    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $written += $read
                        if ($written -gt [long] $entry.Length) { Throw-SmokeFailure 'OUTER_ENTRY_INVALID' }
                        $target.Write($buffer, 0, $read)
                    }
                }
                finally { $target.Dispose(); $input.Dispose() }
                if ($written -ne [long] $entry.Length) { Throw-SmokeFailure 'OUTER_ENTRY_INVALID' }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Assert-OuterFileSet {
    param(
        [Parameter(Mandatory)] [string] $OuterRoot,
        [Parameter(Mandatory)] [string] $PayloadFile
    )
    $expected = @(
        'Install-Nyx.ps1',
        'Uninstall-Nyx.ps1',
        'first-run-defaults.json',
        'Nyx.Desktop.Update.exe',
        'release-notes.md',
        'release.json',
        ('payload/' + $PayloadFile)
    )
    $actual = @(Get-ChildItem -LiteralPath $OuterRoot -File -Recurse -Force | ForEach-Object {
        $_.FullName.Substring($OuterRoot.TrimEnd('\').Length + 1).Replace('\', '/')
    })
    if ($actual.Count -ne $expected.Count) { Throw-SmokeFailure 'OUTER_ENTRY_SET_INVALID' }
    foreach ($path in $actual) {
        if ($expected -cnotcontains $path) { Throw-SmokeFailure 'OUTER_ENTRY_SET_INVALID' }
    }
}

function Expand-ManifestPayload {
    param(
        [Parameter(Mandatory)] [object] $Manifest,
        [Parameter(Mandatory)] [string] $ArchivePath,
        [Parameter(Mandatory)] [string] $Destination
    )
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    [long] $declaredTotal = 0
    foreach ($file in @($Manifest.files)) {
        $relative = [string] $file.path
        $sha256 = [string] $file.sha256
        [long] $size = $file.size
        if (-not (Test-SafeRelativeFile $relative) -or
            $sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $size -lt 0 -or
            $expected.ContainsKey($relative)) {
            Throw-SmokeFailure 'MANIFEST_FILE_SET_INVALID'
        }
        if ($size -gt (6GB - $declaredTotal)) {
            Throw-SmokeFailure 'MANIFEST_SIZE_INVALID'
        }
        $declaredTotal += $size
        $expected.Add($relative, $file)
    }
    if ($expected.Count -le 0 -or $expected.Count -gt 8192) {
        Throw-SmokeFailure 'MANIFEST_FILE_SET_INVALID'
    }
    $stream = [IO.File]::Open($ArchivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            if ($archive.Entries.Count -ne $expected.Count) { Throw-SmokeFailure 'PAYLOAD_ENTRY_SET_INVALID' }
            $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $archive.Entries) {
                $relative = $entry.FullName
                [object] $file = $null
                if (-not (Test-SafeRelativeFile $relative) -or
                    -not $expected.TryGetValue($relative, [ref] $file) -or
                    -not $seen.Add($relative) -or
                    (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or
                    (([IO.FileAttributes] $entry.ExternalAttributes) -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                    [long] $entry.Length -ne [long] $file.size) {
                    Throw-SmokeFailure 'PAYLOAD_ENTRY_INVALID'
                }
                $output = Join-Path $Destination ($relative.Replace('/', '\'))
                $parent = Split-Path -Parent $output
                [void] [IO.Directory]::CreateDirectory($parent)
                $input = $entry.Open()
                $target = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                [long] $written = 0
                $buffer = New-Object byte[] 131072
                try {
                    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $written += $read
                        if ($written -gt [long] $file.size) { Throw-SmokeFailure 'PAYLOAD_ENTRY_INVALID' }
                        $target.Write($buffer, 0, $read)
                    }
                    $target.Flush()
                }
                finally { $target.Dispose(); $input.Dispose() }
                if ($written -ne [long] $file.size -or
                    (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string] $file.sha256) {
                    Throw-SmokeFailure 'PAYLOAD_HASH_MISMATCH'
                }
            }
            if ($seen.Count -ne $expected.Count) { Throw-SmokeFailure 'PAYLOAD_ENTRY_SET_INVALID' }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function New-DataRootState {
    param(
        [Parameter(Mandatory)] [string] $Original,
        [Parameter(Mandatory)] [string] $Backup
    )
    return [pscustomobject]@{
        Original = $Original
        Backup = $Backup
        HadOriginal = $false
        Moved = $false
        Isolated = $false
    }
}

function Initialize-DataRootIsolation {
    param([Parameter(Mandatory)] [object[]] $States)
    Assert-NoNyxProcesses
    foreach ($state in $States) {
        if ([IO.Path]::GetPathRoot($state.Original) -cne [IO.Path]::GetPathRoot($state.Backup)) {
            Throw-SmokeFailure 'BACKUP_VOLUME_INVALID'
        }
        Assert-NoReparseComponents -LiteralPath (Split-Path -Parent $state.Original)
        if (Test-Path -LiteralPath $state.Backup) { Throw-SmokeFailure 'BACKUP_ALREADY_EXISTS' }
        if (Test-Path -LiteralPath $state.Original) {
            $item = Get-Item -LiteralPath $state.Original -Force
            if (-not $item.PSIsContainer -or (Test-ReparsePoint $item)) {
                Throw-SmokeFailure 'DATA_ROOT_INVALID'
            }
            $state.HadOriginal = $true
        }
    }
    foreach ($state in $States) {
        if ($state.HadOriginal) {
            Assert-NoReparseComponents -LiteralPath $state.Original
            Assert-NoReparseComponents -LiteralPath $state.Backup
            Assert-NoNyxProcesses
            [IO.Directory]::Move($state.Original, $state.Backup)
            $state.Moved = $true
        }
        $state.Isolated = $true
    }
}

function Assert-RestorePaths {
    param([Parameter(Mandatory)] [object] $State)
    Assert-NoReparseComponents -LiteralPath $State.Original
    Assert-NoReparseComponents -LiteralPath $State.Backup
    if (-not (Test-Path -LiteralPath $State.Backup -PathType Container)) {
        Throw-SmokeFailure 'BACKUP_MISSING'
    }
    $backup = Get-Item -LiteralPath $State.Backup -Force -ErrorAction Stop
    if (-not $backup.PSIsContainer -or (Test-ReparsePoint $backup)) {
        Throw-SmokeFailure 'BACKUP_INVALID'
    }
    if (Test-Path -LiteralPath $State.Original) {
        $original = Get-Item -LiteralPath $State.Original -Force -ErrorAction Stop
        if (-not $original.PSIsContainer -or (Test-ReparsePoint $original)) {
            Throw-SmokeFailure 'DATA_ROOT_INVALID'
        }
    }
}

function Restore-DataRoots {
    param([Parameter(Mandatory)] [object[]] $States)
    $hasWork = @($States | Where-Object {
        (Test-Path -LiteralPath $_.Backup) -or
            ($_.Isolated -and -not $_.HadOriginal -and (Test-Path -LiteralPath $_.Original))
    }).Count -ne 0
    if (-not $hasWork) { return }
    try { Stop-SmokeProcesses }
    catch { Throw-SmokeFailure 'RESTORE_PROCESS_STOP_FAILED' }
    $failed = $false
    for ($index = $States.Count - 1; $index -ge 0; $index--) {
        $state = $States[$index]
        try {
            $originalExists = Test-Path -LiteralPath $state.Original
            $backupExists = Test-Path -LiteralPath $state.Backup
            if (-not $backupExists) {
                if ($originalExists -and $state.Isolated -and -not $state.HadOriginal) {
                    Assert-NoReparseComponents -LiteralPath $state.Original
                    Assert-NoReparseComponents -LiteralPath $state.Backup
                    $original = Get-Item -LiteralPath $state.Original -Force -ErrorAction Stop
                    if (-not $original.PSIsContainer -or (Test-ReparsePoint $original)) {
                        Throw-SmokeFailure 'DATA_ROOT_INVALID'
                    }
                    Assert-NoNyxProcesses
                    Remove-SafeTree -LiteralPath $state.Original
                }
                continue
            }
            Assert-RestorePaths -State $state
            Assert-NoNyxProcesses
            if ($originalExists) {
                Remove-SafeTree -LiteralPath $state.Original
                Assert-RestorePaths -State $state
                Assert-NoNyxProcesses
            }
            [IO.Directory]::Move($state.Backup, $state.Original)
        }
        catch { $failed = $true }
    }
    if ($failed -or
        @($States | Where-Object { Test-Path -LiteralPath $_.Backup }).Count -ne 0) {
        Throw-SmokeFailure 'RESTORE_PATH_FAILED'
    }
}

function Initialize-SyntheticCachedResourceFixture {
    param(
        [Parameter(Mandatory)] [object[]] $States,
        [Parameter(Mandatory)] [string] $StateWorkerPath,
        [Parameter(Mandatory)] [string] $DotNetPath
    )
    if ($States.Count -ne 2) { Throw-SmokeFailure 'SMOKE_ISOLATION_INVALID' }
    Assert-NoNyxProcesses
    foreach ($state in $States) {
        if (-not $state.Isolated -or
            $state.Moved -ne $state.HadOriginal -or
            (Test-Path -LiteralPath $state.Original) -or
            ($state.Moved -and -not (Test-Path -LiteralPath $state.Backup -PathType Container)) -or
            (-not $state.Moved -and (Test-Path -LiteralPath $state.Backup))) {
            Throw-SmokeFailure 'SMOKE_ISOLATION_INVALID'
        }
    }
    $root = [IO.Path]::GetFullPath($States[0].Original)
    if (-not [string]::Equals(
            $root,
            [IO.Path]::GetFullPath($canonicalDataRoot),
            [StringComparison]::OrdinalIgnoreCase)) {
        Throw-SmokeFailure 'SMOKE_ISOLATION_INVALID'
    }

    [void] [IO.Directory]::CreateDirectory($root)
    $marker = Join-Path $root '.nyx-native-smoke-isolated-v1'
    $markerBytes = [Text.Encoding]::UTF8.GetBytes("NYX_NATIVE_SMOKE_ISOLATED_V1:$runId")
    $markerStream = [IO.File]::Open(
        $marker,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $markerStream.Write($markerBytes, 0, $markerBytes.Length)
        $markerStream.Flush($true)
    }
    finally { $markerStream.Dispose() }
    Assert-NoReparseComponents -LiteralPath $marker

    Assert-NoNyxProcesses
    & $DotNetPath $StateWorkerPath 'seed-native-smoke' $root $marker $runId *> $null
    if ($LASTEXITCODE -ne 0) { Throw-SmokeFailure 'SYNTHETIC_CACHE_FIXTURE_FAILED' }
    $script:publisherAccountFixture = 'synthetic-isolated-no-live-account'
}

function Find-ExactElement {
    param(
        [Parameter(Mandatory)] [object] $Root,
        [Parameter(Mandatory)] [string] $Name
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-AutomationIdElement {
    param(
        [Parameter(Mandatory)] [object] $Root,
        [Parameter(Mandatory)] [string] $AutomationId
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ExactElement {
    param(
        [Parameter(Mandatory)] [object] $Root,
        [Parameter(Mandatory)] [string] $Name,
        [int] $Seconds = 20
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        Assert-UiDeadline
        $element = Find-ExactElement -Root $Root -Name $Name
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Throw-SmokeFailure 'UI_ELEMENT_MISSING'
}

function Wait-ExactElementGone {
    param(
        [Parameter(Mandatory)] [object] $Root,
        [Parameter(Mandatory)] [string] $Name
    )
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Assert-UiDeadline
        if ($null -eq (Find-ExactElement -Root $Root -Name $Name)) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Throw-SmokeFailure 'DIALOG_DID_NOT_CLOSE'
}

function Wait-Window {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Nyx - Pengo')
    do {
        Assert-UiDeadline
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $condition)
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    Throw-SmokeFailure 'WINDOW_NOT_FOUND'
}

function Wait-CachedResourceMetric {
    param(
        [Parameter(Mandatory)] [object] $Root,
        [Parameter(Mandatory)] [Diagnostics.Stopwatch] $Stopwatch
    )
    do {
        Assert-UiDeadline
        $script:uiChecks.cachedResourceLastObservedState = 'missing'
        try {
            $metric = Find-AutomationIdElement `
                -Root $Root `
                -AutomationId 'LaunchResourceMetricsPanel'
            if ($null -ne $metric) {
                if ([string] $metric.Current.Name -ceq 'ORIGINAL RESIN  137/200') {
                    $script:uiChecks.cachedResourceLastObservedState = 'genshin'
                }
                elseif ([string] $metric.Current.Name -ceq 'TRAILBLAZE POWER  211/300') {
                    $script:uiChecks.cachedResourceLastObservedState = 'star-rail'
                }
                else {
                    $script:uiChecks.cachedResourceLastObservedState = 'other'
                }
            }
            if ($script:uiChecks.cachedResourceLastObservedState -ceq 'genshin') {
                $elapsed = $Stopwatch.Elapsed.TotalMilliseconds
                if ($elapsed -le 1000) {
                    return [pscustomobject]@{
                        ElapsedMilliseconds = $elapsed
                    }
                }
            }
        }
        catch { }
        if ($Stopwatch.Elapsed.TotalMilliseconds -ge 1000) { break }
        Start-Sleep -Milliseconds 10
    } while ($true)
    Throw-SmokeFailure 'CACHED_RESOURCE_SWITCH_UI_TIMEOUT'
}

function Wait-GameItem {
    param(
        [Parameter(Mandatory)] [object] $Root,
        [Parameter(Mandatory)] [string] $GameName
    )
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Assert-UiDeadline
        $elements = $Root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($element in $elements) {
            $name = [string] $element.Current.Name
            if ($name.StartsWith($GameName + '. ', [StringComparison]::Ordinal) -and
                $name.EndsWith('. Select game.', [StringComparison]::Ordinal)) {
                [object] $pattern = $null
                if ($element.TryGetCurrentPattern(
                        [System.Windows.Automation.SelectionItemPattern]::Pattern,
                        [ref] $pattern)) {
                    return [pscustomobject]@{ Element = $element; Pattern = $pattern }
                }
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Throw-SmokeFailure 'GAME_ITEM_MISSING'
}

function Invoke-SafeElement {
    param(
        [Parameter(Mandatory)] [object] $Element,
        [Parameter(Mandatory)] [string] $ExpectedName
    )
    Assert-UiDeadline
    $safeNames = @(
        'Settings',
        'Collapse Banners', 'Expand Banners',
        'Collapse Account', 'Expand Account'
    )
    if ($safeNames -cnotcontains $ExpectedName -or $Element.Current.Name -cne $ExpectedName) {
        Throw-SmokeFailure 'UNSAFE_UI_ACTION_BLOCKED'
    }
    [object] $pattern = $null
    if (-not $Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref] $pattern)) {
        Throw-SmokeFailure 'SAFE_CONTROL_NOT_INVOKABLE'
    }
    $pattern.Invoke()
}

function Assert-SideEffectControl {
    param(
        [Parameter(Mandatory)] [object] $Root,
        [Parameter(Mandatory)] [string] $AutomationId
    )
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Assert-UiDeadline
        $element = Find-AutomationIdElement -Root $Root -AutomationId $AutomationId
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Throw-SmokeFailure 'SIDE_EFFECT_CONTROL_MISSING'
}

function Test-IsDescendant {
    param(
        [Parameter(Mandatory)] [object] $Element,
        [Parameter(Mandatory)] [object] $Ancestor
    )
    $ancestorId = $Ancestor.GetRuntimeId() -join ','
    $current = $Element
    while ($null -ne $current) {
        Assert-UiDeadline
        if (($current.GetRuntimeId() -join ',') -ceq $ancestorId) { return $true }
        $current = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($current)
    }
    return $false
}

function Get-ModalContainer {
    param(
        [Parameter(Mandatory)] [object] $Window,
        [Parameter(Mandatory)] [object] $Cancel,
        [Parameter(Mandatory)] [string] $Title
    )
    $windowId = $Window.GetRuntimeId() -join ','
    $candidate = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($Cancel)
    while ($null -ne $candidate -and ($candidate.GetRuntimeId() -join ',') -cne $windowId) {
        Assert-UiDeadline
        if ($null -ne (Find-ExactElement -Root $candidate -Name $Title)) { return $candidate }
        $candidate = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($candidate)
    }
    Throw-SmokeFailure 'MODAL_BOUNDARY_MISSING'
}

function Send-SafeKey {
    param([Parameter(Mandatory)] [ValidateSet('Tab', 'ShiftTab', 'Enter', 'Escape')] [string] $Key)
    Assert-UiDeadline
    $keys = @{ Tab = '{TAB}'; ShiftTab = '+{TAB}'; Enter = '{ENTER}'; Escape = '{ESC}' }
    [Windows.Forms.SendKeys]::SendWait($keys[$Key])
    Start-Sleep -Milliseconds 200
}

function Assert-FocusIs {
    param([Parameter(Mandatory)] [object] $Expected)
    $expectedId = $Expected.GetRuntimeId() -join ','
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    do {
        Assert-UiDeadline
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -ne $focused -and ($focused.GetRuntimeId() -join ',') -ceq $expectedId) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    Throw-SmokeFailure 'SAFE_FOCUS_NOT_SET'
}

function Save-SanitizedScreenshot {
    param(
        [Parameter(Mandatory)] [object] $Window,
        [Parameter(Mandatory)] [string] $FileName,
        [Parameter(Mandatory)] [string] $Surface
    )
    Assert-UiDeadline
    $rect = $Window.Current.BoundingRectangle
    $width = [Math]::Min([int] $rect.Width, 1280)
    $height = [Math]::Min([int] $rect.Height, 720)
    if ($width -lt 320 -or $height -lt 120) { Throw-SmokeFailure 'SCREENSHOT_BOUNDS_INVALID' }
    $bitmap = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $device = $graphics.GetHdc()
            try {
                $handle = [IntPtr] $Window.Current.NativeWindowHandle
                if ($handle -eq [IntPtr]::Zero -or
                    -not [NyxNativeSmokeCapture]::PrintWindow($handle, $device, 2)) {
                    Throw-SmokeFailure 'SCREENSHOT_CAPTURE_FAILED'
                }
            }
            finally { $graphics.ReleaseHdc($device) }
        }
        finally { $graphics.Dispose() }
        [double] $sum = 0
        [double] $sumSquares = 0
        [int] $samples = 0
        for ($y = 0; $y -lt $height; $y += 6) {
            for ($x = 0; $x -lt $width; $x += 6) {
                $pixel = $bitmap.GetPixel($x, $y)
                $brightness = (0.2126 * $pixel.R) + (0.7152 * $pixel.G) + (0.0722 * $pixel.B)
                $sum += $brightness
                $sumSquares += $brightness * $brightness
                $samples++
            }
        }
        $mean = $sum / $samples
        $variance = ($sumSquares / $samples) - ($mean * $mean)
        if ($mean -lt 8 -or $variance -lt 64) { Throw-SmokeFailure 'SCREENSHOT_BLACK_OR_FLAT' }
        $path = Join-Path $EvidenceDirectory $FileName
        $safeHeight = [Math]::Min($height, 120)
        $safeBitmap = $bitmap.Clone(
            [Drawing.Rectangle]::new(0, 0, $width, $safeHeight),
            [Drawing.Imaging.PixelFormat]::Format24bppRgb)
        try { $safeBitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png) }
        finally { $safeBitmap.Dispose() }
        return [ordered]@{
            file = $FileName
            surface = $Surface
            inspectedWidth = $width
            inspectedHeight = $height
            persistedHeight = $safeHeight
            meanBrightness = [Math]::Round($mean, 2)
            variance = [Math]::Round($variance, 2)
        }
    }
    finally { $bitmap.Dispose() }
}

function Test-NativeUi {
    param([Parameter(Mandatory)] [string] $AppRoot)
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NyxNativeSmokeCapture
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);
}
'@

    $entryPoint = Join-Path $AppRoot 'Nyx.Desktop.App.exe'
    if (-not (Test-Path -LiteralPath $entryPoint -PathType Leaf)) { Throw-SmokeFailure 'ENTRY_POINT_MISSING' }
    $script:uiRuntime = [Diagnostics.Stopwatch]::StartNew()
    $script:appProcess = Start-Process -FilePath $entryPoint -WorkingDirectory $AppRoot -PassThru
    $window = Wait-Window
    [void] (Wait-ExactElement -Root $window -Name 'Games')
    [void] (Wait-ExactElement -Root $window -Name 'Settings')
    [void] (Wait-ExactElement -Root $window -Name 'Minimize')
    [void] (Wait-ExactElement -Root $window -Name 'Close')
    $script:uiChecks.shellControlsFound = $true

    $hsrGame = Wait-GameItem -Root $window -GameName 'Honkai: Star Rail'
    $hsrMetric = $null
    $hsrMetricDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Assert-UiDeadline
        $hsrMetric = Find-AutomationIdElement `
            -Root $window `
            -AutomationId 'LaunchResourceMetricsPanel'
        if ($hsrGame.Pattern.Current.IsSelected -and
            $null -ne $hsrMetric -and
            [string] $hsrMetric.Current.Name -ceq 'TRAILBLAZE POWER  211/300') { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $hsrMetricDeadline)
    if (-not $hsrGame.Pattern.Current.IsSelected -or
        $null -eq $hsrMetric -or
        [string] $hsrMetric.Current.Name -cne 'TRAILBLAZE POWER  211/300') {
        Throw-SmokeFailure 'CACHED_RESOURCE_SWITCH_PRECONDITION_FAILED'
    }

    $giGame = Wait-GameItem -Root $window -GameName 'Genshin Impact'
    if ($giGame.Pattern.Current.IsSelected) { Throw-SmokeFailure 'CACHED_RESOURCE_SWITCH_PRECONDITION_FAILED' }
    $cachedResourceTimer = [Diagnostics.Stopwatch]::StartNew()
    $giGame.Pattern.Select()
    $script:uiChecks.cachedResourceSelectionMilliseconds = [Math]::Round(
        $cachedResourceTimer.Elapsed.TotalMilliseconds,
        2)
    $cachedResource = Wait-CachedResourceMetric -Root $window -Stopwatch $cachedResourceTimer
    $cachedResourceTimer.Stop()
    $script:uiChecks.cachedResourceVisibleWithinOneSecondOfGameSwitch = $true
    $script:uiChecks.cachedResourceGameSwitchMilliseconds = [Math]::Round(
        $cachedResource.ElapsedMilliseconds,
        2)

    Assert-UiDeadline
    $primaryWindowId = $window.GetRuntimeId() -join ','
    $secondaryProcess = Start-Process -FilePath $entryPoint -WorkingDirectory $AppRoot -PassThru
    if (-not $secondaryProcess.WaitForExit(10000)) {
        try { $secondaryProcess.Kill() } catch { }
        Throw-SmokeFailure 'SECOND_INSTANCE_NOT_SUPPRESSED'
    }
    if ($secondaryProcess.ExitCode -ne 0) { Throw-SmokeFailure 'SECOND_INSTANCE_EXIT_FAILED' }
    $window = Wait-Window
    if ($script:appProcess.HasExited -or
        $window.Current.ProcessId -ne $script:appProcess.Id -or
        ($window.GetRuntimeId() -join ',') -cne $primaryWindowId) {
        Throw-SmokeFailure 'PRIMARY_INSTANCE_LOST'
    }
    $script:uiChecks.secondInstanceSuppressed = $true

    foreach ($gameName in $gameNames) {
        Assert-UiDeadline
        $game = Wait-GameItem -Root $window -GameName $gameName
        $game.Pattern.Select()
        Start-Sleep -Milliseconds 250
        $launch = Assert-SideEffectControl -Root $window -AutomationId 'LaunchButton'
        [void] (Assert-SideEffectControl -Root $window -AutomationId 'StableOpenUpdaterButton')
        [void] (Assert-SideEffectControl -Root $window -AutomationId 'StableOpenScreenshotFolderButton')
        $script:uiChecks.gamesSelected += $gameName
        $script:uiChecks.sideEffectControlsInspected += 3
        $script:uiChecks.retryStateCheckedForGames++
        $retryState = if ([string] $launch.Current.Name -like 'Try *') { 'retry' } else { 'not-present' }
        $script:uiChecks.retryStates += [ordered]@{ game = $gameName; state = $retryState }
        if ($retryState -ceq 'retry') { $script:retryControlObserved = $true }
        $frameNumber = @($script:uiChecks.gamesSelected).Count
        $frameName = if ($frameNumber -eq $gameNames.Count) {
            'shell-safe.png'
        }
        else {
            "shell-game-$frameNumber-safe.png"
        }
        $script:screenshotEvidence += Save-SanitizedScreenshot `
            -Window $window `
            -FileName $frameName `
            -Surface $gameName
    }

    $banner = Wait-ExactElement -Root $window -Name 'Collapse Banners'
    Invoke-SafeElement -Element $banner -ExpectedName 'Collapse Banners'
    $banner = Wait-ExactElement -Root $window -Name 'Expand Banners'
    Invoke-SafeElement -Element $banner -ExpectedName 'Expand Banners'
    [void] (Wait-ExactElement -Root $window -Name 'Collapse Banners')
    $script:uiChecks.bannersCollapsedAndExpanded = $true

    $account = Find-ExactElement -Root $window -Name 'Collapse Account'
    if ($null -ne $account) {
        $script:uiChecks.accountControlObserved = $true
        Invoke-SafeElement -Element $account -ExpectedName 'Collapse Account'
        $account = Wait-ExactElement -Root $window -Name 'Expand Account'
        Invoke-SafeElement -Element $account -ExpectedName 'Expand Account'
        [void] (Wait-ExactElement -Root $window -Name 'Collapse Account')
        $script:uiChecks.accountCollapsedAndExpanded = $true
    }

    $settings = Wait-ExactElement -Root $window -Name 'Settings'
    [void] [NyxNativeSmokeCapture]::SetForegroundWindow([IntPtr] $window.Current.NativeWindowHandle)
    $settings.SetFocus()
    Assert-FocusIs -Expected $settings
    Send-SafeKey -Key Tab
    if (-not (Test-IsDescendant -Element ([System.Windows.Automation.AutomationElement]::FocusedElement) -Ancestor $window)) {
        Throw-SmokeFailure 'MAIN_FOCUS_ESCAPED'
    }
    $script:uiChecks.mainTabAndShiftTabContained = $true
    Send-SafeKey -Key ShiftTab
    if (-not (Test-IsDescendant -Element ([System.Windows.Automation.AutomationElement]::FocusedElement) -Ancestor $window)) {
        Throw-SmokeFailure 'MAIN_FOCUS_ESCAPED'
    }
    Invoke-SafeElement -Element $settings -ExpectedName 'Settings'
    $title = 'Settings - Arknights: Endfield'
    [void] (Wait-ExactElement -Root $window -Name $title)
    $cancel = Wait-ExactElement -Root $window -Name 'Cancel'
    $modal = Get-ModalContainer -Window $window -Cancel $cancel -Title $title
    [void] [NyxNativeSmokeCapture]::SetForegroundWindow([IntPtr] $window.Current.NativeWindowHandle)
    $cancel.SetFocus()
    Assert-FocusIs -Expected $cancel
    Send-SafeKey -Key ShiftTab
    if (-not (Test-IsDescendant -Element ([System.Windows.Automation.AutomationElement]::FocusedElement) -Ancestor $modal)) {
        Throw-SmokeFailure 'MODAL_FOCUS_ESCAPED'
    }
    $script:uiChecks.modalTabAndShiftTabContained = $true
    Send-SafeKey -Key Tab
    if (-not (Test-IsDescendant -Element ([System.Windows.Automation.AutomationElement]::FocusedElement) -Ancestor $modal)) {
        Throw-SmokeFailure 'MODAL_FOCUS_ESCAPED'
    }
    $script:screenshotEvidence += Save-SanitizedScreenshot `
        -Window $window `
        -FileName 'settings-safe.png' `
        -Surface $title
    [void] [NyxNativeSmokeCapture]::SetForegroundWindow([IntPtr] $window.Current.NativeWindowHandle)
    $cancel.SetFocus()
    Assert-FocusIs -Expected $cancel
    Send-SafeKey -Key Enter
    Wait-ExactElementGone -Root $window -Name $title
    $script:uiChecks.settingsCanceledWithEnter = $true

    $settings = Wait-ExactElement -Root $window -Name 'Settings'
    Invoke-SafeElement -Element $settings -ExpectedName 'Settings'
    [void] (Wait-ExactElement -Root $window -Name $title)
    $cancel = Wait-ExactElement -Root $window -Name 'Cancel'
    [void] [NyxNativeSmokeCapture]::SetForegroundWindow([IntPtr] $window.Current.NativeWindowHandle)
    $cancel.SetFocus()
    Assert-FocusIs -Expected $cancel
    Send-SafeKey -Key Escape
    Wait-ExactElementGone -Root $window -Name $title
    $script:uiChecks.settingsCanceledWithEscape = $true
    Assert-UiDeadline
    $script:uiRuntime.Stop()
}

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$pengoParent = Join-Path $localAppData 'Pengo'
$canonicalDataRoot = Join-Path $pengoParent 'Nyx'
$desktopRoot = Split-Path -Parent $PSScriptRoot
$expectedStateWorker = Join-Path $desktopRoot 'tests\Nyx.Desktop.StateWorker\bin\Release\net10.0\Nyx.Desktop.StateWorker.dll'
$dataStates = @(
    (New-DataRootState -Original $canonicalDataRoot -Backup (Join-Path $pengoParent ("Nyx.native-smoke-backup-$runId"))),
    (New-DataRootState -Original (Join-Path $localAppData 'Nyx') -Backup (Join-Path $localAppData ("Nyx.native-smoke-backup-$runId")))
)
$pengoParentCreated = $false

try {
    if (-not [Environment]::UserInteractive -or
        -not [Environment]::MachineName.Equals('PENGO', [StringComparison]::OrdinalIgnoreCase)) {
        Throw-SmokeFailure 'INTERACTIVE_PENGO_REQUIRED'
    }
    try { $stateWorkerPath = Assert-ExistingNormalFile -LiteralPath $StateWorker }
    catch { Throw-SmokeFailure 'STATE_WORKER_INVALID' }
    if (-not [string]::Equals(
            $stateWorkerPath,
            [IO.Path]::GetFullPath($expectedStateWorker),
            [StringComparison]::OrdinalIgnoreCase)) {
        Throw-SmokeFailure 'STATE_WORKER_INVALID'
    }
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $dotnet) { Throw-SmokeFailure 'STATE_WORKER_RUNTIME_INVALID' }
    try { $dotnetPath = Assert-ExistingNormalFile -LiteralPath $dotnet.Source }
    catch { Throw-SmokeFailure 'STATE_WORKER_RUNTIME_INVALID' }
    $workerProbe = @(& $dotnetPath $stateWorkerPath 'probe-native-smoke' 2>&1)
    if ($LASTEXITCODE -ne 0 -or
        $workerProbe.Count -ne 1 -or
        ([string] $workerProbe[0]) -cne 'NYX_STATE_WORKER=READY') {
        Throw-SmokeFailure 'STATE_WORKER_PROBE_FAILED'
    }

    $packagePath = Assert-ExistingNormalFile -LiteralPath $PackageZip
    $sidecarPath = Assert-ExistingNormalFile -LiteralPath $Sha256Sidecar
    if ((Get-Item -LiteralPath $sidecarPath).Length -gt 512) { Throw-SmokeFailure 'SIDECAR_INVALID' }
    $sidecarText = [IO.File]::ReadAllText($sidecarPath).Trim()
    $sidecarMatch = [regex]::Match($sidecarText, '^([0-9A-Fa-f]{64})  \*?([^\r\n]+)$')
    if (-not $sidecarMatch.Success -or
        $sidecarMatch.Groups[2].Value -cne [IO.Path]::GetFileName($packagePath) -or
        (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -cne $sidecarMatch.Groups[1].Value.ToUpperInvariant()) {
        Throw-SmokeFailure 'OUTER_HASH_MISMATCH'
    }

    if ([IO.Path]::IsPathRooted($EvidenceDirectory) -eq $false -or $EvidenceDirectory -notmatch '^[A-Za-z]:\\') {
        Throw-SmokeFailure 'EVIDENCE_PATH_INVALID'
    }
    $evidenceFull = [IO.Path]::GetFullPath($EvidenceDirectory)
    $evidenceParent = Split-Path -Parent $evidenceFull
    foreach ($state in $dataStates) {
        foreach ($dataPath in @($state.Original, $state.Backup)) {
            if (Test-PathsOverlap -Left $evidenceFull -Right $dataPath) {
                Throw-SmokeFailure 'EVIDENCE_DATA_PATH_OVERLAP'
            }
        }
    }
    if (-not (Test-Path -LiteralPath $evidenceParent -PathType Container) -or
        (Test-Path -LiteralPath $evidenceFull)) {
        Throw-SmokeFailure 'EVIDENCE_PATH_INVALID'
    }
    Assert-NoReparseComponents -LiteralPath $evidenceParent
    [void] [IO.Directory]::CreateDirectory($evidenceFull)
    $EvidenceDirectory = $evidenceFull
    $evidenceCreated = $true

    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("NyxNativeSmoke-$runId")
    Assert-NoReparseComponents -LiteralPath ([IO.Path]::GetTempPath())
    if (Test-Path -LiteralPath $temporaryRoot) { Throw-SmokeFailure 'TEMP_PATH_INVALID' }
    [void] [IO.Directory]::CreateDirectory($temporaryRoot)
    $outerRoot = Join-Path $temporaryRoot 'outer'
    $appRoot = Join-Path $temporaryRoot 'app'
    [void] [IO.Directory]::CreateDirectory($outerRoot)
    [void] [IO.Directory]::CreateDirectory($appRoot)
    Expand-SafeOuterPackage -ArchivePath $packagePath -Destination $outerRoot

    $manifestPath = Join-Path $outerRoot 'release.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        (Get-Item -LiteralPath $manifestPath).Length -gt 4MB) {
        Throw-SmokeFailure 'MANIFEST_INVALID'
    }
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    $payloadFile = [string] $manifest.packageFile
    if (-not (Test-SafeRelativeFile $payloadFile) -or $payloadFile.Contains('/')) {
        Throw-SmokeFailure 'MANIFEST_INVALID'
    }
    Assert-OuterFileSet -OuterRoot $outerRoot -PayloadFile $payloadFile
    $updater = Join-Path $outerRoot 'Nyx.Desktop.Update.exe'
    $payload = Join-Path (Join-Path $outerRoot 'payload') $payloadFile

    Assert-NoNyxProcesses
    if (-not (Test-Path -LiteralPath $pengoParent)) {
        [void] [IO.Directory]::CreateDirectory($pengoParent)
        $pengoParentCreated = $true
    }
    Initialize-DataRootIsolation -States $dataStates

    $updaterOutput = @(& $updater verify --manifest $manifestPath --package $payload 2>&1)
    if ($LASTEXITCODE -ne 0) { Throw-SmokeFailure 'UPDATER_VERIFY_FAILED' }
    Expand-ManifestPayload -Manifest $manifest -ArchivePath $payload -Destination $appRoot
    Initialize-SyntheticCachedResourceFixture `
        -States $dataStates `
        -StateWorkerPath $stateWorkerPath `
        -DotNetPath $dotnetPath
    Test-NativeUi -AppRoot $appRoot
}
catch {
    $failureCode = Get-FailureCode -ErrorRecord $_
}
finally {
    try { Restore-DataRoots -States $dataStates }
    catch {
        $restoreFailed = $true
        $restoreFailureCode = Get-FailureCode -ErrorRecord $_
    }
    if (-not $restoreFailed -and
        $pengoParentCreated -and
        (Test-Path -LiteralPath $pengoParent -PathType Container)) {
        try {
            if (@(Get-ChildItem -LiteralPath $pengoParent -Force).Count -eq 0) {
                [IO.Directory]::Delete($pengoParent, $false)
            }
        }
        catch {
            $pengoParentCleanupFailureCode = 'PENGO_PARENT_CLEANUP_FAILED'
            if ($null -eq $failureCode) { $failureCode = $pengoParentCleanupFailureCode }
        }
    }
    if ($null -ne $temporaryRoot) {
        try { Remove-SafeTree -LiteralPath $temporaryRoot }
        catch { if ($null -eq $failureCode) { $failureCode = 'TEMP_CLEANUP_FAILED' } }
    }
    if ($evidenceCreated) {
        try {
            $result = [ordered]@{
                schemaVersion = 1
                status = if ($null -eq $failureCode -and -not $restoreFailed) { 'passed' } else { 'failed' }
                failureCode = if ($restoreFailed) {
                    if ($null -ne $restoreFailureCode) { $restoreFailureCode } else { 'RESTORE_FAILED' }
                } else { $failureCode }
                gamesSelected = @($uiChecks.gamesSelected).Count
                sideEffectControlsInvoked = $false
                retryControlObserved = $retryControlObserved
                publisherAccountFixture = $publisherAccountFixture
                uiDeadlineSeconds = $uiDeadlineSeconds
                uiChecks = $uiChecks
                screenshots = @($screenshotEvidence)
                dataRootsRestored = -not $restoreFailed
                pengoParentCleanupFailureCode = $pengoParentCleanupFailureCode
                dataRootRecovery = @(
                    $dataStates | Where-Object { Test-Path -LiteralPath $_.Backup } | ForEach-Object {
                        [ordered]@{
                            restoreTo = $_.Original
                            preservedAt = $_.Backup
                            restoreTargetExists = Test-Path -LiteralPath $_.Original
                            preservedBackupExists = Test-Path -LiteralPath $_.Backup
                        }
                    }
                )
            }
            [IO.File]::WriteAllText(
                (Join-Path $EvidenceDirectory 'evidence.json'),
                (($result | ConvertTo-Json -Depth 5) + "`n"),
                [Text.UTF8Encoding]::new($false))
        }
        catch { if ($null -eq $failureCode) { $failureCode = 'EVIDENCE_WRITE_FAILED' } }
    }
}

if ($restoreFailed) {
    $reportedRestoreFailure = if ($null -ne $restoreFailureCode) {
        $restoreFailureCode
    } else {
        'RESTORE_FAILED'
    }
    [Console]::Error.WriteLine("NYX_NATIVE_SMOKE=FAILED CODE=$reportedRestoreFailure")
    exit 2
}
if ($null -ne $failureCode) {
    [Console]::Error.WriteLine("NYX_NATIVE_SMOKE=FAILED CODE=$failureCode")
    exit 1
}
Write-Output 'NYX_NATIVE_SMOKE=PASSED'
exit 0
