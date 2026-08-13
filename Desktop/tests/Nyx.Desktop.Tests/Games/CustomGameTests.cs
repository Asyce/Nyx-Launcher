using System.Runtime.Versioning;
using System.Diagnostics;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Games;

namespace Nyx.Desktop.Tests.Games;

public sealed class CustomGameTests
{
    [Theory]
    [InlineData("evil")]
    [InlineData("gi")]
    [InlineData("custom-")]
    [InlineData("custom_bad")]
    [InlineData(" custom-good ")]
    [InlineData("custom-good!")]
    public void Every_custom_game_boundary_rejects_noncanonical_ids(string id)
    {
        var probe = new FakeProbe();
        var validation = CustomGameValidator.Validate(
            new CustomGameDraft(
                "Game",
                @"C:\Games\game.exe",
                @"C:\Games\icon.png",
                Id: id),
            probe: probe);

        Assert.False(CustomGameId.IsValid(id));
        Assert.Equal(CustomGameValidationError.InvalidId, validation.Error);
        Assert.Throws<ArgumentException>(() => new CustomGameSessionAdapter(
            new CustomGameDefinition
            {
                Id = id,
                Name = "Game",
                ExecutablePath = @"C:\Games\game.exe",
                IconPath = @"C:\Games\icon.png",
            },
            new FakeInspector(),
            new FakeStarter(),
            probe));
    }

    [Fact]
    public void Validator_rejects_shell_syntax_and_duplicate_canonical_executable()
    {
        var probe = new FakeProbe();
        var existing = new CustomGameDefinition
        {
            Id = "custom-old", Name = "Old", ExecutablePath = @"C:\Games\game.exe", IconPath = @"C:\Games\old.png",
        };
        var draft = new CustomGameDraft("New", @"C:\Games\game.exe", @"C:\Games\icon.png", RawArguments: "--safe & whoami");
        var unsafeResult = CustomGameValidator.Validate(draft, [existing], probe);
        Assert.Equal(CustomGameValidationError.UnsafeArguments, unsafeResult.Error);

        var duplicateResult = CustomGameValidator.Validate(draft with { RawArguments = "--safe" }, [existing], probe);
        Assert.Equal(CustomGameValidationError.DuplicateExecutable, duplicateResult.Error);
    }

    [Fact]
    public void Validator_requires_absolute_exe_and_local_assets_and_generates_stable_id()
    {
        var probe = new FakeProbe();
        var relative = CustomGameValidator.Validate(
            new CustomGameDraft("Game", "game.exe", @"C:\Games\icon.png"), probe: probe);
        Assert.Equal(CustomGameValidationError.ExecutableNotAbsoluteLocalPath, relative.Error);

        var valid = CustomGameValidator.Validate(
            new CustomGameDraft(" Game ", @"C:\Games\game.exe", @"C:\Games\icon.png", RawArguments: "--name \"hello world\""),
            probe: probe);
        Assert.True(valid.IsValid);
        Assert.StartsWith("custom-", valid.Game!.Id, StringComparison.Ordinal);
        Assert.Equal("Game", valid.Game.Name);
        Assert.Equal(["--name", "hello world"], CustomArgumentParser.Parse(valid.Game.RawArguments));
    }

