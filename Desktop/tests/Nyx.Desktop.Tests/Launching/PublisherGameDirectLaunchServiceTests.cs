using System.ComponentModel;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.Launching;
using Nyx.Desktop.Infrastructure.PublisherGames;
using Nyx.Desktop.Tests.PublisherGames;

namespace Nyx.Desktop.Tests.Launching;

public sealed class PublisherGameDirectLaunchServiceTests
{
    private const string WuWaRoot = @"C:\Games\Wuthering Waves";
    private const string EndfieldRoot = @"C:\Games\GRYPHLINK";

    [Theory]
    [InlineData("wuwa", WuWaRoot, @"Wuthering Waves Game\Wuthering Waves.exe")]
    [InlineData("ae", EndfieldRoot, @"games\EndField Game\Endfield.exe")]
    public void Ready_check_exposes_only_fixed_zero_argument_game_specification(
        string gameId,
        string root,
        string relativeExecutable)
    {
        var fixture = new Fixture(gameId, root);

        var result = fixture.Service.CheckGame(gameId, root);

        Assert.Equal(PublisherGameLaunchStatus.Ready, result.Status);
        Assert.Equal(Path.Combine(root, relativeExecutable), result.Specification!.FileName);
        Assert.Equal(Path.GetDirectoryName(result.Specification.FileName), result.Specification.WorkingDirectory);
        Assert.Empty(result.Specification.Arguments);
        Assert.False(result.Specification.UseShellExecute);
        Assert.Equal(
            gameId == "wuwa"
                ? PublisherGameInspectionReason.VersionConflict
                : PublisherGameInspectionReason.VersionUnavailable,
            result.InspectionReason);
        Assert.Empty(fixture.Starter.Starts);
        Assert.All(fixture.Validator.Inspections, inspection => Assert.True(inspection.Disposed));
    }

    [Fact]
    public void WuWa_observes_only_exact_bootstrap_and_runtime_paths()
    {
        var fixture = new Fixture("wuwa", WuWaRoot);

        fixture.Service.CheckGame("wuwa", WuWaRoot);

        Assert.Equal(
            [
                ("Wuthering Waves", Path.Combine(WuWaRoot, @"Wuthering Waves Game\Wuthering Waves.exe")),
                ("Client-Win64-Shipping", Path.Combine(WuWaRoot, @"Wuthering Waves Game\Client\Binaries\Win64\Client-Win64-Shipping.exe")),
            ],
            fixture.Process.Checks);
    }

