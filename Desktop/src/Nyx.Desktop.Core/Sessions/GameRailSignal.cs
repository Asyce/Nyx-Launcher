using Nyx.Desktop.Core.PublisherMaintenance;

namespace Nyx.Desktop.Core.Sessions;

public enum GameRailSignalKind
{
    Checking,
    Ready,
    Starting,
    Running,
    UpdateAndPreDownload,
    UpdateAvailable,
    PreDownloadAvailable,
    RetryAvailable,
    NeedsReview,
    NotFound,
    Unsupported,
}

public sealed record GameRailSignal(
    GameRailSignalKind Kind,
    string Description);

public static class GameRailSignalProjector
{
    public static GameRailSignal Project(
        string gameId,
        GameSessionSnapshot snapshot,
        HoyoPublisherStatusResult? publisherStatus,
        bool directLaunchSupported)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(gameId, snapshot.GameId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The rail game must match the session snapshot.", nameof(snapshot));
        }

        if (snapshot.Status is LocalGameStatus.Running)
        {
            return new(GameRailSignalKind.Running, "Running");
        }

        if (snapshot.Status is LocalGameStatus.Starting)
        {
            return new(GameRailSignalKind.Starting, "Starting");
        }

        if (snapshot.Status is LocalGameStatus.LaunchFailed)
        {
            return new(GameRailSignalKind.RetryAvailable, "Launch failed; retry available");
        }

        if (!directLaunchSupported)
        {
            return new(
                GameRailSignalKind.Unsupported,
                "Direct launch not enabled; use the official launcher");
        }

        if (snapshot.Readiness is LocalReadinessEvidence.Unknown)
        {
            return new(GameRailSignalKind.Checking, "Checking local install");
        }

        if (snapshot.Status is LocalGameStatus.Ready
            && snapshot.Readiness is LocalReadinessEvidence.Ready)
        {
            var publisherSignal = ProjectPublisher(gameId, publisherStatus);
            if (publisherSignal is not null)
            {
                return publisherSignal;
            }
        }

        return snapshot.Status switch
        {
            LocalGameStatus.Ready => new(GameRailSignalKind.Ready, "Ready to launch"),
            LocalGameStatus.NeedsReview => new(GameRailSignalKind.NeedsReview, "Needs review"),
            LocalGameStatus.NotFound => new(GameRailSignalKind.NotFound, "Not installed"),
            _ => new(GameRailSignalKind.Checking, "Checking local install"),
        };
    }

    private static GameRailSignal? ProjectPublisher(
        string gameId,
        HoyoPublisherStatusResult? result)
    {
        if (result is null || result.Failure is not PublisherCheckFailure.None)
        {
            return null;
        }

        var publisherGameId = gameId == "gi" ? "genshin" : gameId;
        var status = result.Current.FirstOrDefault(game => game.GameId == publisherGameId);
        if (status is null
            || status.Observation is not PublisherObservationState.Available
            || status.Update is PublisherUpdateState.Unknown)
        {
            return null;
        }

        return (status.Update, status.PreDownload) switch
        {
            (PublisherUpdateState.UpdateOffered, PublisherPreDownloadState.Offered) =>
                new(
                    GameRailSignalKind.UpdateAndPreDownload,
                    "Update and pre-download available in HoYoPlay"),
            (PublisherUpdateState.UpdateOffered, _) =>
                new(GameRailSignalKind.UpdateAvailable, "Update available in HoYoPlay"),
            (_, PublisherPreDownloadState.Offered) =>
                new(GameRailSignalKind.PreDownloadAvailable, "Pre-download available in HoYoPlay"),
            _ => null,
        };
    }
}
