using Nyx.Desktop.Core.PublisherMaintenance;
using Nyx.Desktop.Core.Sessions;

namespace Nyx.Desktop.Tests.Sessions;

public sealed class GameRailSignalProjectorTests
{
    [Fact]
    public void Running_beats_publisher_and_unsupported_signals()
    {
        var signal = GameRailSignalProjector.Project(
            "hsr",
            Snapshot("hsr", LocalReadinessEvidence.Ready, LocalGameStatus.Running),
            Publisher("hsr", PublisherUpdateState.UpdateOffered, PublisherPreDownloadState.Offered),
            directLaunchSupported: false);

        Assert.Equal(new(GameRailSignalKind.Running, "Running"), signal);
    }

    [Fact]
    public void Starting_beats_publisher_signal()
    {
        var signal = GameRailSignalProjector.Project(
            "zzz",
            Snapshot("zzz", LocalReadinessEvidence.Ready, LocalGameStatus.Starting),
            Publisher("zzz", PublisherUpdateState.UpdateOffered, PublisherPreDownloadState.Offered),
            directLaunchSupported: true);

        Assert.Equal(GameRailSignalKind.Starting, signal.Kind);
    }

    [Theory]
    [InlineData(PublisherUpdateState.UpdateOffered, PublisherPreDownloadState.Offered, GameRailSignalKind.UpdateAndPreDownload)]
    [InlineData(PublisherUpdateState.UpdateOffered, PublisherPreDownloadState.NotOffered, GameRailSignalKind.UpdateAvailable)]
    [InlineData(PublisherUpdateState.Current, PublisherPreDownloadState.Offered, GameRailSignalKind.PreDownloadAvailable)]
    public void Known_publisher_offers_beat_idle_readiness(
        PublisherUpdateState update,
        PublisherPreDownloadState preDownload,
        GameRailSignalKind expected)
    {
        var signal = GameRailSignalProjector.Project(
            "gi",
            Snapshot("gi", LocalReadinessEvidence.Ready, LocalGameStatus.Ready),
            Publisher("genshin", update, preDownload),
            directLaunchSupported: true);

        Assert.Equal(expected, signal.Kind);
        Assert.Contains("HoYoPlay", signal.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_local_update_state_never_claims_remote_predownload_on_the_rail()
    {
        var signal = GameRailSignalProjector.Project(
            "hsr",
            Snapshot("hsr", LocalReadinessEvidence.Ready, LocalGameStatus.Ready),
            Publisher("hsr", PublisherUpdateState.Unknown, PublisherPreDownloadState.Offered),
            directLaunchSupported: true);

        Assert.Equal(new(GameRailSignalKind.Ready, "Ready to launch"), signal);
    }

    [Fact]
    public void Failed_launch_is_retryable_when_no_publisher_offer_exists()
    {
        var signal = GameRailSignalProjector.Project(
            "zzz",
            Snapshot("zzz", LocalReadinessEvidence.Ready, LocalGameStatus.LaunchFailed),
            Publisher("zzz", PublisherUpdateState.Current, PublisherPreDownloadState.NotOffered),
            directLaunchSupported: true);

        Assert.Equal(GameRailSignalKind.RetryAvailable, signal.Kind);
    }

    [Theory]
    [InlineData(LocalReadinessEvidence.Ready, LocalGameStatus.LaunchFailed, true, GameRailSignalKind.RetryAvailable)]
    [InlineData(LocalReadinessEvidence.NeedsReview, LocalGameStatus.NeedsReview, true, GameRailSignalKind.NeedsReview)]
    [InlineData(LocalReadinessEvidence.NotFound, LocalGameStatus.NotFound, true, GameRailSignalKind.NotFound)]
    [InlineData(LocalReadinessEvidence.Ready, LocalGameStatus.Ready, false, GameRailSignalKind.Unsupported)]
    public void Cached_publisher_offer_never_hides_current_local_or_capability_state(
        LocalReadinessEvidence readiness,
        LocalGameStatus status,
        bool directLaunchSupported,
        GameRailSignalKind expected)
    {
        var signal = GameRailSignalProjector.Project(
            "hsr",
            Snapshot("hsr", readiness, status),
            Publisher("hsr", PublisherUpdateState.UpdateOffered, PublisherPreDownloadState.Offered),
            directLaunchSupported);

        Assert.Equal(expected, signal.Kind);
    }

    [Theory]
    [InlineData(LocalReadinessEvidence.Unknown, LocalGameStatus.NeedsReview, GameRailSignalKind.Unsupported)]
    [InlineData(LocalReadinessEvidence.NotFound, LocalGameStatus.NotFound, GameRailSignalKind.Unsupported)]
    public void Unsupported_game_stays_honest_when_idle(
        LocalReadinessEvidence readiness,
        LocalGameStatus status,
        GameRailSignalKind expected)
    {
        var signal = GameRailSignalProjector.Project(
            "wuwa",
            Snapshot("wuwa", readiness, status),
            publisherStatus: null,
            directLaunchSupported: false);

        Assert.Equal(expected, signal.Kind);
        Assert.Contains("official launcher", signal.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LocalReadinessEvidence.Unknown, LocalGameStatus.NeedsReview, GameRailSignalKind.Checking)]
    [InlineData(LocalReadinessEvidence.Ready, LocalGameStatus.Ready, GameRailSignalKind.Ready)]
    [InlineData(LocalReadinessEvidence.NeedsReview, LocalGameStatus.NeedsReview, GameRailSignalKind.NeedsReview)]
    [InlineData(LocalReadinessEvidence.NotFound, LocalGameStatus.NotFound, GameRailSignalKind.NotFound)]
    public void Supported_idle_states_have_exact_plain_signals(
        LocalReadinessEvidence readiness,
        LocalGameStatus status,
        GameRailSignalKind expected)
    {
        var signal = GameRailSignalProjector.Project(
            "gi",
            Snapshot("gi", readiness, status),
            publisherStatus: null,
            directLaunchSupported: true);

        Assert.Equal(expected, signal.Kind);
    }

    [Fact]
    public void Snapshot_for_another_game_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => GameRailSignalProjector.Project(
            "hsr",
            Snapshot("zzz", LocalReadinessEvidence.Ready, LocalGameStatus.Ready),
            publisherStatus: null,
            directLaunchSupported: true));
    }

