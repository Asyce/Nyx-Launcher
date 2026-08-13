using System.Runtime.Versioning;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

public sealed class WuWaIdentityAdapter
{
    private const string Publisher = "KURO TECHNOLOGY (HONG KONG) CO., LIMITED";
    private static readonly ExecutableIdentity LauncherIdentity =
        new(Publisher, "Wuthering Waves", null, null);
    private static readonly ExecutableIdentity BlankGameIdentity =
        new(Publisher, string.Empty, string.Empty, string.Empty, string.Empty);
    private static readonly LauncherProfile LauncherProfile =
        new("launcher.exe", LauncherIdentity, RequireByteIdenticalPair: true, []);

    private readonly PublisherGamePathGuard pathGuard;
    private readonly OfficialLauncherValidator launcherValidator;
    private readonly WuWaPublicEvidenceParser evidenceParser;

    [SupportedOSPlatform("windows")]
    public WuWaIdentityAdapter()
        : this(
            new WindowsPublisherExecutableMetadataReader(),
            new SystemDriveTypeReader(),
            new SystemVolumeFileSystemReader())
    {
    }

    internal WuWaIdentityAdapter(
        IPublisherExecutableMetadataReader metadataReader,
        IDriveTypeReader driveTypeReader,
        IVolumeFileSystemReader fileSystemReader,
        IPublisherFileIdentityReader? identityReader = null,
        IPublisherReparsePointReader? reparsePointReader = null,
        IPublisherExecutableEntryOpener? entryOpener = null)
    {
        pathGuard = new(driveTypeReader, fileSystemReader, reparsePointReader);
        launcherValidator = new(metadataReader, pathGuard, identityReader, entryOpener);
        evidenceParser = new();
    }

    public PublisherGameInspectionResult Inspect(string? candidateRoot)
    {
        using var inspection = InspectProtected(candidateRoot);
        return inspection.Result;
    }

