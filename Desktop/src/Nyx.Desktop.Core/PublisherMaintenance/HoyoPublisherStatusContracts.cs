using System.Collections.ObjectModel;

namespace Nyx.Desktop.Core.PublisherMaintenance;

public enum PublisherObservationState
{
    Unknown,
    Available,
}

public enum PublisherUpdateState
{
    Unknown,
    Current,
    UpdateOffered,
}

public enum PublisherPreDownloadState
{
    Unknown,
    NotOffered,
    Offered,
}

public enum PublisherOptionalSignal
{
    Unknown,
    NotAdvertised,
    Advertised,
}

public enum PublisherCheckFailure
{
    None,
    Debounced,
    Canceled,
    Shutdown,
    Timeout,
    Network,
    HttpStatus,
    ContentType,
    ResponseTooLarge,
    InvalidResponse,
}

public enum PublisherRefreshIntent
{
    Automatic,
    Manual,
}

public sealed record HoyoLocalVersions(
    string? Genshin,
    string? Hsr,
    string? Zzz);

public sealed record HoyoPublisherGameStatus
{
    internal HoyoPublisherGameStatus(
        string gameId,
        PublisherObservationState observation,
        PublisherUpdateState update,
        PublisherPreDownloadState preDownload,
        string? liveVersion,
        string? preDownloadVersion,
        PublisherOptionalSignal incrementalPathAdvertised,
        PublisherOptionalSignal basePackagePreDownloadCapability)
    {
        PublisherContractGuard.ValidateGameId(gameId);
        PublisherContractGuard.ValidateEnum(observation, nameof(observation));
        PublisherContractGuard.ValidateEnum(update, nameof(update));
        PublisherContractGuard.ValidateEnum(preDownload, nameof(preDownload));
        PublisherContractGuard.ValidateEnum(incrementalPathAdvertised, nameof(incrementalPathAdvertised));
        PublisherContractGuard.ValidateEnum(
            basePackagePreDownloadCapability,
            nameof(basePackagePreDownloadCapability));
        if (observation is PublisherObservationState.Unknown)
        {
            if (update is not PublisherUpdateState.Unknown
                || preDownload is not PublisherPreDownloadState.Unknown
                || liveVersion is not null
                || preDownloadVersion is not null
                || incrementalPathAdvertised is not PublisherOptionalSignal.Unknown
                || basePackagePreDownloadCapability is not PublisherOptionalSignal.Unknown)
            {
                throw new ArgumentException("Unknown publisher status cannot carry authoritative facts.");
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(liveVersion);
            ValidatePreDownload(preDownload, preDownloadVersion);
        }

        GameId = gameId;
        Observation = observation;
        Update = update;
        PreDownload = preDownload;
        LiveVersion = liveVersion;
        PreDownloadVersion = preDownloadVersion;
        IncrementalPathAdvertised = incrementalPathAdvertised;
        BasePackagePreDownloadCapability = basePackagePreDownloadCapability;
    }

    public string GameId { get; }

    public PublisherObservationState Observation { get; }

    public PublisherUpdateState Update { get; }

    public PublisherPreDownloadState PreDownload { get; }

    public string? LiveVersion { get; }

    public string? PreDownloadVersion { get; }

    public PublisherOptionalSignal IncrementalPathAdvertised { get; }

    public PublisherOptionalSignal BasePackagePreDownloadCapability { get; }

    private static void ValidatePreDownload(
        PublisherPreDownloadState preDownload,
        string? preDownloadVersion)
    {
        if (preDownload is PublisherPreDownloadState.Offered)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(preDownloadVersion);
        }
        else if (preDownloadVersion is not null)
        {
            throw new ArgumentException("A non-offer cannot carry a pre-download version.");
        }
    }
}

public sealed record HoyoPublisherRemoteFacts
{
    internal HoyoPublisherRemoteFacts(
        string gameId,
        string liveVersion,
        PublisherPreDownloadState preDownload,
        string? preDownloadVersion,
        PublisherOptionalSignal incrementalPathAdvertised,
        PublisherOptionalSignal basePackagePreDownloadCapability)
    {
        PublisherContractGuard.ValidateGameId(gameId);
        PublisherContractGuard.ValidateEnum(preDownload, nameof(preDownload));
        PublisherContractGuard.ValidateEnum(incrementalPathAdvertised, nameof(incrementalPathAdvertised));
        PublisherContractGuard.ValidateEnum(
            basePackagePreDownloadCapability,
            nameof(basePackagePreDownloadCapability));
        ArgumentException.ThrowIfNullOrWhiteSpace(liveVersion);
        if (preDownload is PublisherPreDownloadState.Unknown)
        {
            if (preDownloadVersion is not null)
            {
                throw new ArgumentException("Unknown pre-download facts cannot carry a version.");
            }
        }
        else if (preDownload is PublisherPreDownloadState.Offered)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(preDownloadVersion);
        }
        else if (preDownloadVersion is not null)
        {
            throw new ArgumentException("A non-offer cannot carry a pre-download version.");
        }

