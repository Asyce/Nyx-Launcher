using System.Security.Cryptography;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Core.Features;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.Exports;

public sealed class ExportCoordinatorTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public void Any_armed_export_can_start_after_the_game_process_is_running(
        bool pullsArmed,
        bool achievementsArmed,
        bool expected)
    {
        var arm = new ExportArmSnapshot("gi", pullsArmed, achievementsArmed);

        Assert.Equal(expected, arm.CanStartWhileGameRunning);
    }

    [Theory]
    [InlineData(false, false, 0, 0)]
    [InlineData(true, false, 1, 0)]
    [InlineData(false, true, 0, 1)]
    [InlineData(true, true, 1, 1)]
    public async Task Each_arm_combination_starts_only_its_requested_provider(
        bool pullsArmed,
        bool achievementsArmed,
        int expectedPulls,
        int expectedAchievements)
    {
        var pulls = new FakePullProvider();
        var achievements = new FakeAchievementProvider();
        await using var coordinator = new ExportCoordinator(
            pulls, achievements, achievementPrepareTimeout: TimeSpan.FromMilliseconds(50));
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", pullsArmed, achievementsArmed),
            _ => ValueTask.FromResult(true));


        await EventuallyAsync(() => pulls.Calls == expectedPulls && achievements.Calls == expectedAchievements);
        Assert.True(result.LaunchAdmitted);
        Assert.Equal(expectedPulls, pulls.Calls);
        Assert.Equal(expectedAchievements, achievements.Calls);
    }

    [Fact]
    public async Task Failed_pull_does_not_cancel_successful_achievement_and_job_isolated()
    {
        var pulls = new FakePullProvider { Failure = new IOException("C:\\private\\token=secret") };
        var achievements = new FakeAchievementProvider();
        await using var coordinator = new ExportCoordinator(pulls, achievements);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("hsr", true, true), _ => ValueTask.FromResult(true));

        var final = await WaitForFinishedAsync(coordinator, result.JobId);
        Assert.Equal(ExportJobState.Failed, final.State);
        Assert.Equal(ExportTaskState.Failed, final.Pulls.State);
        Assert.Equal(ExportTaskState.Succeeded, final.Achievements.State);
        Assert.Equal("io-failed", final.Pulls.ErrorCode);
    }

    [Fact]
    public async Task Achievement_prepare_timeout_is_bounded_and_does_not_delay_launch_admission()
    {
        var pulls = new FakePullProvider();
        var achievements = new FakeAchievementProvider { Block = true };
        await using var coordinator = new ExportCoordinator(
            pulls, achievements, achievementPrepareTimeout: TimeSpan.FromMilliseconds(30));
        var launchReturned = false;
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            _ => { launchReturned = true; return ValueTask.FromResult(true); });

        Assert.True(launchReturned);
        var final = await WaitForFinishedAsync(coordinator, result.JobId);
        Assert.Equal(ExportTaskState.Failed, final.Achievements.State);
        Assert.Equal("timed-out", final.Achievements.ErrorCode);
        Assert.Equal(1, achievements.Canceled);
        Assert.Equal(1, achievements.Disposed);
    }

    [Fact]
    public async Task Unexpected_provider_exception_is_redacted_and_does_not_escape_task()
    {
        var pulls = new FakePullProvider { Failure = new Exception(@"secret=C:\users\alice\token") };
        var statuses = new RecordingStatusSink();
        await using var coordinator = new ExportCoordinator(pulls, new FakeAchievementProvider(), statusSink: statuses);
        var result = await coordinator.RunForLaunchAsync(new ExportArmSnapshot("gi", true, false), _ => ValueTask.FromResult(true));
        var final = await WaitForFinishedAsync(coordinator, result.JobId);
        Assert.Equal("provider-failed", final.Pulls.ErrorCode);
        Assert.DoesNotContain("alice", string.Join("\n", statuses.Lines), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", string.Join("\n", statuses.Lines), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Baseline_is_prepared_before_admission_and_both_lanes_continue_automatically()
    {
        var sequence = new List<string>();
        var pulls = new FakePullProvider { Sequence = sequence };
        var achievements = new FakeAchievementProvider { Sequence = sequence };
        await using var coordinator = new ExportCoordinator(
            pulls, achievements);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", true, true),
            _ =>
            {
                sequence.Add("admission");
                Assert.Contains("pull-baseline", sequence);
                Assert.Contains("achievement-ready", sequence);
                Assert.DoesNotContain("pull-export", sequence);
                return ValueTask.FromResult(true);
            });
        var final = await WaitForFinishedAsync(coordinator, result.JobId);
        Assert.True(sequence.IndexOf("pull-baseline") < sequence.IndexOf("admission"));
        Assert.True(sequence.IndexOf("admission") < sequence.IndexOf("pull-export"));
        Assert.Equal(ExportTaskState.Succeeded, final.Pulls.State);
        Assert.Equal(ExportTaskState.Succeeded, final.Achievements.State);
    }

    [Fact]
    public async Task Pull_baseline_failure_does_not_block_launch_or_achievements()
    {
        var pulls = new FakePullProvider { PrepareFailure = new IOException("private cache path") };
        var achievements = new FakeAchievementProvider();
        await using var coordinator = new ExportCoordinator(pulls, achievements);

        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", true, true),
            _ => ValueTask.FromResult(true));

        var final = await WaitForFinishedAsync(coordinator, result.JobId);
        Assert.True(result.LaunchAdmitted);
        Assert.Equal(ExportTaskState.Failed, final.Pulls.State);
        Assert.Equal("io-failed", final.Pulls.ErrorCode);
        Assert.Equal(ExportTaskState.Succeeded, final.Achievements.State);
    }

    [Fact]
    public async Task Already_running_route_uses_the_same_baseline_admission_export_sequence()
    {
        var sequence = new List<string>();
        var pulls = new FakePullProvider { Sequence = sequence };
        await using var coordinator = new ExportCoordinator(pulls, new FakeAchievementProvider());

        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("hsr", true, false),
            _ =>
            {
                sequence.Add("already-running");
                return ValueTask.FromResult(true);
            });

        await WaitForFinishedAsync(coordinator, result.JobId);
        Assert.Equal(["pull-baseline", "already-running", "pull-export"], sequence);
    }

    [Fact]
    public void Feature_flags_mask_armed_lanes_and_cannot_activate_dormant_providers()
    {
        var state = new ExportArmingState
        {
            Games = new Dictionary<string, ExportGameArming>(StringComparer.Ordinal)
            {
                ["gi"] = new() { PullsArmed = true, AchievementsArmed = true },
                ["zzz"] = new() { PullsArmed = true, AchievementsArmed = true },
            },
        };
        var flags = LauncherFeatureFlags.Defaults() with
        {
            AchievementHelperReady = true,
            GiPulls = false,
            ZzzPulls = true,
            ZzzAchievements = true,
        };

        var genshin = ExportArmSnapshot.From(state, "gi", flags);
        var zzz = ExportArmSnapshot.From(state, "zzz", flags);

        Assert.False(genshin.PullsArmed);
        Assert.True(genshin.AchievementsArmed);
        Assert.Equal(ExportKind.Pulls, zzz.RequestedKinds);
    }

    [Theory]
    [InlineData(AchievementExportSources.HoyoLab, false, false, false)]
    [InlineData(AchievementExportSources.HoyoLab, false, true, false)]
    [InlineData(AchievementExportSources.HoyoLab, true, false, true)]
    [InlineData(AchievementExportSources.HoyoLab, true, true, true)]
    [InlineData(AchievementExportSources.Game, false, false, false)]
    [InlineData(AchievementExportSources.Game, false, true, true)]
    [InlineData(AchievementExportSources.Game, true, false, false)]
    [InlineData(AchievementExportSources.Game, true, true, true)]
    public async Task Star_rail_saved_source_capability_is_the_coordinator_arming_boundary(
        string source,
        bool hoyoLabConsent,
        bool achievementHelperReady,
        bool expected)
    {
        var state = new ExportArmingState
        {
            Games = new Dictionary<string, ExportGameArming>(StringComparer.Ordinal)
            {
                ["hsr"] = new()
                {
                    AchievementsArmed = true,
                    AchievementSource = source,
                },
            },
        };
        var flags = LauncherFeatureFlags.Defaults() with
        {
            HsrPulls = false,
            HsrAchievements = true,
            HoyoLabAccountAccess = hoyoLabConsent,
            AchievementHelperReady = achievementHelperReady,
        };
        var achievements = new FakeAchievementProvider();
        await using var coordinator = new ExportCoordinator(
            new FakePullProvider(),
            achievements);

        var arm = ExportArmSnapshot.From(state, "hsr", flags);
        var result = await coordinator.RunForLaunchAsync(
            arm,
            _ => ValueTask.FromResult(true));
        await EventuallyAsync(() => achievements.Calls == (expected ? 1 : 0));

        Assert.Equal(expected, arm.AchievementsArmed);
        Assert.Equal(expected ? 1 : 0, achievements.Calls);
        Assert.True(result.LaunchAdmitted);
    }

    [Fact]
    public async Task Non_admitted_launch_starts_no_provider_and_is_canceled()
    {
        var pulls = new FakePullProvider();
        var achievements = new FakeAchievementProvider();
        await using var coordinator = new ExportCoordinator(pulls, achievements);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", true, true), _ => ValueTask.FromResult(false));

        Assert.False(result.LaunchAdmitted);
        Assert.Equal(ExportJobState.Canceled, result.Snapshot.State);
        Assert.Equal(0, pulls.Calls);
        Assert.Equal(1, pulls.PrepareCalls);
        Assert.Equal(1, achievements.Calls);
    }

    [Fact]
    public async Task Dispose_during_initial_publish_waits_for_retained_job_cleanup()
    {
        var statuses = new BlockingStatusSink();
        var pulls = new FakePullProvider();
        var coordinator = new ExportCoordinator(
            pulls,
            new FakeAchievementProvider(),
            statusSink: statuses);
        var launching = coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", true, false),
            static _ => ValueTask.FromResult(true)).AsTask();

        await statuses.Entered.Task;
        var disposing = coordinator.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);

        try
        {
            statuses.Release.TrySetResult();
            var result = await launching;
            await disposing;

            Assert.False(result.LaunchAdmitted);
            Assert.Equal(ExportJobState.Canceled, result.Snapshot.State);
            Assert.Equal(1, pulls.Disposed);
        }
        finally
        {
            statuses.Release.TrySetResult();
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unsupported_slots_never_invoke_providers()
    {
        var pulls = new FakePullProvider();
        var achievements = new FakeAchievementProvider();
        await using var coordinator = new ExportCoordinator(pulls, achievements);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("ae", true, true), _ => ValueTask.FromResult(true));

        Assert.Equal(ExportJobState.Unsupported, result.Snapshot.State);
        Assert.Equal(ExportTaskState.Unsupported, result.Snapshot.Pulls.State);
        Assert.Equal(ExportTaskState.Unsupported, result.Snapshot.Achievements.State);
        Assert.Equal(0, pulls.Calls);
        Assert.Equal(0, achievements.Calls);
        Assert.Throws<KeyNotFoundException>(() => coordinator.GetSnapshot(result.JobId));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await coordinator.WaitForCompletionAsync(result.JobId));
    }

    [Fact]
    public async Task Retains_only_the_latest_completed_job_for_a_game()
    {
        var pulls = new FakePullProvider();
        await using var coordinator = new ExportCoordinator(pulls, new FakeAchievementProvider());
        var firstId = Guid.Empty;
        ExportLaunchResult latest = default!;

        for (var i = 0; i < 100; i++)
        {
            latest = await coordinator.RunForLaunchAsync(
                new ExportArmSnapshot("gi", true, false),
                static _ => ValueTask.FromResult(true));
            await WaitForFinishedAsync(coordinator, latest.JobId);
            if (i == 0) firstId = latest.JobId;
        }

        Assert.NotEqual(firstId, latest.JobId);
        Assert.Equal(latest.JobId, coordinator.GetSnapshot(latest.JobId).JobId);
        Assert.Throws<KeyNotFoundException>(() => coordinator.GetSnapshot(firstId));
    }

    [Fact]
    public async Task In_progress_same_game_job_is_rejected_until_completion()
    {
        var pulls = new FakePullProvider { Block = true };
        var coordinator = new ExportCoordinator(pulls, new FakeAchievementProvider());
        var first = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", true, false),
            static _ => ValueTask.FromResult(true));
        await EventuallyAsync(() => pulls.Calls == 1);

        var second = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", true, false),
            static _ => ValueTask.FromResult(true));

        Assert.False(second.LaunchAdmitted);
        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(1, pulls.PrepareCalls);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task A_waiter_captured_before_replacement_still_completes()
    {
        var completion = new TaskCompletionSource<ExportArtifactMetadata>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var achievements = new FakeAchievementProvider { CompletionGate = completion };
        await using var coordinator = new ExportCoordinator(new FakePullProvider(), achievements);
        var first = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        var waiter = coordinator.WaitForCompletionAsync(first.JobId).AsTask();
        await EventuallyAsync(() => !waiter.IsCompleted);

        completion.SetResult(new("achievements", 1, 1, "ndjson", DateTimeOffset.UtcNow));
        await EventuallyAsync(() => waiter.IsCompleted);
        var second = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        var firstFinal = await waiter;

        Assert.Equal(first.JobId, firstFinal.JobId);
        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Throws<KeyNotFoundException>(() => coordinator.GetSnapshot(first.JobId));
    }

    [Fact]
    public async Task Cancellation_cleanup_gate_keeps_replacement_rejected()
    {
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var achievements = new FakeAchievementProvider
        {
            BlockCompletion = true,
            AllowCleanup = allowCleanup.Task,
        };
        var coordinator = new ExportCoordinator(new FakePullProvider(), achievements);
        var first = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        await EventuallyAsync(() => achievements.Calls == 1);
        Assert.True(coordinator.Cancel(first.JobId));

        var second = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        Assert.False(second.LaunchAdmitted);
        Assert.Equal(first.JobId, second.JobId);

        allowCleanup.SetResult();
        await coordinator.WaitForCompletionAsync(first.JobId);
        var replacement = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        Assert.True(replacement.LaunchAdmitted);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Full_dispose_drains_native_job_after_launcher_close()
    {
        var achievements = new FakeAchievementProvider
        {
            LauncherIndependent = true,
            CompletionGate = new TaskCompletionSource<ExportArtifactMetadata>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var coordinator = new ExportCoordinator(new FakePullProvider(), achievements);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));

        await coordinator.ShutDownForLauncherCloseAsync();
        Assert.Equal(ExportJobState.Running, coordinator.GetSnapshot(result.JobId).State);
        await coordinator.DisposeAsync();

        Assert.Equal(1, achievements.Canceled);
        Assert.Equal(1, achievements.Disposed);
        Assert.Throws<KeyNotFoundException>(() => coordinator.GetSnapshot(result.JobId));
    }

    [Fact]
    public async Task Jobs_run_concurrently_and_cancel_unfinished_work_on_close()
    {
        var pulls = new FakePullProvider { Block = true };
        var achievements = new FakeAchievementProvider { BlockCompletion = true };
        var coordinator = new ExportCoordinator(pulls, achievements);
        var first = await coordinator.RunForLaunchAsync(new ExportArmSnapshot("gi", true, true), _ => ValueTask.FromResult(true));
        var second = await coordinator.RunForLaunchAsync(new ExportArmSnapshot("hsr", true, true), _ => ValueTask.FromResult(true));
        await EventuallyAsync(() => pulls.Calls == 2 && achievements.Calls == 2);

        var firstWait = coordinator.WaitForCompletionAsync(first.JobId).AsTask();
        var secondWait = coordinator.WaitForCompletionAsync(second.JobId).AsTask();
        await coordinator.DisposeAsync();
        Assert.Equal(ExportJobState.Canceled, (await firstWait).State);
        Assert.Equal(ExportJobState.Canceled, (await secondWait).State);
        Assert.Throws<KeyNotFoundException>(() => coordinator.GetSnapshot(first.JobId));
        Assert.Throws<KeyNotFoundException>(() => coordinator.GetSnapshot(second.JobId));
        Assert.Equal(2, pulls.Canceled);
        Assert.Equal(2, achievements.Canceled);
        Assert.Equal(2, pulls.Disposed);
        Assert.Equal(2, achievements.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Launcher_close_after_native_ready_preserves_one_bounded_completion(
        bool failCapture)
    {
        var completion = new TaskCompletionSource<ExportArtifactMetadata>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var achievements = new FakeAchievementProvider
        {
            LauncherIndependent = true,
            CompletionGate = completion,
        };
        var coordinator = new ExportCoordinator(new FakePullProvider(), achievements);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            _ => ValueTask.FromResult(true));

        Assert.True(result.LaunchAdmitted);
        Assert.True(coordinator.IsLauncherIndependentAchievementJob(result.JobId));
        await coordinator.ShutDownForLauncherCloseAsync();

        Assert.Equal(ExportJobState.Running, coordinator.GetSnapshot(result.JobId).State);
        Assert.Equal(0, achievements.Canceled);
        Assert.Equal(0, achievements.Disposed);
        if (failCapture)
            completion.SetException(new ExportProviderException("capture_closed"));
        else
            completion.SetResult(new(
                "achievements",
                1,
                1,
                "pengo-achievements-v1",
                DateTimeOffset.UtcNow));

        var final = await WaitForFinishedAsync(coordinator, result.JobId);
        Assert.Equal(
            failCapture ? ExportJobState.Failed : ExportJobState.Completed,
            final.State);
        Assert.Equal(
            failCapture ? ExportTaskState.Failed : ExportTaskState.Succeeded,
            final.Achievements.State);
        Assert.Equal(failCapture ? "capture_closed" : null, final.Achievements.ErrorCode);
        Assert.Equal(0, achievements.Canceled);
        Assert.Equal(1, achievements.Disposed);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Launcher_close_before_native_ready_cancels_and_cleans_up()
    {
        var achievements = new FakeAchievementProvider
        {
            LauncherIndependent = true,
            Block = true,
        };
        var coordinator = new ExportCoordinator(
            new FakePullProvider(),
            achievements,
            achievementPrepareTimeout: TimeSpan.FromSeconds(30));
        var launching = coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            _ => ValueTask.FromResult(true)).AsTask();
        await EventuallyAsync(() => achievements.Calls == 1);

        await coordinator.ShutDownForLauncherCloseAsync();
        var result = await launching;

        Assert.False(result.LaunchAdmitted);
        Assert.Equal(ExportJobState.Canceled, coordinator.GetSnapshot(result.JobId).State);
        Assert.Equal(1, achievements.Canceled);
        Assert.Equal(1, achievements.Disposed);
    }

    [Fact]
    public async Task Launcher_close_cancels_mixed_job_while_pull_lane_is_active()
    {
        var pulls = new FakePullProvider { Block = true };
        var achievements = new FakeAchievementProvider
        {
            LauncherIndependent = true,
            BlockCompletion = true,
        };
        var coordinator = new ExportCoordinator(pulls, achievements);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", true, true),
            _ => ValueTask.FromResult(true));
        await EventuallyAsync(() => pulls.Calls == 1 && achievements.Calls == 1);

        await coordinator.ShutDownForLauncherCloseAsync();

        var final = coordinator.GetSnapshot(result.JobId);
        Assert.Equal(ExportJobState.Canceled, final.State);
        Assert.Equal(1, pulls.Canceled);
        Assert.Equal(1, pulls.Disposed);
        Assert.Equal(1, achievements.Canceled);
        Assert.Equal(1, achievements.Disposed);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Explicit_cancel_after_native_ready_is_not_detached_on_launcher_close()
    {
        var completion = new TaskCompletionSource<ExportArtifactMetadata>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var achievements = new FakeAchievementProvider
        {
            LauncherIndependent = true,
            CompletionGate = completion,
        };
        var coordinator = new ExportCoordinator(new FakePullProvider(), achievements);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("hsr", false, true),
            _ => ValueTask.FromResult(true));

        Assert.True(coordinator.Cancel(result.JobId));
        await coordinator.ShutDownForLauncherCloseAsync();
        var final = await WaitForFinishedAsync(coordinator, result.JobId);

        Assert.Equal(ExportJobState.Canceled, final.State);
        Assert.Equal(ExportTaskState.Canceled, final.Achievements.State);
        Assert.Equal(1, achievements.Canceled);
        Assert.Equal(1, achievements.Disposed);
    }

    [Fact]
    public async Task Simultaneous_lane_failures_keep_both_sanitized_error_codes()
    {
        var pulls = new FakePullProvider { Failure = new IOException("pull secret") };
        var achievements = new FakeAchievementProvider
        {
            CompletionFailure = new ExportProviderException("capture_closed"),
        };
        await using var coordinator = new ExportCoordinator(pulls, achievements);

        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", true, true),
            _ => ValueTask.FromResult(true));

        var final = await WaitForFinishedAsync(coordinator, result.JobId);
        Assert.Equal(ExportJobState.Failed, final.State);
        Assert.Equal("io-failed", final.Pulls.ErrorCode);
        Assert.Equal("capture_closed", final.Achievements.ErrorCode);
    }

    [Fact]
    public async Task Close_waits_until_capture_cancellation_and_temporary_cleanup_finish()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "nyx-capture-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temporary, "sanitized fixture");
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var achievements = new FakeAchievementProvider
        {
            BlockCompletion = true,
            CleanupPath = temporary,
            AllowCleanup = allowCleanup.Task,
        };
        var coordinator = new ExportCoordinator(new FakePullProvider(), achievements);
        await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            _ => ValueTask.FromResult(true));

        var closing = coordinator.DisposeAsync().AsTask();
        var closingAgain = coordinator.DisposeAsync().AsTask();
        await EventuallyAsync(() => achievements.Canceled == 1);
        Assert.False(closing.IsCompleted);
        Assert.False(closingAgain.IsCompleted);
        Assert.True(File.Exists(temporary));

        allowCleanup.SetResult();
        await Task.WhenAll(closing, closingAgain);
        Assert.False(File.Exists(temporary));
        Assert.Equal(1, achievements.Disposed);
    }

    [Fact]
    public async Task Fixed_helper_path_and_allowlisted_arguments_are_used_and_status_is_redacted()
    {
        var runner = new FakeHelperRunner();
        var directory = Path.Combine(Path.GetTempPath(), "nyx-helper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, VerifiedAchievementHelperBoundary.ExpectedHelperFileName);
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(path, bytes);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var helper = new VerifiedAchievementHelperBoundary(path, hash, runner);
            var session = await helper.StartAsync("gi", null, CancellationToken.None);
            var artifact = await session.Completion;

            Assert.Equal(path, runner.Invocation!.HelperPath);
            Assert.Equal("gi", runner.Invocation.GameId);
            Assert.Contains("--launcher", runner.Invocation.Arguments);
            Assert.Contains("--parent-watch", runner.Invocation.Arguments);
            Assert.Contains("named-mutex", runner.Invocation.Arguments);
            Assert.Contains("downloads", runner.Invocation.Arguments);
            Assert.Equal("achievements", artifact.Kind);
            Assert.Empty(NdjsonExportStatusParser.Parse(["{bad", "{\"gameId\":\"zzz\",\"kind\":\"achievements\"}"]));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Helper_hash_mismatch_fails_closed_before_runner_or_capture()
    {
        var runner = new FakeHelperRunner();
        var directory = Path.Combine(Path.GetTempPath(), "nyx-helper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, VerifiedAchievementHelperBoundary.ExpectedHelperFileName);
        File.WriteAllText(path, "tampered");
        try
        {
            var helper = new VerifiedAchievementHelperBoundary(path, new string('0', 64), runner);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await helper.StartAsync("hsr", null, default));
            Assert.Null(runner.Invocation);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }

    private static async Task<ExportJobSnapshot> WaitForFinishedAsync(ExportCoordinator coordinator, Guid id)
    {
        for (var i = 0; i < 100; i++)
        {
            var snapshot = coordinator.GetSnapshot(id);
            if (snapshot.IsFinished) return snapshot;
            await Task.Delay(10);
        }
        return coordinator.GetSnapshot(id);
    }

    private sealed class FakePullProvider : IPullExportProvider
    {
        public int PrepareCalls;
        public int Calls;
        public int Canceled;
        public int Disposed;
        public bool Block;
        public Exception? Failure;
        public Exception? PrepareFailure;
        public List<string>? Sequence;

        public ValueTask<IPullExportSession> PrepareAsync(string gameId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PrepareCalls);
            Sequence?.Add("pull-baseline");
            if (PrepareFailure is not null) throw PrepareFailure;
            return ValueTask.FromResult<IPullExportSession>(new FakePullSession(this));
        }

        private sealed class FakePullSession(FakePullProvider owner) : IPullExportSession
        {
            public async ValueTask<ExportArtifactMetadata> ExportAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Calls);
                owner.Sequence?.Add("pull-export");
                if (owner.Block)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) { Interlocked.Increment(ref owner.Canceled); throw; }
                }
                if (owner.Failure is not null) throw owner.Failure;
                return new("pulls", 1, 1, "json", DateTimeOffset.UtcNow);
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disposed);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeAchievementProvider : IAchievementExportProvider
    {
        public int Calls;
        public int Canceled;
        public int Disposed;
        public bool Block;
        public bool BlockCompletion;
        public Exception? CompletionFailure;
        public List<string>? Sequence;
        public string? CleanupPath;
        public Task? AllowCleanup;
        public bool LauncherIndependent;
        public TaskCompletionSource<ExportArtifactMetadata>? CompletionGate;
        public ValueTask<IAchievementExportSession> StartAsync(string gameId, string? outputPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            IAchievementExportSession session = new FakeAchievementSession(this, cancellationToken);
            if (LauncherIndependent)
                session = new LauncherIndependentAchievementSession(session);
            return ValueTask.FromResult(session);
        }

        private sealed class FakeAchievementSession : IAchievementExportSession
        {
            private readonly FakeAchievementProvider owner;
            private readonly CancellationTokenSource cancellation;
            public FakeAchievementSession(FakeAchievementProvider owner, CancellationToken token)
            {
                this.owner = owner;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                Ready = PrepareAsync(owner, cancellation.Token);
                Completion = CompleteAsync(owner, cancellation.Token);
            }
            public Task Ready { get; }
            public Task<ExportArtifactMetadata> Completion { get; }
            private static async Task PrepareAsync(FakeAchievementProvider owner, CancellationToken token)
            {
                if (owner.Block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                owner.Sequence?.Add("achievement-ready");
            }
            private static async Task<ExportArtifactMetadata> CompleteAsync(FakeAchievementProvider owner, CancellationToken token)
            {
                await PrepareGateAsync(owner, token);
                if (owner.BlockCompletion) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                if (owner.CompletionGate is not null)
                    return await owner.CompletionGate.Task.WaitAsync(token);
                if (owner.CompletionFailure is not null) throw owner.CompletionFailure;
                owner.Sequence?.Add("achievement-complete");
                return new("achievements", 1, 1, "ndjson", DateTimeOffset.UtcNow);
            }
            private static async Task PrepareGateAsync(FakeAchievementProvider owner, CancellationToken token)
            {
                if (owner.Block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            public async ValueTask DisposeAsync()
            {
                cancellation.Cancel();
                try { await Completion; }
                catch (OperationCanceledException) { Interlocked.Increment(ref owner.Canceled); }
                catch (Exception) { }
                if (owner.AllowCleanup is not null) await owner.AllowCleanup;
                if (owner.CleanupPath is not null) File.Delete(owner.CleanupPath);
                cancellation.Dispose();
                Interlocked.Increment(ref owner.Disposed);
            }
        }

        private sealed class LauncherIndependentAchievementSession(
            IAchievementExportSession inner) : ILauncherIndependentAchievementExportSession
        {
            public Task Ready => inner.Ready;
            public Task<ExportArtifactMetadata> Completion => inner.Completion;
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class FakeHelperRunner : IVerifiedAchievementHelperRunner
    {
        public AchievementHelperInvocation? Invocation;
        public ValueTask<IAchievementExportSession> StartAsync(
            AchievementHelperInvocation invocation,
            VerifiedAchievementHelperLaunchBinding helperBinding,
            CancellationToken cancellationToken)
        {
            ProcessAchievementHelperRunner.EnsureBoundHelper(invocation, helperBinding);
            Invocation = invocation;
            return ValueTask.FromResult<IAchievementExportSession>(new CompletedAchievementSession());
        }

        private sealed class CompletedAchievementSession : IAchievementExportSession
        {
            public Task Ready => Task.CompletedTask;
            public Task<ExportArtifactMetadata> Completion => Task.FromResult(
                new ExportArtifactMetadata("achievements", 1, 2, "ndjson", DateTimeOffset.UtcNow));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStatusSink : IExportStatusSink
    {
        public List<string> Lines { get; } = [];
        public ValueTask PublishAsync(ExportStatusEvent status, CancellationToken cancellationToken)
        {
            Lines.Add(status.ToNdjson());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingStatusSink : IExportStatusSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;

        public async ValueTask PublishAsync(ExportStatusEvent status, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
        }
    }

}
