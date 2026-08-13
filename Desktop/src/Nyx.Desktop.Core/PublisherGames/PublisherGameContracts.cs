namespace Nyx.Desktop.Core.PublisherGames;

public enum PublisherGameInspectionStatus
{
    NotFound,
    NeedsReview,
    Ready,
}

public enum PublisherGameInspectionReason
{
    None,
    PathNotProvided,
    DirectoryNotFound,
    PathIsNotLocalAndCanonical,
    DriveIsNotLocalFixed,
    FileSystemIsNotNtfs,
    ReparsePointFound,
    RootLauncherMissing,
    VersionedLauncherMissing,
    LauncherVersionInvalid,
    LauncherMismatch,
    GameDirectoryMissing,
    GamesExecutableMissing,
    BootstrapMissing,
    RuntimeMissing,
    ConfigMissing,
    ConfigTooLarge,
    ConfigMalformed,
    ConfigIdentityMismatch,
    ResourceEvidenceMissing,
    ResourceEvidenceTooLarge,
    ResourceEvidenceMalformed,
    SignatureInvalid,
    PublisherMismatch,
    ProductIdentityMismatch,
    ExecutableIdentityMismatch,
    VersionConflict,
    VersionUnavailable,
    AmbiguousCandidates,
    TargetChangedDuringInspection,
    InspectionFailed,
}

public enum PublisherGameVersionState
{
    Available,
    Conflict,
    Unavailable,
}

public sealed record PublisherGameInspectionResult
{
    internal PublisherGameInspectionResult(
        string gameId,
        PublisherGameInspectionStatus status,
        PublisherGameInspectionReason reason,
        PublisherGameVersionState versionState,
        string? canonicalRoot = null,
        string? version = null,
        ValidatedOfficialMaintenanceTarget? maintenanceTarget = null)
    {
        if (gameId is not ("wuwa" or "ae"))
        {
            throw new ArgumentOutOfRangeException(nameof(gameId));
        }

        if (versionState is PublisherGameVersionState.Available)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }
        else if (version is not null)
        {
            throw new ArgumentException("Conflicting or unavailable versions cannot be claimed.", nameof(version));
        }

        if (maintenanceTarget is not null
            && (status, reason) is not (
                PublisherGameInspectionStatus.Ready,
                PublisherGameInspectionReason.None)
                and not (
                    PublisherGameInspectionStatus.NeedsReview,
                    PublisherGameInspectionReason.VersionConflict)
                and not (
                    PublisherGameInspectionStatus.NeedsReview,
                    PublisherGameInspectionReason.VersionUnavailable))
        {
            throw new ArgumentException(
                "Only a fully validated installation can expose a maintenance target.",
                nameof(maintenanceTarget));
        }

        if (maintenanceTarget is not null
            && (!string.Equals(maintenanceTarget.GameId, gameId, StringComparison.Ordinal)
                || !string.Equals(
                    maintenanceTarget.CanonicalRoot,
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "The maintenance proof must match the inspected installation.",
                nameof(maintenanceTarget));
        }

        if (status is PublisherGameInspectionStatus.Ready && maintenanceTarget is null)
        {
            throw new ArgumentException(
                "A ready installation requires a full-install maintenance proof.",
                nameof(maintenanceTarget));
        }

        GameId = gameId;
        Status = status;
        Reason = reason;
        VersionState = versionState;
        CanonicalRoot = canonicalRoot;
        Version = version;
        MaintenanceTarget = maintenanceTarget;
    }

    public string GameId { get; }

    public PublisherGameInspectionStatus Status { get; }

    public PublisherGameInspectionReason Reason { get; }

    public PublisherGameVersionState VersionState { get; }

    public string? CanonicalRoot { get; }

    public string? Version { get; }

    public ValidatedOfficialMaintenanceTarget? MaintenanceTarget { get; }

    public bool HasFullInstallMaintenanceProof => MaintenanceTarget is not null;

    public bool AllowsDirectGameLaunch => false;
}

/// <summary>
/// A short-lived read-only proof that the official launcher and every required game
/// executable/evidence item formed one fully validated installation during inspection.
/// This is not durable execution authorization. Future execution must repeat the same
/// full-install validation while holding equivalent protected file bindings and match
/// the same target immediately before process admission.
/// </summary>
public sealed class ValidatedOfficialMaintenanceTarget
{
    internal ValidatedOfficialMaintenanceTarget(
        string gameId,
        string canonicalRoot,
        string launcherPath,
        string launcherVersion)
    {
        if (gameId is not ("wuwa" or "ae"))
        {
            throw new ArgumentOutOfRangeException(nameof(gameId));
        }

        GameId = gameId;
        CanonicalRoot = canonicalRoot;
        LauncherPath = launcherPath;
        LauncherVersion = launcherVersion;
    }

    public string GameId { get; }

    public string CanonicalRoot { get; }

    public string LauncherPath { get; }

    public string LauncherVersion { get; }
}

/// <summary>
/// A disposable, protected view of one complete WuWa or Endfield installation.
/// Implementations keep every executable binding alive until the caller has
/// finished the exact process check or dispatch decision.
/// </summary>
internal interface IProtectedPublisherGameInspection : IDisposable
{
    PublisherGameInspectionResult Result { get; }

    bool RemainsCompleteAndStable();
}

internal interface IPublisherGameDirectLaunchIdentityValidator
{
    IProtectedPublisherGameInspection InspectProtected(string gameId, string? root);
}
