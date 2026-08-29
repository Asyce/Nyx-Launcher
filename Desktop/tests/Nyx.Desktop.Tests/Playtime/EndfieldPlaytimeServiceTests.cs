using System.Globalization;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Playtime;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Playtime;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.Playtime;

public sealed class EndfieldPlaytimeServiceTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Default_limits_and_root_are_fixed()
    {
        Assert.Equal(32, EndfieldPlaytimeScanLimits.Default.MaximumFiles);
        Assert.Equal(64L * 1024 * 1024, EndfieldPlaytimeScanLimits.Default.MaximumBytes);
        Assert.Equal(1_000_000, EndfieldPlaytimeScanLimits.Default.MaximumLines);
        Assert.Equal(TimeSpan.FromSeconds(10), EndfieldPlaytimeScanLimits.Default.MaximumTime);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "LocalLow",
                "Gryphline"),
            EndfieldPlaytimeService.DefaultLogRoot);
    }

    [Fact]
    public async Task Scan_uses_newest_files_when_injected_file_limit_is_small()
    {
        using var temp = new TempDirectory();
        var saves = new List<EndfieldPlaytimeState>();
        var limits = Limits(maximumFiles: 2);
        using var rig = await TestRig.CreateAsync(saves, limits);
        WriteLog(temp.Root, "games-old.log", UtcAt(2026, 8, 1, 10, 1),
            GameplayPair(8, 1, 9, 9, 30));
        WriteLog(temp.Root, "games-middle.log", UtcAt(2026, 8, 2, 10, 1),
            GameplayPair(8, 2, 9, 9, 30));
        WriteLog(temp.Root, "games-new.log", UtcAt(2026, 8, 3, 10, 1),
            GameplayPair(8, 3, 9, 9, 30));

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Capped, snapshot.ScanStatus);
        Assert.Equal(2, snapshot.ScannedFiles);
        var intervals = Assert.Single(saves).Intervals;
        Assert.Equal(2, intervals.Count);
        Assert.DoesNotContain(intervals, value => value.StartUtc == UtcAt(2026, 8, 1, 9));
        Assert.Contains(intervals, value => value.StartUtc == UtcAt(2026, 8, 2, 9));
        Assert.Contains(intervals, value => value.StartUtc == UtcAt(2026, 8, 3, 9));
    }

    [Fact]
    public async Task Scan_stops_before_a_file_that_exceeds_byte_cap()
    {
        using var temp = new TempDirectory();
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            Limits(maximumBytes: 1));
        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 12),
            GameplayPair(8, 29, 10, 11));

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Capped, snapshot.ScanStatus);
        Assert.Equal(0, snapshot.ScannedFiles);
        Assert.Empty(Assert.Single(saves).Intervals);
    }

    [Fact]
    public async Task Scan_stops_without_accepting_a_pair_after_line_cap()
    {
        using var temp = new TempDirectory();
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            Limits(maximumLines: 2));
        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 12),
            GameplayPair(8, 29, 10, 11));

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Capped, snapshot.ScanStatus);
        Assert.Equal(0, snapshot.ScannedFiles);
        Assert.Empty(Assert.Single(saves).Intervals);
    }

    [Fact]
    public async Task Scan_stops_at_a_safely_tiny_time_cap()
    {
        using var temp = new TempDirectory();
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            Limits(maximumTime: TimeSpan.FromTicks(1)));
        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 12),
            GameplayPair(8, 29, 10, 11));

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Capped, snapshot.ScanStatus);
        Assert.Empty(Assert.Single(saves).Intervals);
    }

    [Fact]
    public async Task Scan_skips_child_reparse_points_and_rejects_reparse_root_when_supported()
    {
        using var root = new TempDirectory();
        using var target = new TempDirectory();
        WriteLog(target.Root, "games.log", UtcAt(2026, 8, 29, 12),
            GameplayPair(8, 29, 10, 11));
        var childLink = Path.Combine(root.Root, "linked");
        if (!TryCreateDirectorySymlink(childLink, target.Root)) return;

        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(saves);
        var childSnapshot = await rig.ScanAsync(root.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Empty, childSnapshot.ScanStatus);
        Assert.Equal(0, childSnapshot.ScannedFiles);
        Assert.Empty(Assert.Single(saves).Intervals);

        Directory.Delete(childLink);
        var rootLink = Path.Combine(root.Root, "root-link");
        if (!TryCreateDirectorySymlink(rootLink, target.Root)) return;

        var rootSnapshot = await rig.ScanAsync(rootLink);

        Assert.Equal(EndfieldPlaytimeScanStatus.Corrupt, rootSnapshot.ScanStatus);
        Assert.Equal(0, rootSnapshot.ScannedFiles);
        Directory.Delete(rootLink);
    }

    [Fact]
    public async Task Scan_accepts_only_games_log_files()
    {
        using var temp = new TempDirectory();
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(saves);
        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 28, 12),
            GameplayPair(8, 28, 10, 11));
        WriteLog(temp.Root, "games-rotated.log", UtcAt(2026, 8, 29, 12),
            GameplayPair(8, 29, 10, 11));
        WriteLog(temp.Root, "game.log", UtcAt(2026, 8, 27, 12),
            GameplayPair(8, 27, 10, 11));
        WriteLog(temp.Root, "games.txt", UtcAt(2026, 8, 26, 12),
            GameplayPair(8, 26, 10, 11));
        WriteLog(temp.Root, "games.log.bak", UtcAt(2026, 8, 25, 12),
            GameplayPair(8, 25, 10, 11));
        WriteLog(temp.Root, "notgames.log", UtcAt(2026, 8, 24, 12),
            GameplayPair(8, 24, 10, 11));

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Normal, snapshot.ScanStatus);
        Assert.Equal(2, snapshot.ScannedFiles);
        var intervals = Assert.Single(saves).Intervals;
        Assert.Equal(2, intervals.Count);
        Assert.Contains(intervals, value => value.StartUtc == UtcAt(2026, 8, 28, 10));
        Assert.Contains(intervals, value => value.StartUtc == UtcAt(2026, 8, 29, 10));
    }

    [Fact]
    public async Task Scan_can_read_a_file_open_for_game_writes()
    {
        using var temp = new TempDirectory();
        var path = WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 12),
            GameplayPair(8, 29, 10, 11));
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(saves);
        using var gameWriter = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        gameWriter.Flush(flushToDisk: true);

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Normal, snapshot.ScanStatus);
        Assert.Equal(1, snapshot.ScannedFiles);
        Assert.Single(Assert.Single(saves).Intervals);
    }

    [Fact]
    public async Task Fully_read_files_are_kept_when_a_later_file_hits_line_cap()
    {
        using var temp = new TempDirectory();
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            Limits(maximumLines: 3));
        WriteLog(temp.Root, "games-complete.log", UtcAt(2026, 8, 2, 12),
            GameplayPair(8, 2, 10, 11));
        WriteLog(
            temp.Root,
            "games-incomplete.log",
            UtcAt(2026, 8, 1, 12),
            "[08-01 10:00:00.000] Create game process Endfield.exe");

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Capped, snapshot.ScanStatus);
        Assert.Equal(1, snapshot.ScannedFiles);
        var intervals = Assert.Single(saves).Intervals;
        Assert.Single(intervals);
        Assert.Equal(UtcAt(2026, 8, 2, 10), intervals[0].StartUtc);
    }

    [Fact]
    public async Task Empty_and_corrupt_scans_are_distinct_and_warnings_are_sanitized()
    {
        using var temp = new TempDirectory();
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(saves);

        var empty = await rig.ScanAsync(temp.Root);
        Assert.Equal(EndfieldPlaytimeScanStatus.Empty, empty.ScanStatus);

        var missing = await rig.ScanAsync(Path.Combine(temp.Root, "missing"));
        Assert.Equal(EndfieldPlaytimeScanStatus.Corrupt, missing.ScanStatus);
        Assert.Equal(1, missing.Warnings.UnreadableFiles);

        const string rawLine = "[not-a-timestamp] Create game process Endfield.exe";
        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 12), rawLine);
        var malformed = await rig.ScanAsync(temp.Root);

        Assert.Equal(EndfieldPlaytimeScanStatus.Empty, malformed.ScanStatus);
        Assert.Equal(1, malformed.Warnings.RejectedMarkers);
        Assert.Contains(nameof(EndfieldPlaytimeSnapshot), malformed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(rawLine, malformed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Root, malformed.ToString(), StringComparison.Ordinal);
        Assert.Equal(nameof(EndfieldPlaytimeWarnings), malformed.Warnings.ToString());
        Assert.DoesNotContain(rawLine, malformed.Statistics.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Root, malformed.Statistics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_reuses_a_selected_root_only_in_memory()
    {
        using var temp = new TempDirectory();
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(saves);
        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 12),
            GameplayPair(8, 29, 10, 11));

        await rig.ScanAsync(temp.Root);
        WriteLog(temp.Root, "games-second.log", UtcAt(2026, 8, 30, 12),
            GameplayPair(8, 30, 10, 11));
        await rig.ScanAsync(null);

        Assert.Equal(2, saves.Count);
        Assert.Equal(2, saves[^1].Intervals.Count);
        var serialized = LauncherStateMigrations.Write(new LauncherState
        {
            EndfieldPlaytime = saves[^1],
        });
        Assert.DoesNotContain(temp.Root, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scan_reports_save_failure_without_adopting_unsaved_data()
    {
        using var temp = new TempDirectory();
        var attempts = 0;
        using var rig = await TestRig.CreateAsync(
            persist: _ =>
            {
                attempts++;
                return false;
            });
        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 12),
            GameplayPair(8, 29, 10, 11));

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.Equal(1, attempts);
        Assert.True(snapshot.SaveFailed);
        Assert.Equal(EndfieldPlaytimeScanStatus.Normal, snapshot.ScanStatus);
        Assert.Equal(0, snapshot.Statistics.Gameplay.Sessions);
        Assert.False(snapshot.HasPendingSession);
    }

    [Fact]
    public async Task First_running_transition_after_confirmed_absence_starts_one_pending_session()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            aeEvidence: [AbsentEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var snapshot = await rig.RefreshAsync();

        Assert.True(snapshot.IsRunning);
        Assert.True(snapshot.HasPendingSession);
        var pending = Assert.Single(saves).PendingStart;
        Assert.NotNull(pending);
        Assert.Equal(clock.GetUtcNow(), pending.StartedAt);
        Assert.Equal(Utc.Id, pending.TimeZoneId);
    }

    [Fact]
    public async Task Failed_start_save_retries_the_exact_boundary_and_keeps_the_full_session()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            persist: state =>
            {
                attempts++;
                if (attempts == 1) return false;
                saves.Add(state);
                return true;
            },
            aeEvidence: [AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        var exactStart = clock.GetUtcNow();
        var failed = await rig.RefreshAsync();
        Assert.True(failed.SaveFailed);
        Assert.False(failed.HasPendingSession);

        clock.Advance(TimeSpan.FromMinutes(2));
        var recovered = await rig.RefreshAsync();
        Assert.False(recovered.SaveFailed);
        Assert.Equal(exactStart, Assert.Single(saves).PendingStart!.StartedAt);

        clock.Advance(TimeSpan.FromMinutes(8));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.False(closed.HasPendingSession);
        var interval = Assert.Single(saves[^1].Intervals);
        Assert.Equal(exactStart, interval.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), interval.Duration);
    }

    [Fact]
    public async Task Startup_mid_session_does_not_override_the_later_complete_log_interval()
    {
        using var temp = new TempDirectory();
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            aeEvidence: [RuntimeEvidence, AbsentEvidence, AbsentEvidence],
            timeProvider: clock);

        var running = await rig.RefreshAsync();
        Assert.True(running.IsRunning);
        Assert.False(running.HasPendingSession);

        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();
        Assert.False(closed.HasPendingSession);
        Assert.Equal(0, closed.Statistics.Gameplay.Sessions);

        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 12, 11),
            "[08-29 10:00:00.000] Create game process Endfield.exe",
            "[08-29 12:10:00.000] Child process exits");
        var scanned = await rig.ScanAsync(temp.Root);

        Assert.Equal(1, scanned.Statistics.Gameplay.Sessions);
        var interval = Assert.Single(saves).Intervals.Single();
        Assert.Equal(UtcAt(2026, 8, 29, 10), interval.StartUtc);
        Assert.Equal(UtcAt(2026, 8, 29, 12, 10), interval.EndUtc);
    }

    [Fact]
    public async Task Uncertain_observation_neither_starts_nor_ends_a_pending_session()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var started = clock.GetUtcNow().AddMinutes(-5);
        var initial = PendingState(started);
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            initialState: initial,
            aeEvidence: [UncertainEvidence],
            timeProvider: clock);

        var snapshot = await rig.RefreshAsync();

        Assert.False(snapshot.IsRunning);
        Assert.True(snapshot.HasPendingSession);
        Assert.Empty(saves);
        Assert.Equal(0, snapshot.Statistics.Gameplay.Sessions);
    }

    [Fact]
    public async Task Confirmed_absence_commits_using_the_coordinator_close_boundary()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            aeEvidence: [AbsentEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.False(closed.IsRunning);
        Assert.False(closed.HasPendingSession);
        var committed = saves[^1];
        var interval = Assert.Single(committed.Intervals);
        Assert.Equal(UtcAt(2026, 8, 29, 12), interval.StartUtc);
        Assert.Equal(UtcAt(2026, 8, 29, 12, 10), interval.EndUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), interval.Duration);
    }

    [Fact]
    public async Task Failed_close_save_retries_the_same_confirmed_end()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            persist: state =>
            {
                attempts++;
                if (attempts == 2) return false;
                saves.Add(state);
                return true;
            },
            aeEvidence: [AbsentEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var failed = await rig.RefreshAsync();

        Assert.True(failed.SaveFailed);
        Assert.True(failed.HasPendingSession);
        clock.Advance(TimeSpan.FromMinutes(5));
        var recovered = await rig.RefreshAsync();

        Assert.False(recovered.SaveFailed);
        Assert.False(recovered.HasPendingSession);
        Assert.Equal(3, attempts);
        var committed = Assert.Single(saves[^1].Intervals);
        Assert.Equal(UtcAt(2026, 8, 29, 12, 10), committed.EndUtc);
    }

    [Fact]
    public async Task Restart_while_running_keeps_the_existing_pending_session()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var started = clock.GetUtcNow().AddMinutes(-5);
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            initialState: PendingState(started),
            aeEvidence: [RuntimeEvidence],
            timeProvider: clock);

        var snapshot = await rig.RefreshAsync();

        Assert.True(snapshot.IsRunning);
        Assert.True(snapshot.HasPendingSession);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task Restart_while_absent_does_not_invent_an_end_time()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var started = clock.GetUtcNow().AddMinutes(-5);
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            initialState: PendingState(started),
            aeEvidence: [AbsentEvidence],
            timeProvider: clock);

        var snapshot = await rig.RefreshAsync();

        Assert.False(snapshot.IsRunning);
        Assert.True(snapshot.HasPendingSession);
        Assert.Empty(saves);
        Assert.Equal(0, snapshot.Statistics.Gameplay.Sessions);
    }

    [Fact]
    public async Task Restart_pending_confirmed_absent_is_not_reused_for_a_later_launch()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var started = clock.GetUtcNow().AddHours(-2);
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            initialState: PendingState(started),
            aeEvidence: [AbsentEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        var closed = await rig.RefreshAsync();

        Assert.True(closed.HasPendingSession);
        Assert.Equal(0, closed.Statistics.Gameplay.Sessions);
        Assert.Empty(saves);
    }

    [Fact]
    public async Task Later_matching_history_reconciles_a_restart_pending_session()
    {
        using var temp = new TempDirectory();
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var started = UtcAt(2026, 8, 29, 10);
        var saves = new List<EndfieldPlaytimeState>();
        using var rig = await TestRig.CreateAsync(
            saves,
            initialState: PendingState(started),
            timeProvider: clock);
        WriteLog(temp.Root, "games.log", UtcAt(2026, 8, 29, 10, 31),
            "[08-29 10:00:30.000] Create game process Endfield.exe",
            "[08-29 10:30:00.000] Child process exits");

        var snapshot = await rig.ScanAsync(temp.Root);

        Assert.False(snapshot.HasPendingSession);
        Assert.Equal(1, snapshot.Statistics.Gameplay.Sessions);
        var reconciled = Assert.Single(saves).Intervals;
        var interval = Assert.Single(reconciled);
        Assert.Equal(started, interval.StartUtc);
        Assert.Equal(UtcAt(2026, 8, 29, 10, 30), interval.EndUtc);
    }

    private static EndfieldPlaytimeState PendingState(DateTimeOffset startedAt) => new()
    {
        PendingStart = new()
        {
            StartedAt = startedAt,
            TimeZoneId = Utc.Id,
        },
    };

    private static EndfieldPlaytimeScanLimits Limits(
        int maximumFiles = 32,
        long maximumBytes = 64L * 1024 * 1024,
        long maximumLines = 1_000_000,
        TimeSpan? maximumTime = null) => new(
            maximumFiles,
            maximumBytes,
            maximumLines,
            maximumTime ?? TimeSpan.FromSeconds(10));

    private static string WriteLog(
        string root,
        string name,
        DateTimeOffset lastWriteUtc,
        params string[] lines)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
        return path;
    }

    private static string GameplayPair(
        int month,
        int day,
        int startHour,
        int endHour,
        int endMinute = 0) => string.Join(
            Environment.NewLine,
            $"[{month:00}-{day:00} {startHour:00}:00:00.000] Create game process Endfield.exe",
            $"[{month:00}-{day:00} {endHour:00}:{endMinute:00}:00.000] Child process exits");

    private static DateTimeOffset UtcAt(
        int year,
        int month,
        int day,
        int hour,
        int minute = 0,
        int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static bool TryCreateDirectorySymlink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return Directory.Exists(path);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static readonly GameSessionEvidence RuntimeEvidence = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Absent,
        ExactProcessPresence.Present);

    private static readonly GameSessionEvidence UncertainEvidence = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Uncertain,
        ExactProcessPresence.Absent);

    private static readonly GameSessionEvidence AbsentEvidence =
        GameSessionEvidence.ReadyAndAbsent;

    private sealed class TestRig : IDisposable
    {
        private readonly GameSessionCoordinator coordinator;
        private readonly GameSessionRefreshPump pump;
        private readonly EndfieldPlaytimeService service;

        private TestRig(
            GameSessionCoordinator coordinator,
            GameSessionRefreshPump pump,
            EndfieldPlaytimeService service)
        {
            this.coordinator = coordinator;
            this.pump = pump;
            this.service = service;
        }

        public static Task<TestRig> CreateAsync(
            List<EndfieldPlaytimeState>? saves = null,
            EndfieldPlaytimeScanLimits? limits = null,
            EndfieldPlaytimeState? initialState = null,
            Func<EndfieldPlaytimeState, bool>? persist = null,
            GameSessionEvidence[]? aeEvidence = null,
            TimeProvider? timeProvider = null)
        {
            persist ??= state =>
            {
                saves?.Add(state);
                return true;
            };
            var adapters = GameCatalog.All
                .Select(game => game.Id == "ae"
                    ? new SequenceAdapter(game.Id, aeEvidence ?? Array.Empty<GameSessionEvidence>())
                    : new SequenceAdapter(game.Id, Array.Empty<GameSessionEvidence>()))
                .Cast<IGameSessionAdapter>()
                .ToArray();
            var coordinator = new GameSessionCoordinator(
                adapters,
                timeProvider,
                startupTimeout: TimeSpan.FromSeconds(10),
                adapterCallTimeout: TimeSpan.FromSeconds(2),
                absenceConfirmationInterval: TimeSpan.FromSeconds(1));
            var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));
            var service = limits is null
                ? new EndfieldPlaytimeService(
                    initialState ?? new(),
                    persist,
                    pump,
                    timeProvider,
                    Utc)
                : new EndfieldPlaytimeService(
                    initialState ?? new(),
                    persist,
                    pump,
                    timeProvider,
                    Utc,
                    limits);
            return Task.FromResult(new TestRig(coordinator, pump, service));
        }

        public Task<EndfieldPlaytimeSnapshot> ScanAsync(string? root = null) =>
            service.ScanAsync(root);

        public Task<EndfieldPlaytimeSnapshot> RefreshAsync() =>
            pump.RefreshNowAsync().AsTask().ContinueWith(
                _ => service.Current,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        public void Dispose()
        {
            service.Dispose();
            pump.DisposeAsync().AsTask().GetAwaiter().GetResult();
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class SequenceAdapter(
        string gameId,
        IEnumerable<GameSessionEvidence> observations) : IGameSessionAdapter
    {
        private readonly Queue<GameSessionEvidence> observations = new(observations);

        public string GameId { get; } = gameId;

        public ValueTask<GameSessionEvidence> ObserveSessionAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(observations.Count > 0
                ? observations.Dequeue()
                : GameSessionEvidence.ReadyAndAbsent);
        }

        public ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GameLaunchDispatchResult.Accepted);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Root = Directory.CreateTempSubdirectory("nyx-playtime-tests-").FullName;

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
