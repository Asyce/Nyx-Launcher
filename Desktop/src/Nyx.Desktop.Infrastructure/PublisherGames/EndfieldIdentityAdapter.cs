using System.Runtime.Versioning;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

public sealed class EndfieldIdentityAdapter
{
    private const string Publisher = "GRYPH FRONTIER PTE. LTD.";
    private static readonly ExecutableIdentity LauncherIdentity =
        new(Publisher, "GRYPHLINK", null, "Launcher.exe");
    private static readonly ExecutableIdentity GamesIdentity =
        new(Publisher, "GRYPHLINK", "GRYPHLINK", "Games.exe", "Gryph Frontier Pte. Ltd.");
    private static readonly ExecutableIdentity GameIdentity =
        new(Publisher, string.Empty, string.Empty, string.Empty, string.Empty);
    private static readonly ExecutableIdentity PlatformIdentity =
        new(Publisher, "PlatformProcess", "PlatformProcess", "PlatformProcess.exe", "PlatformProcess");
    private static readonly LauncherProfile LauncherProfile =
        new(
            "Launcher.exe",
            LauncherIdentity,
            RequireByteIdenticalPair: true,
            [
                new(
                    "Games.exe",
                    GamesIdentity,
                    PublisherGameInspectionReason.GamesExecutableMissing,
                    RequireLauncherVersion: true),
            ],
            VersionFolderComponentCount: 3);

    private readonly PublisherGamePathGuard pathGuard;
    private readonly OfficialLauncherValidator launcherValidator;

    [SupportedOSPlatform("windows")]
    public EndfieldIdentityAdapter()
        : this(
            new WindowsPublisherExecutableMetadataReader(),
            new SystemDriveTypeReader(),
            new SystemVolumeFileSystemReader())
    {
    }

    internal EndfieldIdentityAdapter(
        IPublisherExecutableMetadataReader metadataReader,
        IDriveTypeReader driveTypeReader,
        IVolumeFileSystemReader fileSystemReader,
        IPublisherFileIdentityReader? identityReader = null,
        IPublisherReparsePointReader? reparsePointReader = null,
        IPublisherExecutableEntryOpener? entryOpener = null)
    {
        pathGuard = new(driveTypeReader, fileSystemReader, reparsePointReader);
        launcherValidator = new(metadataReader, pathGuard, identityReader, entryOpener);
    }

    public PublisherGameInspectionResult Inspect(string? candidateRoot)
    {
        using var inspection = InspectProtected(candidateRoot);
        return inspection.Result;
    }

    internal EndfieldProtectedInspection InspectProtected(string? candidateRoot)
    {
        var launcher = launcherValidator.Validate(candidateRoot, LauncherProfile);
        if (launcher.Status is not PublisherGameInspectionStatus.Ready)
        {
            return Protected(Result(launcher.Status, launcher.Reason, launcher.CanonicalRoot), launcher);
        }

        var root = launcher.CanonicalRoot!;
        ProtectedPublisherExecutableObservation? gameProof = null;
        ProtectedPublisherExecutableObservation? platformProof = null;
        try
        {
            var gameRoot = PublisherGamePathGuard.GetChildPath(root, @"games\EndField Game");
            if (pathGuard.HasReparseComponent(gameRoot))
            {
                return Protected(Review(PublisherGameInspectionReason.ReparsePointFound, root), launcher);
            }

            if (!Directory.Exists(gameRoot))
            {
                return Protected(Review(PublisherGameInspectionReason.GameDirectoryMissing, root), launcher);
            }

            var gamePath = PublisherGamePathGuard.GetChildPath(gameRoot, "Endfield.exe");
            var platformPath = PublisherGamePathGuard.GetChildPath(gameRoot, "PlatformProcess.exe");
            var gameReason = launcherValidator.ValidateExecutable(
                gamePath,
                root,
                GameIdentity,
                PublisherGameInspectionReason.BootstrapMissing,
                out gameProof);
            if (gameReason is not PublisherGameInspectionReason.None)
            {
                return Protected(Review(gameReason, root), launcher);
            }

            var platformReason = launcherValidator.ValidateExecutable(
                platformPath,
                root,
                PlatformIdentity,
                PublisherGameInspectionReason.RuntimeMissing,
                out platformProof);
            if (platformReason is not PublisherGameInspectionReason.None)
            {
                return Protected(Review(platformReason, root), launcher, gameProof);
            }

            var rootSecond = pathGuard.CheckRoot(root);
            if (rootSecond.Status is not PublisherGameInspectionStatus.Ready
                || !launcherValidator.RemainsStable(launcher)
                || !launcherValidator.RemainsStable(
                    gameProof!,
                    GameIdentity)
                || !launcherValidator.RemainsStable(
                    platformProof!,
                    PlatformIdentity)
                || pathGuard.CheckRoot(root).Status is not PublisherGameInspectionStatus.Ready
                || pathGuard.HasReparseComponent(gameRoot)
                || !Directory.Exists(gameRoot))
            {
                return Protected(
                    Review(PublisherGameInspectionReason.TargetChangedDuringInspection, root),
                    launcher,
                    gameProof,
                    platformProof);
            }

            var maintenance = new ValidatedOfficialMaintenanceTarget(
                "ae",
                root,
                launcher.LauncherPath!,
                launcher.LauncherVersion!);
            return Protected(
                new(
                    "ae",
                    PublisherGameInspectionStatus.NeedsReview,
                    PublisherGameInspectionReason.VersionUnavailable,
                    PublisherGameVersionState.Unavailable,
                    root,
                    maintenanceTarget: maintenance),
                launcher,
                gameProof,
                platformProof,
                () => RemainsCompleteAndStable(launcher, gameProof!, platformProof!, root, gameRoot));
        }
        catch (Exception exception) when (PublisherGamePathGuard.IsInspectionException(exception))
        {
            return Protected(
                Review(PublisherGameInspectionReason.InspectionFailed, root),
                launcher,
                gameProof,
                platformProof);
        }
        catch
        {
            platformProof?.Dispose();
            gameProof?.Dispose();
            launcher.Dispose();
            throw;
        }
    }

