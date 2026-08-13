using System.Collections.ObjectModel;
using System.Text.Json;
using Nyx.Desktop.Core.PublisherMaintenance;

namespace Nyx.Desktop.Infrastructure.PublisherMaintenance;

internal interface IPublisherClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemPublisherClock : IPublisherClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class HoyoPublisherStatusService : IAsyncDisposable
{
    internal static readonly TimeSpan ProductionManualDebounce = TimeSpan.FromSeconds(5);

    private static readonly string[] CanonicalGameOrder = ["genshin", "hsr", "zzz"];

    private readonly object sync = new();
    private readonly IHoyoBranchTransport transport;
    private readonly HoyoBranchResponseParser parser;
    private readonly IPublisherClock clock;
    private readonly TimeSpan manualDebounce;
    private readonly CancellationTokenSource shutdown = new();
    private Task<FetchOutcome>? inFlight;
    private SuccessfulObservation? previousSuccess;
    private DateTimeOffset? lastManualRequestAt;
    private bool disposed;

    public HoyoPublisherStatusService()
        : this(
            new HoyoBranchHttpTransport(),
            new HoyoBranchResponseParser(),
            new SystemPublisherClock(),
            ProductionManualDebounce)
    {
    }

    internal HoyoPublisherStatusService(
        IHoyoBranchTransport transport,
        HoyoBranchResponseParser parser,
        IPublisherClock clock,
        TimeSpan manualDebounce)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (manualDebounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(manualDebounce));
        }

        this.manualDebounce = manualDebounce;
    }

    public async Task<HoyoPublisherStatusResult> RefreshAsync(
        HoyoLocalVersions localVersions,
        PublisherRefreshIntent intent = PublisherRefreshIntent.Automatic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localVersions);
        if (cancellationToken.IsCancellationRequested)
        {
            return BuildFailure(
                PublisherCheckFailure.Canceled,
                clock.UtcNow,
                includePreviousAdvisory: false);
        }

        Task<FetchOutcome>? request;
        var checkedAt = clock.UtcNow;
        lock (sync)
        {
            if (disposed)
            {
                return BuildFailure(PublisherCheckFailure.Shutdown, checkedAt);
            }

            if (inFlight is { IsCompleted: false })
            {
                request = inFlight;
            }
            else if (intent is PublisherRefreshIntent.Manual
                     && lastManualRequestAt is { } lastManual
                     && checkedAt - lastManual < manualDebounce)
            {
                return BuildFailure(PublisherCheckFailure.Debounced, checkedAt);
            }
            else
            {
                if (intent is PublisherRefreshIntent.Manual)
                {
                    lastManualRequestAt = checkedAt;
                }

                request = FetchAndProjectAsync();
                inFlight = request;
            }
        }

        FetchOutcome outcome;
        try
        {
            outcome = await request.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BuildFailure(PublisherCheckFailure.Canceled, clock.UtcNow);
        }

        return outcome.Failure is PublisherCheckFailure.None
            ? BuildSuccess(outcome, localVersions)
            : BuildFailure(outcome.Failure, outcome.ObservedAt);
    }

    public async ValueTask DisposeAsync()
    {
        Task<FetchOutcome>? request;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            request = inFlight;
        }

        await shutdown.CancelAsync().ConfigureAwait(false);
        if (request is not null)
        {
            try
            {
                await request.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Shutdown is fail-closed. Transport details are intentionally not exposed or logged.
            }
        }

        shutdown.Dispose();
        if (transport is IDisposable disposableTransport)
        {
            disposableTransport.Dispose();
        }
    }

    private async Task<FetchOutcome> FetchAndProjectAsync()
    {
        try
        {
            var body = await transport.FetchAsync(shutdown.Token).ConfigureAwait(false);
            var observedAt = clock.UtcNow;
            if (!parser.TryParse(body, out var batch))
            {
                return new(PublisherCheckFailure.InvalidResponse, observedAt, null);
            }

            if (shutdown.IsCancellationRequested)
            {
                return new(PublisherCheckFailure.Shutdown, observedAt, null);
            }

            var successful = new SuccessfulObservation(observedAt, batch!);
            lock (sync)
            {
                previousSuccess = successful;
            }

            return new(PublisherCheckFailure.None, observedAt, batch);
        }
        catch (HoyoTransportException exception)
        {
            return new(exception.Failure, clock.UtcNow, null);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return new(PublisherCheckFailure.Shutdown, clock.UtcNow, null);
        }
        catch (HttpRequestException)
        {
            return new(PublisherCheckFailure.Network, clock.UtcNow, null);
        }
        catch (IOException)
        {
            return new(PublisherCheckFailure.Network, clock.UtcNow, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(PublisherCheckFailure.InvalidResponse, clock.UtcNow, null);
        }
    }

    private HoyoPublisherStatusResult BuildSuccess(FetchOutcome outcome, HoyoLocalVersions localVersions)
    {
        var games = MapGames(outcome.Batch!, localVersions);
        return new(
            outcome.ObservedAt,
            PublisherCheckFailure.None,
            games,
            previousSuccessfulAdvisory: null);
    }

    private HoyoPublisherStatusResult BuildFailure(
        PublisherCheckFailure failure,
        DateTimeOffset checkedAt,
        bool includePreviousAdvisory = true)
    {
        var unknown = new ReadOnlyCollection<HoyoPublisherGameStatus>(
            CanonicalGameOrder.Select(UnknownGame).ToArray());
        return new(
            checkedAt,
            failure,
            unknown,
            includePreviousAdvisory ? BuildPreviousAdvisory() : null);
    }

    private HoyoPublisherAdvisorySnapshot? BuildPreviousAdvisory()
    {
        SuccessfulObservation? successful;
        lock (sync)
        {
            successful = previousSuccess;
        }

        if (successful is null)
        {
            return null;
        }

        return new(successful.ObservedAt, MapRemoteFacts(successful.Batch));
    }

    private static IReadOnlyList<HoyoPublisherRemoteFacts> MapRemoteFacts(HoyoRemoteBranchBatch batch)
    {
        var facts = CanonicalGameOrder
            .Select(gameId =>
            {
                var remote = batch.Games[gameId];
                return new HoyoPublisherRemoteFacts(
                    remote.GameId,
                    remote.LiveVersion.ToString(),
                    remote.PreDownload,
                    remote.PreDownloadVersion?.ToString(),
                    remote.IncrementalPathAdvertised,
                    remote.BasePackagePreDownloadCapability);
            })
            .ToArray();
        return new ReadOnlyCollection<HoyoPublisherRemoteFacts>(facts);
    }

    private static IReadOnlyList<HoyoPublisherGameStatus> MapGames(
        HoyoRemoteBranchBatch batch,
        HoyoLocalVersions localVersions)
    {
        var local = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["genshin"] = localVersions.Genshin,
            ["hsr"] = localVersions.Hsr,
            ["zzz"] = localVersions.Zzz,
        };
        var projected = CanonicalGameOrder
            .Select(gameId => MapGame(batch.Games[gameId], local[gameId]))
            .ToArray();
        return new ReadOnlyCollection<HoyoPublisherGameStatus>(projected);
    }

    private static HoyoPublisherGameStatus MapGame(HoyoRemoteGameBranch remote, string? localVersion)
    {
        var update = PublisherUpdateState.Unknown;
        if (StrictVersion.TryParse(localVersion, out var local))
        {
            if (local > remote.LiveVersion)
            {
                return UnknownGame(remote.GameId);
            }

            update = local == remote.LiveVersion
                ? PublisherUpdateState.Current
                : PublisherUpdateState.UpdateOffered;
        }

        return new(
            remote.GameId,
            PublisherObservationState.Available,
            update,
            remote.PreDownload,
            remote.LiveVersion.ToString(),
            remote.PreDownloadVersion?.ToString(),
            remote.IncrementalPathAdvertised,
            remote.BasePackagePreDownloadCapability);
    }

    private static HoyoPublisherGameStatus UnknownGame(string gameId) =>
        new(
            gameId,
            PublisherObservationState.Unknown,
            PublisherUpdateState.Unknown,
            PublisherPreDownloadState.Unknown,
            null,
            null,
            PublisherOptionalSignal.Unknown,
            PublisherOptionalSignal.Unknown);

    private sealed record SuccessfulObservation(DateTimeOffset ObservedAt, HoyoRemoteBranchBatch Batch);

    private sealed record FetchOutcome(
        PublisherCheckFailure Failure,
        DateTimeOffset ObservedAt,
        HoyoRemoteBranchBatch? Batch);
}
