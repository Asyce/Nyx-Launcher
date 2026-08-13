using System.ComponentModel;
using System.Reflection;
using System.Runtime.Versioning;
using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class WuWaOfficialMaintenanceExecutorTests
{
    [Theory]
    [InlineData("3.5.0", "3.5.0", PublisherGameInspectionReason.None)]
    [InlineData("3.5.0", "3.5.1", PublisherGameInspectionReason.VersionConflict)]
    public void Ready_and_version_conflict_full_proofs_authorize_only_visible_maintenance(
        string configVersion,
        string resourceVersion,
        PublisherGameInspectionReason expectedReason)
    {
        using var fixture = FakePublisherInstall.CreateWuWa(configVersion, resourceVersion);
        var request = Request(fixture);
        var starter = new FakeStarter();
        var executor = Executor(fixture, new FakeProcessInspector(), starter);

        var result = executor.Open(request);

        Assert.Equal(WuWaOfficialMaintenanceStatus.Opened, result.Status);
        Assert.Equal(expectedReason, result.InspectionReason);
        Assert.Equal(request.Target.CanonicalRoot, result.Request!.Target.CanonicalRoot);
        Assert.Empty(Assert.Single(starter.Requests).Arguments);
        Assert.False(result.Request.AllowsDirectUpdate);
        Assert.False(result.Request.AllowsDirectGameLaunch);
    }

    [Fact]
    public void Missing_fresh_root_has_no_execution_capability()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var request = Request(fixture);
        Directory.Delete(fixture.Root, recursive: true);
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(request);

        Assert.Equal(WuWaOfficialMaintenanceStatus.NotFound, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Endfield_handoff_is_unsupported_before_WuWa_validation_or_start()
    {
        using var wuwa = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var endfield = FakePublisherInstall.CreateEndfield();
        var endfieldTarget = Assert.IsType<ValidatedOfficialMaintenanceTarget>(
            endfield.CreateEndfieldAdapter().Inspect(endfield.Root).MaintenanceTarget);
        var starter = new FakeStarter();

        var result = Executor(wuwa, new FakeProcessInspector(), starter)
            .Open(OfficialMaintenanceHandoffFactory.Create(endfieldTarget));

        Assert.Equal(WuWaOfficialMaintenanceStatus.Unsupported, result.Status);
        Assert.Empty(wuwa.Metadata.ReadPaths);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Endfield_handoff_does_not_enter_or_wait_on_the_Kuro_family()
    {
        using var wuwa = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var endfield = FakePublisherInstall.CreateEndfield();
        var endfieldTarget = Assert.IsType<ValidatedOfficialMaintenanceTarget>(
            endfield.CreateEndfieldAdapter().Inspect(endfield.Root).MaintenanceTarget);
        var admission = new WuWaOfficialLauncherAdmission();
        using var held = admission.TryEnter();
        var executor = new WuWaOfficialMaintenanceExecutor(
            wuwa.CreateWuWaAdapter(),
            new FakeProcessInspector(),
            new FakeStarter(),
            admission);

        var result = executor.Open(OfficialMaintenanceHandoffFactory.Create(endfieldTarget));

        Assert.NotNull(held);
        Assert.Equal(WuWaOfficialMaintenanceStatus.Unsupported, result.Status);
    }

    [Fact]
    public void First_and_fresh_launcher_version_mismatch_never_starts()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var first = Request(fixture);
        var mismatchedTarget = new ValidatedOfficialMaintenanceTarget(
            "wuwa",
            first.Target.CanonicalRoot,
            first.Target.LauncherPath,
            "9.9.9.9");
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter)
            .Open(OfficialMaintenanceHandoffFactory.Create(mismatchedTarget));

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Fresh_launcher_path_drift_never_starts()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var request = Request(fixture);
        File.Move(fixture.PathOf("launcher.exe"), fixture.PathOf("launcher-moved.exe"));
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(request);

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Equal(PublisherGameInspectionReason.RootLauncherMissing, result.InspectionReason);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void First_and_fresh_launcher_path_mismatch_never_starts()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var first = Request(fixture);
        var mismatchedTarget = new ValidatedOfficialMaintenanceTarget(
            "wuwa",
            first.Target.CanonicalRoot,
            fixture.PathOf(@"2.6.3.0\launcher.exe"),
            first.Target.LauncherVersion);
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter)
            .Open(OfficialMaintenanceHandoffFactory.Create(mismatchedTarget));

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Non_factory_maintenance_instructions_never_start()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var target = Request(fixture).Target;
        var altered = new OfficialMaintenanceHandoffRequest(target, "Invented operation");
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(altered);

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Theory]
    [InlineData(@"Wuthering Waves Game\Wuthering Waves.exe")]
    [InlineData(@"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe")]
    [InlineData(@"Wuthering Waves Game\launcherDownloadConfig.json")]
    [InlineData(@"Wuthering Waves Game\LocalGameResources.json")]
    public void No_start_without_a_fresh_complete_install_proof(string requiredEvidence)
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var request = Request(fixture);
        fixture.Delete(requiredEvidence);
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(request);

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Ambiguous_roots_cannot_mint_an_executor_request()
    {
        using var first = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var second = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        first.Metadata.Import(second.Metadata);

        var result = first.CreateWuWaAdapter().InspectCandidates([first.Root, second.Root]);

        Assert.Equal(PublisherGameInspectionReason.AmbiguousCandidates, result.Reason);
        Assert.Null(result.MaintenanceTarget);
        Assert.Throws<ArgumentNullException>(
            () => OfficialMaintenanceHandoffFactory.Create(result.MaintenanceTarget!));
    }

    [Fact]
    public void Evidence_drift_after_process_observation_fails_final_admission()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var request = Request(fixture);
        var config = fixture.PathOf(@"Wuthering Waves Game\launcherDownloadConfig.json");
        var process = new FakeProcessInspector(
            RunningProcessStatus.NotRunning,
            () => File.WriteAllText(
                config,
                "{\"version\":\"3.5.1\",\"isPreDownload\":false,\"appId\":\"50004\"}"));
        var starter = new FakeStarter();

        var result = Executor(fixture, process, starter).Open(request);

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Executable_metadata_drift_after_process_observation_fails_final_admission()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var request = Request(fixture);
        var launcher = fixture.PathOf("launcher.exe");
        var changed = fixture.Metadata.Get(launcher) with { ProductName = "Changed" };
        var process = new FakeProcessInspector(
            RunningProcessStatus.NotRunning,
            () => fixture.Metadata.Set(launcher, changed));
        var starter = new FakeStarter();

        var result = Executor(fixture, process, starter).Open(request);

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Theory]
    [InlineData(RunningProcessStatus.Running, WuWaOfficialMaintenanceStatus.Running)]
    [InlineData(RunningProcessStatus.Uncertain, WuWaOfficialMaintenanceStatus.NeedsReview)]
    public void Exact_running_or_inaccessible_same_name_launcher_never_starts(
        RunningProcessStatus processStatus,
        WuWaOfficialMaintenanceStatus expected)
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var starter = new FakeStarter();
        var process = new FakeProcessInspector(processStatus);

        var result = Executor(fixture, process, starter).Open(Request(fixture));

        Assert.Equal(expected, result.Status);
        Assert.Equal(
            ("launcher", fixture.PathOf("launcher.exe")),
            Assert.Single(process.Checks));
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Different_path_same_name_launcher_is_uncertain_and_never_starts()
    {
        var status = Nyx.Desktop.Infrastructure.Launching.WindowsRunningProcessInspector
            .EvaluateSameNamePaths(
                [@"C:\Different\launcher.exe"],
                @"C:\Expected\launcher.exe",
                differentPathIsUncertain: true);

        Assert.Equal(RunningProcessStatus.Uncertain, status);
    }

    [Fact]
    public void Protected_launcher_bootstrap_and_runtime_bindings_survive_through_start_admission()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var protectedPaths = new[]
        {
            fixture.PathOf("launcher.exe"),
            fixture.PathOf(@"Wuthering Waves Game\Wuthering Waves.exe"),
            fixture.PathOf(@"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe"),
        };
        var starter = new FakeStarter(onStart: () =>
        {
            foreach (var path in protectedPaths)
            {
                Assert.Throws<IOException>(() =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                });
            }
        });

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(Request(fixture));

        Assert.Equal(WuWaOfficialMaintenanceStatus.Opened, result.Status);
        foreach (var path in protectedPaths)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }
    }

    [Fact]
    public async Task Separate_executor_instances_share_one_injected_family_admission()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var starter = new FakeStarter(entered: entered, release: release);
        var admission = new WuWaOfficialLauncherAdmission();
        var firstExecutor = new WuWaOfficialMaintenanceExecutor(
            fixture.CreateWuWaAdapter(),
            new FakeProcessInspector(),
            starter,
            admission);
        var secondExecutor = new WuWaOfficialMaintenanceExecutor(
            fixture.CreateWuWaAdapter(),
            new FakeProcessInspector(),
            starter,
            admission);
        var request = Request(fixture);

        var first = Task.Run(() => firstExecutor.Open(request));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var second = secondExecutor.Open(request);

        Assert.Equal(WuWaOfficialMaintenanceStatus.Busy, second.Status);
        release.Set();
        Assert.Equal(
            WuWaOfficialMaintenanceStatus.Opened,
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Separate_executor_observer_waits_for_shared_family_then_never_dispatches()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var starter = new FakeStarter(entered: entered, release: release);
        var admission = new WuWaOfficialLauncherAdmission();
        var firstExecutor = new WuWaOfficialMaintenanceExecutor(
            fixture.CreateWuWaAdapter(),
            new FakeProcessInspector(),
            starter,
            admission);
        var secondExecutor = new WuWaOfficialMaintenanceExecutor(
            fixture.CreateWuWaAdapter(),
            new FakeProcessInspector(),
            starter,
            admission);
        var request = Request(fixture);

        var first = Task.Run(() => firstExecutor.Open(request));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var observer = secondExecutor.OpenOrObserveCurrentAsync(request);
        Assert.False(observer.IsCompleted);
        release.Set();

        Assert.Equal(
            WuWaOfficialMaintenanceStatus.Opened,
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(
            WuWaOfficialMaintenanceStatus.Ready,
            (await observer.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Public_production_executors_share_one_Kuro_family_admission()
    {
        var field = typeof(WuWaOfficialMaintenanceExecutor).GetField(
            "familyAdmission",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var first = new WuWaOfficialMaintenanceExecutor();
        var second = new WuWaOfficialMaintenanceExecutor();

        Assert.NotNull(field);
        Assert.Same(field.GetValue(first), field.GetValue(second));
    }

    [Fact]
    public async Task Canceled_waiter_never_starts_and_does_not_leak_family_admission()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var starter = new FakeStarter(entered: entered, release: release);
        var executor = Executor(fixture, new FakeProcessInspector(), starter);
        var request = Request(fixture);
        var first = Task.Run(() => executor.Open(request));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var waiter = executor.OpenOrObserveCurrentAsync(request, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
        release.Set();

        Assert.Equal(
            WuWaOfficialMaintenanceStatus.Opened,
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(WuWaOfficialMaintenanceStatus.Ready, executor.Check(request).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public void Kuro_family_admission_is_independent_from_HoYoPlay_family()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var hoyoAdmission = new OfficialLauncherFamilyAdmission();
        using var heldHoyo = hoyoAdmission.TryEnter();
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(Request(fixture));

        Assert.NotNull(heldHoyo);
        Assert.Equal(WuWaOfficialMaintenanceStatus.Opened, result.Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public void Bounded_start_failure_has_no_fallback()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var starter = new FakeStarter(new Win32Exception(740));

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(Request(fixture));

        Assert.Equal(WuWaOfficialMaintenanceStatus.Failed, result.Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public void Inaccessible_process_observation_fails_closed_without_start()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var starter = new FakeStarter();
        var process = new ThrowingProcessInspector(new UnauthorizedAccessException());

        var result = Executor(fixture, process, starter).Open(Request(fixture));

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Kuro_family_lease_releases_exactly_once()
    {
        var admission = new WuWaOfficialLauncherAdmission();
        var first = admission.TryEnter();

        Assert.NotNull(first);
        Assert.Null(admission.TryEnter());
        first.Dispose();
        first.Dispose();

        using var final = admission.TryEnter();
        Assert.NotNull(final);
    }

    [Fact]
    public void Windows_start_specification_is_exact_visible_zero_argument_and_non_shell()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");

        var startInfo = WindowsWuWaOfficialMaintenanceProcessStarter.CreateStartInfo(Request(fixture));

        Assert.Equal(fixture.PathOf("launcher.exe"), startInfo.FileName);
        Assert.Equal(fixture.Root, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.False(startInfo.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Normal, startInfo.WindowStyle);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Empty(startInfo.Verb);
    }

    [Fact]
    public void Public_surface_has_no_generic_path_argument_update_shell_or_elevation_capability()
    {
        var constructors = typeof(WuWaOfficialMaintenanceExecutor).GetConstructors();
        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());

        var methods = typeof(WuWaOfficialMaintenanceExecutor).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(methods);
        Assert.All(methods, method => Assert.All(method.GetParameters(), parameter =>
            Assert.Contains(
                parameter.ParameterType,
                new[] { typeof(OfficialMaintenanceHandoffRequest), typeof(CancellationToken) })));
        Assert.DoesNotContain(methods, method =>
            method.Name.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("LaunchGame", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Elevat", StringComparison.OrdinalIgnoreCase));
        Assert.False(typeof(IWuWaOfficialMaintenanceProcessStarter).IsPublic);
    }

    private static OfficialMaintenanceHandoffRequest Request(FakePublisherInstall fixture)
    {
        var target = Assert.IsType<ValidatedOfficialMaintenanceTarget>(
            fixture.CreateWuWaAdapter().Inspect(fixture.Root).MaintenanceTarget);
        return OfficialMaintenanceHandoffFactory.Create(target);
    }

    private static WuWaOfficialMaintenanceExecutor Executor(
        FakePublisherInstall fixture,
        IStrictRunningProcessInspector processInspector,
        IWuWaOfficialMaintenanceProcessStarter starter) =>
        new(fixture.CreateWuWaAdapter(), processInspector, starter);

    private sealed class FakeProcessInspector(
        RunningProcessStatus status = RunningProcessStatus.NotRunning,
        Action? onCheck = null) : IStrictRunningProcessInspector
    {
        public List<(string Name, string Path)> Checks { get; } = [];

        public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath)
        {
            Checks.Add((processName, expectedExecutablePath));
            onCheck?.Invoke();
            return status;
        }
    }

    private sealed class FakeStarter(
        Exception? failure = null,
        ManualResetEventSlim? entered = null,
        ManualResetEventSlim? release = null,
        Action? onStart = null) : IWuWaOfficialMaintenanceProcessStarter
    {
        public List<OfficialMaintenanceHandoffRequest> Requests { get; } = [];

        public void Start(OfficialMaintenanceHandoffRequest request)
        {
            Requests.Add(request);
            onStart?.Invoke();
            entered?.Set();
            release?.Wait(TimeSpan.FromSeconds(5));
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    private sealed class ThrowingProcessInspector(Exception failure)
        : IStrictRunningProcessInspector
    {
        public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath) =>
            throw failure;
    }
}