    internal WuWaProtectedInspection InspectProtected(string? candidateRoot)
    {
        var launcher = launcherValidator.Validate(candidateRoot, LauncherProfile);
        if (launcher.Status is not PublisherGameInspectionStatus.Ready)
        {
            return Protected(Result(launcher.Status, launcher.Reason, launcher.CanonicalRoot), launcher);
        }

        var root = launcher.CanonicalRoot!;
        ProtectedPublisherExecutableObservation? bootstrapProof = null;
        ProtectedPublisherExecutableObservation? runtimeProof = null;

        try
        {
            var gameRoot = PublisherGamePathGuard.GetChildPath(root, "Wuthering Waves Game");
            if (pathGuard.HasReparseComponent(gameRoot))
            {
                return Protected(Review(PublisherGameInspectionReason.ReparsePointFound, root), launcher);
            }

            if (!Directory.Exists(gameRoot))
            {
                return Protected(Review(PublisherGameInspectionReason.GameDirectoryMissing, root), launcher);
            }

            var bootstrapPath = PublisherGamePathGuard.GetChildPath(gameRoot, "Wuthering Waves.exe");
            var runtimePath = PublisherGamePathGuard.GetChildPath(
                gameRoot,
                @"Client\Binaries\Win64\Client-Win64-Shipping.exe");
            var rootConfigPath = PublisherGamePathGuard.GetChildPath(gameRoot, "launcherDownloadConfig.json");
            var nestedConfigPath = PublisherGamePathGuard.GetChildPath(
                gameRoot,
                @"launcherDownload\launcherDownloadConfig.json");
            var resourcePath = PublisherGamePathGuard.GetChildPath(gameRoot, "LocalGameResources.json");

            var bootstrapReason = launcherValidator.ValidateExecutable(
                bootstrapPath,
                root,
                BlankGameIdentity,
                PublisherGameInspectionReason.BootstrapMissing,
                out bootstrapProof);
            if (bootstrapReason is not PublisherGameInspectionReason.None)
            {
                return Protected(Review(bootstrapReason, root), launcher);
            }

            var runtimeReason = launcherValidator.ValidateExecutable(
                runtimePath,
                root,
                BlankGameIdentity,
                PublisherGameInspectionReason.RuntimeMissing,
                out runtimeProof);
            if (runtimeReason is not PublisherGameInspectionReason.None)
            {
                return Protected(Review(runtimeReason, root), launcher, bootstrapProof);
            }

            var rootConfig = evidenceParser.ReadConfig(rootConfigPath);
            if (rootConfig.Reason is not PublisherGameInspectionReason.None)
            {
                return Protected(Review(rootConfig.Reason, root), launcher, bootstrapProof, runtimeProof);
            }

            var nestedConfig = evidenceParser.ReadConfig(nestedConfigPath);
            if (nestedConfig.Reason is not PublisherGameInspectionReason.None)
            {
                return Protected(Review(nestedConfig.Reason, root), launcher, bootstrapProof, runtimeProof);
            }

            if (rootConfig.Value != nestedConfig.Value)
            {
                return Protected(Review(PublisherGameInspectionReason.ConfigIdentityMismatch, root), launcher, bootstrapProof, runtimeProof);
            }

            var resource = evidenceParser.ReadResource(resourcePath);
            if (resource.Reason is not PublisherGameInspectionReason.None)
            {
                return Protected(Review(resource.Reason, root), launcher, bootstrapProof, runtimeProof);
            }
            if (resource.Value!.RuntimeSize != runtimeProof!.Snapshot.Length
                || !PublisherFileIdentity.FixedTimeEquals(
                    resource.Value.RuntimeMd5,
                    runtimeProof.Md5Digest))
            {
                return Protected(
                    Review(PublisherGameInspectionReason.ResourceEvidenceMalformed, root),
                    launcher,
                    bootstrapProof,
                    runtimeProof);
            }

            var rootSecond = pathGuard.CheckRoot(root);
            if (rootSecond.Status is not PublisherGameInspectionStatus.Ready
                || !launcherValidator.RemainsStable(launcher)
                || !launcherValidator.RemainsStable(
                    bootstrapProof!,
                    BlankGameIdentity)
                || !launcherValidator.RemainsStable(
                    runtimeProof!,
                    BlankGameIdentity))
            {
                return Protected(
                    Review(PublisherGameInspectionReason.TargetChangedDuringInspection, root),
                    launcher,
                    bootstrapProof,
                    runtimeProof);
            }

            var rootConfigSecond = evidenceParser.ReadConfig(rootConfigPath);
            var nestedConfigSecond = evidenceParser.ReadConfig(nestedConfigPath);
            var resourceSecond = evidenceParser.ReadResource(resourcePath);
            if (rootConfigSecond.Reason is not PublisherGameInspectionReason.None
                || nestedConfigSecond.Reason is not PublisherGameInspectionReason.None
                || resourceSecond.Reason is not PublisherGameInspectionReason.None
                || rootConfigSecond.Fingerprint != rootConfig.Fingerprint
                || nestedConfigSecond.Fingerprint != nestedConfig.Fingerprint
                || resourceSecond.Fingerprint != resource.Fingerprint
                || pathGuard.CheckRoot(root).Status is not PublisherGameInspectionStatus.Ready
                || pathGuard.HasReparseComponent(gameRoot)
                || !Directory.Exists(gameRoot))
            {
                return Protected(
                    Review(PublisherGameInspectionReason.TargetChangedDuringInspection, root),
                    launcher,
                    bootstrapProof,
                    runtimeProof);
            }

            var maintenance = new ValidatedOfficialMaintenanceTarget(
                "wuwa",
                root,
                launcher.LauncherPath!,
                launcher.LauncherVersion!);
            if (resource.Value.Version is not null
                && !string.Equals(
                    rootConfig.Value!.Version,
                    resource.Value.Version,
                    StringComparison.Ordinal))
            {
                return Protected(new(
                    "wuwa",
                    PublisherGameInspectionStatus.NeedsReview,
                    PublisherGameInspectionReason.VersionConflict,
                    PublisherGameVersionState.Conflict,
                    root,
                    maintenanceTarget: maintenance),
                    launcher,
                    bootstrapProof,
                    runtimeProof,
                    () => RemainsCompleteAndStable(
                        launcher,
                        bootstrapProof!,
                        runtimeProof!,
                        root,
                        gameRoot,
                        rootConfigPath,
                        nestedConfigPath,
                        resourcePath,
                        rootConfig.Fingerprint!,
                        nestedConfig.Fingerprint!,
                        resource.Fingerprint!));
            }

            return Protected(new(
                "wuwa",
                PublisherGameInspectionStatus.Ready,
                PublisherGameInspectionReason.None,
                PublisherGameVersionState.Available,
                root,
                rootConfig.Value!.Version,
                maintenance),
                launcher,
                bootstrapProof,
                runtimeProof,
                () => RemainsCompleteAndStable(
                    launcher,
                    bootstrapProof!,
                    runtimeProof!,
                    root,
                    gameRoot,
                    rootConfigPath,
                    nestedConfigPath,
                    resourcePath,
                    rootConfig.Fingerprint!,
                    nestedConfig.Fingerprint!,
                    resource.Fingerprint!));
        }
        catch (Exception exception) when (PublisherGamePathGuard.IsInspectionException(exception))
        {
            return Protected(
                Review(PublisherGameInspectionReason.InspectionFailed, root),
                launcher,
                bootstrapProof,
                runtimeProof);
        }
        catch
        {
            runtimeProof?.Dispose();
            bootstrapProof?.Dispose();
            launcher.Dispose();
            throw;
        }
    }

