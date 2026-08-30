using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Playtime;
using Nyx.Desktop.Infrastructure.Sessions;
using Nyx.Desktop.Infrastructure.State;

namespace Nyx.Desktop.Tests.Playtime;

public sealed class GamePlaytimeServiceTests
{
    [Fact]
    public async Task External_runtime_is_excluded_but_an_accepted_Nyx_launch_is_counted()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, RuntimeEvidence, AbsentEvidence, AbsentEvidence, AbsentEvidence, RuntimeEvidence, AbsentEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await rig.RefreshAsync();
        Assert.Equal(TimeSpan.Zero, rig.Service.Current("ae").Total);

        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();

        Assert.Equal(TimeSpan.FromMinutes(10), rig.Service.Current("ae").Total);
    }

    [Fact]
    public async Task Already_running_dispatch_does_not_create_a_Nyx_playtime_session()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, AbsentEvidence],
            dispatchResult: GameLaunchDispatchResult.AlreadyRunning,
            timeProvider: clock);

        await rig.RefreshAsync();
        var result = await rig.RequestLaunchResultAsync();

        Assert.Equal(GameLaunchRequestOutcome.AlreadyRunning, result.Outcome);
        Assert.False(result.Snapshot.CurrentSessionLaunchedByNyx);
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();

        Assert.Equal(TimeSpan.Zero, rig.Service.Current("ae").Total);
        Assert.False(rig.Service.Current("ae").IsTracking);
    }

    [Fact]
    public async Task Slow_unrelated_game_does_not_extend_a_fast_games_playtime_sample()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, AbsentEvidence],
            timeProvider: clock);
        Assert.True(rig.Register(new SequenceAdapter(
            "custom-slow",
            [AbsentEvidence, AbsentEvidence, AbsentEvidence],
            beforeObserve: async cancellationToken =>
            {
                await Task.Delay(50, cancellationToken);
                clock.Advance(TimeSpan.FromMinutes(5));
            })));

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        await rig.RefreshAsync();
        await rig.RefreshAsync();

        Assert.Equal(TimeSpan.FromMinutes(5), rig.Service.Current("ae").Total);
    }

    [Fact]
    public async Task Two_Nyx_launched_games_track_independent_totals()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            observationsByGame: new Dictionary<string, GameSessionEvidence[]>(StringComparer.Ordinal)
            {
                ["ae"] = [AbsentEvidence, AbsentEvidence, RuntimeEvidence, AbsentEvidence],
                ["gi"] = [AbsentEvidence, AbsentEvidence, RuntimeEvidence, AbsentEvidence],
            },
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync("ae");
        await rig.RequestNyxLaunchAsync("gi");
        clock.Advance(TimeSpan.FromMinutes(1));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(7));
        await rig.RefreshAsync();

        Assert.Equal(TimeSpan.FromMinutes(7), rig.Service.Current("ae").Total);
        Assert.Equal(TimeSpan.FromMinutes(7), rig.Service.Current("gi").Total);
        Assert.Equal(TimeSpan.Zero, rig.Service.Current("hsr").Total);
    }

    [Fact]
    public async Task Uncertainty_closes_at_last_confirmed_time_and_never_reclaims_the_process()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence, UncertainEvidence, RuntimeEvidence, AbsentEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        await rig.RefreshAsync();
        Assert.False(rig.Service.Current("ae").IsTracking);
        Assert.Equal(TimeSpan.FromMinutes(3), rig.Service.Current("ae").Total);

        clock.Advance(TimeSpan.FromMinutes(4));
        await rig.RefreshAsync();
        Assert.False(rig.Service.Current("ae").IsTracking);

        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        Assert.False(rig.Service.Current("ae").IsTracking);
        Assert.Equal(TimeSpan.FromMinutes(3), rig.Service.Current("ae").Total);
    }

    [Fact]
    public async Task Runtime_loss_behind_a_bootstrap_ends_Nyx_ownership()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence, BootstrapEvidence, RuntimeEvidence, AbsentEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await rig.RefreshAsync();

        Assert.Equal(TimeSpan.FromMinutes(4), rig.Service.Current("ae").Total);

        clock.Advance(TimeSpan.FromMinutes(4));
        await rig.RefreshAsync();
        Assert.False(rig.Service.Current("ae").IsTracking);
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        Assert.Equal(TimeSpan.FromMinutes(4), rig.Service.Current("ae").Total);
    }

    [Fact]
    public async Task Resume_clears_ownership_without_counting_the_suspended_gap()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence, AbsentEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.ResetAfterResumeAndRefreshAsync();

        Assert.False(rig.Service.Current("ae").IsTracking);
        Assert.Equal(TimeSpan.Zero, rig.Service.Current("ae").Total);

        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();
        Assert.Equal(TimeSpan.Zero, rig.Service.Current("ae").Total);
    }

    [Fact]
    public async Task Suspend_closes_at_last_confirmed_and_ignores_the_gap_until_reset_publication()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var persistAttempts = 0;
        using var rig = TestRig.Create(
            observations:
            [
                AbsentEvidence,
                AbsentEvidence,
                RuntimeEvidence,
                RuntimeEvidence,
                RuntimeEvidence,
                AbsentEvidence,
            ],
            timeProvider: clock,
            persist: _ =>
            {
                persistAttempts++;
                return true;
            });

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        Assert.Equal(180, rig.Service.Current("ae").TotalSeconds);

        Assert.True(rig.Suspend());
        Assert.False(rig.Service.Current("ae").IsTracking);
        Assert.Equal(180, rig.Service.Current("ae").TotalSeconds);
        Assert.Equal(0, persistAttempts);

        clock.Advance(TimeSpan.FromHours(1));
        await rig.RefreshAsync();
        Assert.Equal(180, rig.Service.Current("ae").TotalSeconds);
        Assert.Equal(0, persistAttempts);

        Assert.True(rig.Resume());
        await rig.RefreshAsync();
        Assert.Equal(180, rig.Service.Current("ae").TotalSeconds);
        Assert.Equal(1, persistAttempts);
    }

    [Fact]
    public void Registration_failure_hides_totals_and_disables_tracking_fail_closed()
    {
        using var rig = TestRig.Create(
            initialTotals: new Dictionary<string, long> { ["ae"] = 123 });

        rig.Service.DisableTracking();

        var current = rig.Service.Current("ae");
        Assert.False(current.TrackingAvailable);
        Assert.False(current.IsTracking);
        Assert.Equal(123, current.TotalSeconds);
    }

    [Fact]
    public async Task Missing_snapshot_closes_at_last_confirmed_sample()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(timeProvider: clock);
        var custom = new SequenceAdapter(
            "custom-missing",
            [AbsentEvidence, AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence]);
        Assert.True(rig.Register(custom));

        await rig.RefreshAsync();
        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync("custom-missing");
        clock.Advance(TimeSpan.FromMinutes(4));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        Assert.True(rig.Service.Current("custom-missing").IsTracking);
        rig.Remove("custom-missing");
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();

        Assert.Equal(TimeSpan.FromMinutes(3), rig.Service.Current("custom-missing").Total);
    }

    [Fact]
    public async Task Custom_definition_validation_failure_closes_at_last_confirmed_runtime_sample()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var probe = new MutableCustomPathProbe();
        var inspector = new MutableCustomInspector { Presence = ExactProcessPresence.Absent };
        var game = new CustomGameDefinition
        {
            Id = "custom-validation",
            Name = "Validation",
            ExecutablePath = @"C:\Games\validation.exe",
            IconPath = @"C:\Games\validation.png",
        };
        using var rig = TestRig.Create(timeProvider: clock);
        Assert.True(rig.RegisterAdapter(new CustomGameSessionAdapter(
            game,
            inspector,
            new NoopCustomStarter(),
            probe)));

        await rig.RefreshAsync();
        var launch = await rig.RequestLaunchResultAsync(game.Id);
        Assert.Equal(GameLaunchRequestOutcome.Accepted, launch.Outcome);

        inspector.Presence = ExactProcessPresence.Present;
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        Assert.True(rig.Service.Current(game.Id).IsTracking);

        probe.FilesExist = false;
        clock.Advance(TimeSpan.FromMinutes(4));
        await rig.RefreshAsync();

        Assert.False(rig.Service.Current(game.Id).IsTracking);
        Assert.Equal(TimeSpan.FromMinutes(3), rig.Service.Current(game.Id).Total);
    }

    [Fact]
    public async Task Persistence_failure_keeps_total_and_retries_without_double_counting()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saved = new List<IReadOnlyDictionary<string, long>>();
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, AbsentEvidence],
            timeProvider: clock,
            persist: totals =>
            {
                attempts++;
                if (attempts == 1) return false;
                saved.Add(totals);
                return true;
            });

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        var failed = await rig.RefreshAsync();

        Assert.Equal(TimeSpan.FromMinutes(10), failed.Total);
        Assert.True(failed.SaveFailed);
        Assert.Equal(600, rig.Service.SnapshotTotals()["ae"]);

        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await rig.RefreshAsync();
        Assert.Equal(TimeSpan.FromMinutes(10), recovered.Total);
        Assert.False(recovered.SaveFailed);
        Assert.Equal(2, attempts);
        Assert.Single(saved);
        Assert.Equal(600, saved[0]["ae"]);
    }

    [Fact]
    public async Task Snapshot_totals_does_not_close_an_active_runtime()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            initialTotals: new Dictionary<string, long> { ["ae"] = 12 },
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        await rig.RefreshAsync();

        var before = rig.Service.Current("ae");
        var snapshot = rig.Service.SnapshotTotals();
        var after = rig.Service.Current("ae");

        Assert.True(before.IsTracking);
        Assert.True(after.IsTracking);
        Assert.Equal(before.TotalSeconds, after.TotalSeconds);
        Assert.Equal(12, snapshot["ae"]);
    }

    [Fact]
    public async Task Current_displays_only_confirmed_active_seconds_without_persisting_them()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            initialTotals: new Dictionary<string, long> { ["ae"] = 10 },
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();

        var current = rig.Service.Current("ae");
        Assert.True(current.IsTracking);
        Assert.Equal(190, current.TotalSeconds);
        Assert.Equal(10, rig.Service.SnapshotTotals()["ae"]);
        Assert.False(current.SaveFailed);
    }

    [Fact]
    public async Task Reentrant_failed_newer_save_survives_outer_success_and_retries()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saved = new List<IReadOnlyDictionary<string, long>>();
        TestRig? rig = null;
        rig = TestRig.Create(
            observationsByGame: new Dictionary<string, GameSessionEvidence[]>(StringComparer.Ordinal)
            {
                ["ae"] = [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
                ["gi"] = [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
            },
            timeProvider: clock,
            persist: candidate =>
            {
                attempts++;
                if (attempts == 1)
                {
                    saved.Add(candidate);
                    rig!.Service.CloseRuntime("gi");
                    return true;
                }

                if (attempts == 2)
                {
                    return false;
                }

                saved.Add(candidate);
                return true;
            });
        using (rig)
        {
            await rig.RefreshAsync();
            await rig.RequestNyxLaunchAsync("ae");
            await rig.RequestNyxLaunchAsync("gi");
            clock.Advance(TimeSpan.FromMinutes(1));
            await rig.RefreshAsync();
            clock.Advance(TimeSpan.FromMinutes(2));
            await rig.RefreshAsync();

            rig.Service.CloseRuntime("ae");

            Assert.True(rig.Service.Current("ae").SaveFailed);
            Assert.True(rig.Service.Current("gi").SaveFailed);
            Assert.Equal(120, rig.Service.SnapshotTotals()["ae"]);
            Assert.Equal(120, rig.Service.SnapshotTotals()["gi"]);

            await rig.RefreshAsync();

            Assert.Equal(3, attempts);
            Assert.False(rig.Service.Current("ae").SaveFailed);
            Assert.False(rig.Service.Current("gi").SaveFailed);
            Assert.Equal(2, saved.Count);
            Assert.False(saved[0].ContainsKey("gi"));
            Assert.Equal(120, saved[1]["ae"]);
            Assert.Equal(120, saved[1]["gi"]);
        }
    }

    [Fact]
    public async Task Wall_clock_changes_do_not_invent_playtime()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, AbsentEvidence],
            timeProvider: clock);

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await rig.RefreshAsync();
        clock.Set(clock.GetUtcNow().AddDays(1));
        await rig.RefreshAsync();

        Assert.Equal(TimeSpan.Zero, rig.Service.Current("ae").Total);
    }

    [Fact]
    public async Task Total_seconds_saturate_at_long_max_value()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var saved = new List<IReadOnlyDictionary<string, long>>();
        using var rig = TestRig.Create(
            initialTotals: new Dictionary<string, long> { ["ae"] = long.MaxValue - 5 },
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, AbsentEvidence],
            timeProvider: clock,
            persist: totals =>
            {
                saved.Add(totals);
                return true;
            });

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await rig.RefreshAsync();

        Assert.Equal(long.MaxValue, saved[^1]["ae"]);
        Assert.Equal(long.MaxValue, rig.Service.Current("ae").TotalSeconds);
    }

    [Fact]
    public async Task Forget_removed_game_rebuilds_pending_save_without_dropping_other_games()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        const string removedId = "custom-deleted";
        const string changedId = "custom-changed";
        var candidates = new List<IReadOnlyDictionary<string, long>>();
        using var rig = TestRig.Create(
            initialTotals: new Dictionary<string, long>
            {
                [removedId] = 40,
                [changedId] = 70,
            },
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
            timeProvider: clock,
            persist: candidate =>
            {
                candidates.Add(candidate);
                return false;
            });

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        rig.Service.CloseRuntime("ae");

        Assert.Contains(removedId, candidates[^1].Keys);
        rig.Service.ForgetRemovedGame(removedId);

        Assert.DoesNotContain(removedId, rig.Service.SnapshotTotals().Keys);
        Assert.DoesNotContain(removedId, candidates[^1].Keys);
        Assert.Equal(70, candidates[^1][changedId]);
        Assert.Equal(120, candidates[^1]["ae"]);
        Assert.False(rig.Service.Current(removedId).SaveFailed);
        Assert.True(rig.Service.Current("ae").SaveFailed);
    }

    [Fact]
    public void Forgotten_deleted_custom_total_is_not_reintroduced_by_prepared_restore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-playtime-forget-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string removedId = "custom-deleted";
            const string changedId = "custom-changed";
            var removed = new CustomGameDefinition
            {
                Id = removedId,
                Name = "Deleted",
                ExecutablePath = @"C:\Games\Deleted.exe",
                IconPath = @"C:\Games\Deleted.png",
                CreationOrder = 1,
            };
            var changed = new CustomGameDefinition
            {
                Id = changedId,
                Name = "Changed",
                ExecutablePath = @"C:\Games\Changed.exe",
                IconPath = @"C:\Games\Changed.png",
                CreationOrder = 2,
            };
            var store = new LauncherStateStore(directory);
            store.Save(LauncherState.Defaults() with
            {
                CustomGames = [removed, changed],
                PlaytimeSecondsByGame = new Dictionary<string, long>
                {
                    [removedId] = 240,
                    [changedId] = 77,
                },
            });
            store.Save(LauncherState.Defaults() with
            {
                CustomGames = [changed with { Name = "Changed after edit" }],
                PlaytimeSecondsByGame = new Dictionary<string, long> { [changedId] = 77 },
            });
            using var rig = TestRig.Create(initialTotals: new Dictionary<string, long>
            {
                [removedId] = 240,
                [changedId] = 77,
            });

            rig.Service.CloseRuntime(changedId);
            rig.Service.ForgetRemovedGame(removedId);
            var preparedResult = store.PrepareLastKnownGoodRestore(out var prepared);

            Assert.True(preparedResult.IsUsable);
            Assert.NotNull(prepared);
            var restored = store.CommitPreparedLastKnownGoodRestore(
                prepared,
                rig.Service.SnapshotTotals()).State!;
            Assert.Contains(restored.CustomGames, game => game.Id == removedId);
            Assert.DoesNotContain(removedId, restored.PlaytimeSecondsByGame.Keys);
            Assert.Equal(77, restored.PlaytimeSecondsByGame[changedId]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Close_runtime_before_custom_adapter_replacement_excludes_gap_and_external_runtime()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        const string gameId = "custom-replacement";
        var saved = new List<IReadOnlyDictionary<string, long>>();
        using var rig = TestRig.Create(
            persist: totals =>
            {
                saved.Add(totals);
                return true;
            },
            timeProvider: clock);
        Assert.True(rig.Register(new SequenceAdapter(
            gameId,
            [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence])));

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync(gameId);
        clock.Advance(TimeSpan.FromMinutes(5));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();

        using var publication = await rig.AcquirePublicationAsync();
        Assert.NotNull(publication);
        var replacement = new SequenceAdapter(gameId, [RuntimeEvidence]);
        Assert.True(rig.Reserve(
            new Dictionary<string, IGameSessionAdapter?> { [gameId] = replacement },
            out var reservation));
        Assert.NotNull(reservation);
        rig.Service.CloseRuntime(gameId);
        reservation.Commit();
        reservation.Dispose();
        publication.Dispose();

        clock.Advance(TimeSpan.FromMinutes(10));
        await rig.RefreshAsync();

        Assert.Single(saved);
        Assert.Equal(180, saved[0][gameId]);
        Assert.Equal(TimeSpan.FromMinutes(3), rig.Service.Current(gameId).Total);
    }

    [Fact]
    public async Task Failed_custom_mutation_releases_reservation_with_adapter_and_active_timer_unchanged()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        const string gameId = "custom-rollback";
        using var rig = TestRig.Create(timeProvider: clock);
        Assert.True(rig.Register(new SequenceAdapter(
            gameId,
            [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence, RuntimeEvidence])));

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync(gameId);
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        await rig.RefreshAsync();
        var before = rig.Service.Current(gameId);

        using (var publication = await rig.AcquirePublicationAsync())
        {
            Assert.NotNull(publication);
            Assert.True(rig.Reserve(
                new Dictionary<string, IGameSessionAdapter?>
                {
                    [gameId] = new SequenceAdapter(gameId, [RuntimeEvidence]),
                },
                out var reservation));
            Assert.NotNull(reservation);
            reservation.Dispose();
        }

        var unchanged = rig.Service.Current(gameId);
        Assert.True(unchanged.IsTracking);
        Assert.Equal(before.TotalSeconds, unchanged.TotalSeconds);
        Assert.True(rig.HasSnapshot(gameId));

        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        Assert.Equal(before.TotalSeconds + 120, rig.Service.Current(gameId).TotalSeconds);
    }

    [Fact]
    public async Task Dispose_persists_a_confirmed_session_once()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var saved = new List<IReadOnlyDictionary<string, long>>();
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
            timeProvider: clock,
            persist: totals =>
            {
                saved.Add(totals);
                return true;
            });

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        await rig.RefreshAsync();
        rig.DisposeService();
        rig.DisposeService();

        Assert.Single(saved);
        Assert.Equal(360, saved[0]["ae"]);
    }

    [Fact]
    public async Task Second_dispose_retries_a_failed_final_save_without_double_counting()
    {
        var clock = new MutableTimeProvider(UtcAt(2026, 8, 29, 12));
        var attempts = 0;
        var saved = new List<IReadOnlyDictionary<string, long>>();
        using var rig = TestRig.Create(
            observations: [AbsentEvidence, AbsentEvidence, RuntimeEvidence, RuntimeEvidence],
            timeProvider: clock,
            persist: totals =>
            {
                attempts++;
                if (attempts == 1) return false;
                saved.Add(totals);
                return true;
            });

        await rig.RefreshAsync();
        await rig.RequestNyxLaunchAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        await rig.RefreshAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        await rig.RefreshAsync();

        rig.DisposeService();
        Assert.True(rig.Service.Current("ae").SaveFailed);
        rig.DisposeService();

        Assert.Equal(2, attempts);
        Assert.Single(saved);
        Assert.Equal(300, saved[0]["ae"]);
    }

    private static readonly GameSessionEvidence RuntimeEvidence = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Absent,
        ExactProcessPresence.Present);

    private static readonly GameSessionEvidence UncertainEvidence = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Uncertain,
        ExactProcessPresence.Uncertain);

    private static readonly GameSessionEvidence BootstrapEvidence = new(
        LocalReadinessEvidence.Ready,
        ExactProcessPresence.Present,
        ExactProcessPresence.Absent);

    private static readonly GameSessionEvidence AbsentEvidence =
        GameSessionEvidence.ReadyAndAbsent;

    private static DateTimeOffset UtcAt(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private sealed class TestRig : IDisposable
    {
        private readonly GameSessionCoordinator coordinator;
        private readonly GameSessionRefreshPump pump;

        private TestRig(
            GameSessionCoordinator coordinator,
            GameSessionRefreshPump pump,
            GamePlaytimeService service,
            IReadOnlyDictionary<string, SequenceAdapter> adapters)
        {
            this.coordinator = coordinator;
            this.pump = pump;
            Service = service;
            Adapters = adapters;
        }

        public GamePlaytimeService Service { get; }

        private IReadOnlyDictionary<string, SequenceAdapter> Adapters { get; }

        public static TestRig Create(
            GameSessionEvidence[]? observations = null,
            IReadOnlyDictionary<string, GameSessionEvidence[]>? observationsByGame = null,
            IReadOnlyDictionary<string, long>? initialTotals = null,
            Func<IReadOnlyDictionary<string, long>, bool>? persist = null,
            TimeProvider? timeProvider = null,
            GameLaunchDispatchResult? dispatchResult = null)
        {
            var sequences = new Dictionary<string, SequenceAdapter>(StringComparer.Ordinal);
            foreach (var game in GameCatalog.All)
            {
                var values = observationsByGame?.TryGetValue(game.Id, out var selected) == true
                    ? selected
                    : game.Id == "ae"
                        ? observations ?? []
                        : [];
                sequences.Add(game.Id, new SequenceAdapter(game.Id, values, dispatchResult));
            }

            var adapters = sequences.Values.Cast<IGameSessionAdapter>().ToArray();
            var coordinator = new GameSessionCoordinator(
                adapters,
                timeProvider,
                startupTimeout: TimeSpan.FromSeconds(10),
                adapterCallTimeout: TimeSpan.FromSeconds(2),
                absenceConfirmationInterval: TimeSpan.FromSeconds(1));
            var pump = new GameSessionRefreshPump(coordinator, TimeSpan.FromHours(1));
            var service = new GamePlaytimeService(
                initialTotals ?? new Dictionary<string, long>(),
                persist ?? (_ => true),
                pump,
                timeProvider);
            return new(coordinator, pump, service, sequences);
        }

        public async Task<GamePlaytimeSnapshot> RefreshAsync()
        {
            await pump.RefreshNowAsync();
            return Service.Current("ae");
        }

        public async Task<GamePlaytimeSnapshot> ResetAfterResumeAndRefreshAsync()
        {
            await pump.ResetAfterResumeAndRefreshAsync();
            return Service.Current("ae");
        }

        public bool Suspend() => pump.RequestSystemSuspend();

        public bool Resume() => pump.RequestSystemResume();

        public async Task RequestNyxLaunchAsync(string gameId = "ae")
        {
            var result = await RequestLaunchResultAsync(gameId);
            Assert.Equal(GameLaunchRequestOutcome.Accepted, result.Outcome);
        }

        public async Task<GameLaunchRequestResult> RequestLaunchResultAsync(string gameId = "ae") =>
            await coordinator.RequestLaunchAsync(gameId);

        public bool Register(SequenceAdapter adapter) => coordinator.TryRegisterCustomAdapter(adapter);

        public bool RegisterAdapter(IGameSessionAdapter adapter) => coordinator.TryRegisterCustomAdapter(adapter);

        public bool Remove(string gameId) => coordinator.TryRemoveCustomAdapter(gameId);

        public ValueTask<IDisposable?> AcquirePublicationAsync() =>
            pump.TryAcquireExclusivePublicationAsync();

        public bool Reserve(
            IReadOnlyDictionary<string, IGameSessionAdapter?> mutations,
            out GameSessionCoordinator.CustomAdapterMutationLease? reservation) =>
            coordinator.TryReserveCustomAdapterMutations(mutations, out reservation);

        public bool HasSnapshot(string gameId) => coordinator.TryGetSnapshot(gameId, out _);

        public void DisposeService()
        {
            Service.Dispose();
        }

        public TimeSpan SavedTotal(string gameId)
        {
            return Service.Current(gameId).Total;
        }

        public void Dispose()
        {
            DisposeService();
            pump.DisposeAsync().AsTask().GetAwaiter().GetResult();
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class SequenceAdapter(
        string gameId,
        IEnumerable<GameSessionEvidence> values,
        GameLaunchDispatchResult? dispatchResult = null,
        Func<CancellationToken, ValueTask>? beforeObserve = null) : IGameSessionAdapter
    {
        private readonly Queue<GameSessionEvidence> observations = new(values);
        private readonly GameLaunchDispatchResult dispatchOutcome =
            dispatchResult ?? GameLaunchDispatchResult.Accepted;

        public string GameId { get; } = gameId;

        public ValueTask<GameSessionEvidence> ObserveSessionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeObserve is not null)
            {
                return ObserveAfterAsync(cancellationToken);
            }

            return ValueTask.FromResult(observations.Count > 0
                ? observations.Dequeue()
                : GameSessionEvidence.ReadyAndAbsent);
        }

        private async ValueTask<GameSessionEvidence> ObserveAfterAsync(CancellationToken cancellationToken)
        {
            await beforeObserve!(cancellationToken);
            return observations.Count > 0
                ? observations.Dequeue()
                : GameSessionEvidence.ReadyAndAbsent;
        }

        public ValueTask<GameLaunchDispatchResult> RequestValidatedLaunchAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(dispatchOutcome);
    }

    private sealed class MutableCustomPathProbe : ICustomGamePathProbe
    {
        public bool FilesExist { get; set; } = true;

        public bool FileExists(string path) => FilesExist;

        public bool DirectoryExists(string path) => false;

        public bool IsReparsePoint(string path) => false;

        public string GetCanonicalPath(string path) => path.Replace('/', '\\');
    }

    private sealed class MutableCustomInspector : ICustomGameProcessInspector
    {
        public ExactProcessPresence Presence { get; set; }

        public ExactProcessPresence Check(string executablePath) => Presence;
    }

    private sealed class NoopCustomStarter : ICustomGameProcessStarter
    {
        public void Start(
            string executablePath,
            IReadOnlyList<string> arguments,
            bool requestAdministrator)
        {
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset value = current;
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => value;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan amount)
        {
            value += amount;
            timestamp += amount.Ticks;
        }

        public void Set(DateTimeOffset next) => value = next;
    }
}
