using System.ComponentModel;
using System.Reflection;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.PublisherGames;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class EndfieldOfficialMaintenanceExecutorTests
{
    [Fact]
    public void Full_version_unavailable_proof_opens_only_visible_official_maintenance()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var starter = new FakeStarter();
        var process = new FakeProcessInspector();

        var result = Executor(fixture, process, starter).Open(Request(fixture));

        Assert.Equal(EndfieldOfficialMaintenanceStatus.Opened, result.Status);
        Assert.Equal(PublisherGameInspectionReason.VersionUnavailable, result.InspectionReason);
        Assert.Empty(Assert.Single(starter.Requests).Arguments);
        Assert.Equal([("Launcher", fixture.PathOf("Launcher.exe"))], process.Checks);
        Assert.False(result.Request!.AllowsDirectUpdate);
        Assert.False(result.Request.AllowsDirectGameLaunch);
    }

    [Fact]
    public void WuWa_handoff_is_rejected_before_Endfield_validation_or_admission()
    {
        using var endfield = FakePublisherInstall.CreateEndfield();
        using var wuwa = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var starter = new FakeStarter();
        var request = OfficialMaintenanceHandoffFactory.Create(Assert.IsType<ValidatedOfficialMaintenanceTarget>(
            wuwa.CreateWuWaAdapter().Inspect(wuwa.Root).MaintenanceTarget));

        var result = Executor(endfield, new FakeProcessInspector(), starter).Open(request);

        Assert.Equal(EndfieldOfficialMaintenanceStatus.Unsupported, result.Status);
        Assert.Empty(endfield.Metadata.ReadPaths);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Forged_version_path_or_instructions_never_start()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var valid = Request(fixture);
        var forgedVersion = OfficialMaintenanceHandoffFactory.Create(new ValidatedOfficialMaintenanceTarget(
            "ae",
            valid.Target.CanonicalRoot,
            valid.Target.LauncherPath,
            "9.9.9.9"));
        var forgedPath = OfficialMaintenanceHandoffFactory.Create(new ValidatedOfficialMaintenanceTarget(
            "ae",
            valid.Target.CanonicalRoot,
            fixture.PathOf(@"1.5.0\Launcher.exe"),
            valid.Target.LauncherVersion));
        var forgedInstructions = new OfficialMaintenanceHandoffRequest(valid.Target, "Invented operation");

        foreach (var request in new[] { forgedVersion, forgedPath, forgedInstructions })
        {
            var starter = new FakeStarter();
            var result = Executor(fixture, new FakeProcessInspector(), starter).Open(request);
            Assert.Equal(EndfieldOfficialMaintenanceStatus.NeedsReview, result.Status);
            Assert.Empty(starter.Requests);
        }
    }

    [Theory]
    [InlineData(@"games\EndField Game\Endfield.exe")]
    [InlineData(@"games\EndField Game\PlatformProcess.exe")]
    [InlineData(@"1.5.0\Games.exe")]
    public void Fresh_identity_drift_never_starts(string requiredFile)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var request = Request(fixture);
        fixture.Delete(requiredFile);
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(request);

        Assert.Equal(EndfieldOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Post_process_check_metadata_drift_is_blocked_by_protected_stability_proof()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var request = Request(fixture);
        var game = fixture.PathOf(@"games\EndField Game\Endfield.exe");
        var starter = new FakeStarter();
        var process = new FakeProcessInspector(onCheck: () =>
        {
            var current = fixture.Metadata.Get(game);
            fixture.Metadata.Set(game, current with { Publisher = "Changed" });
        });

        var result = Executor(fixture, process, starter).Open(request);

        Assert.Equal(EndfieldOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Theory]
    [InlineData(RunningProcessStatus.Running, EndfieldOfficialMaintenanceStatus.Running)]
    [InlineData(RunningProcessStatus.Uncertain, EndfieldOfficialMaintenanceStatus.NeedsReview)]
    public void Exact_running_or_uncertain_launcher_never_dispatches(
        RunningProcessStatus processStatus,
        EndfieldOfficialMaintenanceStatus expected)
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(processStatus), starter)
            .Open(Request(fixture));

        Assert.Equal(expected, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public async Task Repeated_click_waits_and_observes_without_second_dispatch()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var starter = new FakeStarter(entered: entered, release: release);
        var executor = Executor(fixture, new FakeProcessInspector(), starter);
        var request = Request(fixture);

        var first = executor.OpenOrObserveCurrentAsync(request);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var repeated = executor.OpenOrObserveCurrentAsync(request);
        Assert.False(repeated.IsCompleted);
        release.Set();

        Assert.Equal(EndfieldOfficialMaintenanceStatus.Opened, (await first).Status);
        Assert.Equal(EndfieldOfficialMaintenanceStatus.Ready, (await repeated).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Direct_launch_admission_is_independent_from_held_Gryphlink_admission()
    {
        var admission = new EndfieldOfficialLauncherAdmission();
        using var held = admission.TryEnter();
        var adapter = new PublisherGameSessionAdapter(
            "ae",
            () => @"C:\Games\GRYPHLINK",
            _ => new(PublisherGameLaunchStatus.Ready),
            _ => new(PublisherGameLaunchStatus.Running));

        var launch = await adapter.RequestValidatedLaunchAsync(default);

        Assert.NotNull(held);
        Assert.Equal(GameLaunchDispatchStatus.Accepted, launch.Status);
    }

    [Fact]
    public void Gryphlink_family_is_independent_from_held_WuWa_admission()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var wuwaAdmission = new WuWaOfficialLauncherAdmission();
        using var heldWuWa = wuwaAdmission.TryEnter();
        var starter = new FakeStarter();

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(Request(fixture));

        Assert.NotNull(heldWuWa);
        Assert.Equal(EndfieldOfficialMaintenanceStatus.Opened, result.Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public void Bounded_start_failure_has_no_elevation_or_shell_fallback()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var starter = new FakeStarter(new Win32Exception(740));

        var result = Executor(fixture, new FakeProcessInspector(), starter).Open(Request(fixture));

        Assert.Equal(EndfieldOfficialMaintenanceStatus.Failed, result.Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public void Windows_start_specification_is_exact_visible_zero_argument_and_non_shell()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();

        var startInfo = WindowsEndfieldOfficialMaintenanceProcessStarter.CreateStartInfo(Request(fixture));

        Assert.Equal(fixture.PathOf("Launcher.exe"), startInfo.FileName);
        Assert.Equal(fixture.Root, startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.False(startInfo.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Normal, startInfo.WindowStyle);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Empty(startInfo.Verb);
    }

    [Fact]
    public void Service_uses_only_saved_root_and_fully_checks_before_ready_or_open()
    {
        using var fixture = FakePublisherInstall.CreateEndfield();
        var values = new Dictionary<string, object>();
        var store = new EndfieldInstallRootStore(values);
        var executor = Executor(fixture, new FakeProcessInspector(), new FakeStarter());
        var service = new EndfieldOfficialMaintenanceService(
            store.Load,
            fixture.CreateEndfieldAdapter(),
            executor);

        Assert.Equal(EndfieldOfficialMaintenanceStatus.NotFound, service.Check().Status);
        Assert.True(store.TrySave(fixture.Root));
        Assert.Equal(EndfieldOfficialMaintenanceStatus.Ready, service.Check().Status);
    }

    [Fact]
    public void Public_surfaces_accept_no_path_game_id_arguments_protocol_update_shell_or_elevation()
    {
        var executorConstructor = Assert.Single(typeof(EndfieldOfficialMaintenanceExecutor).GetConstructors());
        Assert.Empty(executorConstructor.GetParameters());
        var executorMethods = typeof(EndfieldOfficialMaintenanceExecutor).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        Assert.All(executorMethods, method => Assert.All(method.GetParameters(), parameter =>
            Assert.Contains(parameter.ParameterType, new[]
            {
                typeof(OfficialMaintenanceHandoffRequest),
                typeof(CancellationToken),
            })));
        var serviceConstructor = Assert.Single(typeof(EndfieldOfficialMaintenanceService).GetConstructors());
        Assert.Equal([typeof(EndfieldInstallRootStore)], serviceConstructor.GetParameters()
            .Select(parameter => parameter.ParameterType).ToArray());
        Assert.All(typeof(EndfieldOfficialMaintenanceService).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly), method =>
            Assert.All(method.GetParameters(), parameter =>
                Assert.Equal(typeof(CancellationToken), parameter.ParameterType)));
        Assert.False(typeof(IEndfieldOfficialMaintenanceProcessStarter).IsPublic);
    }

    private static OfficialMaintenanceHandoffRequest Request(FakePublisherInstall fixture) =>
        OfficialMaintenanceHandoffFactory.Create(Assert.IsType<ValidatedOfficialMaintenanceTarget>(
            fixture.CreateEndfieldAdapter().Inspect(fixture.Root).MaintenanceTarget));

    private static EndfieldOfficialMaintenanceExecutor Executor(
        FakePublisherInstall fixture,
        IStrictRunningProcessInspector process,
        IEndfieldOfficialMaintenanceProcessStarter starter) =>
        new(fixture.CreateEndfieldAdapter(), process, starter);

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
        ManualResetEventSlim? release = null) : IEndfieldOfficialMaintenanceProcessStarter
    {
        public List<OfficialMaintenanceHandoffRequest> Requests { get; } = [];

        public void Start(OfficialMaintenanceHandoffRequest request)
        {
            Requests.Add(request);
            entered?.Set();
            release?.Wait(TimeSpan.FromSeconds(5));
            if (failure is not null)
            {
                throw failure;
            }
        }
    }
}
