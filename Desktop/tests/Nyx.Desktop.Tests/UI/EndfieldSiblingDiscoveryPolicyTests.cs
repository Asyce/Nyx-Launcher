using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherGames;
using Nyx_Desktop_App;

namespace Nyx.Desktop.Tests.UI;

public sealed class EndfieldSiblingDiscoveryPolicyTests
{
    private const string WuWaRoot = @"C:\Library\Wuthering Waves";
    private const string GenshinRoot = @"C:\Library\Genshin Impact\Genshin Impact Game";
    private const string Expected = @"C:\Library\GRYPHLINK";

    [Fact]
    public void Exact_known_suffixes_derive_only_the_Gryphlink_sibling()
    {
        Assert.Equal([Expected], EndfieldSiblingDiscoveryPolicy.DeriveCandidates(WuWaRoot, null));
        Assert.Equal([Expected], EndfieldSiblingDiscoveryPolicy.DeriveCandidates(null, GenshinRoot));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(@"C:\Library\Other", null)]
    [InlineData(null, @"C:\Library\Genshin Impact Game")]
    [InlineData(@"relative\Wuthering Waves", null)]
    [InlineData(@"\\server\Library\Wuthering Waves", null)]
    [InlineData(@"\\?\C:\Library\Wuthering Waves", null)]
    [InlineData(@"C:\Library\Other\..\Wuthering Waves", null)]
    [InlineData(@"C:\Library\Wuthering Waves ", null)]
    public void Missing_wrong_suffix_remote_device_relative_or_noncanonical_roots_derive_nothing(
        string? wuwa,
        string? genshin)
    {
        Assert.Empty(EndfieldSiblingDiscoveryPolicy.DeriveCandidates(wuwa, genshin));
    }

    [Fact]
    public void Same_sibling_from_two_validated_roots_is_deduplicated()
    {
        Assert.Equal(
            [Expected],
            EndfieldSiblingDiscoveryPolicy.DeriveCandidates(WuWaRoot, GenshinRoot));
    }

    [Fact]
    public void Zero_candidates_never_inspect_or_save()
    {
        var calls = new Calls();

        var result = Discover(null, null, calls);

        Assert.Equal(EndfieldSiblingDiscoveryStatus.NoCandidate, result.Status);
        Assert.Empty(calls.Checked);
        Assert.Empty(calls.Saved);
    }

    [Theory]
    [InlineData(PublisherGameLaunchStatus.Ready)]
    [InlineData(PublisherGameLaunchStatus.Running)]
    public void One_full_ready_or_running_candidate_is_rechecked_then_saved(
        PublisherGameLaunchStatus status)
    {
        var calls = new Calls(Result(status), Result(status));

        var result = Discover(WuWaRoot, null, calls);

        Assert.Equal(EndfieldSiblingDiscoveryStatus.Saved, result.Status);
        Assert.Equal(Expected, result.SavedRoot);
        Assert.Equal([Expected, Expected], calls.Checked);
        Assert.Equal([Expected], calls.Saved);
    }

    [Fact]
    public void Deterministic_nonmatch_beside_one_root_does_not_hide_one_unique_full_match()
    {
        var secondLibrary = @"D:\Other\Genshin Impact\Genshin Impact Game";
        var calls = new Calls(
            Result(PublisherGameLaunchStatus.NeedsReview, PublisherGameInspectionReason.DirectoryNotFound),
            Result(PublisherGameLaunchStatus.Ready),
            Result(PublisherGameLaunchStatus.Ready));

        var result = Discover(WuWaRoot, secondLibrary, calls);

        Assert.Equal(EndfieldSiblingDiscoveryStatus.Saved, result.Status);
        Assert.Equal(@"D:\Other\GRYPHLINK", result.SavedRoot);
    }

    [Fact]
    public void Two_full_matches_are_ambiguous_and_never_save()
    {
        var otherGenshin = @"D:\Other\Genshin Impact\Genshin Impact Game";
        var calls = new Calls(
            Result(PublisherGameLaunchStatus.Ready),
            Result(PublisherGameLaunchStatus.Running));

        var result = Discover(WuWaRoot, otherGenshin, calls);

        Assert.Equal(EndfieldSiblingDiscoveryStatus.Ambiguous, result.Status);
        Assert.Empty(calls.Saved);
    }

