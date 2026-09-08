using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabSyncCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Older = Now.AddHours(-2);
    private static readonly DateTimeOffset Newer = Now.AddHours(-1);
    private const string DisplayCode =
        "NYX-HOYO-AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH";
    private const string AlternateCode =
        "NYX-HOYO-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH-JJJJ";
    private const string FixtureUid = "123456789";
    private const string FixtureNickname = "Test Trailblazer";
    private static readonly Vector Fixture = LoadVector();

    [Theory]
    [InlineData("gi")]
    [InlineData("hsr")]
    public async Task Automatic_sync_requires_separate_per_game_opt_in_and_reuses_encrypted_sync(string gameId)
    {
        var bundle = gameId == "gi" ? GenshinBundleWithResource(Older, 80) : BundleWithResource(Older, 80);
        using var harness = new Harness(bundle);
        Assert.Equal(HoyoLabManualSyncStatus.NotEnabled, harness.Coordinator.SetAutomaticSync(true, gameId: gameId).Status);
        Assert.Equal(HoyoLabManualSyncStatus.NotEnabled,
            (await harness.Coordinator.SyncAutomaticallyAsync(true, gameId: gameId)).Status);
        Assert.Empty(harness.Cloud.Requests);
        Assert.Equal(HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode, gameId: gameId)).Status);
        harness.Cloud.ClearRequests();
        Assert.False(harness.Coordinator.GetSummary(gameId).AutomaticEnabled);
        Assert.Equal(HoyoLabManualSyncStatus.NotEnabled,
            (await harness.Coordinator.SyncAutomaticallyAsync(true, gameId: gameId)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true, gameId: gameId).Status);
        Assert.True(harness.Coordinator.GetSummary(gameId).AutomaticEnabled);
        Assert.False(harness.Coordinator.GetSummary(gameId == "hsr" ? "gi" : "hsr").AutomaticEnabled);
        Assert.Empty(harness.Cloud.Requests);

        SaveBundle(harness.ProtectedRoot,
            gameId == "gi" ? GenshinBundleWithResource(Newer, 90) : BundleWithResource(Newer, 90), gameId);
        Assert.Equal(HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.SyncAutomaticallyAsync(true, gameId: gameId)).Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(item => item.Action));
        Assert.All(harness.Cloud.Requests, request =>
        {
            Assert.Equal(gameId, request.GameId);
            var body = Encoding.UTF8.GetString(request.Body);
            Assert.DoesNotContain(DisplayCode, body);
            Assert.DoesNotContain(bundle.SelectedRole!.RoleId, body);
        });
        using var secrets = Secrets(DisplayCode);
        Assert.Equal(90, Assert.Single(harness.Cloud.GetBundle(Fixture.SyncId, secrets, gameId).Roles).Resource!.Current);
    }

    [Theory]
    [InlineData("hsr")]
    [InlineData("gi")]
    public async Task Automatic_sync_propagates_local_opt_out_without_restoring_the_removed_capability(string gameId)
    {
        var bundle = gameId == "gi" ? GenshinBundleWithResource(Older, 80) : BundleWithResource(Older, 80);
        using var harness = new Harness(bundle);
        Assert.Equal(HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode, gameId: gameId)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true, gameId: gameId).Status);
        var store = new HoyoLabGameBundleStore(harness.ProtectedRoot, new CopyProtector(),
            new SystemPublisherRoleBindingFileBoundary(), harness.Clock, gameId);
        Assert.True(store.TrySetCapabilityConsent(HoyoLabGameBundleRules.Resources, false));
        harness.Cloud.ClearRequests();

        Assert.Equal(HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.SyncAutomaticallyAsync(true, gameId: gameId)).Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(item => item.Action));
        using var secrets = Secrets(DisplayCode);
        var remote = harness.Cloud.GetBundle(Fixture.SyncId, secrets, gameId);
        Assert.Null(Assert.Single(remote.Roles).Resource);
        Assert.Equal(HoyoLabGameBundleRules.Resources, Assert.Single(remote.CapabilityTombstones).Capability);
        Assert.False(LoadBundle(harness.ProtectedRoot, gameId).Consents.Resources);
        Assert.True(harness.Coordinator.GetSummary(gameId).AutomaticEnabled);
    }

    [Fact]
    public async Task Automatic_resource_interval_survives_restart_and_full_or_manual_refresh_is_immediate()
    {
        using var harness = new Harness(BundleWithResource(Older, 80));
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.SyncAutomaticallyAsync(false)).Status);
        using var cloud = new FakeCloud(harness.Clock);
        harness.Cloud.CopyTo(cloud, Fixture.SyncId);
        harness.Coordinator.Dispose();
        using var restart = CreateCoordinator(harness.PublisherRoot, harness.SlotId, harness.ProtectedRoot,
            harness.Authority, cloud, clock: harness.Clock);
        Assert.True(restart.GetSummary().AutomaticEnabled);
        harness.Clock.UtcNow = Now.AddHours(1).AddMilliseconds(-1);
        Assert.Equal(HoyoLabManualSyncStatus.Deferred, (await restart.SyncAutomaticallyAsync(false)).Status);
        Assert.Empty(cloud.Requests);
        harness.Clock.UtcNow = Now.AddHours(1);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await restart.SyncAutomaticallyAsync(false)).Status);
        Assert.Equal(["pull"], cloud.Requests.Select(item => item.Action));
        cloud.ClearRequests();

        SaveBundle(harness.ProtectedRoot, BundleWithResource(Newer, 90), "hsr");
        Assert.Equal(HoyoLabManualSyncStatus.Deferred, (await restart.SyncAutomaticallyAsync(false)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await restart.SyncAutomaticallyAsync(true)).Status);
        Assert.Equal(["pull", "push"], cloud.Requests.Select(item => item.Action));
        cloud.ClearRequests();
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await restart.SyncNowAsync()).Status);
        Assert.Equal(["pull"], cloud.Requests.Select(item => item.Action));
    }

    [Fact]
    public async Task Automatic_resource_rate_limit_is_independent_for_each_game()
    {
        using var harness = new Harness(BundleWithResource(Older, 80));
        SaveBundle(harness.ProtectedRoot, GenshinBundleWithResource(Older, 60), "gi");
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.SyncNowAsync(gameId: "gi")).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true, gameId: "gi").Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.SyncAutomaticallyAsync(false)).Status);
        harness.Cloud.ClearRequests();
        Assert.Equal(HoyoLabManualSyncStatus.Deferred, (await harness.Coordinator.SyncAutomaticallyAsync(false)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.SyncAutomaticallyAsync(false, gameId: "gi")).Status);
        Assert.Equal("gi", Assert.Single(harness.Cloud.Requests).GameId);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("account")]
    [InlineData("role")]
    [InlineData("capability")]
    public async Task Automatic_sync_pauses_for_remote_deletion_without_recreating_or_erasing_local_data(string mode)
    {
        var local = BundleWithResource(Older, 80);
        using var harness = new Harness(local);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true, gameId: "gi").Status);
        if (mode == "copy") harness.Cloud.Remove(Fixture.SyncId);
        else if (mode == "account") harness.Cloud.RemoveAccount(Fixture.SyncId);
        else
        {
            var remote = mode == "role"
                ? HoyoLabSyncCoordinator.RemoveRoleAt(local, local.SelectedRole!, Newer)
                : local with
                {
                    Roles = [local.Roles[0] with { Resource = null, Observations = local.Roles[0].Observations with { Resources = null } }],
                    CapabilityTombstones = [new(local.SelectedRole!, HoyoLabGameBundleRules.Resources, Newer)],
                };
            harness.Cloud.SeedBundle(Fixture.SyncId, DisplayCode, remote, Now);
            // A later automatic local observation must not silently undo the other device's deletion.
            SaveBundle(harness.ProtectedRoot, BundleWithResource(Now, 90), "hsr");
        }
        harness.Cloud.ClearRequests();
        var before = ReadTree(harness.ProtectedRoot);
        Assert.Equal(HoyoLabManualSyncStatus.AutomaticSyncPaused,
            (await harness.Coordinator.SyncAutomaticallyAsync(true)).Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(item => item.Action));
        AssertTreeEqual(before, ReadTree(harness.ProtectedRoot));
        Assert.False(harness.Coordinator.GetSummary().AutomaticEnabled);
        Assert.True(harness.Coordinator.GetSummary("gi").AutomaticEnabled);
        harness.Cloud.ClearRequests();
        Assert.Equal(HoyoLabManualSyncStatus.NotEnabled, (await harness.Coordinator.SyncAutomaticallyAsync(true)).Status);
        Assert.Empty(harness.Cloud.Requests);
        if (mode is "copy" or "account")
        {
            Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.SyncNowAsync()).Status);
            Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(item => item.Action));
            Assert.False(harness.Coordinator.GetSummary().AutomaticEnabled);
        }
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("role")]
    public async Task Automatic_cas_retry_does_not_restore_a_copy_deleted_after_its_first_pull(string mode)
    {
        var original = BundleWithResource(Older, 80);
        using var harness = new Harness(original);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true).Status);
        SaveBundle(harness.ProtectedRoot, BundleWithResource(Newer, 90), "hsr");
        harness.Cloud.ClearRequests();
        harness.Cloud.OnRequest = request =>
        {
            if (request.Action != "push") return null;
            if (mode == "copy") harness.Cloud.Remove(Fixture.SyncId);
            else harness.Cloud.SeedBundle(Fixture.SyncId, DisplayCode,
                HoyoLabSyncCoordinator.RemoveRoleAt(original, original.SelectedRole!, Now), Now);
            return ConflictResponse();
        };
        Assert.Equal(HoyoLabManualSyncStatus.AutomaticSyncPaused,
            (await harness.Coordinator.SyncAutomaticallyAsync(true)).Status);
        Assert.Equal(["pull", "push", "pull"], harness.Cloud.Requests.Select(item => item.Action));
        Assert.False(harness.Coordinator.GetSummary().AutomaticEnabled);
        Assert.Equal(90, Assert.Single(LoadBundle(harness.ProtectedRoot).Roles).Resource!.Current);
    }

    [Theory]
    [InlineData("opt-out", HoyoLabManualSyncStatus.NotEnabled)]
    [InlineData("authority", HoyoLabManualSyncStatus.Canceled)]
    [InlineData("cancel", HoyoLabManualSyncStatus.Canceled)]
    [InlineData("offline", HoyoLabManualSyncStatus.NetworkUnavailable)]
    [InlineData("malformed", HoyoLabManualSyncStatus.InvalidCloudData)]
    public async Task Automatic_sync_preserves_local_data_and_stops_after_invalidated_or_failed_pull(
        string mode, HoyoLabManualSyncStatus expected)
    {
        using var harness = new Harness(BundleWithResource(Older, 80));
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(true).Status);
        SaveBundle(harness.ProtectedRoot, BundleWithResource(Newer, 90), "hsr");
        var before = ReadTree(harness.ProtectedRoot);
        harness.Cloud.ClearRequests();
        using var cancellation = new CancellationTokenSource();
        harness.Cloud.OnRequest = _ =>
        {
            if (mode == "opt-out") Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.SetAutomaticSync(false).Status);
            if (mode == "authority") harness.Authority.Allowed = false;
            if (mode == "cancel") cancellation.Cancel();
            if (mode == "offline") throw new HttpRequestException("offline");
            return mode == "malformed" ? JsonResponse(HttpStatusCode.OK, "{\"ok\":true}") : null;
        };
        Assert.Equal(expected, (await harness.Coordinator.SyncAutomaticallyAsync(true, cancellation.Token)).Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(item => item.Action));
        AssertTreeEqual(before, ReadTree(harness.ProtectedRoot));
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Null(state.AutomaticSync.HsrLastSyncedAt);
    }

    [Fact]
    public async Task Two_separate_device_roots_share_updates_but_not_automatic_consent_or_remote_role_resurrection()
    {
        using var cloud = new FakeCloud();
        using var first = new Harness(BundleWithResource(Older, 80), cloud);
        using var second = new Harness(BundleWithResource(Older, 80), cloud);
        Assert.NotEqual(first.PublisherRoot, second.PublisherRoot);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await first.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await second.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, first.Coordinator.SetAutomaticSync(true).Status);
        Assert.False(second.Coordinator.GetSummary().AutomaticEnabled);
        SaveBundle(first.ProtectedRoot, BundleWithResource(Newer, 90), "hsr");
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await first.Coordinator.SyncAutomaticallyAsync(true)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await second.Coordinator.SyncNowAsync()).Status);
        Assert.Equal(90, Assert.Single(LoadBundle(second.ProtectedRoot).Roles).Resource!.Current);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, second.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await second.Coordinator.RetryDeletionsAsync()).Status);
        cloud.ClearRequests();
        Assert.Equal(HoyoLabManualSyncStatus.AutomaticSyncPaused,
            (await first.Coordinator.SyncAutomaticallyAsync(true)).Status);
        Assert.Equal(["pull"], cloud.Requests.Select(item => item.Action));
        using var secrets = Secrets(DisplayCode);
        Assert.Empty(cloud.GetBundle(Fixture.SyncId, secrets).Roles);
    }

    [Fact]
    public async Task Connect_uses_pull_then_push_persists_only_derived_state_and_restart_is_quiet()
    {
        using var harness = new Harness(VectorBundle());
        var legacyPath = Path.Combine(
            harness.ProtectedRoot,
            ".protected-role-bindings",
            "hsr.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        var legacyBytes = Encoding.UTF8.GetBytes("frozen-v1-sentinel");
        File.WriteAllBytes(legacyPath, legacyBytes);

        var result = await harness.Coordinator.ConnectAsync(DisplayCode);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(static item => item.Action));
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacyPath));
        Assert.All(harness.Cloud.Requests, request =>
        {
            var body = Encoding.UTF8.GetString(request.Body);
            Assert.DoesNotContain(DisplayCode, body, StringComparison.Ordinal);
            Assert.DoesNotContain(FixtureUid, body, StringComparison.Ordinal);
            Assert.DoesNotContain(FixtureNickname, body, StringComparison.Ordinal);
        });
        Assert.All(
            Directory.EnumerateFiles(harness.PublisherRoot, "*", SearchOption.AllDirectories),
            path =>
            {
                Assert.DoesNotContain(DisplayCode, path, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(FixtureUid, path, StringComparison.Ordinal);
                Assert.DoesNotContain(FixtureNickname, path, StringComparison.Ordinal);
            });

        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            var credential = Assert.IsType<HoyoLabSyncCredential>(state.CurrentCredential);
            Assert.Equal(Fixture.SyncId, credential.SyncId);
            Assert.Equal(Fixture.Token, Convert.ToHexStringLower(credential.Token.Span));
            Assert.NotNull(state.WorkerRevision);
            Assert.Empty(state.PendingDeletions);
            Assert.Empty(state.PendingRoleDeletions);
        }

        var summary = harness.Coordinator.GetSummary();
        Assert.Equal(new(true, true, 0, Now), summary);
        var savedStateBytes = ReadTree(harness.ManagedSlotRoot);
        Assert.All(
            savedStateBytes.Values.SelectMany(static bytes => bytes),
            static _ => { });

        harness.Coordinator.Dispose();
        using var restartHandler = new FakeCloud();
        using var restart = CreateCoordinator(
            harness.PublisherRoot,
            harness.SlotId,
            harness.ProtectedRoot,
            harness.Authority,
            restartHandler);
        Assert.Equal(summary, restart.GetSummary());
        var retry = await restart.RetryDeletionsAsync();
        Assert.Equal(HoyoLabManualSyncStatus.Completed, retry.Status);
        Assert.Empty(restartHandler.Requests);
    }

    [Fact]
    public async Task Newer_remote_observations_save_without_reupload_while_opted_out_capabilities_stay_empty()
    {
        using var harness = new Harness(BundleWithResource(Now.AddHours(-2), 100));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.ClearRequests();

        var remote = BundleWithResource(Now.AddHours(-1), 200);
        harness.Cloud.SeedBundle(Fixture.SyncId, DisplayCode, remote, Now.AddMinutes(-1));

        var result = await harness.Coordinator.SyncNowAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        Assert.Equal(Now.AddMinutes(-1), result.UpdatedAt);
        Assert.Equal(result.UpdatedAt, harness.Coordinator.GetSummary().LastSyncedAt);
        var loaded = LoadBundle(harness.ProtectedRoot);
        var role = Assert.Single(loaded.Roles);
        Assert.Equal(200, role.Resource!.Current);
        Assert.Equal(Now.AddHours(-1), role.Observations.Resources);
        Assert.True(loaded.Consents.Resources);
        Assert.True(loaded.Consents.Achievements);
        Assert.False(loaded.Consents.Inventory);
        Assert.False(loaded.Consents.Builds);
        Assert.False(loaded.Consents.Exploration);
        Assert.False(loaded.Consents.Endgame);
        Assert.False(loaded.Consents.Events);
        Assert.False(loaded.Consents.Currency);
    }

    [Theory]
    [InlineData("gi-resources")]
    [InlineData("hsr-resources-achievements")]
    [InlineData("gi-builds-exploration")]
    [InlineData("hsr-builds")]
    [InlineData("gi-events")]
    [InlineData("hsr-events")]
    public async Task Unchanged_cloud_bundle_is_read_without_another_upload(string capability)
    {
        var bundle = capability switch
        {
            "gi-resources" => GenshinBundleWithResource(Older, 80),
            "gi-builds-exploration" => GenshinBundleWithBuildsAndExploration(Older),
            "hsr-builds" => HsrBundleWithBuilds(Older),
            "gi-events" => GenshinBundleWithEvents(Older),
            "hsr-events" => HsrBundleWithEvents(Older),
            _ => VectorBundle(),
        };
        using var harness = new Harness(bundle);
        Assert.Equal(HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode, gameId: bundle.GameId)).Status);
        var revision = Now.AddMinutes(-1);
        harness.Cloud.SetRevision(Fixture.SyncId, revision, bundle.GameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.SyncNowAsync(gameId: bundle.GameId);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        Assert.Equal(revision, result.UpdatedAt);
        Assert.Equal(revision, harness.Coordinator.GetSummary().LastSyncedAt);
        Assert.Equal(revision, harness.Cloud.GetRevision(Fixture.SyncId, bundle.GameId));
    }

    [Theory]
    [InlineData("gi")]
    [InlineData("hsr")]
    public async Task Local_newer_bundle_still_uploads_even_when_merge_is_locally_idempotent(string gameId)
    {
        var bundle = gameId == "gi" ? GenshinBundleWithResource(Older, 80) : BundleWithResource(Older, 80);
        using var harness = new Harness(bundle);
        Assert.Equal(HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode, gameId: gameId)).Status);
        SaveBundle(harness.ProtectedRoot,
            gameId == "gi" ? GenshinBundleWithResource(Newer, 90) : BundleWithResource(Newer, 90), gameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.SyncNowAsync(gameId: gameId);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(static item => item.Action));
        using var secrets = Secrets(DisplayCode);
        Assert.Equal(90, Assert.Single(harness.Cloud.GetBundle(Fixture.SyncId, secrets, gameId).Roles).Resource!.Current);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("consent")]
    [InlineData("tombstone")]
    public async Task Non_observation_changes_are_not_mistaken_for_an_unchanged_cloud_copy(string change)
    {
        var bundle = TwoRoleBundle(FixtureBinding);
        using var harness = new Harness(bundle);
        Assert.Equal(HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        var local = change switch
        {
            "selection" => bundle with { SelectedRole = SurvivorBinding },
            "consent" => bundle with { Consents = bundle.Consents with { Builds = true } },
            _ => bundle with
            {
                Roles = [bundle.Roles[0]],
                RoleTombstones = [new(SurvivorBinding, Newer)],
            },
        };
        SaveBundle(harness.ProtectedRoot, local, local.GameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.SyncNowAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(static item => item.Action));
        using var secrets = Secrets(DisplayCode);
        var remote = harness.Cloud.GetBundle(Fixture.SyncId, secrets);
        Assert.Equal(local.SelectedRole, remote.SelectedRole);
        Assert.Equal(local.Consents, remote.Consents);
        Assert.Equal(local.RoleTombstones, remote.RoleTombstones);
        harness.Cloud.ClearRequests();
        Assert.Equal(HoyoLabManualSyncStatus.Completed, (await harness.Coordinator.SyncNowAsync()).Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
    }

    [Fact]
    public async Task Equal_time_different_values_stop_without_local_overwrite_or_push()
    {
        var local = BundleWithResource(Now.AddHours(-1), 100);
        using var harness = new Harness(local);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.ClearRequests();
        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            BundleWithResource(Now.AddHours(-1), 200),
            Now.AddMinutes(-1));
        var before = ReadTree(harness.ProtectedRoot);

        var result = await harness.Coordinator.SyncNowAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        AssertTreeEqual(before, ReadTree(harness.ProtectedRoot));
    }

    [Fact]
    public async Task First_cas_conflict_repulls_and_does_not_reupload_an_already_current_bundle()
    {
        using var harness = new Harness(BundleWithResource(Now.AddHours(-2), 100));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.Remove(Fixture.SyncId);
        harness.Cloud.ClearRequests();
        var remoteRevision = Now.AddMinutes(-1);
        harness.Cloud.OnRequest = request =>
        {
            if (request.Action != "push" || harness.Cloud.Requests.Count(item => item.Action == "push") != 1)
                return null;
            harness.Cloud.SeedBundle(
                Fixture.SyncId,
                DisplayCode,
                BundleWithResource(Now.AddHours(-1), 200),
                remoteRevision);
            return ConflictResponse();
        };

        var result = await harness.Coordinator.SyncNowAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(
            ["pull", "push", "pull"],
            harness.Cloud.Requests.Select(static item => item.Action));
        var push = Assert.Single(harness.Cloud.Requests, static item => item.Action == "push");
        Assert.Equal(JsonValueKind.Null, push.Root.GetProperty("baseUpdatedAt").ValueKind);
        Assert.False(push.Root.TryGetProperty("force", out _));
        Assert.Equal(remoteRevision, result.UpdatedAt);
        Assert.Equal(remoteRevision, harness.Cloud.GetRevision(Fixture.SyncId));
        var loaded = LoadBundle(harness.ProtectedRoot);
        Assert.Equal(200, Assert.Single(loaded.Roles).Resource!.Current);
    }

    [Fact]
    public async Task Second_cas_conflict_stops_without_force_or_a_third_push()
    {
        using var harness = new Harness(BundleWithResource(Now.AddHours(-2), 100));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.Remove(Fixture.SyncId);
        harness.Cloud.ClearRequests();
        harness.Cloud.OnRequest = request =>
            request.Action == "push" ? ConflictResponse() : null;
        var before = ReadTree(harness.ProtectedRoot);

        var result = await harness.Coordinator.SyncNowAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(
            ["pull", "push", "pull", "push"],
            harness.Cloud.Requests.Select(static item => item.Action));
        Assert.All(harness.Cloud.Requests, static request =>
            Assert.False(request.Root.TryGetProperty("force", out _)));
        AssertTreeEqual(before, ReadTree(harness.ProtectedRoot));
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("malformed")]
    [InlineData("offline")]
    [InlineData("canceled")]
    [InlineData("stale-authority")]
    public async Task Invalid_remote_or_authority_operations_preserve_local_bundle_and_never_push(
        string mode)
    {
        using var harness = new Harness(BundleWithResource(Now.AddHours(-2), 100));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.ClearRequests();
        var before = ReadTree(harness.ProtectedRoot);
        var expected = mode switch
        {
            "wrong-key" or "malformed" => HoyoLabManualSyncStatus.InvalidCloudData,
            "offline" => HoyoLabManualSyncStatus.NetworkUnavailable,
            _ => HoyoLabManualSyncStatus.Canceled,
        };

        switch (mode)
        {
            case "wrong-key":
                using (var wrongSecrets = Secrets(AlternateCode))
                {
                    Assert.True(HoyoLabSyncCrypto.TryEncryptBundle(
                        wrongSecrets,
                        BundleWithResource(Now.AddHours(-1), 200),
                        Now,
                        FixedNonce(9),
                        out var wrongEnvelope));
                    harness.Cloud.SeedRaw(
                        Fixture.SyncId,
                        wrongEnvelope!,
                        Now.AddMinutes(-1));
                }
                break;
            case "malformed":
                harness.Cloud.OnRequest = request => request.Action == "pull"
                    ? JsonResponse(HttpStatusCode.OK, "{\"ok\":true}")
                    : null;
                break;
            case "offline":
                harness.Cloud.OnRequest = _ => throw new HttpRequestException("offline");
                break;
            case "stale-authority":
                harness.Cloud.OnRequest = request =>
                {
                    if (request.Action == "pull") harness.Authority.Allowed = false;
                    return null;
                };
                break;
        }

        HoyoLabManualSyncResult result;
        if (mode == "canceled")
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            result = await harness.Coordinator.SyncNowAsync(cancellation.Token);
        }
        else
        {
            result = await harness.Coordinator.SyncNowAsync();
        }

        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain("push", harness.Cloud.Requests.Select(static item => item.Action));
        AssertTreeEqual(before, ReadTree(harness.ProtectedRoot));
    }

    [Fact]
    public async Task Detach_all_scope_clears_current_before_request_and_restart_retries_delete_account()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.ClearRequests();

        var detached = harness.Coordinator.Detach(HoyoLabSyncStateStore.AllHoyoScope);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, detached.Status);
        Assert.Empty(harness.Cloud.Requests);
        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            Assert.Null(state.CurrentCredential);
            Assert.Null(state.WorkerRevision);
            var pending = Assert.Single(state.PendingDeletions);
            Assert.Equal(HoyoLabSyncStateStore.AllHoyoScope, pending.Scope);
            Assert.Equal(Fixture.SyncId, pending.SyncId);
            Assert.Equal(Fixture.Token, Convert.ToHexStringLower(pending.Token.Span));
        }

        var accountsRoot = Path.Combine(harness.PublisherRoot, "Accounts", "HoYoLAB");
        var index = Path.Combine(accountsRoot, "index.bin");
        Directory.CreateDirectory(accountsRoot);
        File.WriteAllBytes(index, [1, 2, 3]);
        Directory.Delete(accountsRoot, recursive: true);
        harness.Coordinator.Dispose();

        using var retryHandler = new FakeCloud();
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            retryHandler);
        var result = await retry.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        var request = Assert.Single(retryHandler.Requests);
        Assert.Equal("delete-account", request.Action);
        Assert.Equal(Fixture.SyncId, request.SyncId);
        Assert.Equal(Fixture.Token, request.Root.GetProperty("token").GetString());
        Assert.False(Directory.Exists(harness.ManagedSlotRoot));
    }

    [Fact]
    public async Task Remove_local_slot_intent_survives_restart_and_runs_before_delete_account()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.ClearRequests();

        var detached = harness.Coordinator.Detach(
            HoyoLabSyncStateStore.AllHoyoScope,
            removeLocalSlot: true);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, detached.Status);
        Assert.Empty(harness.Cloud.Requests);
        using (var state = LoadState(harness.ManagedSlotRoot))
            Assert.True(Assert.Single(state.PendingDeletions).RemoveLocalSlot);

        var slotContainer = Path.Combine(
            harness.PublisherRoot,
            "Accounts",
            "HoYoLAB",
            harness.SlotId);
        Assert.True(Directory.Exists(slotContainer));
        harness.Coordinator.Dispose();
        using var retryHandler = new FakeCloud();
        var order = new List<string>();
        retryHandler.OnRequest = request =>
        {
            order.Add(request.Action);
            return null;
        };
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            retryHandler);

        var result = await retry.RetryDeletionsAsync(
            removeLocalSlot: id =>
            {
                Assert.Equal(harness.SlotId, id);
                using var state = LoadState(harness.ManagedSlotRoot);
                Assert.True(Assert.Single(state.PendingDeletions).RemoveLocalSlot);
                order.Add("remove");
                if (Directory.Exists(slotContainer)) Directory.Delete(slotContainer, recursive: true);
                return true;
            });

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal("remove", order[0]);
        Assert.Equal("delete-account", order[^1]);
        Assert.All(order.Take(order.Count - 1), static step => Assert.Equal("remove", step));
        Assert.False(Directory.Exists(slotContainer));
        Assert.False(Directory.Exists(harness.ManagedSlotRoot));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    public async Task Remove_local_slot_intent_without_successful_callback_stays_pending_and_skips_http(
        string callbackMode)
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.Detach(
                HoyoLabSyncStateStore.AllHoyoScope,
                removeLocalSlot: true).Status);
        harness.Coordinator.Dispose();

        var slotContainer = Path.Combine(
            harness.PublisherRoot,
            "Accounts",
            "HoYoLAB",
            harness.SlotId);
        using var retryHandler = new FakeCloud();
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            retryHandler);
        Func<string, bool>? removeLocalSlot = callbackMode == "false"
            ? static _ => false
            : null;

        var result = await retry.RetryDeletionsAsync(removeLocalSlot: removeLocalSlot);

        Assert.Equal(HoyoLabManualSyncStatus.LocalStorageUnavailable, result.Status);
        Assert.Empty(retryHandler.Requests);
        Assert.True(Directory.Exists(slotContainer));
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.True(Assert.Single(state.PendingDeletions).RemoveLocalSlot);
    }

    [Fact]
    public async Task Cloud_only_detach_never_invokes_local_slot_removal_callback()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.Detach(HoyoLabSyncStateStore.AllHoyoScope).Status);
        harness.Coordinator.Dispose();

        var slotContainer = Path.Combine(
            harness.PublisherRoot,
            "Accounts",
            "HoYoLAB",
            harness.SlotId);
        using var retryHandler = new FakeCloud();
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            retryHandler);
        var callbackCalls = 0;

        var result = await retry.RetryDeletionsAsync(
            removeLocalSlot: _ =>
            {
                callbackCalls++;
                return false;
            });

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(0, callbackCalls);
        Assert.Equal("delete-account", Assert.Single(retryHandler.Requests).Action);
        Assert.True(Directory.Exists(slotContainer));
        Assert.False(Directory.Exists(harness.ManagedSlotRoot));
    }

    [Fact]
    public async Task Offline_detach_retry_keeps_pending_and_local_only_detach_keeps_prior_pending()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Coordinator.Detach(HoyoLabSyncStateStore.AllHoyoScope);
        harness.Coordinator.Dispose();

        using var offlineHandler = new FakeCloud
        {
            OnRequest = _ => throw new HttpRequestException("offline"),
        };
        using (var offline = CreateCoordinator(
                   harness.PublisherRoot,
                   slotId: null,
                   protectedSlotRoot: null,
                   harness.Authority,
                   offlineHandler))
        {
            Assert.Equal(
                HoyoLabManualSyncStatus.NetworkUnavailable,
                (await offline.RetryDeletionsAsync()).Status);
        }

        using (var state = LoadState(harness.ManagedSlotRoot))
            Assert.Single(state.PendingDeletions);

        using var localOnlyHarness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await localOnlyHarness.Coordinator.ConnectAsync(DisplayCode)).Status);
        using (var current = LoadState(localOnlyHarness.ManagedSlotRoot))
        {
            using var pending = new HoyoLabPendingDeletion(
                current.CurrentCredential!.SyncId,
                current.CurrentCredential.Token,
                HoyoLabSyncStateStore.HsrScope,
                "prior-pending",
                Now);
            var store = new HoyoLabSyncStateStore(
                localOnlyHarness.ManagedSlotRoot,
                localOnlyHarness.Protector,
                localOnlyHarness.Files,
                localOnlyHarness.Clock);
            Assert.True(store.TryEnqueuePendingDeletion(pending));
        }
        localOnlyHarness.Cloud.ClearRequests();

        var localOnly = localOnlyHarness.Coordinator.Detach();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, localOnly.Status);
        Assert.Empty(localOnlyHarness.Cloud.Requests);
        using var preserved = LoadState(localOnlyHarness.ManagedSlotRoot);
        Assert.Null(preserved.CurrentCredential);
        Assert.Equal("prior-pending", Assert.Single(preserved.PendingDeletions).OperationId);
    }

    [Fact]
    public async Task Detach_all_local_clears_current_slots_preserves_pending_and_never_uploads()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);

        var otherRoot = Path.Combine(
            harness.PublisherRoot,
            HoyoLabSyncCoordinator.ManagedDirectoryName,
            SlotId(2));
        using (var secrets = Secrets(AlternateCode))
        using (var credential = CredentialFor(secrets))
        {
            var otherStore = new HoyoLabSyncStateStore(
                otherRoot,
                harness.Protector,
                harness.Files,
                harness.Clock);
            Assert.True(otherStore.TrySetCurrentCredential(credential));
            using var pending = new HoyoLabPendingDeletion(
                credential.SyncId,
                credential.Token,
                HoyoLabSyncStateStore.AllHoyoScope,
                "all-local-pending",
                Now);
            Assert.True(otherStore.TryEnqueuePendingDeletion(pending));
        }

        harness.Cloud.ClearRequests();
        var result = harness.Coordinator.DetachAllLocal();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Empty(harness.Cloud.Requests);
        Assert.Null(TryLoadState(harness.ManagedSlotRoot));
        using var other = LoadState(otherRoot);
        Assert.Null(other.CurrentCredential);
        Assert.Null(other.WorkerRevision);
        Assert.Equal("all-local-pending", Assert.Single(other.PendingDeletions).OperationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_local_cleanup_prepass_finishes_all_slots_before_network_or_retains_later_failure(
        bool failLaterSlot)
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);

        var firstSlotId = harness.SlotId;
        var firstSlotContainer = Path.Combine(
            harness.PublisherRoot,
            "Accounts",
            "HoYoLAB",
            firstSlotId);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.Detach(
                HoyoLabSyncStateStore.AllHoyoScope,
                removeLocalSlot: true).Status);

        var secondSlotId = SlotId(2);
        var secondSlotContainer = Path.Combine(
            harness.PublisherRoot,
            "Accounts",
            "HoYoLAB",
            secondSlotId);
        Directory.CreateDirectory(secondSlotContainer);
        var secondManagedRoot = Path.Combine(
            harness.PublisherRoot,
            HoyoLabSyncCoordinator.ManagedDirectoryName,
            secondSlotId);
        using (var secrets = Secrets(AlternateCode))
        using (var credential = CredentialFor(secrets))
        {
            var store = new HoyoLabSyncStateStore(
                secondManagedRoot,
                harness.Protector,
                harness.Files,
                harness.Clock);
            Assert.True(store.TrySetCurrentCredential(credential));
            using var deletion = new HoyoLabPendingDeletion(
                credential.SyncId,
                credential.Token,
                HoyoLabSyncStateStore.AllHoyoScope,
                "second-local-slot",
                Now,
                removeLocalSlot: true);
            Assert.True(store.TryDetachCurrentCredential(credential, deletion));
        }

        var events = new List<string>();
        using var handler = new FakeCloud();
        handler.OnRequest = _ =>
        {
            events.Add("network");
            if (!failLaterSlot) throw new HttpRequestException("network failure");
            return null;
        };
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            handler,
            harness.Files,
            harness.Protector);

        var result = await retry.RetryDeletionsAsync(
            removeLocalSlot: id =>
            {
                events.Add("local:" + id);
                if (failLaterSlot && id == secondSlotId) return false;
                var target = id == firstSlotId
                    ? firstSlotContainer
                    : id == secondSlotId
                        ? secondSlotContainer
                        : throw new InvalidOperationException("Unexpected slot.");
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                return true;
            });

        if (failLaterSlot)
        {
            Assert.Equal(HoyoLabManualSyncStatus.LocalStorageUnavailable, result.Status);
            Assert.Empty(handler.Requests);
            Assert.DoesNotContain("network", events);
            Assert.Contains("local:" + firstSlotId, events);
            Assert.Contains("local:" + secondSlotId, events);
            Assert.False(Directory.Exists(firstSlotContainer));
            Assert.True(Directory.Exists(secondSlotContainer));
            using var firstState = LoadState(harness.ManagedSlotRoot);
            using var secondState = LoadState(secondManagedRoot);
            Assert.Single(firstState.PendingDeletions);
            Assert.Single(secondState.PendingDeletions);
            return;
        }

        Assert.Equal(HoyoLabManualSyncStatus.NetworkUnavailable, result.Status);
        var firstNetwork = events.IndexOf("network");
        Assert.True(firstNetwork > 0);
        Assert.Contains("local:" + firstSlotId, events.Take(firstNetwork));
        Assert.Contains("local:" + secondSlotId, events.Take(firstNetwork));
        Assert.False(Directory.Exists(firstSlotContainer));
        Assert.False(Directory.Exists(secondSlotContainer));
        Assert.Single(handler.Requests);
        using (var firstState = LoadState(harness.ManagedSlotRoot))
        using (var secondState = LoadState(secondManagedRoot))
        {
            Assert.Single(firstState.PendingDeletions);
            Assert.Single(secondState.PendingDeletions);
        }
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("revoked")]
    public async Task Detach_without_current_credential_rejects_cancellation_or_revocation_without_mutation(
        string invalidation)
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.Detach(HoyoLabSyncStateStore.AllHoyoScope).Status);

        var slotRoot = Path.Combine(
            harness.PublisherRoot,
            "Accounts",
            "HoYoLAB",
            harness.SlotId);
        var beforeSlot = ReadTree(slotRoot);
        var beforeSession = ReadTree(harness.ManagedSlotRoot);
        var beforeSnapshots = ReadTree(harness.ProtectedRoot);
        harness.Cloud.ClearRequests();
        using var canceled = new CancellationTokenSource();
        if (invalidation == "canceled")
            canceled.Cancel();
        else
            harness.Authority.Allowed = false;

        var result = harness.Coordinator.Detach(
            HoyoLabSyncStateStore.AllHoyoScope,
            canceled.Token);

        Assert.Equal(HoyoLabManualSyncStatus.Canceled, result.Status);
        Assert.Empty(harness.Cloud.Requests);
        AssertTreeEqual(beforeSlot, ReadTree(slotRoot));
        AssertTreeEqual(beforeSession, ReadTree(harness.ManagedSlotRoot));
        AssertTreeEqual(beforeSnapshots, ReadTree(harness.ProtectedRoot));
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Null(state.CurrentCredential);
        Assert.Single(state.PendingDeletions);
    }

    [Fact]
    public async Task Pending_same_sync_under_detached_root_blocks_new_connect()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Coordinator.Detach(HoyoLabSyncStateStore.AllHoyoScope);
        harness.Coordinator.Dispose();
        using var handler = new FakeCloud();
        using var reconnect = CreateCoordinator(
            harness.PublisherRoot,
            harness.SlotId,
            harness.ProtectedRoot,
            harness.Authority,
            handler);

        var result = await reconnect.ConnectAsync(DisplayCode);

        Assert.Equal(HoyoLabManualSyncStatus.DeletionPending, result.Status);
        Assert.Empty(handler.Requests);
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Null(state.CurrentCredential);
        Assert.Single(state.PendingDeletions);
    }

    [Fact]
    public async Task Active_identity_in_another_slot_rejects_connect_without_writing_or_uploading()
    {
        using var harness = new Harness(VectorBundle());
        var otherRoot = Path.Combine(
            harness.PublisherRoot,
            HoyoLabSyncCoordinator.ManagedDirectoryName,
            SlotId(2));
        using (var secrets = Secrets(DisplayCode))
        using (var credential = CredentialFor(secrets))
        {
            var otherStore = new HoyoLabSyncStateStore(
                otherRoot,
                harness.Protector,
                harness.Files,
                harness.Clock);
            Assert.True(otherStore.TrySetCurrentCredential(credential));
        }
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.ConnectAsync(DisplayCode);

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Empty(harness.Cloud.Requests);
        Assert.Null(TryLoadState(harness.ManagedSlotRoot));
    }

    [Fact]
    public async Task Empty_detach_root_is_removed_and_can_be_reused()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, harness.Coordinator.Detach().Status);
        Assert.False(Directory.Exists(harness.ManagedSlotRoot));
        harness.Coordinator.Dispose();

        using var handler = new FakeCloud();
        using var reconnect = CreateCoordinator(
            harness.PublisherRoot,
            harness.SlotId,
            harness.ProtectedRoot,
            harness.Authority,
            handler);
        var result = await reconnect.ConnectAsync(DisplayCode);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], handler.Requests.Select(static item => item.Action));
    }

    [Fact]
    public async Task Rotate_proves_target_absent_persists_compensation_before_push_and_promotes_before_old_delete()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.ClearRequests();
        string? replacementSyncId = null;
        var sawCompensation = false;
        var sawOldPendingAfterPromotion = false;
        harness.Cloud.OnRequest = request =>
        {
            if (request.Action == "status")
            {
                replacementSyncId = request.SyncId;
                Assert.NotEqual(Fixture.SyncId, replacementSyncId);
            }
            else if (request.Action == "push")
            {
                Assert.Equal(replacementSyncId, request.SyncId);
                using var state = LoadState(harness.ManagedSlotRoot);
                Assert.Equal(Fixture.SyncId, state.CurrentCredential!.SyncId);
                sawCompensation = state.PendingDeletions.Any(item =>
                    item.SyncId == replacementSyncId
                    && item.Scope == HoyoLabSyncStateStore.AllHoyoScope);
            }
            else if (request.Action == "delete-account")
            {
                using var state = LoadState(harness.ManagedSlotRoot);
                sawOldPendingAfterPromotion = state.CurrentCredential is not null
                    && state.CurrentCredential.SyncId == replacementSyncId
                    && state.PendingDeletions.Any(item => item.SyncId == Fixture.SyncId);
            }
            return null;
        };

        var result = await harness.Coordinator.RotateAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.NotNull(result.RecoveryCode);
        Assert.True(sawCompensation);
        Assert.True(sawOldPendingAfterPromotion);
        Assert.Equal(
            ["pull", "pull", "status", "status", "push", "delete-account"],
            harness.Cloud.Requests.Select(static item => item.Action));
        using var replacementSecrets = Secrets(result.RecoveryCode!);
        Assert.Equal(replacementSyncId, replacementSecrets.SyncId);
        using var stateAfter = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(replacementSyncId, stateAfter.CurrentCredential!.SyncId);
        Assert.Empty(stateAfter.PendingDeletions);
    }

    [Fact]
    public async Task Rotate_authority_loss_after_new_push_keeps_old_current_and_restart_deletes_replacement()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.ClearRequests();
        string? replacementSyncId = null;
        harness.Cloud.OnRequest = request =>
        {
            if (request.Action == "push")
            {
                replacementSyncId = request.SyncId;
                harness.Authority.Allowed = false;
            }
            return null;
        };

        var result = await harness.Coordinator.RotateAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Canceled, result.Status);
        Assert.Null(result.RecoveryCode);
        Assert.NotNull(replacementSyncId);
        using (var failed = LoadState(harness.ManagedSlotRoot))
        {
            Assert.Equal(Fixture.SyncId, failed.CurrentCredential!.SyncId);
            Assert.Contains(failed.PendingDeletions, item => item.SyncId == replacementSyncId);
            Assert.DoesNotContain(failed.PendingDeletions, item => item.SyncId == Fixture.SyncId);
        }

        harness.Authority.Allowed = true;
        harness.Coordinator.Dispose();
        using var retryHandler = new FakeCloud();
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            retryHandler);
        var retryResult = await retry.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, retryResult.Status);
        var request = Assert.Single(retryHandler.Requests);
        Assert.Equal("delete-account", request.Action);
        Assert.Equal(replacementSyncId, request.SyncId);
        Assert.False(request.Root.TryGetProperty("baseUpdatedAt", out _));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Fixture.SyncId, after.CurrentCredential!.SyncId);
        Assert.Empty(after.PendingDeletions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rotate_conditions_old_cleanup_to_the_copied_revision_and_preserves_a_newer_old_copy(
        bool oldCopyExists)
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        DateTimeOffset? oldRevision = oldCopyExists ? Now.AddMinutes(-2) : null;
        var newerRevision = Now.AddMinutes(-1);
        var newerBundle = BundleWithResource(Now.AddMinutes(-1), 250);
        if (oldRevision is { } existingOldRevision)
            harness.Cloud.SeedBundle(Fixture.SyncId, DisplayCode, VectorBundle(), existingOldRevision);
        else
            harness.Cloud.Remove(Fixture.SyncId);
        harness.Cloud.ClearRequests();
        harness.Cloud.OnRequest = request =>
        {
            if (request.Action == "status")
                harness.Cloud.SeedBundle(
                    Fixture.SyncId,
                    DisplayCode,
                    newerBundle,
                    newerRevision);
            return null;
        };

        var result = await harness.Coordinator.RotateAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.NotNull(result.RecoveryCode);
        var oldDelete = Assert.Single(
            harness.Cloud.Requests,
            static item => item.Action == "delete-account");
        Assert.Equal(Fixture.SyncId, oldDelete.SyncId);
        var oldCondition = oldDelete.Root.GetProperty("baseUpdatedAtByGame");
        Assert.Equal(["gi", "hsr"], oldCondition.EnumerateObject()
            .Select(static property => property.Name).Order(StringComparer.Ordinal));
        var oldHsrCondition = oldCondition.GetProperty("hsr");
        var oldGiCondition = oldCondition.GetProperty("gi");
        if (oldRevision is { } expectedOldRevision)
            Assert.Equal(FormatTimestamp(expectedOldRevision), oldHsrCondition.GetString());
        else
            Assert.Equal(JsonValueKind.Null, oldHsrCondition.ValueKind);
        Assert.Equal(JsonValueKind.Null, oldGiCondition.ValueKind);
        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            var pending = Assert.Single(state.PendingDeletions);
            Assert.Equal(Fixture.SyncId, pending.SyncId);
            Assert.True(pending.RequireRevisionMatch);
            Assert.Null(pending.ExpectedRevision);
            Assert.Equal(new HoyoLabGameRevisions(oldRevision, null), pending.ExpectedRevisionsByGame);
        }
        Assert.Equal(newerRevision, harness.Cloud.GetRevision(Fixture.SyncId));
        using (var oldSecrets = Secrets(DisplayCode))
            AssertBundleContentEqual(newerBundle, harness.Cloud.GetBundle(Fixture.SyncId, oldSecrets));

        harness.Cloud.ClearRequests();
        var retry = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, retry.Status);
        var retryDelete = Assert.Single(harness.Cloud.Requests);
        Assert.Equal("delete-account", retryDelete.Action);
        Assert.Equal(
            oldDelete.Root.GetProperty("baseUpdatedAtByGame").GetRawText(),
            retryDelete.Root.GetProperty("baseUpdatedAtByGame").GetRawText());
        Assert.Equal(newerRevision, harness.Cloud.GetRevision(Fixture.SyncId));
        using (var after = LoadState(harness.ManagedSlotRoot))
            Assert.Single(after.PendingDeletions);

        if (oldCopyExists)
            harness.Cloud.RemoveAccount(Fixture.SyncId);
        else
            harness.Cloud.Remove(Fixture.SyncId);
        harness.Cloud.ClearRequests();
        var absentRetry = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, absentRetry.Status);
        var absentDelete = Assert.Single(
            harness.Cloud.Requests,
            static item => item.Action == "delete-account");
        Assert.Equal(Fixture.SyncId, absentDelete.SyncId);
        using var completed = LoadState(harness.ManagedSlotRoot);
        Assert.Empty(completed.PendingDeletions);
    }

    [Fact]
    public async Task Rotate_old_delete_failure_preserves_new_current_and_old_pending()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.ClearRequests();
        string? replacementSyncId = null;
        harness.Cloud.OnRequest = request =>
        {
            if (request.Action == "status") replacementSyncId = request.SyncId;
            if (request.Action == "delete-account") throw new HttpRequestException("old delete offline");
            return null;
        };

        var result = await harness.Coordinator.RotateAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.NotNull(result.RecoveryCode);
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(replacementSyncId, state.CurrentCredential!.SyncId);
        var oldPending = Assert.Single(state.PendingDeletions);
        Assert.Equal(Fixture.SyncId, oldPending.SyncId);
        Assert.Equal(["pull", "pull", "status", "status", "push", "delete-account"],
            harness.Cloud.Requests.Select(static item => item.Action));
    }

    [Fact]
    public async Task Dual_game_sync_and_rotation_preserve_both_game_bundles()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        SaveBundle(
            harness.ProtectedRoot,
            GenshinBundleWithResource(Now.AddHours(-2), 80),
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();

        var genshinSync = await harness.Coordinator.SyncNowAsync(
            gameId: HoyoLabGameBundleRules.GenshinGameId);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, genshinSync.Status);
        Assert.Equal(
            ["pull", "push"],
            harness.Cloud.Requests.Select(static item => item.Action));
        Assert.All(harness.Cloud.Requests, request =>
            Assert.Equal(HoyoLabGameBundleRules.GenshinGameId, request.GameId));

        harness.Cloud.ClearRequests();
        var rotation = await harness.Coordinator.RotateAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, rotation.Status);
        Assert.NotNull(rotation.RecoveryCode);
        Assert.Equal(
            ["pull", "pull", "status", "status", "push", "push", "delete-account"],
            harness.Cloud.Requests.Select(static item => item.Action));
        Assert.Equal(
            ["hsr", "gi", "hsr", "gi", "hsr", "gi", "hsr"],
            harness.Cloud.Requests.Select(static item => item.GameId));
        using var replacementSecrets = Secrets(rotation.RecoveryCode!);
        Assert.Equal(
            HoyoLabGameBundleRules.GameId,
            harness.Cloud.GetBundle(replacementSecrets.SyncId, replacementSecrets).GameId);
        Assert.Equal(
            HoyoLabGameBundleRules.GenshinGameId,
            harness.Cloud.GetBundle(
                replacementSecrets.SyncId,
                replacementSecrets,
                HoyoLabGameBundleRules.GenshinGameId).GameId);
        Assert.False(harness.Cloud.HasCopy(Fixture.SyncId, HoyoLabGameBundleRules.GameId));
        Assert.False(harness.Cloud.HasCopy(Fixture.SyncId, HoyoLabGameBundleRules.GenshinGameId));
    }

    [Fact]
    public async Task Failed_second_rotation_push_keeps_old_current_and_restart_cleans_replacement()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        SaveBundle(
            harness.ProtectedRoot,
            GenshinBundleWithResource(Now.AddHours(-2), 80),
            HoyoLabGameBundleRules.GenshinGameId);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.SyncNowAsync(
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);
        harness.Cloud.ClearRequests();
        string? replacementSyncId = null;
        var failGenshinPush = true;
        harness.Cloud.OnRequest = request =>
        {
            if (request.Action == "status") replacementSyncId = request.SyncId;
            if (request.Action == "push"
                && request.GameId == HoyoLabGameBundleRules.GenshinGameId
                && failGenshinPush)
            {
                failGenshinPush = false;
                throw new HttpRequestException("second game push offline");
            }
            return null;
        };

        var failed = await harness.Coordinator.RotateAsync();

        Assert.Equal(HoyoLabManualSyncStatus.NetworkUnavailable, failed.Status);
        Assert.Null(failed.RecoveryCode);
        Assert.NotNull(replacementSyncId);
        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            Assert.Equal(Fixture.SyncId, state.CurrentCredential!.SyncId);
            var compensation = Assert.Single(state.PendingDeletions);
            Assert.Equal(replacementSyncId, compensation.SyncId);
            Assert.Equal(HoyoLabSyncStateStore.AllHoyoScope, compensation.Scope);
        }
        Assert.True(harness.Cloud.HasCopy(Fixture.SyncId, HoyoLabGameBundleRules.GameId));
        Assert.True(harness.Cloud.HasCopy(Fixture.SyncId, HoyoLabGameBundleRules.GenshinGameId));
        Assert.True(harness.Cloud.HasCopy(replacementSyncId!, HoyoLabGameBundleRules.GameId));
        Assert.False(harness.Cloud.HasCopy(replacementSyncId!, HoyoLabGameBundleRules.GenshinGameId));

        harness.Coordinator.Dispose();
        harness.Cloud.ClearRequests();
        using var restart = CreateCoordinator(
            harness.PublisherRoot,
            harness.SlotId,
            harness.ProtectedRoot,
            harness.Authority,
            harness.Cloud);
        var cleanup = await restart.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, cleanup.Status);
        Assert.Equal("delete-account", Assert.Single(harness.Cloud.Requests).Action);
        Assert.False(harness.Cloud.HasCopy(replacementSyncId!, HoyoLabGameBundleRules.GameId));
        Assert.False(harness.Cloud.HasCopy(replacementSyncId!, HoyoLabGameBundleRules.GenshinGameId));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Fixture.SyncId, after.CurrentCredential!.SyncId);
        Assert.Empty(after.PendingDeletions);
    }

    [Fact]
    public async Task Remote_only_genshin_bundle_is_copied_during_rotation()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        var genshin = GenshinBundleWithResource(Now.AddHours(-1), 120);
        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            genshin,
            Now.AddMinutes(-1),
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();

        var rotation = await harness.Coordinator.RotateAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, rotation.Status);
        using var replacementSecrets = Secrets(rotation.RecoveryCode!);
        Assert.Equal(
            HoyoLabGameBundleRules.GameId,
            harness.Cloud.GetBundle(replacementSecrets.SyncId, replacementSecrets).GameId);
        Assert.Equal(
            HoyoLabGameBundleRules.GenshinGameId,
            harness.Cloud.GetBundle(
                replacementSecrets.SyncId,
                replacementSecrets,
                HoyoLabGameBundleRules.GenshinGameId).GameId);
        Assert.False(harness.Cloud.HasCopy(Fixture.SyncId, HoyoLabGameBundleRules.GameId));
        Assert.False(harness.Cloud.HasCopy(Fixture.SyncId, HoyoLabGameBundleRules.GenshinGameId));
        var localGenshin = new HoyoLabGameBundleStore(
            harness.ProtectedRoot,
            harness.Protector,
            harness.Files,
            harness.Clock,
            HoyoLabGameBundleRules.GenshinGameId).TryLoad();
        Assert.Null(localGenshin);
    }

    [Fact]
    public async Task Game_detach_deletes_only_the_selected_remote_copy_and_keeps_current_credential()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        SaveBundle(
            harness.ProtectedRoot,
            GenshinBundleWithResource(Now.AddHours(-2), 80),
            HoyoLabGameBundleRules.GenshinGameId);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.SyncNowAsync(
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);
        harness.Cloud.ClearRequests();

        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.Detach(HoyoLabSyncStateStore.GenshinScope).Status);
        using (var queued = LoadState(harness.ManagedSlotRoot))
        {
            Assert.Equal(Fixture.SyncId, queued.CurrentCredential!.SyncId);
            Assert.Equal(
                HoyoLabSyncStateStore.GenshinScope,
                Assert.Single(queued.PendingDeletions).Scope);
        }

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        var request = Assert.Single(harness.Cloud.Requests);
        Assert.Equal("delete", request.Action);
        Assert.Equal(HoyoLabGameBundleRules.GenshinGameId, request.GameId);
        Assert.True(harness.Cloud.HasCopy(Fixture.SyncId, HoyoLabGameBundleRules.GameId));
        Assert.False(harness.Cloud.HasCopy(Fixture.SyncId, HoyoLabGameBundleRules.GenshinGameId));
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Fixture.SyncId, state.CurrentCredential!.SyncId);
        Assert.Empty(state.PendingDeletions);
        Assert.Equal(
            HoyoLabGameBundleRules.GameId,
            LoadBundle(harness.ProtectedRoot).GameId);
        Assert.Equal(
            HoyoLabGameBundleRules.GenshinGameId,
            LoadBundle(harness.ProtectedRoot, HoyoLabGameBundleRules.GenshinGameId).GameId);
    }

    [Fact]
    public async Task Queue_role_deletion_preserves_binding_cutoffs_timestamp_and_is_idempotent()
    {
        var local = TwoRoleBundle(selected: FixtureBinding);
        using var harness = new Harness(local);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);

        var first = harness.Coordinator.QueueRoleDeletion(FixtureBinding);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, first.Status);
        Assert.NotNull(first.RoleDeletionAt);
        using var before = LoadState(harness.ManagedSlotRoot);
        var pending = Assert.Single(before.PendingRoleDeletions);
        Assert.Equal(Fixture.SyncId, pending.SyncId);
        Assert.Equal(Fixture.Token, Convert.ToHexStringLower(pending.Token.Span));
        Assert.Equal(FixtureBinding, pending.Binding);
        Assert.Equal(
            VectorBundle().Roles[0].Observations.Resources,
            pending.KnownResourcesAt);
        Assert.Equal(
            VectorBundle().Roles[0].Observations.Achievements,
            pending.KnownAchievementsAt);
        Assert.Equal(first.RoleDeletionAt, pending.DeletedAt);
        var operationId = pending.OperationId;

        var duplicate = harness.Coordinator.QueueRoleDeletion(FixtureBinding);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, duplicate.Status);
        Assert.Equal(first.RoleDeletionAt, duplicate.RoleDeletionAt);
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(operationId, Assert.Single(after.PendingRoleDeletions).OperationId);
    }

    [Fact]
    public async Task Role_queue_persisted_before_local_cleanup_is_replayed_after_restart()
    {
        using var harness = new Harness(TwoRoleBundle(selected: FixtureBinding));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Authority.RemainingAllows = 1;

        var queued = harness.Coordinator.QueueRoleDeletion(FixtureBinding);

        Assert.Equal(HoyoLabManualSyncStatus.Canceled, queued.Status);
        using (var state = LoadState(harness.ManagedSlotRoot))
            Assert.Single(state.PendingRoleDeletions);
        Assert.Contains(LoadBundle(harness.ProtectedRoot).Roles, item =>
            item.Role.Binding == FixtureBinding);

        harness.Authority.RemainingAllows = null;
        using var retryHandler = new FakeCloud();
        harness.Cloud.CopyTo(retryHandler, Fixture.SyncId);
        harness.Coordinator.Dispose();
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            retryHandler,
            harness.Files,
            harness.Protector);

        var result = await retry.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], retryHandler.Requests.Select(static item => item.Action));
        Assert.DoesNotContain(LoadBundle(harness.ProtectedRoot).Roles, item =>
            item.Role.Binding == FixtureBinding);
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Empty(after.PendingRoleDeletions);
    }

    [Fact]
    public async Task Selected_v1_cleanup_failure_after_snapshot_delete_is_replayed_before_intent_completion()
    {
        using var harness = new Harness(TwoRoleBundle(selected: FixtureBinding));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);

        var selectedRole = VectorBundle().Roles[0];
        var legacyRoles = new PublisherRoleBindingStore(
            harness.ProtectedRoot,
            harness.Protector,
            harness.Files);
        var legacySnapshots = new PublisherResourceSnapshotStore(
            harness.ProtectedRoot,
            harness.Protector,
            harness.Files);
        Assert.True(legacyRoles.Save(HoyoLabGameBundleRules.GameId, FixtureBinding));
        Assert.True(legacySnapshots.Save(selectedRole.Resource!, FixtureBinding));

        using var failingCloud = new FakeCloud();
        harness.Cloud.CopyTo(failingCloud, Fixture.SyncId);
        harness.Coordinator.Dispose();
        var failingBoundary = new FailBindingDeleteAfterSnapshotBoundary();
        using (var failing = CreateCoordinator(
                   harness.PublisherRoot,
                   harness.SlotId,
                   harness.ProtectedRoot,
                   harness.Authority,
                   failingCloud,
                   failingBoundary,
                   harness.Protector))
        {
            var queued = failing.QueueRoleDeletion(FixtureBinding);

            Assert.Equal(HoyoLabManualSyncStatus.LocalStorageUnavailable, queued.Status);
            Assert.True(failingBoundary.SnapshotDeleted);
            Assert.True(failingBoundary.BindingDeleteAttempted);
            Assert.Null(legacySnapshots.TryLoad(HoyoLabGameBundleRules.GameId, FixtureBinding));
            Assert.NotNull(legacyRoles.TryLoadRecord(HoyoLabGameBundleRules.GameId));
            using var pending = LoadState(harness.ManagedSlotRoot);
            Assert.Single(pending.PendingRoleDeletions);
        }

        using var retryHandler = new FakeCloud();
        failingCloud.CopyTo(retryHandler, Fixture.SyncId);
        retryHandler.OnRequest = request =>
        {
            if (request.Action == "pull")
            {
                Assert.Null(legacyRoles.TryLoadRecord(HoyoLabGameBundleRules.GameId));
                Assert.Null(legacySnapshots.TryLoad(HoyoLabGameBundleRules.GameId, FixtureBinding));
                using var pending = LoadState(harness.ManagedSlotRoot);
                Assert.Single(pending.PendingRoleDeletions);
            }
            return null;
        };
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            retryHandler,
            harness.Files,
            harness.Protector);

        var result = await retry.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], retryHandler.Requests.Select(static item => item.Action));
        Assert.Null(legacyRoles.TryLoadRecord(HoyoLabGameBundleRules.GameId));
        Assert.Null(legacySnapshots.TryLoad(HoyoLabGameBundleRules.GameId, FixtureBinding));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Empty(after.PendingRoleDeletions);
    }

    [Fact]
    public async Task Pending_role_cleanup_finishes_before_an_earlier_cloud_deletion_failure()
    {
        using var harness = new Harness(TwoRoleBundle(selected: FixtureBinding));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);

        var selectedRole = VectorBundle().Roles[0];
        var legacyRoles = new PublisherRoleBindingStore(
            harness.ProtectedRoot,
            harness.Protector,
            harness.Files);
        var legacySnapshots = new PublisherResourceSnapshotStore(
            harness.ProtectedRoot,
            harness.Protector,
            harness.Files);
        Assert.True(legacyRoles.Save(HoyoLabGameBundleRules.GameId, FixtureBinding));
        Assert.True(legacySnapshots.Save(selectedRole.Resource!, FixtureBinding));

        const string roleOperationId = "pending-role-after-cloud";
        const string cloudOperationId = "earlier-cloud-delete";
        var roleRequestedAt = Now.AddMinutes(-2);
        var cloudRequestedAt = Now.AddMinutes(-1);
        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            var current = Assert.IsType<HoyoLabSyncCredential>(state.CurrentCredential);
            var store = new HoyoLabSyncStateStore(
                harness.ManagedSlotRoot,
                harness.Protector,
                harness.Files,
                harness.Clock);
            using var roleDeletion = new HoyoLabPendingRoleDeletion(
                current.SyncId,
                current.Token,
                current.Key,
                FixtureBinding,
                roleOperationId,
                roleRequestedAt,
                selectedRole.Observations.Resources,
                selectedRole.Observations.Achievements,
                Now);
            Assert.True(store.TryEnqueuePendingRoleDeletion(roleDeletion));

            using var alternateSecrets = Secrets(AlternateCode);
            using var alternate = CredentialFor(alternateSecrets);
            using var cloudDeletion = new HoyoLabPendingDeletion(
                alternate.SyncId,
                alternate.Token,
                HoyoLabSyncStateStore.AllHoyoScope,
                cloudOperationId,
                cloudRequestedAt);
            Assert.False(cloudDeletion.RemoveLocalSlot);
            Assert.True(store.TryEnqueuePendingDeletion(cloudDeletion));
        }

        harness.Cloud.ClearRequests();
        harness.Cloud.OnRequest = request =>
        {
            Assert.Equal("delete-account", request.Action);
            var local = LoadBundle(harness.ProtectedRoot);
            Assert.DoesNotContain(local.Roles, item => item.Role.Binding == FixtureBinding);
            Assert.Contains(local.Roles, item => item.Role.Binding == SurvivorBinding);
            Assert.Equal(TwoRoleBundle(selected: FixtureBinding).Consents, local.Consents);
            Assert.Null(legacyRoles.TryLoadRecord(HoyoLabGameBundleRules.GameId));
            Assert.Null(legacySnapshots.TryLoad(HoyoLabGameBundleRules.GameId, FixtureBinding));
            throw new HttpRequestException("cloud failure");
        };

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.NetworkUnavailable, result.Status);
        Assert.Equal(["delete-account"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        var cloudPending = Assert.Single(after.PendingDeletions);
        Assert.Equal(cloudOperationId, cloudPending.OperationId);
        Assert.Equal(cloudRequestedAt, cloudPending.RequestedAt);
        Assert.False(cloudPending.RemoveLocalSlot);
        var rolePending = Assert.Single(after.PendingRoleDeletions);
        Assert.Equal(roleOperationId, rolePending.OperationId);
        Assert.Equal(roleRequestedAt, rolePending.RequestedAt);
        Assert.Equal(Now, rolePending.DeletedAt);
        var localAfter = LoadBundle(harness.ProtectedRoot);
        Assert.DoesNotContain(localAfter.Roles, item => item.Role.Binding == FixtureBinding);
        Assert.Contains(localAfter.Roles, item => item.Role.Binding == SurvivorBinding);
        Assert.Null(legacyRoles.TryLoadRecord(HoyoLabGameBundleRules.GameId));
        Assert.Null(legacySnapshots.TryLoad(HoyoLabGameBundleRules.GameId, FixtureBinding));
    }

    [Fact]
    public async Task Retry_role_deletion_preserves_unrelated_roles_consents_and_selects_survivor()
    {
        var local = TwoRoleBundle(selected: FixtureBinding);
        using var harness = new Harness(local);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        var queued = harness.Coordinator.QueueRoleDeletion(FixtureBinding);
        Assert.Equal(HoyoLabManualSyncStatus.Completed, queued.Status);
        using var pendingState = LoadState(harness.ManagedSlotRoot);
        var pending = Assert.Single(pendingState.PendingRoleDeletions);
        var deletedAt = pending.DeletedAt;
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(static item => item.Action));
        using var secrets = Secrets(DisplayCode);
        var pushed = PostedBundle(
            Assert.Single(harness.Cloud.Requests, static item => item.Action == "push"),
            secrets);
        Assert.DoesNotContain(pushed.Roles, role => role.Role.Binding == FixtureBinding);
        Assert.Contains(pushed.Roles, role => role.Role.Binding == SurvivorBinding);
        Assert.Equal(SurvivorBinding, pushed.SelectedRole);
        Assert.Equal(local.Consents, pushed.Consents);
        Assert.Equal(FixtureBinding, Assert.Single(pushed.RoleTombstones).Binding);
        Assert.Equal(deletedAt, pushed.RoleTombstones[0].DeletedAt);
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Empty(state.PendingRoleDeletions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_role_deletions_keep_canonical_tombstone_order(
        bool includeUnrelatedNewerTombstone)
    {
        using var harness = new Harness(RoleOrderBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            RoleOrderBundle(includeUnrelatedNewerTombstone),
            Now.AddMinutes(-1));

        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            var credential = Assert.IsType<HoyoLabSyncCredential>(state.CurrentCredential);
            var stateStore = new HoyoLabSyncStateStore(
                harness.ManagedSlotRoot,
                harness.Protector,
                harness.Files,
                harness.Clock);
            using var first = new HoyoLabPendingRoleDeletion(
                credential.SyncId,
                credential.Token,
                credential.Key,
                RoleDeleteA,
                "role-delete-a",
                Now.AddMinutes(-5),
                null,
                null,
                Now.AddMinutes(-4));
            using var second = new HoyoLabPendingRoleDeletion(
                credential.SyncId,
                credential.Token,
                credential.Key,
                RoleDeleteB,
                "role-delete-b",
                Now.AddMinutes(-3),
                null,
                null,
                Now.AddMinutes(-2));
            Assert.True(stateStore.TryEnqueuePendingRoleDeletion(first));
            Assert.True(stateStore.TryEnqueuePendingRoleDeletion(second));
        }
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(
            ["pull", "push", "pull", "push"],
            harness.Cloud.Requests.Select(static item => item.Action));
        using var secrets = Secrets(DisplayCode);
        var pushes = harness.Cloud.Requests
            .Where(static item => item.Action == "push")
            .ToArray();
        var final = PostedBundle(pushes[^1], secrets);
        var expectedBindings = includeUnrelatedNewerTombstone
            ? new[] { RoleDeleteA, RoleDeleteB, UnrelatedTombstoneBinding }
            : new[] { RoleDeleteA, RoleDeleteB };
        Assert.Equal(expectedBindings, final.RoleTombstones.Select(static item => item.Binding));
        Assert.Equal(
            Now.AddMinutes(-4),
            final.RoleTombstones.Single(item => item.Binding == RoleDeleteA).DeletedAt);
        Assert.Equal(
            Now.AddMinutes(-2),
            final.RoleTombstones.Single(item => item.Binding == RoleDeleteB).DeletedAt);
        if (includeUnrelatedNewerTombstone)
            Assert.Equal(
                Now.AddMinutes(-1),
                final.RoleTombstones.Single(item => item.Binding == UnrelatedTombstoneBinding).DeletedAt);
        using var stateAfter = LoadState(harness.ManagedSlotRoot);
        Assert.Empty(stateAfter.PendingRoleDeletions);
    }

    [Fact]
    public async Task Retry_role_deletion_of_the_only_role_selects_null_and_writes_a_tombstone()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        using var secrets = Secrets(DisplayCode);
        var pushed = PostedBundle(
            Assert.Single(harness.Cloud.Requests, static item => item.Action == "push"),
            secrets);
        Assert.Empty(pushed.Roles);
        Assert.Null(pushed.SelectedRole);
        Assert.Equal(FixtureBinding, Assert.Single(pushed.RoleTombstones).Binding);
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Empty(state.PendingRoleDeletions);
    }

    [Fact]
    public async Task Absent_whole_copy_completes_without_push_and_absent_role_writes_a_tombstone()
    {
        using var onlyHarness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await onlyHarness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            onlyHarness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        onlyHarness.Cloud.Remove(Fixture.SyncId);
        onlyHarness.Cloud.ClearRequests();

        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await onlyHarness.Coordinator.RetryDeletionsAsync()).Status);
        Assert.Equal(["pull"], onlyHarness.Cloud.Requests.Select(static item => item.Action));
        using var completedState = LoadState(onlyHarness.ManagedSlotRoot);
        Assert.Empty(completedState.PendingRoleDeletions);

        using var roleHarness = new Harness(TwoRoleBundle(selected: SurvivorBinding));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await roleHarness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            roleHarness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        roleHarness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            BundleWithSurvivorOnly(),
            Now.AddMinutes(-1));
        roleHarness.Cloud.ClearRequests();

        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await roleHarness.Coordinator.RetryDeletionsAsync()).Status);
        using var secrets = Secrets(DisplayCode);
        var pushed = PostedBundle(
            Assert.Single(roleHarness.Cloud.Requests, static item => item.Action == "push"),
            secrets);
        Assert.Contains(pushed.Roles, role => role.Role.Binding == SurvivorBinding);
        Assert.Equal(FixtureBinding, Assert.Single(pushed.RoleTombstones).Binding);
    }

    [Fact]
    public async Task Newer_remote_role_observation_keeps_role_deletion_pending_without_push()
    {
        using var harness = new Harness(TwoRoleBundle(selected: SurvivorBinding));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        var newer = TwoRoleBundle(selected: SurvivorBinding) with
        {
            Roles =
            [
                TwoRoleBundle(selected: SurvivorBinding).Roles[0] with
                {
                    Observations = TwoRoleBundle(selected: SurvivorBinding).Roles[0].Observations with
                    {
                        Resources = Now.AddMinutes(-1),
                    },
                    Resource = TwoRoleBundle(selected: SurvivorBinding).Roles[0].Resource! with
                    {
                        Current = 299,
                        ObservedAt = Now.AddMinutes(-1),
                    },
                },
                TwoRoleBundle(selected: SurvivorBinding).Roles[1],
            ],
        };
        harness.Cloud.SeedBundle(Fixture.SyncId, DisplayCode, newer, Now.AddMinutes(-1));
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Single(state.PendingRoleDeletions);
    }

    [Fact]
    public async Task Genshin_build_cutoff_survives_restart_and_blocks_remote_role_replay()
    {
        using var harness = new Harness(GenshinBundleWithBuilds(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(
                DisplayCode,
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(
                GenshinBinding,
                gameId: HoyoLabGameBundleRules.GenshinGameId).Status);
        using (var pending = LoadState(harness.ManagedSlotRoot))
            Assert.Equal(Older, Assert.Single(pending.PendingRoleDeletions).KnownBuildsAt);

        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            GenshinBundleWithBuilds(
                Newer,
                HoyoLabGenshinBuildSnapshotTests.CharactersJson.Replace(
                    "\"level\":95",
                    "\"level\":96",
                    StringComparison.Ordinal)),
            Newer,
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();
        harness.Coordinator.Dispose();
        using var restart = CreateCoordinator(
            harness.PublisherRoot,
            harness.SlotId,
            harness.ProtectedRoot,
            harness.Authority,
            harness.Cloud,
            harness.Files,
            harness.Protector);

        var result = await restart.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Older, Assert.Single(after.PendingRoleDeletions).KnownBuildsAt);
    }

    [Fact]
    public async Task Genshin_exploration_cutoff_blocks_a_newer_local_refresh_before_network_retry()
    {
        using var harness = new Harness(GenshinBundleWithExploration(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(
                DisplayCode,
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(
                GenshinBinding,
                gameId: HoyoLabGameBundleRules.GenshinGameId).Status);
        using (var pending = LoadState(harness.ManagedSlotRoot))
            Assert.Equal(Older, Assert.Single(pending.PendingRoleDeletions).KnownExplorationAt);

        SaveBundle(
            harness.ProtectedRoot,
            GenshinBundleWithExploration(Newer),
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Empty(harness.Cloud.Requests);
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Older, Assert.Single(after.PendingRoleDeletions).KnownExplorationAt);
    }

    [Fact]
    public async Task Genshin_exploration_cutoff_blocks_a_newer_remote_refresh_and_survives_retry()
    {
        using var harness = new Harness(GenshinBundleWithExploration(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(
                DisplayCode,
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(
                GenshinBinding,
                gameId: HoyoLabGameBundleRules.GenshinGameId).Status);
        using (var pending = LoadState(harness.ManagedSlotRoot))
            Assert.Equal(Older, Assert.Single(pending.PendingRoleDeletions).KnownExplorationAt);

        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            GenshinBundleWithExploration(Newer),
            Newer,
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Older, Assert.Single(after.PendingRoleDeletions).KnownExplorationAt);
    }

    [Fact]
    public async Task Genshin_exploration_without_a_known_cutoff_still_blocks_a_newer_remote_refresh()
    {
        using var harness = new Harness(GenshinBundleWithExploration(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(
                DisplayCode,
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);

        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            var credential = Assert.IsType<HoyoLabSyncCredential>(state.CurrentCredential);
            using var deletion = new HoyoLabPendingRoleDeletion(
                credential.SyncId,
                credential.Token,
                credential.Key,
                GenshinBinding,
                "gi-exploration-no-cutoff",
                Now,
                null,
                null,
                Now,
                HoyoLabGameBundleRules.GenshinGameId);
            var stateStore = new HoyoLabSyncStateStore(
                harness.ManagedSlotRoot,
                harness.Protector,
                harness.Files,
                harness.Clock);
            Assert.True(stateStore.TryEnqueuePendingRoleDeletion(deletion));
        }
        using (var state = LoadState(harness.ManagedSlotRoot))
            Assert.Null(Assert.Single(state.PendingRoleDeletions).KnownExplorationAt);

        SaveBundle(
            harness.ProtectedRoot,
            HoyoLabSyncCoordinator.RemoveRoleAt(
                GenshinBundleWithExploration(Older),
                GenshinBinding,
                Now),
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            GenshinBundleWithExploration(Newer),
            Newer,
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Null(Assert.Single(after.PendingRoleDeletions).KnownExplorationAt);
    }

    [Fact]
    public async Task Genshin_events_cutoff_blocks_a_newer_local_refresh_before_network_retry()
    {
        using var harness = new Harness(GenshinBundleWithEvents(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(
                DisplayCode,
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(
                GenshinBinding,
                gameId: HoyoLabGameBundleRules.GenshinGameId).Status);
        using (var pending = LoadState(harness.ManagedSlotRoot))
            Assert.Equal(Older, Assert.Single(pending.PendingRoleDeletions).KnownEventsAt);

        SaveBundle(
            harness.ProtectedRoot,
            GenshinBundleWithEvents(Newer),
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Empty(harness.Cloud.Requests);
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Older, Assert.Single(after.PendingRoleDeletions).KnownEventsAt);
    }

    [Fact]
    public async Task Genshin_events_cutoff_blocks_a_newer_remote_refresh_and_survives_retry()
    {
        using var harness = new Harness(GenshinBundleWithEvents(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(
                DisplayCode,
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(
                GenshinBinding,
                gameId: HoyoLabGameBundleRules.GenshinGameId).Status);
        using (var pending = LoadState(harness.ManagedSlotRoot))
            Assert.Equal(Older, Assert.Single(pending.PendingRoleDeletions).KnownEventsAt);

        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            GenshinBundleWithEvents(Newer),
            Newer,
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Older, Assert.Single(after.PendingRoleDeletions).KnownEventsAt);
    }

    [Fact]
    public async Task Genshin_events_without_a_known_cutoff_still_blocks_a_newer_remote_refresh()
    {
        using var harness = new Harness(GenshinBundleWithEvents(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(
                DisplayCode,
                gameId: HoyoLabGameBundleRules.GenshinGameId)).Status);

        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            var credential = Assert.IsType<HoyoLabSyncCredential>(state.CurrentCredential);
            using var deletion = new HoyoLabPendingRoleDeletion(
                credential.SyncId,
                credential.Token,
                credential.Key,
                GenshinBinding,
                "gi-events-no-cutoff",
                Now,
                null,
                null,
                Now,
                HoyoLabGameBundleRules.GenshinGameId);
            var stateStore = new HoyoLabSyncStateStore(
                harness.ManagedSlotRoot,
                harness.Protector,
                harness.Files,
                harness.Clock);
            Assert.True(stateStore.TryEnqueuePendingRoleDeletion(deletion));
        }
        using (var state = LoadState(harness.ManagedSlotRoot))
            Assert.Null(Assert.Single(state.PendingRoleDeletions).KnownEventsAt);

        SaveBundle(
            harness.ProtectedRoot,
            HoyoLabSyncCoordinator.RemoveRoleAt(
                GenshinBundleWithEvents(Older),
                GenshinBinding,
                Now),
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            GenshinBundleWithEvents(Newer),
            Newer,
            HoyoLabGameBundleRules.GenshinGameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Null(Assert.Single(after.PendingRoleDeletions).KnownEventsAt);
    }

    [Fact]
    public async Task Genshin_builds_and_exploration_round_trip_through_the_encrypted_wire_together()
    {
        using var harness = new Harness(GenshinBundleWithBuildsAndExploration(Older));

        var result = await harness.Coordinator.ConnectAsync(
            DisplayCode,
            gameId: HoyoLabGameBundleRules.GenshinGameId);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(static item => item.Action));
        Assert.All(harness.Cloud.Requests, request =>
        {
            var body = Encoding.UTF8.GetString(request.Body);
            Assert.DoesNotContain("Mondstadt", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Synthetic Traveler", body, StringComparison.Ordinal);
        });
        using var secrets = Secrets(DisplayCode);
        var pushed = harness.Cloud.GetBundle(
            Fixture.SyncId,
            secrets,
            HoyoLabGameBundleRules.GenshinGameId);
        var pushedRole = Assert.Single(pushed.Roles);
        Assert.True(pushed.Consents.Builds);
        Assert.True(pushed.Consents.Exploration);
        Assert.Equal(Older, pushedRole.Observations.Builds);
        Assert.Equal(Older, pushedRole.Observations.Exploration);
        Assert.True(HoyoLabGenshinBuildRules.ValuesEqual(
            HoyoLabGenshinBuildSnapshotTests.Snapshot(),
            pushedRole.GenshinBuilds));
        Assert.True(HoyoLabGenshinExplorationRules.ValuesEqual(
            ExplorationSnapshot(),
            pushedRole.GenshinExploration));
    }

    [Fact]
    public async Task Genshin_events_round_trip_through_the_encrypted_wire()
    {
        using var harness = new Harness(GenshinBundleWithEvents(Older));

        var result = await harness.Coordinator.ConnectAsync(
            DisplayCode,
            gameId: HoyoLabGameBundleRules.GenshinGameId);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(static item => item.Action));
        using var secrets = Secrets(DisplayCode);
        var pushed = harness.Cloud.GetBundle(
            Fixture.SyncId,
            secrets,
            HoyoLabGameBundleRules.GenshinGameId);
        var pushedRole = Assert.Single(pushed.Roles);
        Assert.True(pushed.Consents.Events);
        Assert.Equal(Older, pushedRole.Observations.Events);
        Assert.True(HoyoLabGenshinEventsRules.ValuesEqual(
            EventsSnapshot(),
            pushedRole.GenshinEvents));
    }

    [Fact]
    public async Task Hsr_events_cutoff_blocks_a_newer_local_refresh_before_network_retry()
    {
        using var harness = new Harness(HsrBundleWithEvents(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        using (var pending = LoadState(harness.ManagedSlotRoot))
            Assert.Equal(Older, Assert.Single(pending.PendingRoleDeletions).KnownEventsAt);

        SaveBundle(
            harness.ProtectedRoot,
            HsrBundleWithEvents(Newer),
            HoyoLabGameBundleRules.GameId);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Empty(harness.Cloud.Requests);
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Older, Assert.Single(after.PendingRoleDeletions).KnownEventsAt);
    }

    [Fact]
    public async Task Hsr_events_cutoff_blocks_a_newer_remote_refresh_and_survives_retry()
    {
        using var harness = new Harness(HsrBundleWithEvents(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        using (var pending = LoadState(harness.ManagedSlotRoot))
            Assert.Equal(Older, Assert.Single(pending.PendingRoleDeletions).KnownEventsAt);

        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            HsrBundleWithEvents(Newer),
            Newer);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Older, Assert.Single(after.PendingRoleDeletions).KnownEventsAt);
    }

    [Fact]
    public async Task Hsr_events_without_a_known_cutoff_still_blocks_a_newer_remote_refresh()
    {
        using var harness = new Harness(HsrBundleWithEvents(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);

        using (var state = LoadState(harness.ManagedSlotRoot))
        {
            var credential = Assert.IsType<HoyoLabSyncCredential>(state.CurrentCredential);
            using var deletion = new HoyoLabPendingRoleDeletion(
                credential.SyncId,
                credential.Token,
                credential.Key,
                FixtureBinding,
                "hsr-events-no-cutoff",
                Now,
                null,
                null,
                Now,
                HoyoLabGameBundleRules.GameId);
            var stateStore = new HoyoLabSyncStateStore(
                harness.ManagedSlotRoot,
                harness.Protector,
                harness.Files,
                harness.Clock);
            Assert.True(stateStore.TryEnqueuePendingRoleDeletion(deletion));
        }
        using (var state = LoadState(harness.ManagedSlotRoot))
            Assert.Null(Assert.Single(state.PendingRoleDeletions).KnownEventsAt);

        SaveBundle(
            harness.ProtectedRoot,
            HoyoLabSyncCoordinator.RemoveRoleAt(
                HsrBundleWithEvents(Older),
                FixtureBinding,
                Now),
            HoyoLabGameBundleRules.GameId);
        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            HsrBundleWithEvents(Newer),
            Newer);
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Null(Assert.Single(after.PendingRoleDeletions).KnownEventsAt);
    }

    [Fact]
    public async Task Hsr_events_round_trip_through_the_encrypted_wire()
    {
        using var harness = new Harness(HsrBundleWithEvents(Older));

        var result = await harness.Coordinator.ConnectAsync(DisplayCode);

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(["pull", "push"], harness.Cloud.Requests.Select(static item => item.Action));
        using var secrets = Secrets(DisplayCode);
        var pushed = harness.Cloud.GetBundle(Fixture.SyncId, secrets);
        var pushedRole = Assert.Single(pushed.Roles);
        Assert.True(pushed.Consents.Events);
        Assert.Equal(Older, pushedRole.Observations.Events);
        Assert.True(HoyoLabHsrEventsRules.ValuesEqual(
            HsrEventsSnapshot(),
            pushedRole.HsrEvents));
    }

    [Fact]
    public async Task Hsr_build_cutoff_survives_restart_and_blocks_remote_role_replay()
    {
        using var harness = new Harness(HsrBundleWithBuilds(Older));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        using (var pending = LoadState(harness.ManagedSlotRoot))
            Assert.Equal(Older, Assert.Single(pending.PendingRoleDeletions).KnownBuildsAt);

        harness.Cloud.SeedBundle(
            Fixture.SyncId,
            DisplayCode,
            HsrBundleWithBuilds(Newer, HsrBuildCharactersJson.Replace(
                "\"level\":80",
                "\"level\":81",
                StringComparison.Ordinal)),
            Newer);
        harness.Cloud.ClearRequests();
        harness.Coordinator.Dispose();
        using var restart = CreateCoordinator(
            harness.PublisherRoot,
            harness.SlotId,
            harness.ProtectedRoot,
            harness.Authority,
            harness.Cloud,
            harness.Files,
            harness.Protector);

        var result = await restart.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Conflict, result.Status);
        Assert.Equal(["pull"], harness.Cloud.Requests.Select(static item => item.Action));
        using var after = LoadState(harness.ManagedSlotRoot);
        Assert.Equal(Older, Assert.Single(after.PendingRoleDeletions).KnownBuildsAt);
    }

    [Fact]
    public async Task Role_delete_cas_conflict_retries_once_without_rebasing_the_tombstone()
    {
        using var harness = new Harness(TwoRoleBundle(selected: SurvivorBinding));
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        using var pendingState = LoadState(harness.ManagedSlotRoot);
        var deletedAt = Assert.Single(pendingState.PendingRoleDeletions).DeletedAt;
        var remoteRevision = Now.AddMinutes(-1);
        var firstPush = true;
        harness.Cloud.OnRequest = request =>
        {
            if (request.Action == "push" && firstPush)
            {
                firstPush = false;
                harness.Cloud.SetRevision(Fixture.SyncId, remoteRevision);
                return ConflictResponse();
            }
            return null;
        };
        harness.Cloud.ClearRequests();

        var result = await harness.Coordinator.RetryDeletionsAsync();

        Assert.Equal(HoyoLabManualSyncStatus.Completed, result.Status);
        Assert.Equal(
            ["pull", "push", "pull", "push"],
            harness.Cloud.Requests.Select(static item => item.Action));
        var pushes = harness.Cloud.Requests.Where(static item => item.Action == "push").ToArray();
        Assert.Equal(
            FormatTimestamp(Now),
            pushes[0].Root.GetProperty("baseUpdatedAt").GetString());
        Assert.Equal(
            FormatTimestamp(remoteRevision),
            pushes[1].Root.GetProperty("baseUpdatedAt").GetString());
        using var secrets = Secrets(DisplayCode);
        var retried = PostedBundle(pushes[1], secrets);
        Assert.Equal(deletedAt, Assert.Single(retried.RoleTombstones).DeletedAt);
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Empty(state.PendingRoleDeletions);
    }

    [Fact]
    public async Task Role_delete_survives_local_cleanup_and_a_new_protected_root_without_resurrection()
    {
        using var harness = new Harness(VectorBundle());
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await harness.Coordinator.ConnectAsync(DisplayCode)).Status);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            harness.Coordinator.QueueRoleDeletion(FixtureBinding).Status);
        harness.Cloud.Remove(Fixture.SyncId);
        harness.Cloud.ClearRequests();
        Directory.Delete(harness.ProtectedRoot, recursive: true);
        Directory.CreateDirectory(harness.ProtectedRoot);
        harness.Coordinator.Dispose();

        using var handler = new FakeCloud();
        using var retry = CreateCoordinator(
            harness.PublisherRoot,
            slotId: null,
            protectedSlotRoot: null,
            harness.Authority,
            handler);
        Assert.Equal(
            HoyoLabManualSyncStatus.Completed,
            (await retry.RetryDeletionsAsync()).Status);
        Assert.Equal(["pull"], handler.Requests.Select(static item => item.Action));
        using var state = LoadState(harness.ManagedSlotRoot);
        Assert.Empty(state.PendingRoleDeletions);
        Assert.Empty(Directory.EnumerateFiles(harness.ProtectedRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Invalid_corrupt_reparse_and_over_bound_managed_roots_fail_closed()
    {
        AssertUnavailable(root =>
        {
            Directory.CreateDirectory(Path.Combine(
                root,
                HoyoLabSyncCoordinator.ManagedDirectoryName));
            File.WriteAllBytes(
                Path.Combine(root, HoyoLabSyncCoordinator.ManagedDirectoryName, "not-a-slot"),
                [1]);
        });
        AssertUnavailable(root =>
        {
            var slotRoot = Path.Combine(
                root,
                HoyoLabSyncCoordinator.ManagedDirectoryName,
                SlotId(1),
                ".protected-hoyolab-sync-state");
            Directory.CreateDirectory(slotRoot);
            File.WriteAllBytes(Path.Combine(slotRoot, "state.bin"), [1, 2, 3]);
        });
        AssertUnavailable(root =>
        {
            var managed = Path.Combine(root, HoyoLabSyncCoordinator.ManagedDirectoryName);
            for (var index = 1; index <= HoyoLabSyncCoordinator.MaximumRetainedSlots + 1; index++)
                Directory.CreateDirectory(Path.Combine(managed, SlotId(index)));
        });

        using var reparseRoot = new TemporaryRoot();
        var managedRoot = Path.Combine(
            reparseRoot.Path,
            HoyoLabSyncCoordinator.ManagedDirectoryName);
        var reparseSlot = Path.Combine(managedRoot, SlotId(1));
        var target = Path.Combine(reparseRoot.Path, "reparse-target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(managedRoot);
        Directory.CreateSymbolicLink(reparseSlot, target);
        using var handler = new FakeCloud();
        using var coordinator = CreateCoordinator(
            reparseRoot.Path,
            SlotId(1),
            Path.Combine(reparseRoot.Path, "Accounts", "HoYoLAB", SlotId(1), "Protected"),
            new Authority(),
            handler);

        Assert.False(coordinator.GetSummary().Available);
        Assert.Empty(handler.Requests);
    }

    private static void AssertUnavailable(Action<string> setup)
    {
        using var root = new TemporaryRoot();
        setup(root.Path);
        using var handler = new FakeCloud();
        var authority = new Authority();
        using var coordinator = CreateCoordinator(
            root.Path,
            SlotId(1),
            Path.Combine(root.Path, "Accounts", "HoYoLAB", SlotId(1), "Protected"),
            authority,
            handler);

        Assert.False(coordinator.GetSummary().Available);
        Assert.Empty(handler.Requests);
    }

    private static HoyoLabSyncCoordinator CreateCoordinator(
        string publisherRoot,
        string? slotId,
        string? protectedSlotRoot,
        Authority authority,
        FakeCloud handler,
        IPublisherRoleBindingFileBoundary? files = null,
        IPublisherRoleBindingProtector? protector = null,
        TimeProvider? clock = null) => new(
        publisherRoot,
        slotId,
        protectedSlotRoot,
        authority.Invoke,
        protector ?? new CopyProtector(),
        files ?? new SystemPublisherRoleBindingFileBoundary(),
        new HoyoLabSyncClient(handler, TimeSpan.FromSeconds(1)),
        clock ?? new FixedTimeProvider(Now));

    private static HoyoLabGameBundle LoadBundle(
        string protectedRoot,
        string gameId = HoyoLabGameBundleRules.GameId)
    {
        var store = new HoyoLabGameBundleStore(
            protectedRoot,
            new CopyProtector(),
            new SystemPublisherRoleBindingFileBoundary(),
            new FixedTimeProvider(Now),
            gameId);
        return Assert.IsType<HoyoLabGameBundle>(store.TryLoad());
    }

    private static void SaveBundle(
        string protectedRoot,
        HoyoLabGameBundle bundle,
        string gameId)
    {
        var store = new HoyoLabGameBundleStore(
            protectedRoot,
            new CopyProtector(),
            new SystemPublisherRoleBindingFileBoundary(),
            new FixedTimeProvider(Now),
            gameId);
        Assert.True(store.TrySave(bundle));
    }

    private static HoyoLabSyncState LoadState(
        string managedSlotRoot,
        IPublisherRoleBindingProtector? protector = null,
        IPublisherRoleBindingFileBoundary? files = null) => Assert.IsType<HoyoLabSyncState>(
            new HoyoLabSyncStateStore(
                managedSlotRoot,
                protector ?? new CopyProtector(),
                files ?? new SystemPublisherRoleBindingFileBoundary(),
                new FixedTimeProvider(Now)).TryLoad());

    private static HoyoLabSyncState? TryLoadState(string managedSlotRoot) =>
        new HoyoLabSyncStateStore(
            managedSlotRoot,
            new CopyProtector(),
            new SystemPublisherRoleBindingFileBoundary(),
            new FixedTimeProvider(Now)).TryLoad();

    private static HoyoLabGameBundle PostedBundle(
        RequestSnapshot request,
        HoyoLabSyncCrypto.DerivedSecrets secrets)
    {
        var payload = Encoding.UTF8.GetBytes(request.Root.GetProperty("payload").GetRawText());
        try
        {
            Assert.True(HoyoLabSyncCrypto.TryParseEnvelope(payload, out var envelope));
            Assert.True(HoyoLabSyncCrypto.TryDecryptBundle(
                secrets,
                envelope,
                Now,
                out var bundle));
            return Assert.IsType<HoyoLabGameBundle>(bundle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void AssertBundleContentEqual(
        HoyoLabGameBundle expected,
        HoyoLabGameBundle actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.GameId, actual.GameId);
        Assert.Equal(expected.SelectedRole, actual.SelectedRole);
        Assert.Equal(expected.Consents, actual.Consents);
        Assert.Equal(expected.Roles.Count, actual.Roles.Count);
        for (var index = 0; index < expected.Roles.Count; index++)
        {
            var expectedRole = expected.Roles[index];
            var actualRole = actual.Roles[index];
            Assert.Equal(expectedRole.Role, actualRole.Role);
            Assert.Equal(expectedRole.Observations, actualRole.Observations);
            Assert.Equal(expectedRole.Resource, actualRole.Resource);
            Assert.Equal(
                expectedRole.CompletedHsrAchievementIds ?? Array.Empty<long>(),
                actualRole.CompletedHsrAchievementIds ?? Array.Empty<long>());
            Assert.True(HoyoLabGenshinBuildRules.ValuesEqual(
                expectedRole.GenshinBuilds,
                actualRole.GenshinBuilds));
            Assert.True(HoyoLabHsrBuildRules.ValuesEqual(
                expectedRole.HsrBuilds,
                actualRole.HsrBuilds));
            Assert.True(HoyoLabGenshinExplorationRules.ValuesEqual(
                expectedRole.GenshinExploration,
                actualRole.GenshinExploration));
            Assert.True(HoyoLabGenshinEventsRules.ValuesEqual(
                expectedRole.GenshinEvents,
                actualRole.GenshinEvents));
            Assert.True(HoyoLabHsrEventsRules.ValuesEqual(
                expectedRole.HsrEvents,
                actualRole.HsrEvents));
        }

        Assert.Equal(
            expected.CapabilityTombstones.Count,
            actual.CapabilityTombstones.Count);
        for (var index = 0; index < expected.CapabilityTombstones.Count; index++)
            Assert.Equal(expected.CapabilityTombstones[index], actual.CapabilityTombstones[index]);

        Assert.Equal(expected.RoleTombstones.Count, actual.RoleTombstones.Count);
        for (var index = 0; index < expected.RoleTombstones.Count; index++)
            Assert.Equal(expected.RoleTombstones[index], actual.RoleTombstones[index]);
    }

    private static HoyoLabSyncCrypto.DerivedSecrets Secrets(string code = DisplayCode)
    {
        Assert.True(HoyoLabSyncCrypto.TryDerive(code, out var secrets));
        return Assert.IsType<HoyoLabSyncCrypto.DerivedSecrets>(secrets);
    }

    private static HoyoLabSyncCredential CredentialFor(
        HoyoLabSyncCrypto.DerivedSecrets secrets) => new(
        secrets.SyncId,
        secrets.Token.ToArray(),
        secrets.Key.ToArray());

    private static HoyoLabGameBundle VectorBundle()
    {
        var plaintext = Encoding.UTF8.GetBytes(Fixture.Plaintext);
        try
        {
            Assert.True(HoyoLabGameBundleStore.TryParseBundle(plaintext, Now, out var bundle));
            return Assert.IsType<HoyoLabGameBundle>(bundle);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static HoyoLabGameBundle BundleWithResource(
        DateTimeOffset observedAt,
        int current)
    {
        var bundle = VectorBundle();
        var role = bundle.Roles[0];
        var resource = role.Resource! with
        {
            Current = current,
            ObservedAt = observedAt,
        };
        return bundle with
        {
            Roles =
            [
                role with
                {
                    Observations = role.Observations with { Resources = observedAt },
                    Resource = resource,
                },
            ],
        };
    }

    private static HoyoLabGameBundle GenshinBundleWithResource(
        DateTimeOffset observedAt,
        int current) => new(
            HoyoLabGameBundleRules.SchemaVersion,
            HoyoLabGameBundleRules.GenshinGameId,
            [
                new(
                    new(
                        GenshinBinding,
                        "Test Traveler",
                        PublisherRoleRecordRules.CanonicalRegionLabel(GenshinBinding.Server)),
                    new(observedAt, null, null, null, null, null, null, null),
                    new(
                        HoyoLabGameBundleRules.GenshinGameId,
                        HoyoLabGameBundleRules.ResourceName(HoyoLabGameBundleRules.GenshinGameId),
                        current,
                        200,
                        observedAt,
                        RecoverySeconds: 38_400,
                        Reserve: null),
                    null),
            ],
            GenshinBinding,
            new(true, false, false, false, false, false, false, false),
            [],
            []);

    private static HoyoLabGameBundle GenshinBundleWithBuilds(
        DateTimeOffset observedAt,
        string charactersJson = HoyoLabGenshinBuildSnapshotTests.CharactersJson) => new(
        HoyoLabGameBundleRules.SchemaVersion,
        HoyoLabGameBundleRules.GenshinGameId,
        [
            new(
                new(
                    GenshinBinding,
                    "Test Traveler",
                    PublisherRoleRecordRules.CanonicalRegionLabel(GenshinBinding.Server)),
                new(null, null, observedAt, null, null, null, null, null),
                null,
                null,
                HoyoLabGenshinBuildSnapshotTests.Snapshot(charactersJson)),
        ],
        GenshinBinding,
        new(false, false, true, false, false, false, false, false),
        [],
        []);

    private static HoyoLabGameBundle GenshinBundleWithExploration(
        DateTimeOffset observedAt,
        string dataJson = HoyoLabGenshinExplorationSnapshotTests.DataJson) => new(
        HoyoLabGameBundleRules.SchemaVersion,
        HoyoLabGameBundleRules.GenshinGameId,
        [
            new(
                new(
                    GenshinBinding,
                    "Test Traveler",
                    PublisherRoleRecordRules.CanonicalRegionLabel(GenshinBinding.Server)),
                new(null, null, null, null, observedAt, null, null, null),
                null,
                null,
                null,
                null,
                ExplorationSnapshot(dataJson)),
        ],
        GenshinBinding,
        new(false, false, false, false, true, false, false, false),
        [],
        []);

    private static HoyoLabGameBundle GenshinBundleWithEvents(
        DateTimeOffset observedAt,
        string dataJson = HoyoLabGenshinEventsSnapshotTests.DataJson) => new(
        HoyoLabGameBundleRules.SchemaVersion,
        HoyoLabGameBundleRules.GenshinGameId,
        [
            new(
                new(
                    GenshinBinding,
                    "Test Traveler",
                    PublisherRoleRecordRules.CanonicalRegionLabel(GenshinBinding.Server)),
                new(null, null, null, null, null, null, observedAt, null),
                null,
                null,
                null,
                null,
                null,
                EventsSnapshot(dataJson)),
        ],
        GenshinBinding,
        new(false, false, false, false, false, false, true, false),
        [],
        []);

    private static HoyoLabGameBundle HsrBundleWithEvents(
        DateTimeOffset observedAt,
        string dataJson = HoyoLabHsrEventsSnapshotTests.DataJson) => new(
        HoyoLabGameBundleRules.SchemaVersion,
        HoyoLabGameBundleRules.GameId,
        [
            new(
                new(
                    FixtureBinding,
                    "Test Trailblazer",
                    PublisherRoleRecordRules.CanonicalRegionLabel(FixtureBinding.Server)),
                new(null, null, null, null, null, null, observedAt, null),
                null,
                null,
                null,
                null,
                null,
                null,
                HsrEventsSnapshot(dataJson)),
        ],
        FixtureBinding,
        new(false, false, false, false, false, false, true, false),
        [],
        []);

    private static HoyoLabGameBundle GenshinBundleWithBuildsAndExploration(
        DateTimeOffset observedAt)
    {
        var bundle = GenshinBundleWithExploration(observedAt);
        var role = bundle.Roles[0];
        return bundle with
        {
            Consents = bundle.Consents with { Builds = true },
            Roles =
            [
                role with
                {
                    Observations = role.Observations with { Builds = observedAt },
                    GenshinBuilds = HoyoLabGenshinBuildSnapshotTests.Snapshot(),
                },
            ],
        };
    }

    private static HoyoLabGenshinExplorationSnapshot ExplorationSnapshot(
        string dataJson = HoyoLabGenshinExplorationSnapshotTests.DataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new(document.RootElement.Clone());
    }

    private static HoyoLabGenshinEventsSnapshot EventsSnapshot(
        string dataJson = HoyoLabGenshinEventsSnapshotTests.DataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new(document.RootElement.Clone());
    }

    private static HoyoLabHsrEventsSnapshot HsrEventsSnapshot(
        string dataJson = HoyoLabHsrEventsSnapshotTests.DataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return new(document.RootElement.Clone());
    }

    private static HoyoLabGameBundle HsrBundleWithBuilds(
        DateTimeOffset observedAt,
        string charactersJson = HsrBuildCharactersJson) => new(
        HoyoLabGameBundleRules.SchemaVersion,
        HoyoLabGameBundleRules.GameId,
        [
            new(
                new(
                    FixtureBinding,
                    "Test Trailblazer",
                    PublisherRoleRecordRules.CanonicalRegionLabel(FixtureBinding.Server)),
                new(null, null, observedAt, null, null, null, null, null),
                null,
                null,
                null,
                HsrSnapshot(charactersJson)),
        ],
        FixtureBinding,
        new(false, false, true, false, false, false, false, false),
        [],
        []);

    private static HoyoLabHsrBuildSnapshot HsrSnapshot(string charactersJson)
    {
        using var document = JsonDocument.Parse(charactersJson);
        return new(document.RootElement.Clone());
    }

    private const string HsrBuildCharactersJson = """
        [{"id":1001,"name":"Synthetic Trailblazer","level":80,"rarity":5,"element":"Quantum","path":3,"rank":1,"enhancedId":1001001,"avatarType":"Girl","lightCone":null,"eidolons":[],"relics":[],"ornaments":[],"properties":[],"traces":[],"specialTraces":[],"memosprite":null}]
        """;

    private static HoyoLabGameBundle TwoRoleBundle(PublisherRoleBinding selected)
    {
        var bundle = VectorBundle();
        var survivor = new HoyoLabGameBundleRole(
            new(
                SurvivorBinding,
                "Survivor",
                PublisherRoleRecordRules.CanonicalRegionLabel(SurvivorBinding.Server)),
            EmptyObservations(),
            null,
            null);
        return bundle with
        {
            Roles = [bundle.Roles[0], survivor],
            SelectedRole = selected,
        };
    }

    private static HoyoLabGameBundle RoleOrderBundle(
        bool includeUnrelatedNewerTombstone = false)
    {
        var bundle = VectorBundle();
        var roles = new[]
        {
            RoleData(RoleDeleteA, "Role A"),
            RoleData(RoleDeleteB, "Role B"),
            RoleData(SurvivorBinding, "Survivor"),
        };
        var tombstones = includeUnrelatedNewerTombstone
            ? new[]
            {
                new HoyoLabRoleTombstone(
                    UnrelatedTombstoneBinding,
                    Now.AddMinutes(-1)),
            }
            : Array.Empty<HoyoLabRoleTombstone>();
        return bundle with
        {
            Roles = roles,
            SelectedRole = SurvivorBinding,
            RoleTombstones = tombstones,
        };
    }

    private static HoyoLabGameBundleRole RoleData(
        PublisherRoleBinding binding,
        string nickname) => new(
        new(
            binding,
            nickname,
            PublisherRoleRecordRules.CanonicalRegionLabel(binding.Server)),
        EmptyObservations(),
        null,
        null);

    private static HoyoLabGameBundle BundleWithSurvivorOnly() =>
        TwoRoleBundle(SurvivorBinding) with
        {
            Roles = [TwoRoleBundle(SurvivorBinding).Roles[1]],
        };

    private static HoyoLabCapabilityObservations EmptyObservations() => new(
        null, null, null, null, null, null, null, null);

    private static PublisherRoleBinding FixtureBinding { get; } =
        new(FixtureUid, "prod_official_eur");

    private static PublisherRoleBinding GenshinBinding { get; } =
        new("123456789", "os_euro");

    private static PublisherRoleBinding SurvivorBinding { get; } =
        new("987654321", "prod_official_usa");

    private static PublisherRoleBinding RoleDeleteA { get; } =
        new("223456789", "prod_official_eur");

    private static PublisherRoleBinding RoleDeleteB { get; } =
        new("323456789", "prod_official_usa");

    private static PublisherRoleBinding UnrelatedTombstoneBinding { get; } =
        new("423456789", "prod_official_asia");

    private static string SlotId(int index) => index.ToString(
        "x",
        CultureInfo.InvariantCulture).PadLeft(32, '0');

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString(
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        CultureInfo.InvariantCulture);

    private static byte[] FixedNonce(byte seed) => Enumerable.Repeat(seed, 12).ToArray();

    private static Dictionary<string, byte[]> ReadTree(string root)
    {
        if (!Directory.Exists(root)) return new(StringComparer.Ordinal);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertTreeEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase),
            actual.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var path in expected.Keys)
            Assert.Equal(expected[path], actual[path]);
    }

    private static Vector LoadVector()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hoyo-sync-vector-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        return new(
            root.GetProperty("displayCode").GetString()!,
            root.GetProperty("syncId").GetString()!,
            root.GetProperty("token").GetString()!,
            root.GetProperty("plaintext").GetString()!);
    }

    private sealed record Vector(
        string DisplayCode,
        string SyncId,
        string Token,
        string Plaintext);

    private sealed class Harness : IDisposable
    {
        internal Harness(HoyoLabGameBundle bundle, FakeCloud? cloud = null)
        {
            Root = new();
            PublisherRoot = Root.Path;
            SlotId = SlotIdForHarness();
            ProtectedRoot = Path.Combine(
                PublisherRoot,
                "Accounts",
                "HoYoLAB",
                SlotId,
                "Protected");
            ManagedSlotRoot = Path.Combine(
                PublisherRoot,
                HoyoLabSyncCoordinator.ManagedDirectoryName,
                SlotId);
            Protector = new CopyProtector();
            Files = new SystemPublisherRoleBindingFileBoundary();
            Clock = new FixedTimeProvider(Now);
            var bundles = new HoyoLabGameBundleStore(
                ProtectedRoot,
                Protector,
                Files,
                Clock,
                bundle.GameId);
            Assert.True(bundles.TrySave(bundle));
            Authority = new Authority();
            Cloud = cloud ?? new FakeCloud(Clock);
            Coordinator = CreateCoordinator(
                PublisherRoot,
                SlotId,
                ProtectedRoot,
                Authority,
                Cloud,
                Files,
                Protector,
                Clock);
        }

        internal TemporaryRoot Root { get; }
        internal string PublisherRoot { get; }
        internal string SlotId { get; }
        internal string ProtectedRoot { get; }
        internal string ManagedSlotRoot { get; }
        internal CopyProtector Protector { get; }
        internal SystemPublisherRoleBindingFileBoundary Files { get; }
        internal FixedTimeProvider Clock { get; }
        internal Authority Authority { get; }
        internal FakeCloud Cloud { get; }
        internal HoyoLabSyncCoordinator Coordinator { get; }

        public void Dispose()
        {
            Coordinator.Dispose();
            Root.Dispose();
        }

        private static string SlotIdForHarness() => SlotId(1);
    }

    private sealed class Authority
    {
        internal bool Allowed { get; set; } = true;
        internal int? RemainingAllows { get; set; }

        internal bool Invoke(Action action)
        {
            if (!Allowed || RemainingAllows is 0) return false;
            if (RemainingAllows is { } remaining) RemainingAllows = remaining - 1;
            action();
            return true;
        }
    }

    private sealed record RequestSnapshot(
        string Action,
        string SyncId,
        string GameId,
        Uri Uri,
        byte[] Body,
        JsonElement Root);

    private sealed record RemoteCopy(
        string PayloadJson,
        DateTimeOffset UpdatedAt,
        int Size);

    private sealed class FakeCloud(TimeProvider? clock = null) : HttpMessageHandler
    {
        private DateTimeOffset UtcNow => clock?.GetUtcNow() ?? Now;
        private readonly Dictionary<(string SyncId, string GameId), RemoteCopy> copies = [];
        private readonly HashSet<string> accounts = new(StringComparer.Ordinal);

        internal List<RequestSnapshot> Requests { get; } = [];
        internal Func<RequestSnapshot, HttpResponseMessage?>? OnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement.Clone();
            var snapshot = new RequestSnapshot(
                request.RequestUri!.Segments[^1].Trim('/'),
                root.GetProperty("syncId").GetString()!,
                root.GetProperty("game").GetString()!,
                request.RequestUri,
                body,
                root);
            Requests.Add(snapshot);
            var overridden = OnRequest?.Invoke(snapshot);
            return overridden ?? Respond(snapshot);
        }

        internal void ClearRequests() => Requests.Clear();

        internal bool HasCopy(
            string syncId,
            string gameId = HoyoLabGameBundleRules.GameId) => copies.ContainsKey((syncId, gameId));

        internal void RemoveAccount(string syncId)
        {
            foreach (var key in copies.Keys.Where(key => key.SyncId == syncId).ToArray())
                copies.Remove(key);
            accounts.Remove(syncId);
        }

        internal void Remove(
            string syncId,
            string gameId = HoyoLabGameBundleRules.GameId) => copies.Remove((syncId, gameId));

        internal void CopyTo(
            FakeCloud target,
            string syncId,
            string gameId = HoyoLabGameBundleRules.GameId)
        {
            target.copies[(syncId, gameId)] = copies[(syncId, gameId)];
            target.accounts.Add(syncId);
        }

        internal void SetRevision(
            string syncId,
            DateTimeOffset updatedAt,
            string gameId = HoyoLabGameBundleRules.GameId)
        {
            var key = (syncId, gameId);
            var copy = copies[key];
            copies[key] = copy with { UpdatedAt = updatedAt };
        }

        internal DateTimeOffset GetRevision(
            string syncId,
            string gameId = HoyoLabGameBundleRules.GameId) => copies[(syncId, gameId)].UpdatedAt;

        internal HoyoLabGameBundle GetBundle(
            string syncId,
            HoyoLabSyncCrypto.DerivedSecrets secrets,
            string gameId = HoyoLabGameBundleRules.GameId)
        {
            var payload = Encoding.UTF8.GetBytes(copies[(syncId, gameId)].PayloadJson);
            try
            {
                Assert.True(HoyoLabSyncCrypto.TryParseEnvelope(payload, out var envelope));
                Assert.True(HoyoLabSyncCrypto.TryDecryptBundle(
                    secrets,
                    envelope,
                    UtcNow,
                    out var bundle,
                    gameId: gameId));
                return Assert.IsType<HoyoLabGameBundle>(bundle);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        internal void SeedBundle(
            string syncId,
            string code,
            HoyoLabGameBundle bundle,
            DateTimeOffset updatedAt,
            string gameId = HoyoLabGameBundleRules.GameId)
        {
            using var secrets = Secrets(code);
            Assert.True(HoyoLabSyncCrypto.TryEncryptBundle(
                secrets,
                bundle,
                UtcNow,
                FixedNonce((byte)(updatedAt.Minute + 1)),
                out var envelope));
            SeedRaw(syncId, envelope!, updatedAt, gameId);
        }

        internal void SeedRaw(
            string syncId,
            HoyoLabSyncCrypto.Envelope envelope,
            DateTimeOffset updatedAt,
            string gameId = HoyoLabGameBundleRules.GameId)
        {
            Assert.True(HoyoLabSyncCrypto.TrySerializeEnvelope(envelope, out var bytes));
            try
            {
                var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
                try
                {
                    copies[(syncId, gameId)] = new(
                        Encoding.UTF8.GetString(bytes),
                        updatedAt,
                        ciphertext.Length);
                    accounts.Add(syncId);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        private HttpResponseMessage Respond(RequestSnapshot request) => request.Action switch
        {
            "pull" => copies.TryGetValue((request.SyncId, request.GameId), out var copy)
                ? PullResponse(copy)
                : JsonResponse(HttpStatusCode.NotFound, "{}"),
            "status" => copies.TryGetValue((request.SyncId, request.GameId), out var statusCopy)
                ? JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"exists\":true,\"updatedAt\":\""
                    + FormatTimestamp(statusCopy.UpdatedAt)
                    + "\",\"size\":" + statusCopy.Size + "}")
                : JsonResponse(HttpStatusCode.NotFound, "{}"),
            "push" => SavePush(request),
            "delete" or "delete-account" => Delete(request),
            _ => throw new InvalidOperationException("Unexpected fake action."),
        };

        private HttpResponseMessage SavePush(RequestSnapshot request)
        {
            if (TryGetRevisionCondition(request, out var expected)
                && !MatchesRevision(request.SyncId, request.GameId, expected))
                return ConflictResponse(copies.TryGetValue((request.SyncId, request.GameId), out var current)
                    ? current.UpdatedAt
                    : null);
            var payload = request.Root.GetProperty("payload").GetRawText();
            var ciphertext = Convert.FromBase64String(
                request.Root.GetProperty("payload").GetProperty("ciphertext").GetString()!);
            try
            {
                var updatedAt = UtcNow;
                copies[(request.SyncId, request.GameId)] = new(payload, updatedAt, ciphertext.Length);
                accounts.Add(request.SyncId);
                return JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"updatedAt\":\""
                    + FormatTimestamp(updatedAt) + "\",\"size\":" + ciphertext.Length + "}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }

        private HttpResponseMessage Delete(RequestSnapshot request)
        {
            if (request.Action == "delete-account")
            {
                if (!accounts.Contains(request.SyncId))
                    return JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"deleted\":true}");
                if (request.Root.TryGetProperty("baseUpdatedAtByGame", out _))
                {
                    if (!TryGetGameRevisionCondition(request, out var expected)
                        || !MatchesGameRevisions(request.SyncId, expected))
                        return ConflictResponse(
                            CurrentRevision(request.SyncId, HoyoLabGameBundleRules.GameId),
                            CurrentRevision(request.SyncId, HoyoLabGameBundleRules.GenshinGameId));
                }
                else if (TryGetRevisionCondition(request, out var scalar))
                {
                    if (copies.ContainsKey((request.SyncId, HoyoLabGameBundleRules.GenshinGameId)))
                        return ConflictResponse(CurrentRevision(request.SyncId, HoyoLabGameBundleRules.GameId),
                            CurrentRevision(request.SyncId, HoyoLabGameBundleRules.GenshinGameId));
                    if (!MatchesRevision(request.SyncId, HoyoLabGameBundleRules.GameId, scalar))
                        return ConflictResponse(CurrentRevision(request.SyncId, HoyoLabGameBundleRules.GameId));
                }
                foreach (var key in copies.Keys.Where(key => key.SyncId == request.SyncId).ToArray())
                    copies.Remove(key);
                accounts.Remove(request.SyncId);
                return JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"deleted\":true}");
            }
            if (TryGetRevisionCondition(request, out var expectedGame)
                && !MatchesRevision(request.SyncId, request.GameId, expectedGame))
                return ConflictResponse(copies.TryGetValue((request.SyncId, request.GameId), out var current)
                    ? current.UpdatedAt
                    : null);
            copies.Remove((request.SyncId, request.GameId));
            return JsonResponse(HttpStatusCode.OK, "{\"ok\":true,\"deleted\":true}");
        }

        private bool MatchesRevision(
            string syncId,
            string gameId,
            DateTimeOffset? expected) =>
            expected is null
                ? !copies.ContainsKey((syncId, gameId))
                : copies.TryGetValue((syncId, gameId), out var current) && current.UpdatedAt == expected;

        private bool MatchesGameRevisions(string syncId, HoyoLabGameRevisions expected) =>
            MatchesRevision(syncId, HoyoLabGameBundleRules.GameId, expected.Hsr)
            && MatchesRevision(syncId, HoyoLabGameBundleRules.GenshinGameId, expected.Genshin);

        private DateTimeOffset? CurrentRevision(string syncId, string gameId) =>
            copies.TryGetValue((syncId, gameId), out var copy) ? copy.UpdatedAt : null;

        private static bool TryGetRevisionCondition(
            RequestSnapshot request,
            out DateTimeOffset? expected)
        {
            expected = null;
            if (!request.Root.TryGetProperty("baseUpdatedAt", out var value)) return false;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParseExact(
                    value.GetString(),
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
                return false;
            expected = parsed;
            return true;
        }

        private static bool TryGetGameRevisionCondition(
            RequestSnapshot request,
            out HoyoLabGameRevisions expected)
        {
            expected = new(null, null);
            if (!request.Root.TryGetProperty("baseUpdatedAtByGame", out var value)
                || value.ValueKind != JsonValueKind.Object)
                return false;
            if (value.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(["gi", "hsr"], StringComparer.Ordinal) is false)
                return false;
            if (!TryGetGameRevision(value.GetProperty("hsr"), out var hsr)
                || !TryGetGameRevision(value.GetProperty("gi"), out var gi))
                return false;
            expected = new(hsr, gi);
            return true;
        }

        private static bool TryGetGameRevision(JsonElement value, out DateTimeOffset? revision)
        {
            revision = null;
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParseExact(
                    value.GetString(),
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
                return false;
            revision = parsed;
            return true;
        }

        private static HttpResponseMessage PullResponse(RemoteCopy copy) => JsonResponse(
            HttpStatusCode.OK,
            "{\"ok\":true,\"payload\":" + copy.PayloadJson
                + ",\"updatedAt\":\"" + FormatTimestamp(copy.UpdatedAt)
                + "\",\"size\":" + copy.Size + "}");
    }

    private sealed class CopyProtector : IPublisherRoleBindingProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed class FailBindingDeleteAfterSnapshotBoundary
        : IPublisherRoleBindingFileBoundary
    {
        private readonly SystemPublisherRoleBindingFileBoundary inner = new();

        internal bool SnapshotDeleted { get; private set; }
        internal bool BindingDeleteAttempted { get; private set; }

        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public bool EntryExists(string path) => inner.EntryExists(path);
        public bool Exists(string path) => inner.Exists(path);
        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);
        public FileStream OpenRead(string path) => inner.OpenRead(path);
        public FileStream CreateNewWriteThrough(string path) => inner.CreateNewWriteThrough(path);
        public void MoveNew(string source, string destination) => inner.MoveNew(source, destination);
        public void MoveOverwrite(string source, string destination) => inner.MoveOverwrite(source, destination);

        public void Delete(string path)
        {
            if (path.Contains(".protected-resource-snapshots", StringComparison.OrdinalIgnoreCase))
            {
                inner.Delete(path);
                SnapshotDeleted = true;
                return;
            }
            if (!BindingDeleteAttempted
                && path.Contains(".protected-role-bindings", StringComparison.OrdinalIgnoreCase))
            {
                BindingDeleteAttempted = true;
                throw new IOException("injected binding delete failure");
            }
            inner.Delete(path);
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        internal TemporaryRoot() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "nyx-hoyolab-coordinator-" + Guid.NewGuid().ToString("N"));

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static HttpResponseMessage ConflictResponse(DateTimeOffset? serverUpdatedAt = null) => JsonResponse(
        HttpStatusCode.Conflict,
        "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"stale\",\"requestId\":\"req-test\"},\"serverUpdatedAt\":"
        + (serverUpdatedAt is null ? "null" : "\"" + FormatTimestamp(serverUpdatedAt.Value) + "\"")
        + "}");

    private static HttpResponseMessage ConflictResponse(
        DateTimeOffset? hsr,
        DateTimeOffset? genshin) => JsonResponse(
        HttpStatusCode.Conflict,
        "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"stale\",\"requestId\":\"req-test\"},\"serverUpdatedAtByGame\":{\"hsr\":"
        + (hsr is null ? "null" : "\"" + FormatTimestamp(hsr.Value) + "\"")
        + ",\"gi\":"
        + (genshin is null ? "null" : "\"" + FormatTimestamp(genshin.Value) + "\"")
        + "}}");

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }
}