    public PublisherGameInspectionResult InspectCandidates(IReadOnlyList<string?> candidateRoots) =>
        PublisherCandidateResolver.Resolve("wuwa", candidateRoots, Inspect);

    private static PublisherGameInspectionResult Result(
        PublisherGameInspectionStatus status,
        PublisherGameInspectionReason reason,
        string? root = null) =>
        new("wuwa", status, reason, PublisherGameVersionState.Unavailable, root);

    private static PublisherGameInspectionResult Review(
        PublisherGameInspectionReason reason,
        string root) =>
        new(
            "wuwa",
            PublisherGameInspectionStatus.NeedsReview,
            reason,
            reason is PublisherGameInspectionReason.VersionConflict
                ? PublisherGameVersionState.Conflict
                : PublisherGameVersionState.Unavailable,
            root);

    private bool RemainsCompleteAndStable(
        LauncherValidation launcher,
        ProtectedPublisherExecutableObservation bootstrap,
        ProtectedPublisherExecutableObservation runtime,
        string root,
        string gameRoot,
        string rootConfigPath,
        string nestedConfigPath,
        string resourcePath,
        string rootConfigFingerprint,
        string nestedConfigFingerprint,
        string resourceFingerprint)
    {
        try
        {
            var rootConfig = evidenceParser.ReadConfig(rootConfigPath);
            var nestedConfig = evidenceParser.ReadConfig(nestedConfigPath);
            var resource = evidenceParser.ReadResource(resourcePath);
            return pathGuard.CheckRoot(root).Status is PublisherGameInspectionStatus.Ready
                && !pathGuard.HasReparseComponent(gameRoot)
                && Directory.Exists(gameRoot)
                && launcherValidator.RemainsStable(launcher)
                && launcherValidator.RemainsStable(bootstrap, BlankGameIdentity)
                && launcherValidator.RemainsStable(runtime, BlankGameIdentity)
                && rootConfig.Reason is PublisherGameInspectionReason.None
                && nestedConfig.Reason is PublisherGameInspectionReason.None
                && resource.Reason is PublisherGameInspectionReason.None
                && rootConfig.Fingerprint == rootConfigFingerprint
                && nestedConfig.Fingerprint == nestedConfigFingerprint
                && resource.Fingerprint == resourceFingerprint;
        }
        catch (Exception exception) when (PublisherGamePathGuard.IsInspectionException(exception))
        {
            return false;
        }
    }

    private static WuWaProtectedInspection Protected(
        PublisherGameInspectionResult result,
        LauncherValidation launcher,
        ProtectedPublisherExecutableObservation? bootstrap = null,
        ProtectedPublisherExecutableObservation? runtime = null,
        Func<bool>? remainsStable = null) =>
        new(result, launcher, bootstrap, runtime, remainsStable);
}

internal sealed class WuWaProtectedInspection : IProtectedPublisherGameInspection
{
    private readonly LauncherValidation launcher;
    private readonly ProtectedPublisherExecutableObservation? bootstrap;
    private readonly ProtectedPublisherExecutableObservation? runtime;
    private readonly Func<bool>? remainsStable;
    private bool disposed;

    public WuWaProtectedInspection(
        PublisherGameInspectionResult result,
        LauncherValidation launcher,
        ProtectedPublisherExecutableObservation? bootstrap,
        ProtectedPublisherExecutableObservation? runtime,
        Func<bool>? remainsStable)
    {
        Result = result;
        this.launcher = launcher;
        this.bootstrap = bootstrap;
        this.runtime = runtime;
        this.remainsStable = remainsStable;
    }

    public PublisherGameInspectionResult Result { get; }

    public bool RemainsCompleteAndStable() =>
        !disposed && remainsStable is not null && remainsStable();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        runtime?.Dispose();
        bootstrap?.Dispose();
        launcher.Dispose();
    }
}
