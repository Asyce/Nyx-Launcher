using Nyx.Desktop.Core.PublisherMaintenance;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.PublisherMaintenance;

namespace Nyx.Desktop.Tests.PublisherMaintenance;

public sealed class HoyoPublisherStatusServiceTests
{
    private static readonly DateTimeOffset InitialTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_batch_maps_current_update_and_hsr_pre_download_independently()
    {
        var clock = new FakeClock(InitialTime);
        await using var service = CreateService(SuccessTransport(), clock);

        var result = await service.RefreshAsync(new("6.7.0", "4.2.0", "2.3.0"));

        Assert.Equal(PublisherCheckFailure.None, result.Failure);
        Assert.True(result.IsCurrentKnown);
        Assert.Equal(InitialTime, result.CheckedAt);
        Assert.Equal(PublisherUpdateState.Current, Game(result, "genshin").Update);
        Assert.Equal(PublisherUpdateState.UpdateOffered, Game(result, "hsr").Update);
        Assert.Equal(PublisherPreDownloadState.Offered, Game(result, "hsr").PreDownload);
        Assert.Equal("4.3.0", Game(result, "hsr").LiveVersion);
        Assert.Equal("4.4.0", Game(result, "hsr").PreDownloadVersion);
        Assert.Equal(PublisherUpdateState.Current, Game(result, "zzz").Update);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4.3")]
    [InlineData("v4.3.0")]
    public async Task Invalid_or_missing_local_version_keeps_remote_observation_but_not_update_claim(string? local)
    {
        await using var service = CreateService(SuccessTransport());

        var result = await service.RefreshAsync(new("6.7.0", local, "2.3.0"));

        Assert.Equal(PublisherObservationState.Available, Game(result, "hsr").Observation);
        Assert.Equal(PublisherUpdateState.Unknown, Game(result, "hsr").Update);
        Assert.Equal(PublisherPreDownloadState.Offered, Game(result, "hsr").PreDownload);
    }

    [Fact]
    public async Task Remote_main_older_than_local_is_unknown_not_current()
    {
        await using var service = CreateService(SuccessTransport());

        var result = await service.RefreshAsync(new("6.7.0", "4.4.0", "2.3.0"));

        var hsr = Game(result, "hsr");
        Assert.Equal(PublisherObservationState.Unknown, hsr.Observation);
        Assert.Equal(PublisherUpdateState.Unknown, hsr.Update);
        Assert.Equal(PublisherPreDownloadState.Unknown, hsr.PreDownload);
        Assert.Null(hsr.LiveVersion);
        Assert.False(result.IsCurrentKnown);
    }

    [Fact]
    public async Task Multi_megabyte_local_version_is_bounded_and_never_enters_output()
    {
        var hugeLocalVersion = new string('9', 4 * 1024 * 1024);
        await using var service = CreateService(SuccessTransport());

        var result = await service.RefreshAsync(new("6.7.0", hugeLocalVersion, "2.3.0"));

        Assert.Equal(PublisherObservationState.Available, Game(result, "hsr").Observation);
        Assert.Equal(PublisherUpdateState.Unknown, Game(result, "hsr").Update);
        Assert.DoesNotContain(hugeLocalVersion, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_refresh_after_success_is_unknown_while_prior_success_is_timestamped_advisory()
    {
        var clock = new FakeClock(InitialTime);
        var transport = new SequenceTransport(
            (_, _) => Task.FromResult(SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch)),
            (_, _) => throw new HttpRequestException("sanitized fixture failure"));
        await using var service = CreateService(transport, clock);
        var localLaunch = LocalGameStatus.Ready;

        var success = await service.RefreshAsync(new("6.7.0", "4.3.0", "2.3.0"));
        clock.UtcNow = InitialTime.AddMinutes(5);
        var failed = await service.RefreshAsync(new("6.7.0", "4.3.0", "2.3.0"));

        Assert.Equal(PublisherCheckFailure.Network, failed.Failure);
        Assert.False(failed.IsCurrentKnown);
        Assert.All(failed.Current, game => Assert.Equal(PublisherObservationState.Unknown, game.Observation));
        Assert.NotNull(failed.PreviousSuccessfulAdvisory);
        Assert.True(failed.PreviousSuccessfulAdvisory.IsAdvisory);
        Assert.Equal(success.CheckedAt, failed.PreviousSuccessfulAdvisory.ObservedAt);
        Assert.Equal("4.3.0", Game(failed.PreviousSuccessfulAdvisory, "hsr").LiveVersion);
        Assert.Equal(PublisherPreDownloadState.Offered, Game(failed.PreviousSuccessfulAdvisory, "hsr").PreDownload);
        Assert.Equal(LocalGameStatus.Ready, localLaunch);
    }

    [Fact]
    public async Task Advisory_contains_remote_facts_only_and_is_independent_of_new_local_version()
    {
        var transport = new SequenceTransport(
            (_, _) => Task.FromResult(SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch)),
            (_, _) => throw new HttpRequestException("sanitized fixture failure"));
        await using var service = CreateService(transport);

        await service.RefreshAsync(new("6.6.0", "4.2.0", "2.2.0"));
        var failed = await service.RefreshAsync(new("6.7.0", "4.4.0", "2.3.0"));

        var advisory = Assert.IsType<HoyoPublisherAdvisorySnapshot>(failed.PreviousSuccessfulAdvisory);
        Assert.Equal("4.3.0", Game(advisory, "hsr").LiveVersion);
        Assert.Null(typeof(HoyoPublisherRemoteFacts).GetProperty("Update"));
        Assert.Null(typeof(HoyoPublisherRemoteFacts).GetProperty("Observation"));
        Assert.Null(typeof(HoyoPublisherRemoteFacts).GetProperty("IsCurrentKnown"));
    }

    [Fact]
    public async Task Malformed_current_response_is_unknown_and_does_not_leak_raw_payload()
    {
        const string marker = "fixture-private-payload-38e4";
        await using var service = CreateService(new SequenceTransport(
            (_, _) => Task.FromResult(SanitizedHoyoFixtures.Utf8($"{{\"retcode\":0,\"{marker}\":"))));

        var result = await service.RefreshAsync(new("6.7.0", "4.3.0", "2.3.0"));

        Assert.Equal(PublisherCheckFailure.InvalidResponse, result.Failure);
        Assert.All(result.Current, game => Assert.Equal(PublisherObservationState.Unknown, game.Observation));
        Assert.DoesNotContain(marker, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tls_or_secure_connection_failure_is_network_unknown_without_launch_effect()
    {
        await using var service = CreateService(new SequenceTransport(
            (_, _) => throw new HttpRequestException("sanitized TLS fixture")));
        var localLaunch = LocalGameStatus.Ready;

        var result = await service.RefreshAsync(new("6.7.0", "4.3.0", "2.3.0"));

        Assert.Equal(PublisherCheckFailure.Network, result.Failure);
        Assert.All(result.Current, game => Assert.Equal(PublisherObservationState.Unknown, game.Observation));
        Assert.Equal(LocalGameStatus.Ready, localLaunch);
        Assert.DoesNotContain("TLS fixture", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_refreshes_share_one_request_but_map_their_own_local_versions()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new SequenceTransport(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch);
        });
        await using var service = CreateService(transport);

        var first = service.RefreshAsync(new("6.7.0", "4.3.0", "2.3.0"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.RefreshAsync(new("6.6.0", "4.2.0", "2.2.0"));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, transport.CallCount);
        Assert.Equal(PublisherUpdateState.Current, Game(results[0], "hsr").Update);
        Assert.Equal(PublisherUpdateState.UpdateOffered, Game(results[1], "hsr").Update);
    }

    [Fact]
    public async Task Manual_refresh_debounce_is_five_seconds_with_exact_boundary_allowed()
    {
        var clock = new FakeClock(InitialTime);
        var transport = SuccessTransport();
        await using var service = CreateService(
            transport,
            clock,
            HoyoPublisherStatusService.ProductionManualDebounce);
        var local = new HoyoLocalVersions("6.7.0", "4.3.0", "2.3.0");

        Assert.Equal(PublisherCheckFailure.None, (await service.RefreshAsync(local, PublisherRefreshIntent.Manual)).Failure);
        clock.UtcNow = InitialTime.AddMilliseconds(4999);
        var debounced = await service.RefreshAsync(local, PublisherRefreshIntent.Manual);
        clock.UtcNow = InitialTime.AddSeconds(5);
        var boundary = await service.RefreshAsync(local, PublisherRefreshIntent.Manual);

        Assert.Equal(PublisherCheckFailure.Debounced, debounced.Failure);
        Assert.All(debounced.Current, game => Assert.Equal(PublisherObservationState.Unknown, game.Observation));
        Assert.NotNull(debounced.PreviousSuccessfulAdvisory);
        Assert.Equal(PublisherCheckFailure.None, boundary.Failure);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task Automatic_refresh_is_not_blocked_by_manual_debounce()
    {
        var clock = new FakeClock(InitialTime);
        var transport = SuccessTransport();
        await using var service = CreateService(transport, clock, TimeSpan.FromSeconds(5));
        var local = new HoyoLocalVersions("6.7.0", "4.3.0", "2.3.0");

        await service.RefreshAsync(local, PublisherRefreshIntent.Manual);
        var automatic = await service.RefreshAsync(local, PublisherRefreshIntent.Automatic);

        Assert.Equal(PublisherCheckFailure.None, automatic.Failure);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task Canceling_one_waiter_does_not_cancel_shared_request()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new SequenceTransport(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch);
        });
        await using var service = CreateService(transport);
        var local = new HoyoLocalVersions("6.7.0", "4.3.0", "2.3.0");

        var survivor = service.RefreshAsync(local);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var canceled = new CancellationTokenSource();
        var canceledWaiter = service.RefreshAsync(local, cancellationToken: canceled.Token);
        canceled.Cancel();
        var canceledResult = await canceledWaiter;
        release.TrySetResult();
        var survivorResult = await survivor;

        Assert.Equal(PublisherCheckFailure.Canceled, canceledResult.Failure);
        Assert.All(canceledResult.Current, game => Assert.Equal(PublisherObservationState.Unknown, game.Observation));
        Assert.Equal(PublisherCheckFailure.None, survivorResult.Failure);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Shutdown_cancels_inflight_transport_and_future_checks_are_unknown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new SequenceTransport(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException();
        });
        var service = CreateService(transport);
        var local = new HoyoLocalVersions("6.7.0", "4.3.0", "2.3.0");

        var refresh = service.RefreshAsync(local);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await refresh;
        var afterShutdown = await service.RefreshAsync(local);

        Assert.Equal(PublisherCheckFailure.Shutdown, result.Failure);
        Assert.Equal(PublisherCheckFailure.Shutdown, afterShutdown.Failure);
        Assert.All(afterShutdown.Current, game => Assert.Equal(PublisherObservationState.Unknown, game.Observation));
    }

    [Fact]
    public async Task Sole_pre_canceled_manual_refresh_has_no_request_debounce_or_cache_side_effect()
    {
        var clock = new FakeClock(InitialTime);
        var transport = SuccessTransport();
        await using var service = CreateService(
            transport,
            clock,
            HoyoPublisherStatusService.ProductionManualDebounce);
        var local = new HoyoLocalVersions("6.7.0", "4.3.0", "2.3.0");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var canceledResult = await service.RefreshAsync(
            local,
            PublisherRefreshIntent.Manual,
            canceled.Token);
        var immediateManual = await service.RefreshAsync(local, PublisherRefreshIntent.Manual);

        Assert.Equal(PublisherCheckFailure.Canceled, canceledResult.Failure);
        Assert.All(canceledResult.Current, game => Assert.Equal(PublisherObservationState.Unknown, game.Observation));
        Assert.Null(canceledResult.PreviousSuccessfulAdvisory);
        Assert.Equal(PublisherCheckFailure.None, immediateManual.Failure);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Pre_canceled_refresh_does_not_read_or_expose_existing_advisory_cache()
    {
        var transport = SuccessTransport();
        await using var service = CreateService(transport);
        var local = new HoyoLocalVersions("6.7.0", "4.3.0", "2.3.0");
        await service.RefreshAsync(local);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var result = await service.RefreshAsync(local, cancellationToken: canceled.Token);

        Assert.Equal(PublisherCheckFailure.Canceled, result.Failure);
        Assert.Null(result.PreviousSuccessfulAdvisory);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public void Result_contracts_have_no_public_constructors_and_reject_contradictory_internal_state()
    {
        Assert.Empty(typeof(HoyoPublisherGameStatus).GetConstructors());
        Assert.Empty(typeof(HoyoPublisherRemoteFacts).GetConstructors());
        Assert.Empty(typeof(HoyoPublisherAdvisorySnapshot).GetConstructors());
        Assert.Empty(typeof(HoyoPublisherStatusResult).GetConstructors());

        Assert.Throws<ArgumentException>(() => new HoyoPublisherGameStatus(
            "hsr",
            PublisherObservationState.Unknown,
            PublisherUpdateState.UpdateOffered,
            PublisherPreDownloadState.Unknown,
            null,
            null,
            PublisherOptionalSignal.Unknown,
            PublisherOptionalSignal.Unknown));

        var available = AvailableStatus("hsr");
        var unknownGenshin = UnknownStatus("genshin");
        var unknownZzz = UnknownStatus("zzz");
        Assert.Throws<ArgumentException>(() => new HoyoPublisherStatusResult(
            InitialTime,
            PublisherCheckFailure.Network,
            [unknownGenshin, available, unknownZzz]));
        Assert.Throws<ArgumentException>(() => new HoyoPublisherStatusResult(
            InitialTime,
            PublisherCheckFailure.None,
            [unknownGenshin, unknownGenshin, unknownZzz]));
    }

    [Fact]
    public void Result_contracts_copy_input_collections_and_derive_known_state()
    {
        var source = new[]
        {
            AvailableStatus("genshin"),
            AvailableStatus("hsr"),
            AvailableStatus("zzz"),
        };
        var result = new HoyoPublisherStatusResult(
            InitialTime,
            PublisherCheckFailure.None,
            source);

        source[1] = UnknownStatus("hsr");

        Assert.True(result.IsCurrentKnown);
        Assert.Equal(PublisherObservationState.Available, Assert.Single(result.Current, game => game.GameId == "hsr").Observation);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<HoyoPublisherGameStatus>)result.Current).Add(UnknownStatus("hsr")));
    }

