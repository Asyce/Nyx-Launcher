using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Infrastructure.Hoyo;
using static Nyx.Desktop.Tests.Hoyo.HoyoPlayGlobalValidatorTests;

namespace Nyx.Desktop.Tests.Hoyo;

public sealed class HoyoPlayHandoffExecutorTests
{
    [Theory]
    [InlineData("hsr", "--game=hkrpg_global")]
    [InlineData("zzz", "--game=nap_global")]
    public void Check_exposes_only_the_exact_visible_game_handoff(string gameId, string argument)
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var process = new FakeProcessInspector();
        var starter = new FakeStarter();
        var executor = new HoyoPlayHandoffExecutor(fixture.CreateValidator(), process, starter);

        var result = executor.Check(gameId, fixture.Root);

        Assert.Equal(HoyoPlayOpenStatus.Ready, result.Status);
        Assert.Equal([argument], result.Request!.Arguments);
        Assert.Equal(fixture.Root, result.Request.Installation.CanonicalRoot);
        Assert.Equal(
            ("launcher", Path.Combine(fixture.Root, "launcher.exe")),
            Assert.Single(process.Checks));
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Genshin_handoff_is_visible_and_argument_free()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var starter = new FakeStarter();
        var executor = new HoyoPlayHandoffExecutor(
            fixture.CreateValidator(),
            new FakeProcessInspector(),
            starter);

        var result = executor.Open("gi", fixture.Root);