    [Fact]
    public void Validator_rejects_a_reparse_point_in_any_parent_component()
    {
        var probe = new FakeProbe();
        probe.ReparsePaths.Add(@"C:\Games");

        var result = CustomGameValidator.Validate(
            new CustomGameDraft("Game", @"C:\Games\Nested\game.exe", @"C:\Games\Nested\icon.png"),
            probe: probe);

        Assert.Equal(CustomGameValidationError.ReparsePoint, result.Error);
        Assert.Contains(@"C:\Games", probe.InspectedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Custom_adapter_revalidates_immediately_before_start_and_fails_closed()
    {
        var probe = new FakeProbe
        {
            ReparseOnExactPathInspection = 2,
            ReparseTarget = @"C:\Games\game.exe",
        };
        var inspector = new FakeInspector { Presence = ExactProcessPresence.Absent };
        var starter = new FakeStarter();
        var game = new CustomGameDefinition
        {
            Id = "custom-1",
            Name = "Game",
            ExecutablePath = @"C:\Games\game.exe",
            IconPath = @"C:\Games\icon.png",
        };
        var adapter = new CustomGameSessionAdapter(game, inspector, starter, probe);

        var result = await adapter.RequestValidatedLaunchAsync(CancellationToken.None);

        Assert.Equal(GameLaunchDispatchStatus.NeedsReview, result.Status);
        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Loaded_state_with_an_unsafe_parent_is_not_registered()
    {
        var probe = new FakeProbe();
        probe.ReparsePaths.Add(@"C:\Games");
        var game = new CustomGameDefinition
        {
            Id = "custom-loaded",
            Name = "Loaded",
            ExecutablePath = @"C:\Games\game.exe",
            IconPath = @"C:\Games\icon.png",
        };

        var registered = CustomGameSessionFactory.TryCreateValidated(game, out var adapter, probe);

        Assert.False(registered);
        Assert.Null(adapter);
    }

    [Fact]
    public async Task Custom_adapter_reports_exact_presence_and_suppresses_duplicate_start()
    {
        var exe = Path.Combine(Path.GetTempPath(), "nyx-custom-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(exe, "stub");
        try
        {
            var inspector = new FakeInspector { Presence = ExactProcessPresence.Present };
            var starter = new FakeStarter();
            var game = new CustomGameDefinition { Id = "custom-1", Name = "Game", ExecutablePath = exe, IconPath = exe };
            var adapter = new CustomGameSessionAdapter(game, inspector, starter);

            var evidence = await adapter.ObserveSessionAsync(CancellationToken.None);
            Assert.Equal(LocalReadinessEvidence.Ready, evidence.Readiness);
            Assert.Equal(ExactProcessPresence.Present, evidence.Overall);
            var result = await adapter.RequestValidatedLaunchAsync(CancellationToken.None);
            Assert.Equal(GameLaunchDispatchStatus.Accepted, result.Status);
            Assert.Equal(0, starter.Starts);

            inspector.Presence = ExactProcessPresence.Absent;
            result = await adapter.RequestValidatedLaunchAsync(CancellationToken.None);
            Assert.Equal(GameLaunchDispatchStatus.Accepted, result.Status);
            Assert.Equal(1, starter.Starts);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public async Task Moved_custom_game_stays_a_repairable_needs_review_session()
    {
        var probe = new FakeProbe { FilesExist = false };
        var starter = new FakeStarter();
        var game = new CustomGameDefinition
        {
            Id = "custom-moved",
            Name = "Moved",
            ExecutablePath = @"C:\Games\moved.exe",
            IconPath = @"C:\Games\icon.png",
        };
        var adapter = new CustomGameSessionAdapter(
            game,
            new FakeInspector { Presence = ExactProcessPresence.Absent },
            starter,
            probe);

        var observation = await adapter.ObserveSessionAsync(CancellationToken.None);
        var launch = await adapter.RequestValidatedLaunchAsync(CancellationToken.None);

        Assert.Equal(LocalReadinessEvidence.NeedsReview, observation.Readiness);
        Assert.Equal(GameLaunchDispatchStatus.NeedsReview, launch.Status);
        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    public void Startup_and_selection_keep_unusable_custom_entries_visible_without_missing_session_lookups()
    {
        var root = FindWorkspaceRoot();
        var app = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "App.xaml.cs"));
        var page = File.ReadAllText(Path.Combine(root, "Desktop", "src", "Nyx.Desktop.App", "MainPage.xaml.cs"));

        Assert.Contains(".Select(static game => CustomGameSessionFactory.Create(game))", app, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCreateValidated(game", app, StringComparison.Ordinal);
        Assert.Contains("sessions.TryGetSnapshot(selected.Id", page, StringComparison.Ordinal);
        Assert.Contains("SynchronizeCustomSessions(launcherState.Snapshot)", page, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void File_and_parent_identity_lease_is_held_through_normal_and_elevated_dispatch(bool elevated)
    {
        var lease = new FakeLaunchLease();
        var dispatcher = new InspectingDispatcher(() => Assert.False(lease.Disposed));
        var starter = new WindowsCustomGameProcessStarter(
            new FakeLaunchLeaseFactory(lease),
            dispatcher);

        starter.Start(@"C:\Games\game.exe", ["--safe"], elevated);

        Assert.True(lease.Disposed);
        Assert.Equal(elevated, dispatcher.StartInfo!.UseShellExecute);
        Assert.Equal(elevated ? "runas" : string.Empty, dispatcher.StartInfo.Verb);
        Assert.Equal(["--safe"], dispatcher.StartInfo.ArgumentList);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Swap_detected_while_acquiring_launch_identity_never_reaches_process_dispatch()
    {
        var dispatcher = new InspectingDispatcher();
        var starter = new WindowsCustomGameProcessStarter(
            new ThrowingLaunchLeaseFactory(),
            dispatcher);

        Assert.Throws<IOException>(() => starter.Start(
            @"C:\Games\game.exe",
            Array.Empty<string>(),
            requestAdministrator: true));
        Assert.Null(dispatcher.StartInfo);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_launch_lease_blocks_executable_and_parent_replacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-custom-lease-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "Game");
        var executable = Path.Combine(parent, "game.exe");
        try
        {
            Directory.CreateDirectory(parent);
            File.WriteAllText(executable, "stub");
            using var lease = new WindowsCustomGameLaunchLeaseFactory().Acquire(executable);

            Assert.ThrowsAny<IOException>(() => File.Move(executable, executable + ".moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(parent, parent + ".moved"));
            Assert.Equal("stub", File.ReadAllText(executable));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeProbe : ICustomGamePathProbe
    {
        private int exactPathInspections;

        public HashSet<string> ReparsePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> InspectedPaths { get; } = [];
        public int? ReparseOnExactPathInspection { get; init; }
        public string? ReparseTarget { get; init; }

        public bool FilesExist { get; init; } = true;

        public bool FileExists(string path) => FilesExist;
        public bool DirectoryExists(string path) => false;
        public bool IsReparsePoint(string path)
        {
            InspectedPaths.Add(path);
            if (ReparsePaths.Contains(path)) return true;
            if (ReparseTarget is not null
                && string.Equals(path, ReparseTarget, StringComparison.OrdinalIgnoreCase)
                && ++exactPathInspections == ReparseOnExactPathInspection)
            {
                return true;
            }

            return false;
        }
        public string GetCanonicalPath(string path) => path.Replace('/', '\\');
    }

    private sealed class FakeInspector : ICustomGameProcessInspector
    {
        public ExactProcessPresence Presence { get; set; }
        public ExactProcessPresence Check(string executablePath) => Presence;
    }

    private sealed class FakeStarter : ICustomGameProcessStarter
    {
        public int Starts { get; private set; }
        public void Start(string executablePath, IReadOnlyList<string> arguments, bool requestAdministrator) => Starts++;
    }

    private sealed class FakeLaunchLease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeLaunchLeaseFactory(IDisposable lease) : ICustomGameLaunchLeaseFactory
    {
        public IDisposable Acquire(string executablePath) => lease;
    }

    private sealed class ThrowingLaunchLeaseFactory : ICustomGameLaunchLeaseFactory
    {
        public IDisposable Acquire(string executablePath) =>
            throw new IOException("identity changed");
    }

    private sealed class InspectingDispatcher(Action? beforeStart = null) : ICustomGameProcessDispatcher
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public void Start(ProcessStartInfo startInfo)
        {
            beforeStart?.Invoke();
            StartInfo = startInfo;
        }
    }

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src", "Nyx.Desktop.App")))
                return current.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
