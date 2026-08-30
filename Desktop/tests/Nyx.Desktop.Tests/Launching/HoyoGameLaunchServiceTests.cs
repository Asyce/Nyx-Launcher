using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Launching;

namespace Nyx.Desktop.Tests.Launching;

public sealed class HoyoGameLaunchServiceTests
{
    private const string HsrRoot = @"C:\Games\Star Rail Games";
    private const string ZzzRoot = @"C:\Games\ZenlessZoneZero Game";

    [Theory]
    [InlineData("hsr", HsrRoot, "StarRail.exe", "StarRail")]
    [InlineData("zzz", ZzzRoot, "ZenlessZoneZero.exe", "ZenlessZoneZero")]
    public void Ready_check_exposes_only_the_fixed_argument_free_game_specification(
        string gameId,
        string root,
        string executable,
        string processName)
    {
        var fixture = new Fixture(gameId, root);

        var result = fixture.Service.CheckGame(gameId, root);

        Assert.Equal(HoyoGameLaunchStatus.Ready, result.Status);
        Assert.Equal(Path.Combine(root, executable), result.Specification!.FileName);
        Assert.Equal(root, result.Specification.WorkingDirectory);
        Assert.Empty(result.Specification.Arguments);
        Assert.False(result.Specification.UseShellExecute);
        Assert.Equal((processName, Path.Combine(root, executable)), Assert.Single(fixture.Process.Checks));
        Assert.Empty(fixture.Starter.Starts);
    }

    [Theory]
    [InlineData("hsr", HsrRoot, "StarRail.exe")]
    [InlineData("zzz", ZzzRoot, "ZenlessZoneZero.exe")]
    public void Launch_revalidates_twice_then_starts_once_without_shell_or_elevation(
        string gameId,
        string root,
        string executable)
    {
        var fixture = new Fixture(gameId, root);

        var result = fixture.Service.LaunchGame(gameId, root);

        Assert.Equal(HoyoGameLaunchStatus.Running, result.Status);
        Assert.True(result.StartedByThisCall);
        Assert.Equal(2, fixture.Validator.Calls.Count);
        Assert.Equal(2, fixture.Process.Checks.Count);
        var start = Assert.Single(fixture.Starter.Starts);
        Assert.Equal(Path.Combine(root, executable), start.FileName);
        Assert.Equal(root, start.WorkingDirectory);
        Assert.Empty(start.Arguments);
        Assert.False(start.UseShellExecute);
        Assert.Empty(fixture.Starter.ElevatedStarts);
    }

