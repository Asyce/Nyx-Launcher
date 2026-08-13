using System.Globalization;
using Nyx.Desktop.Core.PublisherGames;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

internal sealed class OfficialLauncherValidator(
    IPublisherExecutableMetadataReader metadataReader,
    PublisherGamePathGuard pathGuard,
    IPublisherFileIdentityReader? identityReader = null,
    IPublisherExecutableEntryOpener? entryOpener = null)
{
    private readonly IPublisherExecutableMetadataReader metadataReader =
        metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
    private readonly PublisherGamePathGuard pathGuard =
        pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
    private readonly IPublisherFileIdentityReader identityReader =
        identityReader ?? new WindowsPublisherFileIdentityReader();
    private readonly IPublisherExecutableEntryOpener entryOpener =
        entryOpener ?? new WindowsPublisherExecutableEntryOpener();

    public LauncherValidation Validate(string? candidateRoot, LauncherProfile profile)
    {
        var rootCheck = pathGuard.CheckRoot(candidateRoot);
        if (rootCheck.Status is not PublisherGameInspectionStatus.Ready)
        {
            return new(rootCheck.Status, rootCheck.Reason, rootCheck.CanonicalRoot);
        }

        var root = rootCheck.CanonicalRoot!;
        var observations = new List<ProtectedPublisherExecutableObservation>();
        try
        {
            var rootLauncher = PublisherGamePathGuard.GetChildPath(root, profile.RootLauncherName);
            if (pathGuard.PathOrParentsHaveReparseComponent(rootLauncher))
            {
                return Review(PublisherGameInspectionReason.ReparsePointFound, root);
            }

            if (!File.Exists(rootLauncher))
            {
                return Review(PublisherGameInspectionReason.RootLauncherMissing, root);
            }

            var rootObservation = ProtectedPublisherExecutableObservation.Open(
                rootLauncher,
                root,
                metadataReader,
                identityReader,
                entryOpener);
            observations.Add(rootObservation);
            var rootMetadata = rootObservation.Metadata;
            var rootReason = ValidateMetadata(rootMetadata, profile.RootLauncherIdentity);
            if (rootReason is not PublisherGameInspectionReason.None)
            {
                return ReviewAndDispose(rootReason, root, observations);
            }

            if (!TryParseFourPartVersion(rootMetadata.ProductVersion, out var launcherVersion))
            {
                return ReviewAndDispose(
                    PublisherGameInspectionReason.LauncherVersionInvalid,
                    root,
                    observations);
            }

            var versionFolder = profile.VersionFolderComponentCount switch
            {
                4 => launcherVersion!,
                3 => string.Join('.', launcherVersion!.Split('.')[..3]),
                _ => throw new InvalidOperationException("Only sealed three- or four-part version folders are supported."),
            };
            var versionDirectory = PublisherGamePathGuard.GetChildPath(root, versionFolder);
            var versionedLauncher = PublisherGamePathGuard.GetChildPath(
                versionDirectory,
                profile.RootLauncherName);
            if (pathGuard.HasReparseComponent(versionDirectory)
                || pathGuard.PathOrParentsHaveReparseComponent(versionedLauncher))
            {
                return ReviewAndDispose(
                    PublisherGameInspectionReason.ReparsePointFound,
                    root,
                    observations);
            }

            if (!File.Exists(versionedLauncher))
            {
                return ReviewAndDispose(
                    PublisherGameInspectionReason.VersionedLauncherMissing,
                    root,
                    observations);
            }

            var versionObservation = ProtectedPublisherExecutableObservation.Open(
                versionedLauncher,
                root,
                metadataReader,
                identityReader,
                entryOpener);
            observations.Add(versionObservation);
            var versionMetadata = versionObservation.Metadata;
            var versionReason = ValidateMetadata(versionMetadata, profile.RootLauncherIdentity);
            if (versionReason is not PublisherGameInspectionReason.None)
            {
                return ReviewAndDispose(versionReason, root, observations);
            }

            if (!string.Equals(versionMetadata.ProductVersion, launcherVersion, StringComparison.Ordinal))
            {
                return ReviewAndDispose(
                    PublisherGameInspectionReason.LauncherMismatch,
                    root,
                    observations);
            }

            if (profile.RequireByteIdenticalPair)
            {
                if (!PublisherFileIdentity.FixedTimeEquals(
                        rootObservation.Digest,
                        versionObservation.Digest))
                {
                    return ReviewAndDispose(
                        PublisherGameInspectionReason.LauncherMismatch,
                        root,
                        observations);
                }
            }

            foreach (var companion in profile.VersionFolderCompanions)
            {
                var companionPath = PublisherGamePathGuard.GetChildPath(versionDirectory, companion.RelativePath);
                if (pathGuard.PathOrParentsHaveReparseComponent(companionPath))
                {
                    return ReviewAndDispose(
                        PublisherGameInspectionReason.ReparsePointFound,
                        root,
                        observations);
                }

                if (!File.Exists(companionPath))
                {
                    return ReviewAndDispose(companion.MissingReason, root, observations);
                }

                var companionObservation = ProtectedPublisherExecutableObservation.Open(
                    companionPath,
                    root,
                    metadataReader,
                    identityReader,
                    entryOpener);
                observations.Add(companionObservation);
                var companionMetadata = companionObservation.Metadata;
                var companionReason = ValidateMetadata(companionMetadata, companion.Identity);
                if (companionReason is not PublisherGameInspectionReason.None)
                {
                    return ReviewAndDispose(companionReason, root, observations);
                }

                if (companion.RequireLauncherVersion
                    && !string.Equals(
                        companionMetadata.ProductVersion,
                        launcherVersion,
                        StringComparison.Ordinal))
                {
                    return ReviewAndDispose(
                        PublisherGameInspectionReason.LauncherMismatch,
                        root,
                        observations);
                }
            }

            var rootSecond = pathGuard.CheckRoot(root);
            if (observations.Any(observation =>
                    pathGuard.PathOrParentsHaveReparseComponent(observation.Path)))
            {
                return ReviewAndDispose(
                    PublisherGameInspectionReason.ReparsePointFound,
                    root,
                    observations);
            }

            if (rootSecond.Status is not PublisherGameInspectionStatus.Ready
                || !string.Equals(rootSecond.CanonicalRoot, root, StringComparison.OrdinalIgnoreCase)
                || !observations.All(observation => observation.RemainsBound(metadataReader)))
            {
                return ReviewAndDispose(
                    PublisherGameInspectionReason.TargetChangedDuringInspection,
                    root,
                    observations);
            }

            return new(
                PublisherGameInspectionStatus.Ready,
                PublisherGameInspectionReason.None,
                root,
                launcherVersion,
                rootLauncher,
                observations);
        }
        catch (PublisherReparsePointException)
        {
            return ReviewAndDispose(
                PublisherGameInspectionReason.ReparsePointFound,
                root,
                observations);
        }
        catch (Exception exception) when (PublisherGamePathGuard.IsInspectionException(exception))
        {
            return ReviewAndDispose(
                PublisherGameInspectionReason.InspectionFailed,
                root,
                observations);
        }
    }

    public PublisherGameInspectionReason ValidateExecutable(
        string path,
        string bindingRoot,
        ExecutableIdentity identity,
        PublisherGameInspectionReason missingReason,
        out ProtectedPublisherExecutableObservation? observation)
    {
        observation = null;
        if (pathGuard.PathOrParentsHaveReparseComponent(path))
        {
            return PublisherGameInspectionReason.ReparsePointFound;
        }

        if (!File.Exists(path))
        {
            return missingReason;
        }

        observation = ProtectedPublisherExecutableObservation.Open(
            path,
            bindingRoot,
            metadataReader,
            identityReader,
            entryOpener);
        var reason = ValidateMetadata(observation.Metadata, identity);
        if (reason is not PublisherGameInspectionReason.None)
        {
            observation.Dispose();
            observation = null;
        }

        return reason;
    }

    public bool RemainsStable(
        ProtectedPublisherExecutableObservation observation,
        ExecutableIdentity identity) =>
        !pathGuard.PathOrParentsHaveReparseComponent(observation.Path)
        && observation.RemainsBound(metadataReader)
        && ValidateMetadata(observation.Metadata, identity) is PublisherGameInspectionReason.None;

    public bool RemainsStable(LauncherValidation validation)
    {
        if (validation.Status is not PublisherGameInspectionStatus.Ready
            || validation.Observations.Count == 0)
        {
            return false;
        }

        var rootSecond = pathGuard.CheckRoot(validation.CanonicalRoot);
        return rootSecond.Status is PublisherGameInspectionStatus.Ready
            && string.Equals(
                rootSecond.CanonicalRoot,
                validation.CanonicalRoot,
                StringComparison.OrdinalIgnoreCase)
            && validation.Observations.All(observation =>
                !pathGuard.PathOrParentsHaveReparseComponent(observation.Path)
                && observation.RemainsBound(metadataReader));
    }

    private static PublisherGameInspectionReason ValidateMetadata(
        PublisherExecutableMetadata metadata,
        ExecutableIdentity identity)
    {
        if (!metadata.HasValidAuthenticodeSignature)
        {
            return PublisherGameInspectionReason.SignatureInvalid;
        }

        if (!string.Equals(metadata.Publisher?.Trim(), identity.Publisher, StringComparison.OrdinalIgnoreCase))
        {
            return PublisherGameInspectionReason.PublisherMismatch;
        }

        if (identity.ProductName is not null
            && !string.Equals(metadata.ProductName ?? string.Empty, identity.ProductName, StringComparison.Ordinal))
        {
            return PublisherGameInspectionReason.ProductIdentityMismatch;
        }

        if (identity.FileDescription is not null
            && !string.Equals(
                metadata.FileDescription ?? string.Empty,
                identity.FileDescription,
                StringComparison.Ordinal))
        {
            return PublisherGameInspectionReason.ProductIdentityMismatch;
        }

        if (identity.OriginalFilename is not null
            && !string.Equals(
                metadata.OriginalFilename ?? string.Empty,
                identity.OriginalFilename,
                StringComparison.OrdinalIgnoreCase))
        {
            return PublisherGameInspectionReason.ExecutableIdentityMismatch;
        }

        if (identity.CompanyName is not null
            && !string.Equals(
                metadata.CompanyName ?? string.Empty,
                identity.CompanyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return PublisherGameInspectionReason.ExecutableIdentityMismatch;
        }

        return PublisherGameInspectionReason.None;
    }

    private static bool TryParseFourPartVersion(string? value, out string? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Length > 43)
        {
            return false;
        }

        var segments = value.Split('.');
        if (segments.Length != 4
            || segments.Any(segment =>
                segment.Length == 0
                || segment.Length > 10
                || (segment.Length > 1 && segment[0] == '0')
                || !segment.All(char.IsAsciiDigit)
                || !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        version = value;
        return true;
    }

    private static LauncherValidation Review(
        PublisherGameInspectionReason reason,
        string? root = null) =>
        new(PublisherGameInspectionStatus.NeedsReview, reason, root);

    private static LauncherValidation ReviewAndDispose(
        PublisherGameInspectionReason reason,
        string? root,
        List<ProtectedPublisherExecutableObservation> observations)
    {
        foreach (var observation in observations)
        {
            observation.Dispose();
        }

        observations.Clear();
        return Review(reason, root);
    }
}

internal sealed record ExecutableIdentity(
    string Publisher,
    string? ProductName,
    string? FileDescription,
    string? OriginalFilename,
    string? CompanyName = null);

internal sealed record LauncherCompanion(
    string RelativePath,
    ExecutableIdentity Identity,
    PublisherGameInspectionReason MissingReason,
    bool RequireLauncherVersion = false);

internal sealed record LauncherProfile(
    string RootLauncherName,
    ExecutableIdentity RootLauncherIdentity,
    bool RequireByteIdenticalPair,
    IReadOnlyList<LauncherCompanion> VersionFolderCompanions,
    int VersionFolderComponentCount = 4);

internal sealed class LauncherValidation : IDisposable
{
    private readonly IReadOnlyList<ProtectedPublisherExecutableObservation> observations;

    public LauncherValidation(
        PublisherGameInspectionStatus status,
        PublisherGameInspectionReason reason,
        string? canonicalRoot = null,
        string? launcherVersion = null,
        string? launcherPath = null,
        IReadOnlyList<ProtectedPublisherExecutableObservation>? observations = null)
    {
        Status = status;
        Reason = reason;
        CanonicalRoot = canonicalRoot;
        LauncherVersion = launcherVersion;
        LauncherPath = launcherPath;
        this.observations = observations ?? [];
    }

    public PublisherGameInspectionStatus Status { get; }

    public PublisherGameInspectionReason Reason { get; }

    public string? CanonicalRoot { get; }

    public string? LauncherVersion { get; }

    public string? LauncherPath { get; }

    internal IReadOnlyList<ProtectedPublisherExecutableObservation> Observations => observations;

    public void Dispose()
    {
        foreach (var observation in observations)
        {
            observation.Dispose();
        }
    }
}