        Assert.Equal(HoyoPlayOpenStatus.Opened, result.Status);
        Assert.Empty(Assert.Single(starter.Requests).Arguments);
    }

    [Theory]
    [InlineData("hsr", "--game=hkrpg_global")]
    [InlineData("zzz", "--game=nap_global")]
    public void Open_revalidates_twice_and_starts_one_exact_non_shell_request(
        string gameId,
        string argument)
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var reader = fixture.CreateMetadataReader();
        var process = new FakeProcessInspector();
        var starter = new FakeStarter();
        var executor = new HoyoPlayHandoffExecutor(
            new HoyoPlayGlobalValidator(reader, new FakeDriveTypeReader()),
            process,
            starter);

        var result = executor.Open(gameId, fixture.Root);

        Assert.Equal(HoyoPlayOpenStatus.Opened, result.Status);
        Assert.Equal(8, reader.Paths.Count);
        Assert.Equal(2, process.Checks.Count);
        Assert.Equal([argument], Assert.Single(starter.Requests).Arguments);
    }

    [Theory]
    [InlineData(RunningProcessStatus.Running, HoyoPlayOpenStatus.Running)]
    [InlineData(RunningProcessStatus.Uncertain, HoyoPlayOpenStatus.NeedsReview)]
    public void Running_or_ambiguous_launcher_never_starts(
        RunningProcessStatus processStatus,
        HoyoPlayOpenStatus expected)
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var starter = new FakeStarter();
        var executor = new HoyoPlayHandoffExecutor(
            fixture.CreateValidator(),
            new FakeProcessInspector(processStatus),
            starter);

        var result = executor.Open("hsr", fixture.Root);

        Assert.Equal(expected, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Target_metadata_drift_between_checks_never_starts()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var good = fixture.RootMetadata;
        var bad = good with { ProductVersion = "1.8.0.1" };
        var rootPath = Path.Combine(fixture.Root, "launcher.exe");
        var nestedPath = Path.Combine(fixture.Root, fixture.Version, "launcher.exe");
        var reader = new PathMetadataReader(
            new Dictionary<string, IReadOnlyList<Nyx.Desktop.Infrastructure.Genshin.ExecutableMetadata>>(
                StringComparer.OrdinalIgnoreCase)
            {
                [rootPath] = [good, good, bad, bad],
                [nestedPath] = [good, good, good, good],
            });
        var starter = new FakeStarter();
        var executor = new HoyoPlayHandoffExecutor(
            new HoyoPlayGlobalValidator(reader, new FakeDriveTypeReader()),
            new FakeProcessInspector(),
            starter);

        var result = executor.Open("hsr", fixture.Root);

        Assert.Equal(HoyoPlayOpenStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Start_failure_is_bounded_without_shell_or_elevation_fallback()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var starter = new FakeStarter(new System.ComponentModel.Win32Exception(740));
        var executor = new HoyoPlayHandoffExecutor(
            fixture.CreateValidator(),
            new FakeProcessInspector(),
            starter);

        var result = executor.Open("hsr", fixture.Root);

        Assert.Equal(HoyoPlayOpenStatus.Failed, result.Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Shared_family_admission_allows_only_one_concurrent_open()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var starter = new FakeStarter(entered: entered, release: release);
        var executor = new HoyoPlayHandoffExecutor(
            fixture.CreateValidator(),
            new FakeProcessInspector(),
            starter,
            new OfficialLauncherFamilyAdmission());

        var first = Task.Run(() => executor.Open("hsr", fixture.Root));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var second = executor.Open("zzz", fixture.Root);

        Assert.Equal(HoyoPlayOpenStatus.Busy, second.Status);
        release.Set();
        Assert.Equal(HoyoPlayOpenStatus.Opened, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Reloaded_caller_waits_for_failed_open_then_observes_ready_without_dispatching()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var starter = new FakeStarter(
            new System.ComponentModel.Win32Exception(740),
            entered,
            release);
        var executor = new HoyoPlayHandoffExecutor(
            fixture.CreateValidator(),
            new FakeProcessInspector(),
            starter,
            new OfficialLauncherFamilyAdmission());

        var first = Task.Run(() => executor.Open("hsr", fixture.Root));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var reloadedPage = executor.OpenOrObserveCurrentAsync("zzz", fixture.Root);
        Assert.False(reloadedPage.IsCompleted);

        release.Set();

        Assert.Equal(HoyoPlayOpenStatus.Failed, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(
            HoyoPlayOpenStatus.Ready,
            (await reloadedPage.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Reloaded_caller_waits_for_success_then_observes_running_without_dispatching()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var running = 0;
        var starter = new FakeStarter(
            entered: entered,
            release: release,
            onStart: () => Interlocked.Exchange(ref running, 1));
        var executor = new HoyoPlayHandoffExecutor(
            fixture.CreateValidator(),
            new FakeProcessInspector(() => Volatile.Read(ref running) == 1
                ? RunningProcessStatus.Running
                : RunningProcessStatus.NotRunning),
            starter,
            new OfficialLauncherFamilyAdmission());

        var first = Task.Run(() => executor.Open("hsr", fixture.Root));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var reloadedPage = executor.OpenOrObserveCurrentAsync("zzz", fixture.Root);
        Assert.False(reloadedPage.IsCompleted);

        release.Set();

        Assert.Equal(HoyoPlayOpenStatus.Opened, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(
            HoyoPlayOpenStatus.Running,
            (await reloadedPage.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Canceled_reloaded_observer_never_dispatches_or_leaks_family_admission()
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var starter = new FakeStarter(
            new System.ComponentModel.Win32Exception(740),
            entered,
            release);
        var executor = new HoyoPlayHandoffExecutor(
            fixture.CreateValidator(),
            new FakeProcessInspector(),
            starter,
            new OfficialLauncherFamilyAdmission());

        var first = Task.Run(() => executor.Open("hsr", fixture.Root));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var reloadedPage = executor.OpenOrObserveCurrentAsync(
            "zzz",
            fixture.Root,
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await reloadedPage.WaitAsync(TimeSpan.FromSeconds(5)));
        release.Set();

        Assert.Equal(HoyoPlayOpenStatus.Failed, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(HoyoPlayOpenStatus.Ready, executor.Check("zzz", fixture.Root).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public void Family_admission_releases_exactly_once()
    {
        var admission = new OfficialLauncherFamilyAdmission();
        var first = admission.TryEnter();

        Assert.NotNull(first);
        Assert.Null(admission.TryEnter());
        first.Dispose();
        first.Dispose();

        Assert.NotNull(admission.TryEnter());
    }

    [Theory]
    [InlineData("wuwa")]
    [InlineData("ae")]
    [InlineData("")]
    public void Unsupported_game_has_no_generic_argument_path(string gameId)
    {
        using var fixture = FakeHoyoPlay.Create("1.8.0.0");
        var starter = new FakeStarter();
        var executor = new HoyoPlayHandoffExecutor(
            fixture.CreateValidator(),
            new FakeProcessInspector(),
            starter);

        var result = executor.Open(gameId, fixture.Root);

        Assert.Equal(HoyoPlayOpenStatus.NeedsReview, result.Status);
        Assert.Empty(starter.Requests);
    }

    private sealed class FakeProcessInspector : IStrictRunningProcessInspector
    {
        private readonly Func<RunningProcessStatus> read;

        public FakeProcessInspector(RunningProcessStatus status = RunningProcessStatus.NotRunning)
            : this(() => status)
        {
        }

        public FakeProcessInspector(Func<RunningProcessStatus> read)
        {
            this.read = read;
        }

        public List<(string Name, string Path)> Checks { get; } = [];

        public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath)
        {
            Checks.Add((processName, expectedExecutablePath));
            return read();
        }
    }

    private sealed class FakeStarter(
        Exception? failure = null,
        ManualResetEventSlim? entered = null,
        ManualResetEventSlim? release = null,
        Action? onStart = null) : IHoyoPlayProcessStarter
    {
        public List<HoyoPlayHandoffRequest> Requests { get; } = [];

        public void Start(HoyoPlayHandoffRequest request)
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
}