    public PublisherGameInspectionResult InspectCandidates(IReadOnlyList<string?> candidateRoots) =>
        PublisherCandidateResolver.Resolve("ae", candidateRoots, Inspect);

    private static PublisherGameInspectionResult Result(
        PublisherGameInspectionStatus status,
        PublisherGameInspectionReason reason,
        string? root = null) =>
        new("ae", status, reason, PublisherGameVersionState.Unavailable, root);

    private static PublisherGameInspectionResult Review(
        PublisherGameInspectionReason reason,
        string root) =>
        new(
            "ae",
            PublisherGameInspectionStatus.NeedsReview,
            reason,
            PublisherGameVersionState.Unavailable,
            root);

    private bool RemainsCompleteAndStable(
        LauncherValidation launcher,
        ProtectedPublisherExecutableObservation game,
        ProtectedPublisherExecutableObservation platform,
        string root,
        string gameRoot)
    {
        try
        {
            return pathGuard.CheckRoot(root).Status is PublisherGameInspectionStatus.Ready
                && !pathGuard.HasReparseComponent(gameRoot)
                && Directory.Exists(gameRoot)
                && launcherValidator.RemainsStable(launcher)
                && launcherValidator.RemainsStable(game, GameIdentity)
                && launcherValidator.RemainsStable(platform, PlatformIdentity);
        }
        catch (Exception exception) when (PublisherGamePathGuard.IsInspectionException(exception))
        {
            return false;
        }
    }

    private static EndfieldProtectedInspection Protected(
        PublisherGameInspectionResult result,
        LauncherValidation launcher,
        ProtectedPublisherExecutableObservation? game = null,
        ProtectedPublisherExecutableObservation? platform = null,
        Func<bool>? remainsStable = null) =>
        new(result, launcher, game, platform, remainsStable);
}

internal sealed class EndfieldProtectedInspection : IProtectedPublisherGameInspection
{
    private readonly LauncherValidation launcher;
    private readonly ProtectedPublisherExecutableObservation? game;
    private readonly ProtectedPublisherExecutableObservation? platform;
    private readonly Func<bool>? remainsStable;
    private bool disposed;

    public EndfieldProtectedInspection(
        PublisherGameInspectionResult result,
        LauncherValidation launcher,
        ProtectedPublisherExecutableObservation? game,
        ProtectedPublisherExecutableObservation? platform,
        Func<bool>? remainsStable)
    {
        Result = result;
        this.launcher = launcher;
        this.game = game;
        this.platform = platform;
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
        platform?.Dispose();
        game?.Dispose();
        launcher.Dispose();
    }
}