    [Fact]
    public void Zzz_directx_12_is_a_sealed_argument_and_never_leaks_to_hsr()
    {
        var zzz = new Fixture("zzz", ZzzRoot);
        var hsr = new Fixture("hsr", HsrRoot);

        var zzzResult = zzz.Service.LaunchGame(
            "zzz",
            ZzzRoot,
            HoyoGameRenderingMode.DirectX12,
            ["--user", "value"]);
        var hsrResult = hsr.Service.LaunchGame("hsr", HsrRoot);

        Assert.Equal(HoyoGameLaunchStatus.Running, zzzResult.Status);
        Assert.Equal(["-force-d3d12", "--user", "value"], Assert.Single(zzz.Starter.Starts).Arguments);
        Assert.Equal(HoyoGameLaunchStatus.Running, hsrResult.Status);
        Assert.Empty(Assert.Single(hsr.Starter.Starts).Arguments);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            hsr.Service.CheckGame("hsr", HsrRoot, HoyoGameRenderingMode.DirectX12));
    }

    [Fact]
    public void Already_running_or_uncertain_process_never_starts()
    {
        var running = new Fixture("hsr", HsrRoot) { Running = RunningProcessStatus.Running };
        var uncertain = new Fixture("zzz", ZzzRoot) { Running = RunningProcessStatus.Uncertain };

        var runningResult = running.Service.LaunchGame("hsr", HsrRoot);
        Assert.Equal(HoyoGameLaunchStatus.Running, runningResult.Status);
        Assert.False(runningResult.StartedByThisCall);
        Assert.Equal(HoyoGameLaunchStatus.NeedsReview, uncertain.Service.LaunchGame("zzz", ZzzRoot).Status);
        Assert.Empty(running.Starter.Starts);
        Assert.Empty(uncertain.Starter.Starts);
    }

    [Fact]
    public void Wrong_game_identity_or_redirected_root_fails_closed()
    {
        var wrongGame = new Fixture("hsr", HsrRoot)
        {
            Result = Ready("zzz", HsrRoot),
        };
        var redirected = new Fixture("hsr", HsrRoot)
        {
            Result = Ready("hsr", @"C:\Other\Star Rail"),
        };

        Assert.Equal(HoyoGameLaunchStatus.NeedsReview, wrongGame.Service.LaunchGame("hsr", HsrRoot).Status);
        Assert.Equal(HoyoGameLaunchStatus.NeedsReview, redirected.Service.LaunchGame("hsr", HsrRoot).Status);
        Assert.Empty(wrongGame.Process.Checks);
        Assert.Empty(redirected.Process.Checks);
        Assert.Empty(wrongGame.Starter.Starts);
        Assert.Empty(redirected.Starter.Starts);
    }

    [Fact]
    public void Target_drift_at_dispatch_revalidation_never_starts()
    {
        var validator = new SequencedValidator(
            Ready("hsr", HsrRoot),
            new("hsr", HoyoInspectionStatus.NeedsReview, HoyoInspectionReason.TargetChangedDuringInspection, HsrRoot));
        var process = new FakeProcessInspector();
        var starter = new FakeStarter();
        var service = new HoyoGameLaunchService(validator, process, starter);

        var result = service.LaunchGame("hsr", HsrRoot);

        Assert.Equal(HoyoGameLaunchStatus.NeedsReview, result.Status);
        Assert.Single(process.Checks);
        Assert.Empty(starter.Starts);
    }

    [Fact]
    public void Windows_error_740_revalidates_then_uses_one_sealed_elevation_attempt()
    {
        var fixture = new Fixture("hsr", HsrRoot)
        {
            StartException = new System.ComponentModel.Win32Exception(740),
        };

        var result = fixture.Service.LaunchGame(
            "hsr",
            HsrRoot,
            launchArguments: ["--region", "global"]);

        Assert.Equal(HoyoGameLaunchStatus.Running, result.Status);
        Assert.True(result.StartedByThisCall);
        Assert.Equal(HoyoGameLaunchFailureReason.None, result.FailureReason);
        Assert.Single(fixture.Starter.Starts);
        var request = Assert.Single(fixture.Starter.ElevatedStarts);
        Assert.Equal("hsr", request.GameId);
        Assert.Equal(Path.Combine(HsrRoot, "StarRail.exe"), request.Specification.FileName);
        Assert.Equal(HsrRoot, request.Specification.WorkingDirectory);
        Assert.Equal(["--region", "global"], request.Specification.Arguments);
        Assert.False(request.Specification.UseShellExecute);
        Assert.Equal(3, fixture.Validator.Calls.Count);
        Assert.Equal(3, fixture.Process.Checks.Count);
    }

    [Fact]
    public void Error_740_without_the_sealed_hoyo_elevation_boundary_fails_closed()
    {
        var validator = new DelegateValidator((_, _) => Ready("zzz", ZzzRoot));
        var process = new FakeProcessInspector();
        var starter = new StandardOnlyStarter(new System.ComponentModel.Win32Exception(740));
        var service = new HoyoGameLaunchService(validator, process, starter);

        var result = service.LaunchGame("zzz", ZzzRoot);

        Assert.Equal(HoyoGameLaunchStatus.LaunchFailed, result.Status);
        Assert.Equal(HoyoGameLaunchFailureReason.ElevationRequired, result.FailureReason);
        Assert.Single(starter.Starts);
        Assert.Equal(2, validator.Calls.Count);
    }

    [Fact]
    public void Non_740_windows_start_failure_never_enters_the_elevation_boundary()
    {
        var fixture = new Fixture("hsr", HsrRoot)
        {
            StartException = new System.ComponentModel.Win32Exception(5),
        };

        var result = fixture.Service.LaunchGame("hsr", HsrRoot);

        Assert.Equal(HoyoGameLaunchStatus.LaunchFailed, result.Status);
        Assert.Equal(HoyoGameLaunchFailureReason.WindowsStartFailed, result.FailureReason);
        Assert.Single(fixture.Starter.Starts);
        Assert.Empty(fixture.Starter.ElevatedStarts);
        Assert.Equal(2, fixture.Validator.Calls.Count);
    }

    [Theory]
    [InlineData(1223, HoyoGameLaunchFailureReason.ElevationCancelled)]
    [InlineData(5, HoyoGameLaunchFailureReason.ElevatedStartFailed)]
    public void Elevation_refusal_or_failure_is_bounded_and_never_retried(
        int nativeError,
        HoyoGameLaunchFailureReason expectedReason)
    {
        var fixture = new Fixture("zzz", ZzzRoot)
        {
            StartException = new System.ComponentModel.Win32Exception(740),
            ElevatedStartException = new System.ComponentModel.Win32Exception(nativeError),
        };

        var result = fixture.Service.LaunchGame("zzz", ZzzRoot);

        Assert.Equal(HoyoGameLaunchStatus.LaunchFailed, result.Status);
        Assert.Equal(expectedReason, result.FailureReason);
        Assert.Single(fixture.Starter.Starts);
        Assert.Single(fixture.Starter.ElevatedStarts);
    }

    [Fact]
    public void Identity_drift_after_740_blocks_the_elevation_request()
    {
        var validator = new SequencedValidator(
            Ready("hsr", HsrRoot),
            Ready("hsr", HsrRoot),
            new(
                "hsr",
                HoyoInspectionStatus.NeedsReview,
                HoyoInspectionReason.TargetChangedDuringInspection,
                HsrRoot));
        var process = new FakeProcessInspector();
        var starter = new FakeStarter(() => new System.ComponentModel.Win32Exception(740));
        var service = new HoyoGameLaunchService(validator, process, starter);

        var result = service.LaunchGame("hsr", HsrRoot);

        Assert.Equal(HoyoGameLaunchStatus.NeedsReview, result.Status);
        Assert.Single(starter.Starts);
        Assert.Empty(starter.ElevatedStarts);
        Assert.Equal(2, process.Checks.Count);
    }

    [Fact]
    public void Exact_process_seen_after_740_is_adopted_without_requesting_elevation()
    {
        var validator = new DelegateValidator((_, _) => Ready("hsr", HsrRoot));
        var statuses = new Queue<RunningProcessStatus>(
            [
                RunningProcessStatus.NotRunning,
                RunningProcessStatus.NotRunning,
                RunningProcessStatus.Running,
            ]);
        var process = new FakeProcessInspector(() => statuses.Dequeue());
        var starter = new FakeStarter(() => new System.ComponentModel.Win32Exception(740));
        var service = new HoyoGameLaunchService(validator, process, starter);

        var result = service.LaunchGame("hsr", HsrRoot);

        Assert.Equal(HoyoGameLaunchStatus.Running, result.Status);
        Assert.False(result.StartedByThisCall);
        Assert.Single(starter.Starts);
        Assert.Empty(starter.ElevatedStarts);
    }

    [Theory]
    [InlineData("gi")]
    [InlineData("wuwa")]
    [InlineData("ae")]
    [InlineData("")]
    public void Unsupported_profile_has_no_generic_start_path(string gameId)
    {
        var fixture = new Fixture("hsr", HsrRoot);

        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Service.LaunchGame(gameId, HsrRoot));
        Assert.Empty(fixture.Validator.Calls);
        Assert.Empty(fixture.Process.Checks);
        Assert.Empty(fixture.Starter.Starts);
    }

    [Theory]
    [InlineData("gi", HsrRoot, "StarRail.exe", HsrRoot)]
    [InlineData("hsr", HsrRoot, "ZenlessZoneZero.exe", HsrRoot)]
    [InlineData("zzz", ZzzRoot, "StarRail.exe", ZzzRoot)]
    [InlineData("hsr", HsrRoot, "StarRail.exe", @"C:\Other")]
    public void Windows_elevation_boundary_rejects_unsealed_game_path_or_working_directory(
        string gameId,
        string root,
        string executable,
        string workingDirectory)
    {
        var starter = new Nyx.Desktop.Infrastructure.Launching.DotNetLaunchProcessStarter();
        var request = new ValidatedHoyoGameElevationRequest(
            gameId,
            new(
                Path.Combine(root, executable),
                workingDirectory,
                Array.Empty<string>(),
                UseShellExecute: false));

        Assert.Throws<InvalidOperationException>(() => starter.StartValidatedHoyoGame(request));
    }

    [Fact]
    public void Windows_elevation_boundary_rejects_unsafe_arguments_and_preexisting_shell_requests()
    {
        var starter = new Nyx.Desktop.Infrastructure.Launching.DotNetLaunchProcessStarter();
        var withArguments = new ValidatedHoyoGameElevationRequest(
            "hsr",
            new(
                Path.Combine(HsrRoot, "StarRail.exe"),
                HsrRoot,
                ["unsafe\nargument"],
                UseShellExecute: false));
        var withShell = new ValidatedHoyoGameElevationRequest(
            "zzz",
            new(
                Path.Combine(ZzzRoot, "ZenlessZoneZero.exe"),
                ZzzRoot,
                Array.Empty<string>(),
                UseShellExecute: true));

        Assert.Throws<InvalidOperationException>(() => starter.StartValidatedHoyoGame(withArguments));
        Assert.Throws<InvalidOperationException>(() => starter.StartValidatedHoyoGame(withShell));
    }

    [Theory]
    [InlineData("hsr", HsrRoot)]
    [InlineData("zzz", ZzzRoot)]
    public void Both_hoyo_games_dispatch_the_exact_user_argument_list(string gameId, string root)
    {
        var fixture = new Fixture(gameId, root);

        var result = fixture.Service.LaunchGame(
            gameId,
            root,
            launchArguments: ["--name", "March 7th"]);

        Assert.Equal(HoyoGameLaunchStatus.Running, result.Status);
        Assert.True(result.StartedByThisCall);
        Assert.Equal(["--name", "March 7th"], Assert.Single(fixture.Starter.Starts).Arguments);
    }

    [Fact]
    public void Process_appearing_at_dispatch_is_reported_as_preexisting()
    {
        var validator = new DelegateValidator((_, _) => Ready("hsr", HsrRoot));
        var statuses = new Queue<RunningProcessStatus>([
            RunningProcessStatus.NotRunning,
            RunningProcessStatus.Running]);
        var process = new FakeProcessInspector(() => statuses.Dequeue());
        var starter = new FakeStarter();
        var service = new HoyoGameLaunchService(validator, process, starter);

        var result = service.LaunchGame("hsr", HsrRoot);

        Assert.Equal(HoyoGameLaunchStatus.Running, result.Status);
        Assert.False(result.StartedByThisCall);
        Assert.Empty(starter.Starts);
    }

    [Fact]
    public void Windows_standard_boundary_rejects_shell_unknown_and_unbounded_specifications()
    {
        var starter = new Nyx.Desktop.Infrastructure.Launching.DotNetLaunchProcessStarter();
        var knownPath = Path.Combine(HsrRoot, "StarRail.exe");

        Assert.Throws<InvalidOperationException>(() => starter.Start(new(
            knownPath, HsrRoot, [], UseShellExecute: true)));
        Assert.Throws<InvalidOperationException>(() => starter.Start(new(
            Path.Combine(HsrRoot, "unknown.exe"), HsrRoot, [], UseShellExecute: false)));
        Assert.Throws<InvalidOperationException>(() => starter.Start(new(
            knownPath, HsrRoot, ["bad\0argument"], UseShellExecute: false)));
        Assert.Throws<InvalidOperationException>(() => starter.Start(new(
            knownPath,
            HsrRoot,
            [new string('x', CustomArgumentParser.MaximumArgumentLength + 1)],
            UseShellExecute: false)));
        Assert.Throws<InvalidOperationException>(() => starter.Start(new(
            knownPath,
            HsrRoot,
            Enumerable.Repeat("x", CustomArgumentParser.MaximumArgumentCount + 1).ToArray(),
            UseShellExecute: false)));
    }

    [Fact]
    public void Elevation_request_is_sealed_and_has_no_public_constructor_or_generic_start_method()
    {
        Assert.True(typeof(ValidatedHoyoGameElevationRequest).IsSealed);
        Assert.Empty(typeof(ValidatedHoyoGameElevationRequest).GetConstructors());
        Assert.Equal(
            [typeof(ValidatedHoyoGameElevationRequest)],
            typeof(IHoyoGameElevatedProcessStarter)
                .GetMethod(nameof(IHoyoGameElevatedProcessStarter.StartValidatedHoyoGame))!
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
    }

    private static HoyoGameInspectionResult Ready(string gameId, string root) =>
        new(gameId, HoyoInspectionStatus.Ready, HoyoInspectionReason.None, root, "4.3.0");

    private sealed class Fixture
    {
        private readonly string gameId;
        private readonly string root;

        public Fixture(string gameId, string root)
        {
            this.gameId = gameId;
            this.root = root;
            Validator = new DelegateValidator((_, _) => Result ?? Ready(this.gameId, this.root));
            Process = new FakeProcessInspector(() => Running);
            Starter = new FakeStarter(() => StartException, () => ElevatedStartException);
            Service = new(Validator, Process, Starter);
        }

        public DelegateValidator Validator { get; }

        public FakeProcessInspector Process { get; }

        public FakeStarter Starter { get; }

        public HoyoGameLaunchService Service { get; }

        public HoyoGameInspectionResult? Result { get; set; }

        public RunningProcessStatus Running { get; set; }

        public Exception? StartException { get; set; }

        public Exception? ElevatedStartException { get; set; }
    }

    private sealed class DelegateValidator(Func<string, string?, HoyoGameInspectionResult> validate)
        : IHoyoGameLaunchIdentityValidator
    {
        public List<(string GameId, string? Root)> Calls { get; } = [];

        public HoyoGameInspectionResult Validate(string gameId, string? root)
        {
            Calls.Add((gameId, root));
            return validate(gameId, root);
        }
    }

    private sealed class SequencedValidator(params HoyoGameInspectionResult[] results)
        : IHoyoGameLaunchIdentityValidator
    {
        private readonly Queue<HoyoGameInspectionResult> remaining = new(results);

        public HoyoGameInspectionResult Validate(string gameId, string? root) => remaining.Dequeue();
    }

    private sealed class FakeProcessInspector(Func<RunningProcessStatus>? read = null)
        : IStrictRunningProcessInspector
    {
        public List<(string ProcessName, string Path)> Checks { get; } = [];

        public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath)
        {
            Checks.Add((processName, expectedExecutablePath));
            return read?.Invoke() ?? RunningProcessStatus.NotRunning;
        }
    }

    private sealed class FakeStarter(
        Func<Exception?>? exception = null,
        Func<Exception?>? elevatedException = null)
        : ILaunchProcessStarter, IHoyoGameElevatedProcessStarter
    {
        public List<LaunchSpecification> Starts { get; } = [];

        public List<ValidatedHoyoGameElevationRequest> ElevatedStarts { get; } = [];

        public void Start(LaunchSpecification specification)
        {
            Starts.Add(specification);
            if (exception?.Invoke() is { } failure)
            {
                throw failure;
            }
        }

        public void StartValidatedHoyoGame(ValidatedHoyoGameElevationRequest request)
        {
            ElevatedStarts.Add(request);
            if (elevatedException?.Invoke() is { } failure)
            {
                throw failure;
            }
        }
    }

    private sealed class StandardOnlyStarter(Exception failure) : ILaunchProcessStarter
    {
        public List<LaunchSpecification> Starts { get; } = [];

        public void Start(LaunchSpecification specification)
        {
            Starts.Add(specification);
            throw failure;
        }
    }
}