    [Fact]
    public void Endfield_never_uses_platform_or_ace_as_process_evidence()
    {
        var fixture = new Fixture("ae", EndfieldRoot);

        fixture.Service.CheckGame("ae", EndfieldRoot);

        Assert.Equal(
            [("Endfield", Path.Combine(EndfieldRoot, @"games\EndField Game\Endfield.exe"))],
            fixture.Process.Checks);
        Assert.DoesNotContain(fixture.Process.Checks, item =>
            item.Item1.Contains("Platform", StringComparison.OrdinalIgnoreCase)
            || item.Item1.Contains("ACE", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(RunningProcessStatus.Running, RunningProcessStatus.NotRunning)]
    [InlineData(RunningProcessStatus.NotRunning, RunningProcessStatus.Running)]
    public void WuWa_bootstrap_or_runtime_is_running_but_uncertain_evidence_fails_closed(
        RunningProcessStatus bootstrap,
        RunningProcessStatus runtime)
    {
        var running = new Fixture("wuwa", WuWaRoot, [bootstrap, runtime]);
        var uncertain = new Fixture(
            "wuwa",
            WuWaRoot,
            [RunningProcessStatus.Running, RunningProcessStatus.Uncertain]);

        var result = running.Service.CheckGame("wuwa", WuWaRoot);

        Assert.Equal(PublisherGameLaunchStatus.Running, result.Status);
        Assert.Equal(bootstrap, result.Bootstrap);
        Assert.Equal(runtime, result.Runtime);
        Assert.Equal(
            PublisherGameLaunchStatus.NeedsReview,
            uncertain.Service.CheckGame("wuwa", WuWaRoot).Status);
    }

    [Fact]
    public void Normal_launch_revalidates_and_keeps_protected_binding_alive_through_start()
    {
        var fixture = new Fixture("ae", EndfieldRoot);
        fixture.Starter.OnStart = () =>
        {
            Assert.Equal(2, fixture.Validator.Inspections.Count);
            Assert.False(fixture.Validator.Inspections[1].Disposed);
            Assert.True(fixture.Validator.Inspections[1].StableChecks > 0);
        };

        var result = fixture.Service.LaunchGame("ae", EndfieldRoot);

        Assert.Equal(PublisherGameLaunchStatus.Running, result.Status);
        Assert.Equal(2, fixture.Validator.Calls.Count);
        Assert.Single(fixture.Starter.Starts);
        Assert.All(fixture.Validator.Inspections, inspection => Assert.True(inspection.Disposed));
    }

    [Fact]
    public void Wuwa_directx_11_is_a_sealed_argument_and_never_leaks_to_endfield()
    {
        var wuwa = new Fixture("wuwa", WuWaRoot);
        var endfield = new Fixture("ae", EndfieldRoot);

        var wuwaResult = wuwa.Service.LaunchGame(
            "wuwa",
            WuWaRoot,
            PublisherGameRenderingMode.DirectX11,
            ["--user", "value"]);
        var endfieldResult = endfield.Service.LaunchGame("ae", EndfieldRoot);

        Assert.Equal(PublisherGameLaunchStatus.Running, wuwaResult.Status);
        Assert.Equal(["-dx11", "--user", "value"], Assert.Single(wuwa.Starter.Starts).Arguments);
        Assert.Equal(PublisherGameLaunchStatus.Running, endfieldResult.Status);
        Assert.Empty(Assert.Single(endfield.Starter.Starts).Arguments);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            endfield.Service.CheckGame(
                "ae",
                EndfieldRoot,
                PublisherGameRenderingMode.DirectX11));
    }

    [Fact]
    public void Only_full_proof_expected_reason_and_version_state_are_admitted()
    {
        var cases = new[]
        {
            Result("wuwa", WuWaRoot, PublisherGameInspectionReason.VersionUnavailable, PublisherGameVersionState.Unavailable),
            Result("wuwa", WuWaRoot, PublisherGameInspectionReason.VersionConflict, PublisherGameVersionState.Conflict, maintenance: false),
            Result("ae", EndfieldRoot, PublisherGameInspectionReason.VersionConflict, PublisherGameVersionState.Conflict),
            new PublisherGameInspectionResult(
                "ae",
                PublisherGameInspectionStatus.Ready,
                PublisherGameInspectionReason.None,
                PublisherGameVersionState.Available,
                EndfieldRoot,
                "1.0.0",
                new("ae", EndfieldRoot, Path.Combine(EndfieldRoot, "Launcher.exe"), "1.5.0.1507")),
        };

        foreach (var result in cases)
        {
            var root = result.GameId == "wuwa" ? WuWaRoot : EndfieldRoot;
            var validator = new FakeValidator(() => new FakeInspection(result));
            var process = new FakeProcessInspector();
            var starter = new FakeStarter();
            var service = new PublisherGameDirectLaunchService(validator, process, starter);

            Assert.Equal(PublisherGameLaunchStatus.NeedsReview, service.LaunchGame(result.GameId, root).Status);
            Assert.Empty(process.Checks);
            Assert.Empty(starter.Starts);
        }
    }

    [Fact]
    public void Current_wuwa_versioned_ready_proof_is_admitted_without_weakening_endfield()
    {
        var result = new PublisherGameInspectionResult(
            "wuwa",
            PublisherGameInspectionStatus.Ready,
            PublisherGameInspectionReason.None,
            PublisherGameVersionState.Available,
            WuWaRoot,
            "3.5.3",
            new("wuwa", WuWaRoot, Path.Combine(WuWaRoot, "launcher.exe"), "2.6.3.0"));
        var validator = new FakeValidator(() => new FakeInspection(result));
        var process = new FakeProcessInspector();
        var starter = new FakeStarter();
        var service = new PublisherGameDirectLaunchService(validator, process, starter);

        var observed = service.CheckGame("wuwa", WuWaRoot);

        Assert.Equal(PublisherGameLaunchStatus.Ready, observed.Status);
        Assert.Equal(PublisherGameInspectionReason.None, observed.InspectionReason);
        Assert.Empty(starter.Starts);
    }

    [Fact]
    public void Redirected_root_identity_drift_and_unstable_proof_never_start()
    {
        var redirected = new Fixture("wuwa", WuWaRoot)
        {
            ResultFactory = () => Result("wuwa", @"C:\Other\WuWa", PublisherGameInspectionReason.VersionConflict, PublisherGameVersionState.Conflict),
        };
        var drift = new Fixture("wuwa", WuWaRoot);
        drift.ResultSequence.Enqueue(Result("wuwa", WuWaRoot, PublisherGameInspectionReason.VersionConflict, PublisherGameVersionState.Conflict));
        drift.ResultSequence.Enqueue(Result("wuwa", WuWaRoot, PublisherGameInspectionReason.TargetChangedDuringInspection, PublisherGameVersionState.Unavailable, maintenance: false));
        var unstable = new Fixture("ae", EndfieldRoot) { Stable = false };

        Assert.Equal(PublisherGameLaunchStatus.NeedsReview, redirected.Service.LaunchGame("wuwa", WuWaRoot).Status);
        Assert.Equal(PublisherGameLaunchStatus.NeedsReview, drift.Service.LaunchGame("wuwa", WuWaRoot).Status);
        Assert.Equal(PublisherGameLaunchStatus.NeedsReview, unstable.Service.LaunchGame("ae", EndfieldRoot).Status);
        Assert.Empty(redirected.Starter.Starts);
        Assert.Empty(drift.Starter.Starts);
        Assert.Empty(unstable.Starter.Starts);
    }

    [Fact]
    public void Error_740_alone_revalidates_a_third_time_then_uses_one_sealed_elevation()
    {
        var fixture = new Fixture("wuwa", WuWaRoot)
        {
            StartException = new Win32Exception(740),
        };

        var result = fixture.Service.LaunchGame(
            "wuwa",
            WuWaRoot,
            launchArguments: ["--region", "global"]);

        Assert.Equal(PublisherGameLaunchStatus.Running, result.Status);
        Assert.Equal(3, fixture.Validator.Calls.Count);
        Assert.Single(fixture.Starter.Starts);
        var elevated = Assert.Single(fixture.Starter.ElevatedStarts);
        Assert.Equal("wuwa", elevated.GameId);
        Assert.Equal(Path.Combine(WuWaRoot, @"Wuthering Waves Game\Wuthering Waves.exe"), elevated.Specification.FileName);
        Assert.Equal(["--region", "global"], elevated.Specification.Arguments);
        Assert.Equal(fixture.Starter.Starts[0].Arguments, elevated.Specification.Arguments);
        Assert.All(fixture.Validator.Inspections, inspection => Assert.True(inspection.Disposed));
    }

    [Theory]
    [InlineData(5, PublisherGameLaunchFailureReason.WindowsStartFailed, 0)]
    [InlineData(1223, PublisherGameLaunchFailureReason.ElevationCancelled, 1)]
    [InlineData(87, PublisherGameLaunchFailureReason.ElevatedStartFailed, 1)]
    public void Non_740_never_elevates_and_elevation_cancel_or_failure_is_bounded(
        int nativeError,
        PublisherGameLaunchFailureReason expected,
        int expectedElevatedStarts)
    {
        var fixture = new Fixture("ae", EndfieldRoot)
        {
            StartException = new Win32Exception(nativeError == 5 ? 5 : 740),
            ElevatedStartException = nativeError == 5 ? null : new Win32Exception(nativeError),
        };

        var result = fixture.Service.LaunchGame("ae", EndfieldRoot);

        Assert.Equal(PublisherGameLaunchStatus.LaunchFailed, result.Status);
        Assert.Equal(expected, result.FailureReason);
        Assert.Equal(expectedElevatedStarts, fixture.Starter.ElevatedStarts.Count);
    }

    [Fact]
    public void Process_seen_or_becoming_uncertain_after_740_blocks_elevation()
    {
        var seen = new Fixture(
            "ae",
            EndfieldRoot,
            [RunningProcessStatus.NotRunning, RunningProcessStatus.NotRunning, RunningProcessStatus.Running])
        {
            StartException = new Win32Exception(740),
        };
        var uncertain = new Fixture(
            "ae",
            EndfieldRoot,
            [RunningProcessStatus.NotRunning, RunningProcessStatus.NotRunning, RunningProcessStatus.Uncertain])
        {
            StartException = new Win32Exception(740),
        };

        Assert.Equal(PublisherGameLaunchStatus.Running, seen.Service.LaunchGame("ae", EndfieldRoot).Status);
        Assert.Equal(PublisherGameLaunchStatus.NeedsReview, uncertain.Service.LaunchGame("ae", EndfieldRoot).Status);
        Assert.Empty(seen.Starter.ElevatedStarts);
        Assert.Empty(uncertain.Starter.ElevatedStarts);
    }

    [Fact]
    public void Inaccessible_process_evidence_fails_closed_and_releases_protected_proof()
    {
        var validator = new FakeValidator(() => new FakeInspection(Result(
            "ae",
            EndfieldRoot,
            PublisherGameInspectionReason.VersionUnavailable,
            PublisherGameVersionState.Unavailable)));
        var starter = new FakeStarter();
        var service = new PublisherGameDirectLaunchService(
            validator,
            new ThrowingProcessInspector(),
            starter);

        var result = service.LaunchGame("ae", EndfieldRoot);

        Assert.Equal(PublisherGameLaunchStatus.NeedsReview, result.Status);
        Assert.True(Assert.Single(validator.Inspections).Disposed);
        Assert.Empty(starter.Starts);
    }

    [Theory]
    [InlineData("gi")]
    [InlineData("hsr")]
    [InlineData("zzz")]
    [InlineData("")]
    public void Unsupported_game_has_no_generic_start_capability(string gameId)
    {
        var fixture = new Fixture("wuwa", WuWaRoot);

        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Service.LaunchGame(gameId, WuWaRoot));
        Assert.Empty(fixture.Validator.Calls);
        Assert.Empty(fixture.Starter.Starts);
    }

