namespace Nyx.Desktop.Core.Genshin;

public enum GenshinInspectionStatus
{
    NotFound,
    NeedsReview,
    Ready,
}

public enum GenshinPathOrigin
{
    NewCandidate,
    PreviouslySaved,
}

public enum GenshinInspectionReason
{
    None,
    PathNotProvided,
    DirectoryNotFound,
    SavedDirectoryMissing,
    PathIsNotLocalAndCanonical,
    DriveIsNotLocalFixed,
    ReparsePointFound,
    LaunchTargetMissing,
    DataDirectoryMissing,
    PackageManifestMissing,
    ConfigMissing,
    ConfigTooLarge,
    ConfigMalformed,
    ConfigIdentityMismatch,
    GameVersionInvalid,
    SignatureInvalid,
    PublisherMismatch,
    ProductIdentityMismatch,
    ExecutableVersionInvalid,
    VersionFolderLauncherMissing,
    VersionFolderMismatch,
    InspectionFailed,
}

public sealed record GenshinInspectionResult(
    GenshinInspectionStatus Status,
    GenshinInspectionReason Reason,
    string? CanonicalRoot = null,
    string? Version = null);