    [Fact]
    public void Public_service_has_no_transport_injection_or_raw_dto_surface()
    {
        var constructor = Assert.Single(typeof(HoyoPublisherStatusService).GetConstructors());
        Assert.Empty(constructor.GetParameters());
        var dtoTypes = new[]
        {
            typeof(HoyoPublisherStatusResult),
            typeof(HoyoPublisherGameStatus),
            typeof(HoyoPublisherRemoteFacts),
            typeof(HoyoPublisherAdvisorySnapshot),
        };

        Assert.Equal(
            ["CheckedAt", "Current", "Failure", "IsCurrentKnown", "PreviousSuccessfulAdvisory"],
            typeof(HoyoPublisherStatusResult).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            [
                "BasePackagePreDownloadCapability",
                "GameId",
                "IncrementalPathAdvertised",
                "LiveVersion",
                "Observation",
                "PreDownload",
                "PreDownloadVersion",
                "Update",
            ],
            typeof(HoyoPublisherGameStatus).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            [
                "BasePackagePreDownloadCapability",
                "GameId",
                "IncrementalPathAdvertised",
                "LiveVersion",
                "PreDownload",
                "PreDownloadVersion",
            ],
            typeof(HoyoPublisherRemoteFacts).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            ["Games", "IsAdvisory", "ObservedAt"],
            typeof(HoyoPublisherAdvisorySnapshot).GetProperties().Select(property => property.Name).Order());
        Assert.DoesNotContain(
            dtoTypes.SelectMany(type => type.GetProperties()),
            property => property.PropertyType == typeof(byte[])
                || property.PropertyType == typeof(ReadOnlyMemory<byte>)
                || property.Name.Contains("Json", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Header", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Category", StringComparison.OrdinalIgnoreCase));
    }