    [Fact]
    public void Direct_launch_boundary_exposes_no_generic_path_argument_shell_launcher_or_update_method()
    {
        var methods = typeof(PublisherGameDirectLaunchService)
            .GetMethods(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.DeclaredOnly);

        Assert.Equal(
            [nameof(PublisherGameDirectLaunchService.CheckGame), nameof(PublisherGameDirectLaunchService.LaunchGame)],
            methods.Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.All(methods, method => Assert.Equal(
            [typeof(string), typeof(string), typeof(PublisherGameRenderingMode), typeof(IReadOnlyList<string>)],
            method.GetParameters().Select(parameter => parameter.ParameterType)));
        Assert.DoesNotContain(methods, method =>
            method.Name.Contains("Launcher", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Shell", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Argument", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("wuwa", WuWaRoot)]
    [InlineData("ae", EndfieldRoot)]
    public void Both_publisher_games_dispatch_the_exact_user_argument_list(string gameId, string root)
    {
        var fixture = new Fixture(gameId, root);

        var result = fixture.Service.LaunchGame(
            gameId,
            root,
            launchArguments: ["--name", "Rover One"]);

        Assert.Equal(PublisherGameLaunchStatus.Running, result.Status);
        Assert.Equal(["--name", "Rover One"], Assert.Single(fixture.Starter.Starts).Arguments);
    }

    [Fact]
    public void Protected_validation_seam_and_service_construction_are_not_publicly_forgeable()
    {
        Assert.False(typeof(IProtectedPublisherGameInspection).IsPublic);
        Assert.False(typeof(IPublisherGameDirectLaunchIdentityValidator).IsPublic);
        Assert.False(typeof(PublisherGameDirectLaunchIdentityValidator).IsPublic);
        Assert.Empty(typeof(PublisherGameDirectLaunchService).GetConstructors());

        var factoryMethod = typeof(PublisherGameDirectLaunchFactory)
            .GetMethod(nameof(PublisherGameDirectLaunchFactory.Create));
        Assert.NotNull(factoryMethod);
        Assert.Empty(factoryMethod.GetParameters());
        Assert.Equal(typeof(PublisherGameDirectLaunchService), factoryMethod.ReturnType);
    }

    [Theory]
    [InlineData("wuwa", EndfieldRoot, @"games\EndField Game\Endfield.exe")]
    [InlineData("ae", WuWaRoot, @"Wuthering Waves Game\Wuthering Waves.exe")]
    public void Windows_elevation_boundary_rejects_cross_game_or_wrong_root_specification(
        string gameId,
        string root,
        string relativePath)
    {
        var starter = new DotNetLaunchProcessStarter();
        var specification = new LaunchSpecification(
            Path.Combine(root, relativePath),
            Path.GetDirectoryName(Path.Combine(root, relativePath))!,
            Array.Empty<string>(),
            UseShellExecute: false);
        var request = new ValidatedPublisherGameElevationRequest(gameId, root, specification);

        Assert.Throws<InvalidOperationException>(() => starter.StartValidatedPublisherGame(request));
    }

    [Fact]
    public void Elevation_request_is_sealed_and_boundary_has_only_the_typed_request()
    {
        Assert.True(typeof(ValidatedPublisherGameElevationRequest).IsSealed);
        Assert.Empty(typeof(ValidatedPublisherGameElevationRequest).GetConstructors());
        Assert.Equal(
            [typeof(ValidatedPublisherGameElevationRequest)],
            typeof(IPublisherGameElevatedProcessStarter)
                .GetMethod(nameof(IPublisherGameElevatedProcessStarter.StartValidatedPublisherGame))!
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Real_protected_adapter_keeps_game_file_bound_during_process_admission()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        using var endfield = FakePublisherInstall.CreateEndfield();
        var bootstrap = fixture.PathOf(@"Wuthering Waves Game\Wuthering Waves.exe");
        var originalTimestamp = File.GetLastWriteTimeUtc(bootstrap);
        var swapWasBlocked = false;
        var inspector = new CallbackProcessInspector(() =>
        {
            try
            {
                File.WriteAllBytes(bootstrap, [8, 5, 2]);
                File.SetLastWriteTimeUtc(bootstrap, originalTimestamp);
            }
            catch (IOException)
            {
                swapWasBlocked = true;
            }
        });
        var validator = new PublisherGameDirectLaunchIdentityValidator(
            fixture.CreateWuWaAdapter(),
            endfield.CreateEndfieldAdapter());
        var service = new PublisherGameDirectLaunchService(
            validator,
            inspector,
            new FakeStarter());

        var result = service.CheckGame("wuwa", fixture.Root);

        Assert.True(swapWasBlocked);
        Assert.Equal(PublisherGameLaunchStatus.Ready, result.Status);
    }

    private static PublisherGameInspectionResult Result(
        string gameId,
        string root,
        PublisherGameInspectionReason reason,
        PublisherGameVersionState versionState,
        bool maintenance = true) =>
        new(
            gameId,
            PublisherGameInspectionStatus.NeedsReview,
            reason,
            versionState,
            root,
            maintenanceTarget: maintenance
                ? new(gameId, root, Path.Combine(root, gameId == "wuwa" ? "launcher.exe" : "Launcher.exe"), "1.0.0.0")
                : null);

    private sealed class Fixture
    {
        private readonly string gameId;
        private readonly string root;
        private bool stable = true;

        public Fixture(
            string gameId,
            string root,
            IEnumerable<RunningProcessStatus>? processStatuses = null)
        {
            this.gameId = gameId;
            this.root = root;
            Validator = new FakeValidator(() => new FakeInspection(NextResult(), () => stable));
            Process = new FakeProcessInspector(processStatuses);
            Starter = new FakeStarter(() => StartException, () => ElevatedStartException);
            Service = new(Validator, Process, Starter);
        }

        public Queue<PublisherGameInspectionResult> ResultSequence { get; } = new();

        public Func<PublisherGameInspectionResult>? ResultFactory { get; set; }

        public FakeValidator Validator { get; }

        public FakeProcessInspector Process { get; }

        public FakeStarter Starter { get; }

        public PublisherGameDirectLaunchService Service { get; }

        public bool Stable { set => stable = value; }

        public Exception? StartException { get; set; }

        public Exception? ElevatedStartException { get; set; }

        private PublisherGameInspectionResult NextResult() =>
            ResultSequence.Count > 0
                ? ResultSequence.Dequeue()
                : ResultFactory?.Invoke()
                    ?? Result(
                        gameId,
                        root,
                        gameId == "wuwa"
                            ? PublisherGameInspectionReason.VersionConflict
                            : PublisherGameInspectionReason.VersionUnavailable,
                        gameId == "wuwa"
                            ? PublisherGameVersionState.Conflict
                            : PublisherGameVersionState.Unavailable);
    }

    private sealed class FakeValidator(Func<FakeInspection> create)
        : IPublisherGameDirectLaunchIdentityValidator
    {
        public List<(string GameId, string? Root)> Calls { get; } = [];

        public List<FakeInspection> Inspections { get; } = [];

        public IProtectedPublisherGameInspection InspectProtected(string gameId, string? root)
        {
            Calls.Add((gameId, root));
            var inspection = create();
            Inspections.Add(inspection);
            return inspection;
        }
    }

    private sealed class FakeInspection(
        PublisherGameInspectionResult result,
        Func<bool>? stable = null) : IProtectedPublisherGameInspection
    {
        public PublisherGameInspectionResult Result { get; } = result;

        public bool Disposed { get; private set; }

        public int StableChecks { get; private set; }

        public bool RemainsCompleteAndStable()
        {
            StableChecks++;
            return !Disposed && (stable?.Invoke() ?? true);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProcessInspector(IEnumerable<RunningProcessStatus>? statuses = null)
        : IStrictRunningProcessInspector
    {
        private readonly Queue<RunningProcessStatus> statuses = new(statuses ?? []);

        public List<(string, string)> Checks { get; } = [];

        public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath)
        {
            Checks.Add((processName, expectedExecutablePath));
            return statuses.TryDequeue(out var status)
                ? status
                : RunningProcessStatus.NotRunning;
        }
    }

    private sealed class FakeStarter(
        Func<Exception?>? startException = null,
        Func<Exception?>? elevatedException = null)
        : ILaunchProcessStarter, IPublisherGameElevatedProcessStarter
    {
        public List<LaunchSpecification> Starts { get; } = [];

        public List<ValidatedPublisherGameElevationRequest> ElevatedStarts { get; } = [];

        public Action? OnStart { get; set; }

        public void Start(LaunchSpecification specification)
        {
            Starts.Add(specification);
            OnStart?.Invoke();
            if (startException?.Invoke() is { } failure)
            {
                throw failure;
            }
        }

        public void StartValidatedPublisherGame(ValidatedPublisherGameElevationRequest request)
        {
            ElevatedStarts.Add(request);
            if (elevatedException?.Invoke() is { } failure)
            {
                throw failure;
            }
        }
    }

    private sealed class CallbackProcessInspector(Action callback) : IStrictRunningProcessInspector
    {
        private int calls;

        public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                callback();
            }

            return RunningProcessStatus.NotRunning;
        }
    }

    private sealed class ThrowingProcessInspector : IStrictRunningProcessInspector
    {
        public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath) =>
            throw new UnauthorizedAccessException("sanitized fake denial");
    }
}
