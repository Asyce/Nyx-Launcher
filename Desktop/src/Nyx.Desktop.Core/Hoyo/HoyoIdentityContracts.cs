namespace Nyx.Desktop.Core.Hoyo;

public enum HoyoInspectionStatus
{
    NotFound,
    NeedsReview,
    Ready,
}

public enum HoyoInspectionReason
{
    None,
    CurrentRecordMissing,
    CurrentRecordMalformed,
    CurrentRecordGameBizMismatch,
    CurrentRecordStale,
    AmbiguousCandidates,
    PathNotProvided,
    DirectoryNotFound,
    PathIsNotLocalAndCanonical,
    DriveIsNotLocalFixed,
    ReparsePointFound,
    LaunchTargetMissing,
    DataDirectoryMissing,
    PackageManifestMissing,
    PackageManifestConflict,
    ConfigMissing,
    ConfigTooLarge,
    ConfigMalformed,
    ConfigIdentityMismatch,
    GameVersionInvalid,
    VersionInfoMissing,
    VersionInfoTooLarge,
    VersionInfoMalformed,
    VersionInfoMismatch,
    SignatureInvalid,
    PublisherMismatch,
    ProductIdentityMismatch,
    ExecutableVersionInvalid,
    VersionFolderLauncherMissing,
    VersionFolderMismatch,
    TargetChangedDuringInspection,
    InspectionFailed,
}

public sealed record HoyoGameInspectionResult(
    string GameId,
    HoyoInspectionStatus Status,
    HoyoInspectionReason Reason,
    string? CanonicalRoot = null,
    string? Version = null);

public sealed record HoyoPlayValidationResult(
    HoyoInspectionStatus Status,
    HoyoInspectionReason Reason,
    ValidatedHoyoPlayInstallation? Installation = null);

/// <summary>
/// An immutable proof token produced only by the reviewed HoYoPlay validator.
/// It is not durable authorization or a process-start instruction. Any future executor must
/// rerun the production validator immediately before admission and match the same target.
/// </summary>
public sealed class ValidatedHoyoPlayInstallation
{
    internal ValidatedHoyoPlayInstallation(
        string canonicalRoot,
        string launcherPath,
        string version)
    {
        CanonicalRoot = canonicalRoot;
        LauncherPath = launcherPath;
        Version = version;
    }

    public string CanonicalRoot { get; }

    public string LauncherPath { get; }

    public string Version { get; }
}
