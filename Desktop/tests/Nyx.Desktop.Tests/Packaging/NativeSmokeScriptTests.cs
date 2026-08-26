using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Nyx.Desktop.Tests.Packaging;

public sealed class NativeSmokeScriptTests
{
    private static readonly string Script = Path.Combine(
        FindDesktopRoot(), "scripts", "test-native-smoke.ps1");
    private static readonly string StateWorkerSource = Path.Combine(
        FindDesktopRoot(), "tests", "Nyx.Desktop.StateWorker", "Program.cs");

    [Fact]
    public void Native_smoke_script_keeps_data_and_side_effect_boundaries_closed()
    {
        var source = File.ReadAllText(Script);

        Assert.Contains("Assert-NoNyxProcesses", source, StringComparison.Ordinal);
        Assert.Contains("Initialize-SyntheticCachedResourceFixture `", source, StringComparison.Ordinal);
        Assert.Contains(".nyx-native-smoke-isolated-v1", source, StringComparison.Ordinal);
        Assert.Contains("SYNTHETIC_CACHE_FIXTURE_FAILED", source, StringComparison.Ordinal);
        Assert.Contains("CACHED_RESOURCE_SWITCH_UI_TIMEOUT", source, StringComparison.Ordinal);
        Assert.Contains("CACHED_RESOURCE_SWITCH_PRECONDITION_FAILED", source, StringComparison.Ordinal);
        Assert.Contains("$publisherAccountFixture = 'not-seeded'", source, StringComparison.Ordinal);
        Assert.Contains("$script:publisherAccountFixture = 'synthetic-isolated-no-live-account'", source, StringComparison.Ordinal);
        Assert.Contains("publisherAccountFixture = $publisherAccountFixture", source, StringComparison.Ordinal);
        Assert.Contains("LaunchResourceMetricsPanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PublisherResourceMetricGrid", source, StringComparison.Ordinal);
        Assert.Contains("ORIGINAL RESIN  137/200", source, StringComparison.Ordinal);
        Assert.Contains("TRAILBLAZE POWER  211/300", source, StringComparison.Ordinal);
        Assert.Contains("if ($hsrGame.Pattern.Current.IsSelected -and", source, StringComparison.Ordinal);
        Assert.Contains("if ($elapsed -le 1000)", source, StringComparison.Ordinal);
        Assert.Contains("if ($Stopwatch.Elapsed.TotalMilliseconds -ge 1000)", source, StringComparison.Ordinal);
        Assert.Contains("cachedResourceSelectionMilliseconds = $null", source, StringComparison.Ordinal);
        Assert.Contains("cachedResourceLastObservedState = 'not-observed'", source, StringComparison.Ordinal);
        var cachedResourceMetric = ExtractPowerShellFunction(source, "Wait-CachedResourceMetric");
        Assert.Matches(
            new Regex(@"do \{\r?\n\s*Assert-UiDeadline\r?\n\s*\$script:uiChecks\.cachedResourceLastObservedState = 'missing'\r?\n\s*try \{", RegexOptions.CultureInvariant),
            cachedResourceMetric);
        Assert.Matches(
            new Regex(@"if \(\[string\] \$metric\.Current\.Name -ceq 'ORIGINAL RESIN  137/200'\) \{\r?\n\s*\$script:uiChecks\.cachedResourceLastObservedState = 'genshin'\r?\n\s*\}\r?\n\s*elseif \(\[string\] \$metric\.Current\.Name -ceq 'TRAILBLAZE POWER  211/300'\) \{\r?\n\s*\$script:uiChecks\.cachedResourceLastObservedState = 'star-rail'\r?\n\s*\}\r?\n\s*else \{\r?\n\s*\$script:uiChecks\.cachedResourceLastObservedState = 'other'", RegexOptions.CultureInvariant),
            cachedResourceMetric);
        Assert.Equal(
            new[] { "missing", "genshin", "star-rail", "other" },
            Regex.Matches(
                    cachedResourceMetric,
                    @"\$script:uiChecks\.cachedResourceLastObservedState = '(?<state>[^']+)'",
                    RegexOptions.CultureInvariant)
                .Cast<Match>()
                .Select(match => match.Groups["state"].Value));
        Assert.Matches(
            new Regex(@"\$cachedResourceTimer = \[Diagnostics\.Stopwatch\]::StartNew\(\)\r?\n\s*\$giGame\.Pattern\.Select\(\)\r?\n\s*\$script:uiChecks\.cachedResourceSelectionMilliseconds = \[Math\]::Round\(\r?\n\s*\$cachedResourceTimer\.Elapsed\.TotalMilliseconds,\r?\n\s*2\)\r?\n\s*\$cachedResource = Wait-CachedResourceMetric -Root \$window -Stopwatch \$cachedResourceTimer", RegexOptions.CultureInvariant),
            source);
        Assert.Contains("$uiDeadlineSeconds = 180", source, StringComparison.Ordinal);
        Assert.Contains("$script:uiRuntime.Elapsed.TotalSeconds -ge $uiDeadlineSeconds", source, StringComparison.Ordinal);
        Assert.Contains("UI_DEADLINE_EXCEEDED", source, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"\$script:uiRuntime = \[Diagnostics\.Stopwatch\]::StartNew\(\)\r?\n\s*\$script:appProcess = Start-Process", RegexOptions.CultureInvariant),
            source);
        Assert.Contains("BACKUP_ALREADY_EXISTS", source, StringComparison.Ordinal);
        Assert.Contains("BACKUP_VOLUME_INVALID", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($state.Original, $state.Backup)", source, StringComparison.Ordinal);
        var isolationBlock = ExtractPowerShellFunction(source, "Initialize-DataRootIsolation");
        Assert.Matches(
            new Regex(@"if \(\$state\.HadOriginal\) \{\r?\n\s*Assert-NoReparseComponents -LiteralPath \$state\.Original\r?\n\s*Assert-NoReparseComponents -LiteralPath \$state\.Backup\r?\n\s*Assert-NoNyxProcesses\r?\n\s*\[IO\.Directory\]::Move\(\$state\.Original, \$state\.Backup\)", RegexOptions.CultureInvariant),
            isolationBlock);
        Assert.Contains("RESTORE_PATH_FAILED", source, StringComparison.Ordinal);
        var restoreBlock = ExtractPowerShellFunction(source, "Restore-DataRoots");
        Assert.DoesNotContain("$state.Moved", restoreBlock, StringComparison.Ordinal);
        var restoreWork = restoreBlock.IndexOf("$hasWork =", StringComparison.Ordinal);
        var restoreEarlyReturn = restoreBlock.IndexOf("if (-not $hasWork) { return }", StringComparison.Ordinal);
        var restoreStop = restoreBlock.IndexOf("try { Stop-SmokeProcesses }", StringComparison.Ordinal);
        Assert.True(restoreWork >= 0 && restoreEarlyReturn > restoreWork && restoreStop > restoreEarlyReturn);
        Assert.Contains("$state.Isolated -and -not $state.HadOriginal", restoreBlock, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"if \(-not \$backupExists\)[\s\S]*?\$state\.Isolated -and -not \$state\.HadOriginal[\s\S]*?Assert-NoReparseComponents -LiteralPath \$state\.Original[\s\S]*?Assert-NoReparseComponents -LiteralPath \$state\.Backup[\s\S]*?Assert-NoNyxProcesses[\s\S]*?Remove-SafeTree -LiteralPath \$state\.Original", RegexOptions.CultureInvariant),
            restoreBlock);
        Assert.Matches(
            new Regex(@"Assert-RestorePaths -State \$state[\s\S]*?Assert-NoNyxProcesses[\s\S]*?if \(\$originalExists\)[\s\S]*?Remove-SafeTree -LiteralPath \$state\.Original[\s\S]*?Assert-RestorePaths -State \$state[\s\S]*?Assert-NoNyxProcesses[\s\S]*?\[IO\.Directory\]::Move\(\$state\.Backup, \$state\.Original\)", RegexOptions.CultureInvariant),
            restoreBlock);
        Assert.Matches(
            new Regex(@"function Assert-RestorePaths \{[\s\S]*?Assert-NoReparseComponents -LiteralPath \$State\.Original[\s\S]*?Assert-NoReparseComponents -LiteralPath \$State\.Backup[\s\S]*?BACKUP_MISSING[\s\S]*?BACKUP_INVALID", RegexOptions.CultureInvariant),
            source);
        Assert.Contains("dataRootRecovery = @(", source, StringComparison.Ordinal);
        Assert.Contains("Where-Object { Test-Path -LiteralPath $_.Backup }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Where-Object { $_.Moved }", source, StringComparison.Ordinal);
        Assert.Contains("preservedAt = $_.Backup", source, StringComparison.Ordinal);
        Assert.Contains("if (-not $restoreFailed -and", source, StringComparison.Ordinal);
        Assert.Contains("pengoParentCleanupFailureCode = $pengoParentCleanupFailureCode", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"\$restoreFailed = \$true", RegexOptions.CultureInvariant).Cast<Match>());
        Assert.Contains("finally {", source, StringComparison.Ordinal);
        Assert.Contains("Restore-DataRoots -States $dataStates", source, StringComparison.Ordinal);
        Assert.Contains("Remove-SafeTree -LiteralPath $state.Original", source, StringComparison.Ordinal);
        Assert.Contains("Expand-ManifestPayload", source, StringComparison.Ordinal);
        Assert.Contains("$expected.TryGetValue($relative, [ref] $file)", source, StringComparison.Ordinal);
        Assert.Contains("[long] $declaredTotal = 0", source, StringComparison.Ordinal);
        Assert.Contains("$size -gt (6GB - $declaredTotal)", source, StringComparison.Ordinal);
        Assert.Contains("retryStateCheckedForGames++", source, StringComparison.Ordinal);
        Assert.Contains("retryStates += [ordered]@{ game = $gameName; state = $retryState }", source, StringComparison.Ordinal);
        Assert.Contains("SCREENSHOT_BLACK_OR_FLAT", source, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"\$secondaryProcess = Start-Process[\s\S]*?\$secondaryProcess\.WaitForExit\(10000\)[\s\S]*?\$secondaryProcess\.ExitCode -ne 0[\s\S]*?\$script:appProcess\.HasExited[\s\S]*?\$window\.Current\.ProcessId -ne \$script:appProcess\.Id[\s\S]*?\$window\.GetRuntimeId\(\)[\s\S]*?\$script:uiChecks\.secondInstanceSuppressed = \$true", RegexOptions.CultureInvariant),
            source);
        Assert.Contains("$settings = Wait-ExactElement -Root $window -Name 'Settings'", source, StringComparison.Ordinal);
        Assert.Contains("public static extern bool SetForegroundWindow(IntPtr window);", source, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(source, @"\[NyxNativeSmokeCapture\]::SetForegroundWindow", RegexOptions.CultureInvariant).Count);
        Assert.Contains("[NyxNativeSmokeCapture]::PrintWindow", source, StringComparison.Ordinal);
        Assert.Contains("$height = [Math]::Min([int] $rect.Height, 720)", source, StringComparison.Ordinal);
        Assert.Contains("$safeHeight = [Math]::Min($height, 120)", source, StringComparison.Ordinal);
        Assert.Contains("-Surface $gameName", source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, @"Save-SanitizedScreenshot", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("CopyFromScreen", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"\.Invoke\(\)", RegexOptions.CultureInvariant).Cast<Match>());

        Assert.Contains(".PARAMETER StateWorker", source, StringComparison.Ordinal);
        Assert.Contains("dotnet build Desktop/tests/Nyx.Desktop.StateWorker/Nyx.Desktop.StateWorker.csproj --configuration Release", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Nyx.Desktop.StateWorker.csproj --configuration Release --no-restore", source, StringComparison.Ordinal);
        Assert.Contains("[string] $StateWorker", source, StringComparison.Ordinal);
        Assert.Contains("$stateWorkerPath = Assert-ExistingNormalFile -LiteralPath $StateWorker", source, StringComparison.Ordinal);
        Assert.Contains("STATE_WORKER_INVALID", source, StringComparison.Ordinal);
        Assert.Contains("'probe-native-smoke'", source, StringComparison.Ordinal);
        Assert.Contains("NYX_STATE_WORKER=READY", source, StringComparison.Ordinal);
        Assert.Contains("STATE_WORKER_PROBE_FAILED", source, StringComparison.Ordinal);
        Assert.Contains("-StateWorkerPath $stateWorkerPath", source, StringComparison.Ordinal);
        Assert.Contains("function Test-PathsOverlap", source, StringComparison.Ordinal);
        Assert.Contains("$leftFull.StartsWith($rightFull + '\\'", source, StringComparison.Ordinal);
        Assert.Contains("$rightFull.StartsWith($leftFull + '\\'", source, StringComparison.Ordinal);
        Assert.Contains("foreach ($dataPath in @($state.Original, $state.Backup))", source, StringComparison.Ordinal);
        Assert.Contains("EVIDENCE_DATA_PATH_OVERLAP", source, StringComparison.Ordinal);

        var isolation = source.IndexOf(
            "Initialize-DataRootIsolation -States $dataStates",
            StringComparison.Ordinal);
        var fixture = source.IndexOf(
            "Initialize-SyntheticCachedResourceFixture `",
            StringComparison.Ordinal);
        var timedStart = source.IndexOf(
            "$cachedResourceTimer = [Diagnostics.Stopwatch]::StartNew()",
            StringComparison.Ordinal);
        var hsrSelected = source.IndexOf(
            "$hsrGame = Wait-GameItem -Root $window -GameName 'Honkai: Star Rail'",
            StringComparison.Ordinal);
        var hsrMetric = source.IndexOf(
            "TRAILBLAZE POWER  211/300",
            hsrSelected,
            StringComparison.Ordinal);
        var giSelected = source.IndexOf(
            "$giGame = Wait-GameItem -Root $window -GameName 'Genshin Impact'",
            StringComparison.Ordinal);
        var firstScreenshot = source.IndexOf(
            "$script:screenshotEvidence += Save-SanitizedScreenshot",
            StringComparison.Ordinal);
        var workerPreflight = source.IndexOf(
            "$stateWorkerPath = Assert-ExistingNormalFile -LiteralPath $StateWorker",
            StringComparison.Ordinal);
        var workerProbe = source.IndexOf(
            "$workerProbe = @(& $dotnetPath $stateWorkerPath 'probe-native-smoke' 2>&1)",
            StringComparison.Ordinal);
        var evidenceOverlap = source.IndexOf(
            "if (Test-PathsOverlap -Left $evidenceFull -Right $dataPath)",
            StringComparison.Ordinal);
        var evidenceCreation = source.IndexOf(
            "[void] [IO.Directory]::CreateDirectory($evidenceFull)",
            StringComparison.Ordinal);
        var workerExitCheck = source.IndexOf(
            "if ($LASTEXITCODE -ne 0) { Throw-SmokeFailure 'SYNTHETIC_CACHE_FIXTURE_FAILED' }",
            StringComparison.Ordinal);
        var fixtureSeeded = source.IndexOf(
            "$script:publisherAccountFixture = 'synthetic-isolated-no-live-account'",
            StringComparison.Ordinal);
        Assert.True(isolation >= 0 && fixture > isolation);
        Assert.True(hsrSelected >= 0 && hsrMetric > hsrSelected && giSelected > hsrMetric && timedStart > giSelected);
        Assert.True(timedStart >= 0 && firstScreenshot > timedStart);
        Assert.True(workerPreflight >= 0 && workerPreflight < isolation);
        Assert.True(workerProbe > workerPreflight && workerProbe < isolation);
        Assert.True(evidenceOverlap >= 0 && evidenceOverlap < evidenceCreation);
        Assert.True(workerExitCheck >= 0 && fixtureSeeded > workerExitCheck);
        Assert.Single(Regex.Matches(
            source,
            @"\$script:publisherAccountFixture\s*=",
            RegexOptions.CultureInvariant).Cast<Match>());

        var worker = File.ReadAllText(StateWorkerSource);
        Assert.Contains("IsProvenIsolatedSmokeRoot", worker, StringComparison.Ordinal);
        Assert.Contains("NyxUserDataPaths.CanonicalRoot(localAppData)", worker, StringComparison.Ordinal);
        Assert.Contains("new HoyoLabAccountSlotStore(publisherProfilesRoot)", worker, StringComparison.Ordinal);
        Assert.Contains("new PublisherRoleBindingStore(protectedRoot)", worker, StringComparison.Ordinal);
        Assert.Contains("new PublisherResourceSnapshotStore(protectedRoot)", worker, StringComparison.Ordinal);
        Assert.Contains("new PublisherResourceSnapshot(\"gi\", \"Original Resin\", 137, 200, observedAt)", worker, StringComparison.Ordinal);
        Assert.Contains("new PublisherResourceSnapshot(\"hsr\", \"Trailblaze Power\", 211, 300, observedAt)", worker, StringComparison.Ordinal);
        Assert.Contains("new PublisherResourceSnapshot(\"zzz\", \"Battery Charge\", 177, 240, observedAt)", worker, StringComparison.Ordinal);
        Assert.Contains("RefreshContentOnStartup = false", worker, StringComparison.Ordinal);
        Assert.Contains("AutomaticDailyCheckInGames = Array.Empty<string>()", worker, StringComparison.Ordinal);
        Assert.Contains("HoyoLabAccountAccess = true", worker, StringComparison.Ordinal);
        Assert.Contains("SelectedGameId = \"hsr\"", worker, StringComparison.Ordinal);
        Assert.Contains("typeof(NyxUserDataPaths).Assembly.FullName", worker, StringComparison.Ordinal);
        Assert.Contains("typeof(HoyoLabAccountSlotStore).Assembly.FullName", worker, StringComparison.Ordinal);
        Assert.Contains("HasReparsePointInExistingPath", worker, StringComparison.Ordinal);
        Assert.Contains("HasNyxProcess", worker, StringComparison.Ordinal);
        Assert.Contains("File.GetAttributes(current) & FileAttributes.ReparsePoint", worker, StringComparison.Ordinal);
        Assert.True(
            worker.IndexOf("|| HasNyxProcess()", StringComparison.Ordinal)
            < worker.IndexOf("var publisherProfilesRoot = Path.Combine(root, \"PublisherProfiles\")", StringComparison.Ordinal));

        foreach (var automationId in new[]
                 {
                     "LaunchButton",
                     "StableOpenUpdaterButton",
                     "StableOpenScreenshotFolderButton",
                 })
        {
            Assert.Contains(
                $"Assert-SideEffectControl -Root $window -AutomationId '{automationId}'",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"Invoke-SafeElement -Element ${automationId}",
                source,
                StringComparison.OrdinalIgnoreCase);
        }

        var safeBlock = Regex.Match(
            source,
            @"function Invoke-SafeElement \{(?<body>[\s\S]*?)^\}",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(safeBlock.Success);
        Assert.DoesNotContain("Launch", safeBlock.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Official", safeBlock.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Screenshot", safeBlock.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Export", safeBlock.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            new Regex(@"function Assert-SideEffectControl \{[\s\S]*?AddSeconds\(20\)[\s\S]*?Find-AutomationIdElement -Root \$Root -AutomationId \$AutomationId[\s\S]*?Start-Sleep -Milliseconds 100[\s\S]*?SIDE_EFFECT_CONTROL_MISSING[\s\S]*?^\}", RegexOptions.Multiline | RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(@"\[NyxNativeSmokeCapture\]::SetForegroundWindow\([^\r\n]+\)\r?\n\s*\$settings\.SetFocus\(\)\r?\n\s*Assert-FocusIs -Expected \$settings\r?\n\s*Send-SafeKey -Key Tab", RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(@"\[NyxNativeSmokeCapture\]::SetForegroundWindow\([^\r\n]+\)\r?\n\s*\$cancel\.SetFocus\(\)\r?\n\s*Assert-FocusIs -Expected \$cancel\r?\n\s*Send-SafeKey -Key ShiftTab", RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(@"\[NyxNativeSmokeCapture\]::SetForegroundWindow\([^\r\n]+\)\r?\n\s*\$cancel\.SetFocus\(\)\r?\n\s*Assert-FocusIs -Expected \$cancel\r?\n\s*Send-SafeKey -Key Enter", RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(@"\[NyxNativeSmokeCapture\]::SetForegroundWindow\([^\r\n]+\)\r?\n\s*\$cancel\.SetFocus\(\)\r?\n\s*Assert-FocusIs -Expected \$cancel\r?\n\s*Send-SafeKey -Key Escape", RegexOptions.CultureInvariant),
            source);
    }

    [Fact]
    public void Native_smoke_screenshot_capture_retries_are_bounded_and_fresh()
    {
        var capture = ExtractPowerShellFunction(File.ReadAllText(Script), "Save-SanitizedScreenshot");
        var handle = capture.IndexOf(
            "$handle = [IntPtr] $Window.Current.NativeWindowHandle",
            StringComparison.Ordinal);
        var activation = capture.IndexOf(
            "[void] [NyxNativeSmokeCapture]::SetForegroundWindow($handle)",
            handle,
            StringComparison.Ordinal);
        var loop = capture.IndexOf(
            "for ($attempt = 1; $attempt -le 3; $attempt++)",
            StringComparison.Ordinal);
        var deadline = capture.IndexOf("Assert-UiDeadline", loop, StringComparison.Ordinal);
        var bitmap = capture.IndexOf(
            "$bitmap = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format24bppRgb)",
            loop,
            StringComparison.Ordinal);
        var clear = capture.IndexOf("$graphics.Clear([Drawing.Color]::Black)", bitmap, StringComparison.Ordinal);
        var printWindow = capture.IndexOf(
            "[NyxNativeSmokeCapture]::PrintWindow",
            clear,
            StringComparison.Ordinal);

        Assert.True(
            handle >= 0 && activation > handle && loop > activation && deadline > loop &&
            bitmap > loop && clear > bitmap && printWindow > clear);
        Assert.Contains("if ($handle -ne [IntPtr]::Zero)", capture, StringComparison.Ordinal);
        Assert.Single(
            Regex.Matches(
                    capture,
                    @"\[NyxNativeSmokeCapture\]::PrintWindow",
                    RegexOptions.CultureInvariant)
                .Cast<Match>());
        Assert.Contains("$graphics.Clear([Drawing.Color]::Black)", capture, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds 250", capture, StringComparison.Ordinal);
        Assert.Contains("if ($attempt -eq 3) { Throw-SmokeFailure $failureCode }", capture, StringComparison.Ordinal);
        Assert.Contains("if ($mean -lt 8 -or $variance -lt 64)", capture, StringComparison.Ordinal);
        Assert.Contains("captureAttempts = $attempt", capture, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("untouched-00")]
    [InlineData("untouched-real-10")]
    [InlineData("isolated-synthetic-10")]
    [InlineData("interrupted-move-01")]
    [InlineData("normal-isolated-11")]
    public void Native_smoke_restore_state_transition_is_deterministic(string scenario)
    {
        var setup = scenario switch
        {
            "untouched-00" => (Original: false, Backup: false, Isolated: false, HadOriginal: false, Moved: false, StopCount: 0, FinalOriginal: false, Content: (string?)null),
            "untouched-real-10" => (Original: true, Backup: false, Isolated: false, HadOriginal: true, Moved: false, StopCount: 0, FinalOriginal: true, Content: "current"),
            "isolated-synthetic-10" => (Original: true, Backup: false, Isolated: true, HadOriginal: false, Moved: false, StopCount: 1, FinalOriginal: false, Content: (string?)null),
            "interrupted-move-01" => (Original: false, Backup: true, Isolated: false, HadOriginal: true, Moved: false, StopCount: 1, FinalOriginal: true, Content: "backup"),
            "normal-isolated-11" => (Original: true, Backup: true, Isolated: true, HadOriginal: true, Moved: true, StopCount: 1, FinalOriginal: true, Content: "backup"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var testRoot = Path.Combine(Path.GetTempPath(), $"NyxRestoreState-{Guid.NewGuid():N}");
        var original = Path.Combine(testRoot, "original");
        var backup = Path.Combine(testRoot, "backup");
        Directory.CreateDirectory(testRoot);
        if (setup.Original)
        {
            Directory.CreateDirectory(original);
            File.WriteAllText(Path.Combine(original, "state.txt"), "current");
        }
        if (setup.Backup)
        {
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "state.txt"), "backup");
        }

        try
        {
            var source = File.ReadAllText(Script);
            var command = string.Join(
                Environment.NewLine,
                "Set-StrictMode -Version Latest",
                "$ErrorActionPreference = 'Stop'",
                ExtractPowerShellFunction(source, "Throw-SmokeFailure"),
                ExtractPowerShellFunction(source, "Test-ReparsePoint"),
                ExtractPowerShellFunction(source, "Assert-NoReparseComponents"),
                ExtractPowerShellFunction(source, "Remove-SafeTree"),
                ExtractPowerShellFunction(source, "Assert-RestorePaths"),
                ExtractPowerShellFunction(source, "Restore-DataRoots"),
                "$script:stopCount = 0",
                setup.StopCount == 0
                    ? "function Stop-SmokeProcesses { throw 'STOP_CALLED' }"
                    : "function Stop-SmokeProcesses { $script:stopCount++ }",
                "function Assert-NoNyxProcesses { }",
                $"$state = [pscustomobject]@{{ Original = {PowerShellQuote(original)}; Backup = {PowerShellQuote(backup)}; HadOriginal = ${setup.HadOriginal.ToString().ToLowerInvariant()}; Moved = ${setup.Moved.ToString().ToLowerInvariant()}; Isolated = ${setup.Isolated.ToString().ToLowerInvariant()} }}",
                "Restore-DataRoots -States @($state)",
                "Write-Output ('STOP_COUNT=' + $script:stopCount)");
            var result = RunPowerShell(command);
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Equal($"STOP_COUNT={setup.StopCount}", result.Output.Split('\n')[0].TrimEnd('\r'));

            Assert.False(Directory.Exists(backup));
            Assert.Equal(setup.FinalOriginal, Directory.Exists(original));
            if (setup.Content is not null)
            {
                Assert.Equal(setup.Content, File.ReadAllText(Path.Combine(original, "state.txt")));
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Native_smoke_state_worker_probe_has_fixed_success_response()
    {
        var result = await RunStateWorker("probe-native-smoke");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("NYX_STATE_WORKER=READY", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task Native_smoke_seed_worker_rejects_noncanonical_root_without_writing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"NyxStateWorkerReject-{Guid.NewGuid():N}");
        var marker = Path.Combine(root, ".nyx-native-smoke-isolated-v1");
        const string runId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string markerText = $"NYX_NATIVE_SMOKE_ISOLATED_V1:{runId}";
        Directory.CreateDirectory(root);
        File.WriteAllText(marker, markerText);
        try
        {
            var result = await RunStateWorker("seed-native-smoke", root, marker, runId);
            Assert.Equal(65, result.ExitCode);
            Assert.Equal(
                "NYX_STATE_WORKER=REJECTED CODE=SMOKE_ISOLATION_INVALID",
                result.StandardError);
            Assert.Empty(result.StandardOutput);
            Assert.Equal(markerText, File.ReadAllText(marker));
            Assert.Equal(marker, Assert.Single(Directory.GetFileSystemEntries(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ExtractPowerShellFunction(string source, string name)
    {
        var match = Regex.Match(
            source,
            $@"^function {Regex.Escape(name)} \{{[\s\S]*?^\}}",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"PowerShell function {name} was not found.");
        return match.Value;
    }

    private static string PowerShellQuote(string value) => $"'{value.Replace("'", "''")}'";

    private static (int ExitCode, string Output) RunPowerShell(string command)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)));
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("PowerShell did not start.");
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell did not exit.");
        }
        return (
            process.ExitCode,
            ((standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult()).Trim()));
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunStateWorker(
        params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(FindStateWorker());
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The state worker did not start.");
        var standardError = process.StandardError.ReadToEndAsync();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The state worker did not exit.");
        }
        return (
            process.ExitCode,
            (await standardOutput).Trim(),
            (await standardError).Trim());
    }

    private static string FindDesktopRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (current.Name.Equals("Desktop", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(current.FullName, "Nyx.Desktop.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Desktop repository root was not found.");
    }

    private static string FindStateWorker()
    {
        var targetFramework = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        var configuration = targetFramework.Parent?.Name
            ?? throw new DirectoryNotFoundException("The test build configuration was not found.");
        var path = Path.Combine(
            FindDesktopRoot(),
            "tests",
            "Nyx.Desktop.StateWorker",
            "bin",
            configuration,
            "net10.0",
            "Nyx.Desktop.StateWorker.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("The state worker was not built.", path);
    }
}