    private static GameSessionSnapshot Snapshot(
        string gameId,
        LocalReadinessEvidence readiness,
        LocalGameStatus status) => new(
            gameId,
            readiness,
            status,
            ExactProcessPresence.Absent,
            WasBootstrapObserved: false,
            WasRuntimeObserved: false,
            ConsecutiveAbsentSamples: 0,
            ObservationGeneration: 1,
            FirstAbsentGeneration: null,
            FirstAbsentAt: null,
            LaunchRequestedAt: null,
            BootstrapObservedAt: null,
            RequestedResumeGeneration: 0,
            AppliedResumeGeneration: 0,
            GameSessionFailureReason.None,
            CoordinatorStopped: false);

    private static HoyoPublisherStatusResult Publisher(
        string replacementGameId,
        PublisherUpdateState update,
        PublisherPreDownloadState preDownload)
    {
        var statuses = new[]
        {
            Current("genshin", "6.7.0"),
            Current("hsr", "4.3.0"),
            Current("zzz", "2.3.0"),
        };
        var index = replacementGameId switch
        {
            "genshin" => 0,
            "hsr" => 1,
            "zzz" => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(replacementGameId)),
        };
        statuses[index] = new(
            replacementGameId,
            PublisherObservationState.Available,
            update,
            preDownload,
            statuses[index].LiveVersion,
            preDownload is PublisherPreDownloadState.Offered ? "9.9.9" : null,
            PublisherOptionalSignal.NotAdvertised,
            PublisherOptionalSignal.NotAdvertised);

        return new(DateTimeOffset.UtcNow, PublisherCheckFailure.None, statuses);
    }

    private static HoyoPublisherGameStatus Current(string gameId, string version) => new(
        gameId,
        PublisherObservationState.Available,
        PublisherUpdateState.Current,
        PublisherPreDownloadState.NotOffered,
        version,
        null,
        PublisherOptionalSignal.NotAdvertised,
        PublisherOptionalSignal.NotAdvertised);
}
