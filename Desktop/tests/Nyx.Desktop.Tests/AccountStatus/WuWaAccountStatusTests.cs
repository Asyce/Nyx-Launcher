using System.Net;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class WuWaAccountStatusTests
{
    [Fact]
    public void Consent_flag_defaults_off_and_round_trips_without_a_credential_field()
    {
        Assert.False(LauncherState.Defaults().Preferences.FeatureFlags.WuWaAccountStatus);
        var read = LauncherStateMigrations.Read("""
            {"version":2,"preferences":{"featureFlags":{"wuWaAccountStatus":true}}}
            """);
        Assert.True(read.State!.Preferences.FeatureFlags.WuWaAccountStatus);
        var written = LauncherStateMigrations.Write(read.State);
        Assert.Contains("\"wuWaAccountStatus\": true", written, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Credential_reader_uses_exact_production_cache_and_decodes_only_in_memory()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1784", "wrong", selected: true);
        WriteCache(directory.Path, "A1730", "right-secret", selected: true);
        var reader = new WuWaLauncherCredentialReader(directory.Path);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(WuWaAccountStatusFailure.None, result.Failure);
        Assert.Equal("right-secret", result.Credential!.OAuthCode);
        Assert.DoesNotContain("right-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("right-secret", result.Credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_reader_fails_closed_for_malformed_or_ambiguous_accounts()
    {
        using var malformed = new TemporaryDirectory();
        var malformedPath = CachePath(malformed.Path, "A1730");
        Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
        await File.WriteAllTextAsync(malformedPath, "{not-json");
        Assert.Equal(
            WuWaAccountStatusFailure.CacheMalformed,
            (await new WuWaLauncherCredentialReader(malformed.Path).ReadAsync(CancellationToken.None)).Failure);

        using var ambiguous = new TemporaryDirectory();
        WriteAccounts(ambiguous.Path,
            ("one", "first-secret", false),
            ("two", "second-secret", false));
        var result = await new WuWaLauncherCredentialReader(ambiguous.Path).ReadAsync(CancellationToken.None);
        Assert.Equal(WuWaAccountStatusFailure.MultipleAccounts, result.Failure);
        Assert.Null(result.Credential);
        Assert.DoesNotContain("first-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("second-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_reader_honors_the_bounded_last_login_marker_without_exposing_it()
    {
        using var directory = new TemporaryDirectory();
        WriteAccounts(directory.Path,
            ("not-current", "first-secret", true),
            ("current-account", "second-secret", false));
        WriteLastLogin(directory.Path, "current-account");

        var result = await new WuWaLauncherCredentialReader(directory.Path).ReadAsync(CancellationToken.None);

        Assert.Equal(WuWaAccountStatusFailure.None, result.Failure);
        Assert.Equal("second-secret", result.Credential!.OAuthCode);
        Assert.DoesNotContain("current-account", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("current-account", result.Credential.ToString(), StringComparison.Ordinal);

        WriteLastLogin(directory.Path, "unknown-account");
        var staleMarker = await new WuWaLauncherCredentialReader(directory.Path).ReadAsync(CancellationToken.None);
        Assert.Equal(WuWaAccountStatusFailure.MultipleAccounts, staleMarker.Failure);
        Assert.Null(staleMarker.Credential);
    }

    [Fact]
    public async Task Credential_reader_accepts_one_explicit_selection_and_rejects_two()
    {
        using var selected = new TemporaryDirectory();
        WriteAccounts(selected.Path,
            ("one", "first", false),
            ("two", "second", true));
        var chosen = await new WuWaLauncherCredentialReader(selected.Path).ReadAsync(CancellationToken.None);
        Assert.Equal("second", chosen.Credential!.OAuthCode);

        using var conflicting = new TemporaryDirectory();
        WriteAccounts(conflicting.Path,
            ("one", "first", true),
            ("two", "second", true));
        Assert.Equal(
            WuWaAccountStatusFailure.MultipleAccounts,
            (await new WuWaLauncherCredentialReader(conflicting.Path).ReadAsync(CancellationToken.None)).Failure);
    }

    [Fact]
    public void Parser_requires_region_keyed_nested_player_info_and_complete_bounded_role_schema()
    {
        var parser = new WuWaAccountStatusResponseParser();
        var player = Utf8(ResponseWithNested("America", new { roleId = "123456789" }));
        Assert.True(parser.TryParsePlayerInfo(player, out var identity));
        Assert.Equal("123456789", identity!.PlayerId);
        Assert.Equal("America", identity.Region);

        var role = Utf8(RoleResponse("America", new
        {
            Energy = 180,
            MaxEnergy = 240,
            StoreEnergy = 45,
            StoreEnergyRecoverTime = 1000,
            EnergyRecoverTime = 2000,
            Liveness = 60,
            LivenessMaxCount = 100,
        }));
        Assert.True(parser.TryParseRole(role, "America", out var snapshot));
        Assert.Equal(180, snapshot!.Energy);
        Assert.Equal(60, snapshot.Liveness);

        Assert.False(parser.TryParsePlayerInfo(Utf8("{\"code\":0,\"data\":{\"roleId\":\"1\",\"region\":\"America\"}}"), out _));
        Assert.False(parser.TryParseRole(Utf8(ResponseWithNested("America", new
        {
            Energy = 180, MaxEnergy = 240, StoreEnergy = 45,
            StoreEnergyRecoverTime = 1000, EnergyRecoverTime = 2000,
            Liveness = 60, LivenessMaxCount = 100,
        })), "America", out _));
        Assert.False(parser.TryParseRole(Utf8(ResponseWithNested("America", new { Energy = 999 })), "America", out _));
        Assert.False(parser.TryParseRole(role, "../America", out _));
        Assert.True(parser.IsRejected(Utf8("{\"code\":401,\"msg\":\"contains-sensitive-server-text\"}")));
        Assert.True(parser.IsRedisEmpty(Utf8("{\"code\":1005,\"msg\":\"ignored\"}")));
    }

    [Fact]
    public void Account_identity_and_result_redact_personal_values_and_keep_constructor_compatibility()
    {
        var identity = new WuWaAccountIdentity("2468013579", "Europe");
        Assert.Equal("2468013579 · Europe", identity.DisplayText);
        Assert.Equal(nameof(WuWaAccountIdentity), identity.ToString());
        Assert.DoesNotContain(identity.PlayerId, identity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(identity.Region, identity.ToString(), StringComparison.Ordinal);

        var result = new WuWaAccountStatusResult(
            DateTimeOffset.UtcNow,
            WuWaAccountStatusFailure.None,
            new WuWaAccountStatusSnapshot(1, 2, 3, 4, 5, 6, 7),
            DateTimeOffset.UtcNow,
            false);
        Assert.Null(result.Identity);
        Assert.Equal(nameof(WuWaAccountStatusResult), result.ToString());
        Assert.DoesNotContain(identity.PlayerId, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(identity.Region, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(WuWaAccountStatusRules.MaximumRecoverySeconds, WuWaAccountStatusRules.MaximumRecoverySeconds)]
    [InlineData(WuWaAccountStatusRules.MaximumRecoverySeconds + 1, 0L)]
    [InlineData(long.MaxValue, 0L)]
    [InlineData(long.MinValue, 0L)]
    public void Parser_normalizes_unbounded_recovery_without_dropping_valid_resources(
        long recoverySeconds,
        long expectedRecoverySeconds)
    {
        var parser = new WuWaAccountStatusResponseParser();
        var response = Utf8(RoleResponse("America", new
        {
            Energy = 180,
            MaxEnergy = 300,
            StoreEnergy = 45,
            StoreEnergyRecoverTime = 0L,
            EnergyRecoverTime = recoverySeconds,
            Liveness = 60,
            LivenessMaxCount = 100,
        }));

        Assert.True(parser.TryParseRole(response, "America", out var snapshot));
        Assert.Equal(180, snapshot!.Energy);
        Assert.Equal(45, snapshot.StoreEnergy);
        Assert.Equal(60, snapshot.Liveness);
        Assert.Equal(expectedRecoverySeconds, snapshot.EnergyRecoverTime);
    }

    [Fact]
    public void Parser_normalizes_an_overlarge_stored_energy_recovery_without_dropping_valid_resources()
    {
        var parser = new WuWaAccountStatusResponseParser();
        var response = Utf8(RoleResponse("America", new
        {
            Energy = 180,
            MaxEnergy = 300,
            StoreEnergy = 45,
            StoreEnergyRecoverTime = WuWaAccountStatusRules.MaximumRecoverySeconds + 1,
            EnergyRecoverTime = 7_260L,
            Liveness = 60,
            LivenessMaxCount = 100,
        }));

        Assert.True(parser.TryParseRole(response, "America", out var snapshot));
        Assert.Equal(180, snapshot!.Energy);
        Assert.Equal(45, snapshot.StoreEnergy);
        Assert.Equal(0, snapshot.StoreEnergyRecoverTime);
        Assert.Equal(7_260, snapshot.EnergyRecoverTime);
    }

    [Fact]
    public void Parser_success_requires_one_unambiguous_positive_code_or_retcode()
    {
        var parser = new WuWaAccountStatusResponseParser();
        var data = new Dictionary<string, string>
        {
            ["America"] = JsonSerializer.Serialize(new { roleId = "123456789" }),
        };

        Assert.False(parser.TryParsePlayerInfo(
            Utf8(JsonSerializer.Serialize(new { data })),
            out _));
        Assert.True(parser.TryParsePlayerInfo(
            Utf8(JsonSerializer.Serialize(new { retcode = 200, data })),
            out _));
        Assert.False(parser.TryParsePlayerInfo(
            Utf8(JsonSerializer.Serialize(new { code = 0, retcode = 0, data })),
            out _));
        Assert.False(parser.TryParsePlayerInfo(
            Utf8(JsonSerializer.Serialize(new { code = 0, retcode = 401, data })),
            out _));
        Assert.False(parser.TryParsePlayerInfo(
            Utf8(JsonSerializer.Serialize(new { code = "ok", data })),
            out _));

        var duplicateCode = ResponseWithNested("America", new { roleId = "123456789" })
            .Replace("{\"code\":0", "{\"code\":0,\"code\":0", StringComparison.Ordinal);
        Assert.False(parser.TryParsePlayerInfo(Utf8(duplicateCode), out _));
        Assert.False(parser.IsRejected(Utf8("{\"code\":0,\"retcode\":401}")));
        Assert.False(parser.IsRedisEmpty(Utf8("{\"code\":1005,\"retcode\":1005}")));
    }

    [Fact]
    public async Task Transport_allows_only_fixed_https_endpoints_and_rejects_redirects_and_large_bodies()
    {
        Assert.Equal("https", WuWaAccountStatusTransport.PlayerInfoEndpoint.Scheme);
        Assert.Equal("pc-launcher-sdk-api.kurogame.net", WuWaAccountStatusTransport.RoleEndpoint.Host);
        Assert.Throws<InvalidOperationException>(() =>
            WuWaAccountStatusTransport.ValidateEndpoint(new Uri("https://evil.invalid/game/queryRole")));

        using var redirect = new WuWaAccountStatusTransport(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)),
            TimeSpan.FromSeconds(1));
        var redirectFailure = await Assert.ThrowsAsync<WuWaTransportException>(() =>
            redirect.PostAsync(WuWaAccountStatusTransport.RoleEndpoint, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Equal(WuWaAccountStatusFailure.InvalidResponse, redirectFailure.Failure);

        using var oversized = new WuWaAccountStatusTransport(
            new StubHandler(_ => JsonResponse(new byte[WuWaAccountStatusTransport.MaximumResponseBytes + 1])),
            TimeSpan.FromSeconds(1));
        var sizeFailure = await Assert.ThrowsAsync<WuWaTransportException>(() =>
            oversized.PostAsync(WuWaAccountStatusTransport.RoleEndpoint, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Equal(WuWaAccountStatusFailure.ResponseTooLarge, sizeFailure.Failure);
    }

    [Fact]
    public async Task Transport_times_out_and_production_handler_disables_redirect_cookie_and_proxy_state()
    {
        using var handler = WuWaAccountStatusTransport.CreateProductionHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromSeconds(4), handler.ConnectTimeout);

        using var transport = new WuWaAccountStatusTransport(
            new StubHandler(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return JsonResponse("{}"u8.ToArray());
            }),
            TimeSpan.FromMilliseconds(20));
        var failure = await Assert.ThrowsAsync<WuWaTransportException>(() =>
            transport.PostAsync(WuWaAccountStatusTransport.PlayerInfoEndpoint, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Equal(WuWaAccountStatusFailure.Timeout, failure.Failure);
    }

    [Fact]
    public async Task Service_uses_player_info_before_role_single_flights_and_rate_limits_without_leaking_identity()
    {
        using var directory = new TemporaryDirectory();
        const string oauth = "dummy-oauth-value";
        const string roleId = "2468013579";
        WriteCache(directory.Path, "A1730", oauth, selected: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(release.Task, oauth, roleId,
            Utf8(ResponseWithNested("Europe", new { roleId })),
            Utf8(RoleResponse("Europe", new
            {
                Energy = 100,
                MaxEnergy = 240,
                StoreEnergy = 25,
                StoreEnergyRecoverTime = 10,
                EnergyRecoverTime = 20,
                Liveness = 40,
                LivenessMaxCount = 100,
            })));
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-20T10:00:00Z"));
        await using var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            clock,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10));

        var first = service.RefreshAsync();
        var second = service.RefreshAsync();
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(roleId, results[0].Identity?.PlayerId);
        Assert.Equal("Europe", results[0].Identity?.Region);
        Assert.Equal($"{roleId} · Europe", results[0].Identity?.DisplayText);
        Assert.Equal(2, transport.Calls.Count);
        Assert.Equal(WuWaAccountStatusTransport.PlayerInfoEndpoint, transport.Calls[0].Endpoint);
        Assert.Equal(WuWaAccountStatusTransport.RoleEndpoint, transport.Calls[1].Endpoint);
        Assert.True(transport.Calls[0].HasExpectedOAuth);
        Assert.False(transport.Calls[0].HasExpectedPlayerId);
        Assert.True(transport.Calls[1].HasExpectedOAuth);
        Assert.True(transport.Calls[1].HasExpectedPlayerId);
        Assert.DoesNotContain(oauth, results[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(roleId, results[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Europe", results[0].ToString(), StringComparison.Ordinal);
        var resultIdentity = Assert.IsType<WuWaAccountIdentity>(results[0].Identity);
        Assert.DoesNotContain(roleId, resultIdentity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Europe", resultIdentity.ToString(), StringComparison.Ordinal);

        var limited = await service.RefreshAsync();
        Assert.Same(results[0], limited);
        Assert.Equal(WuWaAccountStatusFailure.None, limited.Failure);
        Assert.Equal(2, transport.Calls.Count);

        clock.Advance(TimeSpan.FromMinutes(11));
        transport.RejectRole();
        var stale = await service.RefreshAsync();
        Assert.Equal(WuWaAccountStatusFailure.InvalidResponse, stale.Failure);
        Assert.NotNull(stale.Snapshot);
        Assert.True(stale.IsStale);
        Assert.Equal(results[0].Identity, stale.Identity);
        Assert.DoesNotContain(oauth, stale.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Over_bound_role_recovery_is_normalized_without_retaining_a_stale_snapshot()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var invalidRecovery = WuWaAccountStatusRules.MaximumRecoverySeconds + 1;
        var transport = new SequencedRoleTransport(
            Utf8(RoleResponse("Europe", ValidRoleBase())),
            Utf8(RoleResponse("Europe", new
            {
                Energy = 100,
                MaxEnergy = 240,
                StoreEnergy = 25,
                StoreEnergyRecoverTime = 10,
                EnergyRecoverTime = invalidRecovery,
                Liveness = 40,
                LivenessMaxCount = 100,
            })));
        await using var service = CreateRetryService(
            directory.Path,
            transport,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var accepted = await service.RefreshAsync();
        var normalized = await service.RefreshAsync();

        Assert.True(accepted.IsSuccess);
        Assert.True(normalized.IsSuccess);
        Assert.False(normalized.IsStale);
        Assert.NotEqual(accepted.Snapshot, normalized.Snapshot);
        Assert.NotNull(normalized.Snapshot);
        Assert.Equal(100, normalized.Snapshot!.Energy);
        Assert.Equal(25, normalized.Snapshot.StoreEnergy);
        Assert.Equal(0, normalized.Snapshot.EnergyRecoverTime);
        Assert.Equal(40, normalized.Snapshot.Liveness);
        Assert.Equal(2, transport.RoleCalls);
    }

    [Fact]
    public async Task Local_cooldown_preserves_the_real_prior_result()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var transport = new RecordingTransport(
            Task.CompletedTask,
            "dummy",
            "1",
            Utf8(ResponseWithNested("Europe", new { roleId = "1" })),
            Utf8(RoleResponse("Europe", ValidRoleBase())));
        transport.RejectPlayerInfo();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            clock,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10));

        var publisherResult = await service.RefreshAsync();
        var manualDuringCooldown = await service.RefreshAsync();

        Assert.Equal(WuWaAccountStatusFailure.InvalidResponse, publisherResult.Failure);
        Assert.Same(publisherResult, manualDuringCooldown);
        Assert.Same(publisherResult, service.Current);
        Assert.Single(transport.Calls);
        Assert.True(service.IsRefreshCoolingDown);
    }

    [Fact]
    public async Task Redis_empty_retries_at_one_second_then_accepts_a_complete_snapshot()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var transport = new SequencedRoleTransport(
            RedisEmptyResponse(),
            RedisEmptyResponse(),
            Utf8(RoleResponse("Europe", ValidRoleBase())));
        var delays = new List<TimeSpan>();
        await using var service = CreateRetryService(
            directory.Path,
            transport,
            (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await service.RefreshAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, transport.RoleCalls);
        Assert.Equal(
            [WuWaAccountStatusService.ProductionRedisEmptyRetryDelay, WuWaAccountStatusService.ProductionRedisEmptyRetryDelay],
            delays);
    }

    [Fact]
    public async Task Redis_empty_retry_count_is_bounded()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var responses = Enumerable
            .Range(0, WuWaAccountStatusService.ProductionRedisEmptyMaximumRetries + 1)
            .Select(_ => RedisEmptyResponse())
            .ToArray();
        var transport = new SequencedRoleTransport(responses);
        var delayCount = 0;
        await using var service = CreateRetryService(
            directory.Path,
            transport,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delayCount++;
                return Task.CompletedTask;
            });

        var result = await service.RefreshAsync();

        Assert.Equal(WuWaAccountStatusFailure.InvalidResponse, result.Failure);
        Assert.Equal(WuWaAccountStatusService.ProductionRedisEmptyMaximumRetries + 1, transport.RoleCalls);
        Assert.Equal(WuWaAccountStatusService.ProductionRedisEmptyMaximumRetries, delayCount);
    }

    [Fact]
    public async Task Exhausted_redis_empty_is_transient_and_preserves_same_account_stale_data()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var responses = new[] { Utf8(RoleResponse("Europe", ValidRoleBase())) }
            .Concat(Enumerable
                .Range(0, WuWaAccountStatusService.ProductionRedisEmptyMaximumRetries + 1)
                .Select(_ => RedisEmptyResponse()))
            .ToArray();
        var transport = new SequencedRoleTransport(responses);
        await using var service = CreateRetryService(
            directory.Path,
            transport,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        var accepted = await service.RefreshAsync();

        var exhausted = await service.RefreshAsync();

        Assert.True(accepted.IsSuccess);
        Assert.Equal(WuWaAccountStatusFailure.InvalidResponse, exhausted.Failure);
        Assert.Equal(accepted.Snapshot, exhausted.Snapshot);
        Assert.Equal(accepted.SuccessfulAt, exhausted.SuccessfulAt);
        Assert.Equal(accepted.Identity, exhausted.Identity);
        Assert.True(exhausted.IsStale);
        Assert.Equal(WuWaAccountStatusService.ProductionRedisEmptyMaximumRetries + 2, transport.RoleCalls);
    }

    [Theory]
    [InlineData(WuWaAccountStatusFailure.Timeout)]
    [InlineData(WuWaAccountStatusFailure.Network)]
    [InlineData(WuWaAccountStatusFailure.InvalidResponse)]
    public async Task Unchanged_account_player_info_transient_failure_preserves_stale_data(
        WuWaAccountStatusFailure failure)
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "unchanged", selected: true);
        var transport = new RecordingTransport(
            Task.CompletedTask,
            "unchanged",
            "1",
            Utf8(ResponseWithNested("Europe", new { roleId = "1" })),
            Utf8(RoleResponse("Europe", ValidRoleBase())));
        await using var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10));
        var accepted = await service.RefreshAsync();
        if (failure is WuWaAccountStatusFailure.InvalidResponse)
            transport.RejectPlayerInfo();
        else
            transport.FailPlayerInfo(failure);

        var transient = await service.RefreshAsync();

        Assert.True(accepted.IsSuccess);
        Assert.Equal(failure, transient.Failure);
        Assert.Equal(accepted.Snapshot, transient.Snapshot);
        Assert.Equal(accepted.SuccessfulAt, transient.SuccessfulAt);
        Assert.Equal(accepted.Identity, transient.Identity);
        Assert.True(transient.IsStale);
    }

    [Fact]
    public async Task Redis_empty_retry_delay_stops_on_explicit_opt_out()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var transport = new SequencedRoleTransport(RedisEmptyResponse());
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateRetryService(
            directory.Path,
            transport,
            async (_, cancellationToken) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        var refresh = service.RefreshAsync();
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.DisableSession();
        var result = await refresh;

        Assert.Equal(WuWaAccountStatusFailure.Canceled, result.Failure);
        Assert.Equal(1, transport.RoleCalls);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task Player_info_redis_empty_retries_then_continues_to_role_lookup()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var transport = new SequencedAccountStatusTransport(
            [RedisEmptyResponse(), PlayerInfoResponse()],
            [Utf8(RoleResponse("Europe", ValidRoleBase()))]);
        var delays = new List<TimeSpan>();
        await using var service = CreateRetryService(
            directory.Path,
            transport,
            (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var result = await service.RefreshAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, transport.PlayerInfoCalls);
        Assert.Equal(1, transport.RoleCalls);
        Assert.Equal([WuWaAccountStatusService.ProductionRedisEmptyRetryDelay], delays);
    }

    [Fact]
    public async Task Exhausted_player_info_redis_empty_preserves_same_credential_stale_data()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var playerResponses = new[] { PlayerInfoResponse() }
            .Concat(Enumerable
                .Range(0, WuWaAccountStatusService.ProductionRedisEmptyMaximumRetries + 1)
                .Select(_ => RedisEmptyResponse()))
            .ToArray();
        var transport = new SequencedAccountStatusTransport(
            playerResponses,
            [Utf8(RoleResponse("Europe", ValidRoleBase()))]);
        await using var service = CreateRetryService(
            directory.Path,
            transport,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        var accepted = await service.RefreshAsync();

        var exhausted = await service.RefreshAsync();

        Assert.True(accepted.IsSuccess);
        Assert.Equal(WuWaAccountStatusFailure.InvalidResponse, exhausted.Failure);
        Assert.Equal(accepted.Snapshot, exhausted.Snapshot);
        Assert.Equal(accepted.SuccessfulAt, exhausted.SuccessfulAt);
        Assert.True(exhausted.IsStale);
        Assert.Equal(WuWaAccountStatusService.ProductionRedisEmptyMaximumRetries + 2, transport.PlayerInfoCalls);
        Assert.Equal(1, transport.RoleCalls);
    }

    [Fact]
    public async Task Player_info_redis_empty_retry_delay_stops_on_explicit_opt_out()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var transport = new SequencedAccountStatusTransport([RedisEmptyResponse()], []);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = CreateRetryService(
            directory.Path,
            transport,
            async (_, cancellationToken) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        var refresh = service.RefreshAsync();
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.DisableSession();
        var result = await refresh;

        Assert.Equal(WuWaAccountStatusFailure.Canceled, result.Failure);
        Assert.Equal(1, transport.PlayerInfoCalls);
        Assert.Equal(0, transport.RoleCalls);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task Canceled_caller_returns_without_canceling_the_bounded_shared_fetch()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(release.Task, "dummy", "1",
            Utf8(ResponseWithNested("Europe", new { roleId = "1" })),
            Utf8(RoleResponse("Europe", new
            {
                Energy = 1, MaxEnergy = 2, StoreEnergy = 0,
                StoreEnergyRecoverTime = 0, EnergyRecoverTime = 1,
                Liveness = 1, LivenessMaxCount = 2,
            })));
        await using var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10));
        using var cancellation = new CancellationTokenSource();
        var canceled = service.RefreshAsync(cancellation.Token);
        var shared = service.RefreshAsync();
        cancellation.Cancel();
        Assert.Equal(WuWaAccountStatusFailure.Canceled, (await canceled).Failure);
        release.SetResult();
        Assert.True((await shared).IsSuccess);
    }

    [Fact]
    public async Task Explicit_opt_out_cancels_the_active_flow_before_role_lookup()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(never.Task, "dummy", "1",
            Utf8(ResponseWithNested("Europe", new { roleId = "1" })),
            Utf8(RoleResponse("Europe", new
            {
                Energy = 1, MaxEnergy = 2, StoreEnergy = 0,
                StoreEnergyRecoverTime = 0, EnergyRecoverTime = 1,
                Liveness = 1, LivenessMaxCount = 2,
            })));
        await using var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10));

        var refresh = service.RefreshAsync();
        await WaitForAsync(() => transport.Calls.Count == 1);
        service.DisableSession();

        Assert.Equal(WuWaAccountStatusFailure.Canceled, (await refresh).Failure);
        Assert.Single(transport.Calls);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task Disposal_waits_for_inflight_cancellation_before_completing()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "dummy", selected: true);
        var transport = new CancellationBarrierTransport();
        var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10));
        var refresh = service.RefreshAsync();
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = service.DisposeAsync().AsTask();
        await transport.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(disposal.IsCompleted);
        transport.Release.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(WuWaAccountStatusFailure.Shutdown, (await refresh).Failure);
        Assert.Null(service.Current);
        var afterDispose = await service.RefreshAsync();
        Assert.Equal(WuWaAccountStatusFailure.Shutdown, afterDispose.Failure);
        Assert.Null(afterDispose.Identity);
    }

    [Fact]
    public async Task Different_account_or_missing_cache_never_receives_previous_account_totals()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "account-a", selected: true);
        var transport = new RecordingTransport(Task.CompletedTask, "account-a", "1",
            Utf8(ResponseWithNested("Europe", new { roleId = "1" })),
            Utf8(RoleResponse("Europe", new
            {
                Energy = 199, MaxEnergy = 240, StoreEnergy = 30,
                StoreEnergyRecoverTime = 0, EnergyRecoverTime = 1,
                Liveness = 80, LivenessMaxCount = 100,
            })));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(10));
        Assert.True((await service.RefreshAsync()).IsSuccess);

        WriteCache(directory.Path, "A1730", "account-b", selected: true);
        clock.Advance(TimeSpan.FromSeconds(2));
        transport.FailPlayerInfo(WuWaAccountStatusFailure.Timeout);
        var changedAccount = await service.RefreshAsync();
        Assert.Equal(WuWaAccountStatusFailure.Timeout, changedAccount.Failure);
        Assert.Null(changedAccount.Snapshot);
        Assert.Null(changedAccount.SuccessfulAt);
        Assert.Null(changedAccount.Identity);
        Assert.Null(service.Current!.Snapshot);

        File.Delete(CachePath(directory.Path, "A1730"));
        clock.Advance(TimeSpan.FromSeconds(2));
        var signedOut = await service.RefreshAsync();
        Assert.Equal(WuWaAccountStatusFailure.CacheNotFound, signedOut.Failure);
        Assert.Null(signedOut.Snapshot);
        Assert.Null(signedOut.SuccessfulAt);
        Assert.Null(signedOut.Identity);
    }

    [Fact]
    public async Task Authentication_rejection_clears_same_account_stale_observation()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "account-a", selected: true);
        var transport = new RecordingTransport(Task.CompletedTask, "account-a", "1",
            Utf8(ResponseWithNested("Europe", new { roleId = "1" })),
            Utf8(RoleResponse("Europe", new
            {
                Energy = 199, MaxEnergy = 240, StoreEnergy = 30,
                StoreEnergyRecoverTime = 0, EnergyRecoverTime = 1,
                Liveness = 80, LivenessMaxCount = 100,
            })));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            clock,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(10));
        Assert.True((await service.RefreshAsync()).IsSuccess);

        clock.Advance(TimeSpan.FromSeconds(2));
        transport.RejectAuthentication();
        var rejected = await service.RefreshAsync();
        Assert.Equal(WuWaAccountStatusFailure.PlayerInfoRejected, rejected.Failure);
        Assert.Null(rejected.Snapshot);
        Assert.Null(rejected.SuccessfulAt);
        Assert.Null(rejected.Identity);
    }

    [Fact]
    public async Task Disable_session_forgets_a_completed_observation()
    {
        using var directory = new TemporaryDirectory();
        WriteCache(directory.Path, "A1730", "account-a", selected: true);
        var transport = new RecordingTransport(Task.CompletedTask, "account-a", "1",
            Utf8(ResponseWithNested("Europe", new { roleId = "1" })),
            Utf8(RoleResponse("Europe", new
            {
                Energy = 199, MaxEnergy = 240, StoreEnergy = 30,
                StoreEnergyRecoverTime = 0, EnergyRecoverTime = 1,
                Liveness = 80, LivenessMaxCount = 100,
            })));
        await using var service = new WuWaAccountStatusService(
            transport,
            new WuWaLauncherCredentialReader(directory.Path),
            new WuWaAccountStatusResponseParser(),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10));
        Assert.True((await service.RefreshAsync()).IsSuccess);

        service.DisableSession();

        Assert.Null(service.Current);
    }

    private static void WriteCache(string root, string appId, string oauth, bool selected) =>
        WriteAccounts(root, appId, [("user", oauth, selected)]);

    private static void WriteAccounts(string root, params (string Cuid, string OAuth, bool Selected)[] accounts) =>
        WriteAccounts(root, "A1730", accounts);

    private static void WriteAccounts(
        string root,
        string appId,
        (string Cuid, string OAuth, bool Selected)[] accounts)
    {
        var path = CachePath(root, appId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new
        {
            accounts = accounts.Select(account => new
            {
                cuid = account.Cuid,
                oauthCode = WuWaLauncherCredentialReader.DecodeOAuthCode(account.OAuth),
                isSelected = account.Selected,
            }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
    }

    private static string CachePath(string root, string appId) =>
        Path.Combine(root, "KR_G153", appId, "KRSDKUserLauncherCache.json");

    private static void WriteLastLogin(string root, string cuid)
    {
        var path = Path.Combine(root, "KR_G153", "A1730", "KRSDKUserCache.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { last_login_cuid = cuid }));
    }

    private static string ResponseWithNested(string region, object value) => JsonSerializer.Serialize(new
    {
        code = 0,
        data = new Dictionary<string, string> { [region] = JsonSerializer.Serialize(value) },
    });

    private static string RoleResponse(string region, object roleBase) => ResponseWithNested(region, new
    {
        Base = roleBase,
        BattlePass = new { },
    });

    private static object ValidRoleBase() => new
    {
        Energy = 100,
        MaxEnergy = 240,
        StoreEnergy = 25,
        StoreEnergyRecoverTime = 10,
        EnergyRecoverTime = 20,
        Liveness = 40,
        LivenessMaxCount = 100,
    };

    private static byte[] RedisEmptyResponse() => "{\"code\":1005}"u8.ToArray();

    private static byte[] PlayerInfoResponse() =>
        Utf8(ResponseWithNested("Europe", new { roleId = "1" }));

    private static WuWaAccountStatusService CreateRetryService(
        string root,
        IWuWaAccountStatusTransport transport,
        Func<TimeSpan, CancellationToken, Task> retryDelay) =>
        new(
            transport,
            new WuWaLauncherCredentialReader(root),
            new WuWaAccountStatusResponseParser(),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10),
            retryDelay);

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static HttpResponseMessage JsonResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
            : this((request, _) => Task.FromResult(send(request))) { }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
            this.send = send;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class RecordingTransport(
        Task release,
        string expectedOAuth,
        string expectedPlayerId,
        byte[] playerResponse,
        byte[] roleResponse) : IWuWaAccountStatusTransport
    {
        private bool rejectPlayerInfo;
        private bool rejectAuthentication;
        private bool rejectRole;
        private WuWaAccountStatusFailure? playerInfoTransportFailure;
        public List<(Uri Endpoint, bool HasExpectedOAuth, bool HasExpectedPlayerId)> Calls { get; } = [];

        public void RejectPlayerInfo() => rejectPlayerInfo = true;

        public void RejectAuthentication() => rejectAuthentication = true;

        public void RejectRole() => rejectRole = true;

        public void FailPlayerInfo(WuWaAccountStatusFailure failure)
        {
            Assert.Contains(failure, new[]
            {
                WuWaAccountStatusFailure.Timeout,
                WuWaAccountStatusFailure.Network,
            });
            playerInfoTransportFailure = failure;
        }

        public async Task<byte[]> PostAsync(Uri endpoint, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var hasOAuth = root.TryGetProperty("oauthCode", out var oauth)
                && string.Equals(oauth.GetString(), expectedOAuth, StringComparison.Ordinal);
            var hasPlayerId = root.TryGetProperty("playerId", out var playerId)
                && string.Equals(playerId.GetString(), expectedPlayerId, StringComparison.Ordinal);
            Calls.Add((endpoint, hasOAuth, hasPlayerId));
            if (Calls.Count == 1) await release.WaitAsync(cancellationToken);
            if (playerInfoTransportFailure is { } transportFailure
                && endpoint == WuWaAccountStatusTransport.PlayerInfoEndpoint)
                throw new WuWaTransportException(transportFailure);
            if (rejectAuthentication && endpoint == WuWaAccountStatusTransport.PlayerInfoEndpoint)
                return "{\"code\":401,\"data\":{}}"u8.ToArray();
            if (rejectPlayerInfo && endpoint == WuWaAccountStatusTransport.PlayerInfoEndpoint)
                return "{\"code\":0,\"data\":{}}"u8.ToArray();
            if (rejectRole && endpoint == WuWaAccountStatusTransport.RoleEndpoint)
                return "{\"code\":0,\"data\":{}}"u8.ToArray();
            return endpoint == WuWaAccountStatusTransport.PlayerInfoEndpoint
                ? playerResponse.ToArray()
                : roleResponse.ToArray();
        }
    }

    private sealed class SequencedRoleTransport(params byte[][] roleResponses) : IWuWaAccountStatusTransport
    {
        private readonly Queue<byte[]> responses = new(roleResponses);

        public int RoleCalls { get; private set; }

        public Task<byte[]> PostAsync(
            Uri endpoint,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (endpoint == WuWaAccountStatusTransport.PlayerInfoEndpoint)
                return Task.FromResult(Utf8(ResponseWithNested("Europe", new { roleId = "1" })));

            Assert.Equal(WuWaAccountStatusTransport.RoleEndpoint, endpoint);
            RoleCalls++;
            return Task.FromResult(responses.Dequeue().ToArray());
        }
    }

    private sealed class SequencedAccountStatusTransport(
        byte[][] playerInfoResponses,
        byte[][] roleResponses) : IWuWaAccountStatusTransport
    {
        private readonly Queue<byte[]> playerResponses = new(playerInfoResponses);
        private readonly Queue<byte[]> roleResponseQueue = new(roleResponses);

        public int PlayerInfoCalls { get; private set; }

        public int RoleCalls { get; private set; }

        public Task<byte[]> PostAsync(
            Uri endpoint,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (endpoint == WuWaAccountStatusTransport.PlayerInfoEndpoint)
            {
                PlayerInfoCalls++;
                return Task.FromResult(playerResponses.Dequeue().ToArray());
            }

            Assert.Equal(WuWaAccountStatusTransport.RoleEndpoint, endpoint);
            RoleCalls++;
            return Task.FromResult(roleResponseQueue.Dequeue().ToArray());
        }
    }

    private sealed class CancellationBarrierTransport : IWuWaAccountStatusTransport
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]> PostAsync(
            Uri endpoint,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult();
                await Release.Task;
                throw;
            }
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nyx-wuwa-status-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