    private static HoyoPublisherStatusService CreateService(
        IHoyoBranchTransport transport,
        FakeClock? clock = null,
        TimeSpan? debounce = null) =>
        new(
            transport,
            new HoyoBranchResponseParser(),
            clock ?? new FakeClock(InitialTime),
            debounce ?? TimeSpan.Zero);

    private static SequenceTransport SuccessTransport() =>
        new((_, _) => Task.FromResult(SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch)));

    private static HoyoPublisherGameStatus Game(HoyoPublisherStatusResult result, string gameId) =>
        Assert.Single(result.Current, game => game.GameId == gameId);

    private static HoyoPublisherRemoteFacts Game(HoyoPublisherAdvisorySnapshot result, string gameId) =>
        Assert.Single(result.Games, game => game.GameId == gameId);

    private static HoyoPublisherGameStatus AvailableStatus(string gameId) =>
        new(
            gameId,
            PublisherObservationState.Available,
            PublisherUpdateState.Current,
            PublisherPreDownloadState.NotOffered,
            "1.0.0",
            null,
            PublisherOptionalSignal.Unknown,
            PublisherOptionalSignal.Unknown);

    private static HoyoPublisherGameStatus UnknownStatus(string gameId) =>
        new(
            gameId,
            PublisherObservationState.Unknown,
            PublisherUpdateState.Unknown,
            PublisherPreDownloadState.Unknown,
            null,
            null,
            PublisherOptionalSignal.Unknown,
            PublisherOptionalSignal.Unknown);

    private sealed class FakeClock(DateTimeOffset initial) : IPublisherClock
    {
        public DateTimeOffset UtcNow { get; set; } = initial;
    }

    private sealed class SequenceTransport(
        params Func<int, CancellationToken, Task<ReadOnlyMemory<byte>>>[] steps) : IHoyoBranchTransport
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<ReadOnlyMemory<byte>> FetchAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref callCount) - 1;
            return steps[Math.Min(call, steps.Length - 1)](call, cancellationToken);
        }
    }
}