    [Fact]
    public void Uncertain_process_or_inspection_evidence_leaves_folder_fallback()
    {
        var processUncertain = new Calls(new PublisherGameDirectLaunchResult(
            PublisherGameLaunchStatus.NeedsReview,
            Bootstrap: RunningProcessStatus.Uncertain));
        var inspectionUncertain = new Calls(Result(
            PublisherGameLaunchStatus.NeedsReview,
            PublisherGameInspectionReason.TargetChangedDuringInspection));

        Assert.Equal(
            EndfieldSiblingDiscoveryStatus.Uncertain,
            Discover(WuWaRoot, null, processUncertain).Status);
        Assert.Equal(
            EndfieldSiblingDiscoveryStatus.Uncertain,
            Discover(WuWaRoot, null, inspectionUncertain).Status);
        Assert.Empty(processUncertain.Saved);
        Assert.Empty(inspectionUncertain.Saved);
    }

    [Fact]
    public void Winner_identity_drift_before_save_never_persists()
    {
        var calls = new Calls(
            Result(PublisherGameLaunchStatus.Ready),
            Result(
                PublisherGameLaunchStatus.NeedsReview,
                PublisherGameInspectionReason.TargetChangedDuringInspection));

        var result = Discover(WuWaRoot, null, calls);

        Assert.Equal(EndfieldSiblingDiscoveryStatus.Drifted, result.Status);
        Assert.Empty(calls.Saved);
    }

    [Fact]
    public void Inspection_exception_is_uncertain_and_save_failure_keeps_fallback()
    {
        var throws = new Calls { InspectionFailure = new IOException("blocked") };
        var saveFails = new Calls(
            Result(PublisherGameLaunchStatus.Ready),
            Result(PublisherGameLaunchStatus.Ready))
        {
            SaveResult = false,
        };

        Assert.Equal(
            EndfieldSiblingDiscoveryStatus.Uncertain,
            Discover(WuWaRoot, null, throws).Status);
        Assert.Equal(
            EndfieldSiblingDiscoveryStatus.SaveFailed,
            Discover(WuWaRoot, null, saveFails).Status);
        Assert.Empty(saveFails.Saved);
    }

    [Fact]
    public void Existing_saved_root_short_circuits_all_derivation_inspection_and_save()
    {
        var calls = new Calls();
        var policy = new EndfieldSiblingDiscoveryPolicy();

        var result = policy.DiscoverAndSave(
            @"E:\Saved\GRYPHLINK",
            WuWaRoot,
            GenshinRoot,
            calls.Check,
            calls.Save);

        Assert.Equal(EndfieldSiblingDiscoveryStatus.ExistingRoot, result.Status);
        Assert.Empty(calls.Checked);
        Assert.Empty(calls.Saved);
    }

    [Fact]
    public void Cancellation_before_inspection_never_checks_or_saves()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = new Calls(Result(PublisherGameLaunchStatus.Ready));

        var result = new EndfieldSiblingDiscoveryPolicy().DiscoverAndSave(
            existingEndfieldRoot: null,
            validatedWuWaRoot: WuWaRoot,
            validatedGenshinGameRoot: null,
            checkEndfield: calls.Check,
            save: calls.Save,
            cancellationToken: cancellation.Token);

        Assert.Equal(EndfieldSiblingDiscoveryStatus.Cancelled, result.Status);
        Assert.Empty(calls.Checked);
        Assert.Empty(calls.Saved);
    }

    [Fact]
    public void Cancellation_after_identity_check_never_rechecks_or_saves()
    {
        using var cancellation = new CancellationTokenSource();
        var checkedPaths = new List<string>();

        var result = new EndfieldSiblingDiscoveryPolicy().DiscoverAndSave(
            existingEndfieldRoot: null,
            validatedWuWaRoot: WuWaRoot,
            validatedGenshinGameRoot: null,
            checkEndfield: path =>
            {
                checkedPaths.Add(path);
                cancellation.Cancel();
                return Result(PublisherGameLaunchStatus.Ready);
            },
            save: _ => throw new InvalidOperationException("A cancelled discovery must not save."),
            cancellationToken: cancellation.Token);

        Assert.Equal(EndfieldSiblingDiscoveryStatus.Cancelled, result.Status);
        Assert.Equal([Expected], checkedPaths);
    }

    private static EndfieldSiblingDiscoveryResult Discover(
        string? wuwa,
        string? genshin,
        Calls calls) => new EndfieldSiblingDiscoveryPolicy().DiscoverAndSave(
            existingEndfieldRoot: null,
            validatedWuWaRoot: wuwa,
            validatedGenshinGameRoot: genshin,
            checkEndfield: calls.Check,
            save: calls.Save);

    private static PublisherGameDirectLaunchResult Result(
        PublisherGameLaunchStatus status,
        PublisherGameInspectionReason reason = PublisherGameInspectionReason.None) =>
        new(status, InspectionReason: reason);

    private sealed class Calls(params PublisherGameDirectLaunchResult[] results)
    {
        private int index;

        public Exception? InspectionFailure { get; init; }

        public bool SaveResult { get; init; } = true;

        public List<string> Checked { get; } = [];

        public List<string> Saved { get; } = [];

        public PublisherGameDirectLaunchResult Check(string path)
        {
            Checked.Add(path);
            if (InspectionFailure is not null)
            {
                throw InspectionFailure;
            }

            return results[Math.Min(index++, results.Length - 1)];
        }

        public bool Save(string path)
        {
            if (SaveResult)
            {
                Saved.Add(path);
            }

            return SaveResult;
        }
    }
}

