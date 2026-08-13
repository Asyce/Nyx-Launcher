using Nyx.Desktop.Core.PublisherMaintenance;
using Nyx.Desktop.Infrastructure.PublisherMaintenance;

namespace Nyx.Desktop.Tests.PublisherMaintenance;

public sealed class HoyoPublisherStatusSourceTests
{
    [Fact]
    public async Task Start_publishes_update_and_predownload_from_current_local_versions()
    {
        var transport = new FakeTransport((_, _) =>
            Task.FromResult(SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch)));
        await using var source = CreateSource(
            transport,
            () => new("6.7.0", "4.2.0", "2.3.0"));
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Updated += (_, _) => updated.TrySetResult();

        source.Start();
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = Assert.IsType<HoyoPublisherStatusResult>(source.Current);
        var hsr = Assert.Single(result.Current, game => game.GameId == "hsr");
        Assert.Equal(PublisherUpdateState.UpdateOffered, hsr.Update);
        Assert.Equal(PublisherPreDownloadState.Offered, hsr.PreDownload);
        Assert.Equal("4.4.0", hsr.PreDownloadVersion);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Simultaneous_refreshes_coalesce_into_one_transport_call()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async (_, _) =>
        {
            entered.TrySetResult();
            return await release.Task;
        });
        await using var source = CreateSource(transport, () => new("6.7.0", "4.3.0", "2.3.0"));

        var first = source.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = source.RefreshAsync();
        release.TrySetResult(SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch));
        await Task.WhenAll(first, second);

        Assert.Equal(1, transport.CallCount);
        Assert.NotNull(source.Current);
    }

    [Fact]
    public async Task Failed_remote_check_is_published_as_unknown_without_throwing()
    {
        var transport = new FakeTransport((_, _) =>
            throw new HttpRequestException("sanitized failure"));
        await using var source = CreateSource(transport, () => new(null, null, null));

        await source.RefreshAsync();

        var result = Assert.IsType<HoyoPublisherStatusResult>(source.Current);
        Assert.Equal(PublisherCheckFailure.Network, result.Failure);
        Assert.All(result.Current, game =>
            Assert.Equal(PublisherObservationState.Unknown, game.Observation));
    }

    [Fact]
    public async Task Dispose_cancels_a_blocked_refresh_and_stops_publication()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadOnlyMemory<byte>.Empty;
        });
        var source = CreateSource(transport, () => new(null, null, null));
        var publications = 0;
        source.Updated += (_, _) => publications++;

        source.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, publications);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.RefreshAsync());
    }

    private static HoyoPublisherStatusSource CreateSource(
        IHoyoBranchTransport transport,
        Func<HoyoLocalVersions> versions)
    {
        var service = new HoyoPublisherStatusService(
            transport,
            new HoyoBranchResponseParser(),
            new FakeClock(),
            TimeSpan.Zero);
        return new(service, versions, TimeSpan.FromMinutes(15));
    }

    private sealed class FakeClock : IPublisherClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeTransport(
        Func<int, CancellationToken, Task<ReadOnlyMemory<byte>>> fetch) : IHoyoBranchTransport
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<ReadOnlyMemory<byte>> FetchAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref callCount);
            return fetch(call, cancellationToken);
        }
    }
}