        GameId = gameId;
        LiveVersion = liveVersion;
        PreDownload = preDownload;
        PreDownloadVersion = preDownloadVersion;
        IncrementalPathAdvertised = incrementalPathAdvertised;
        BasePackagePreDownloadCapability = basePackagePreDownloadCapability;
    }

    public string GameId { get; }

    public string LiveVersion { get; }

    public PublisherPreDownloadState PreDownload { get; }

    public string? PreDownloadVersion { get; }

    public PublisherOptionalSignal IncrementalPathAdvertised { get; }

    public PublisherOptionalSignal BasePackagePreDownloadCapability { get; }
}

public sealed record HoyoPublisherAdvisorySnapshot
{
    internal HoyoPublisherAdvisorySnapshot(
        DateTimeOffset observedAt,
        IReadOnlyList<HoyoPublisherRemoteFacts> games)
    {
        ObservedAt = observedAt;
        Games = PublisherContractGuard.CopyExactGameSet(games);
    }

    public DateTimeOffset ObservedAt { get; }

    public IReadOnlyList<HoyoPublisherRemoteFacts> Games { get; }

    public bool IsAdvisory => true;
}

public sealed record HoyoPublisherStatusResult
{
    internal HoyoPublisherStatusResult(
        DateTimeOffset checkedAt,
        PublisherCheckFailure failure,
        IReadOnlyList<HoyoPublisherGameStatus> current,
        HoyoPublisherAdvisorySnapshot? previousSuccessfulAdvisory = null)
    {
        PublisherContractGuard.ValidateEnum(failure, nameof(failure));

        CheckedAt = checkedAt;
        Failure = failure;
        Current = PublisherContractGuard.CopyExactGameSet(current);
        PreviousSuccessfulAdvisory = previousSuccessfulAdvisory;
        if (failure is not PublisherCheckFailure.None
            && Current.Any(game => game.Observation is not PublisherObservationState.Unknown))
        {
            throw new ArgumentException("A failed current check cannot carry authoritative current facts.");
        }
    }

    public DateTimeOffset CheckedAt { get; }

    public PublisherCheckFailure Failure { get; }

    public IReadOnlyList<HoyoPublisherGameStatus> Current { get; }

    public HoyoPublisherAdvisorySnapshot? PreviousSuccessfulAdvisory { get; }

    public bool IsCurrentKnown =>
        Failure is PublisherCheckFailure.None
        && Current.All(game => game.Observation is PublisherObservationState.Available);
}

internal static class PublisherContractGuard
{
    private static readonly HashSet<string> CanonicalGameIds = new(StringComparer.Ordinal)
    {
        "genshin",
        "hsr",
        "zzz",
    };

    public static void ValidateGameId(string gameId)
    {
        if (!CanonicalGameIds.Contains(gameId))
        {
            throw new ArgumentOutOfRangeException(nameof(gameId));
        }
    }

    public static void ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    public static IReadOnlyList<T> CopyExactGameSet<T>(IReadOnlyList<T> games)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(games);
        var copy = games.ToArray();
        var ids = copy.Select(GetGameId).ToArray();
        if (copy.Length != CanonicalGameIds.Count
            || ids.Distinct(StringComparer.Ordinal).Count() != CanonicalGameIds.Count
            || ids.Any(id => !CanonicalGameIds.Contains(id)))
        {
            throw new ArgumentException("Publisher results require one exact entry per canonical game.", nameof(games));
        }

        return new ReadOnlyCollection<T>(copy);
    }

    private static string GetGameId<T>(T value) => value switch
    {
        HoyoPublisherGameStatus status => status.GameId,
        HoyoPublisherRemoteFacts facts => facts.GameId,
        _ => throw new ArgumentException("Unsupported publisher result type."),
    };
}