public sealed class EndfieldSiblingDiscoveryStartupTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void Startup_activates_first_then_runs_cancellable_bounded_discovery_in_background()
    {
        var app = File.ReadAllText(Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            "App.xaml.cs"));
        var activation = app.IndexOf("_window.Activate();", StringComparison.Ordinal);
        var startRequest = app.IndexOf("StartEndfieldSiblingDiscovery(wuwaRootLocator);", StringComparison.Ordinal);
        var start = app.IndexOf("private EndfieldSiblingDiscoveryResult TryDiscoverEndfieldSibling", StringComparison.Ordinal);
        var end = app.IndexOf("internal GameSessionCoordinator Sessions", start, StringComparison.Ordinal);
        var discovery = app[start..end];

        Assert.True(activation >= 0 && startRequest > activation);
        Assert.Contains("Task.Run(", app, StringComparison.Ordinal);
        Assert.Contains("_endfieldDiscoveryCancellation", app, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _endfieldDiscoveryCancellation, null)?.Cancel()", app, StringComparison.Ordinal);
        Assert.Contains("_sessionRefresh.RefreshNowAsync(cancellationToken)", app, StringComparison.Ordinal);
        Assert.Contains("EndfieldRootAutoDiscovered?.Invoke(this, EventArgs.Empty)", app, StringComparison.Ordinal);
        Assert.True(
            discovery.IndexOf("EndfieldRootStore.Load()", StringComparison.Ordinal)
            < discovery.IndexOf("wuwaRootLocator.LocateRoot()", StringComparison.Ordinal));
        Assert.Contains("PublisherGameLaunchService.CheckGame(\"wuwa\", wuwaRoot)", discovery, StringComparison.Ordinal);
        Assert.Contains("GenshinDiscovery.Discover().GameRoot", discovery, StringComparison.Ordinal);
        Assert.Contains("PublisherGameLaunchService.CheckGame(\"ae\", candidate)", discovery, StringComparison.Ordinal);
        Assert.Contains("EndfieldRootStore.TrySaveIfEmpty", discovery, StringComparison.Ordinal);
        Assert.Contains("cancellationToken: cancellationToken", discovery, StringComparison.Ordinal);
        Assert.Contains("bounded folder picker remains", discovery, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GetDirectories", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateDirectories", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("Process", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry", discovery, StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_auto_save_invalidates_and_reruns_the_independent_maintenance_check()
    {
        var app = File.ReadAllText(Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            "App.xaml.cs"));
        var page = File.ReadAllText(Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.App",
            "MainPage.xaml.cs"));
        var completion = Slice(
            app,
            "private async Task DiscoverEndfieldSiblingAfterActivationAsync",
            "private EndfieldSiblingDiscoveryResult TryDiscoverEndfieldSibling");
        var handler = Slice(
            page,
            "private void App_EndfieldRootAutoDiscovered",
            "private async void LaunchButton_Click");

        var savedGuard = completion.IndexOf(
            "result.Status is not EndfieldSiblingDiscoveryStatus.Saved",
            StringComparison.Ordinal);
        var notification = completion.IndexOf(
            "EndfieldRootAutoDiscovered?.Invoke(this, EventArgs.Empty)",
            StringComparison.Ordinal);
        Assert.True(savedGuard >= 0 && notification > savedGuard);
        Assert.Contains("cancellationToken.IsCancellationRequested", completion, StringComparison.Ordinal);
        Assert.Contains("app.EndfieldRootAutoDiscovered += App_EndfieldRootAutoDiscovered", page, StringComparison.Ordinal);
        Assert.Contains("app.EndfieldRootAutoDiscovered -= App_EndfieldRootAutoDiscovered", page, StringComparison.Ordinal);
        Assert.Contains("sessionUiLifetime.TryRun(lease", handler, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenanceScanFinished = false", handler, StringComparison.Ordinal);
        Assert.Contains("endfieldMaintenanceStatus = null", handler, StringComparison.Ordinal);
        Assert.Contains("RefreshEndfieldMaintenanceAsync(lease)", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenOrObserveCurrentAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestLaunchAsync", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Process", handler, StringComparison.Ordinal);
    }

    private static string Slice(string source, string startValue, string endValue)
    {
        var start = source.IndexOf(startValue, StringComparison.Ordinal);
        var end = source.IndexOf(endValue, start + startValue.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src", "Nyx.Desktop.App")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
