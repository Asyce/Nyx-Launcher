using System.Globalization;
using System.Runtime.Versioning;
using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Infrastructure.Hoyo;

public sealed class HoyoPlayGlobalValidator
{
    private const string ExpectedPublisher = "COGNOSPHERE PTE. LTD.";
    private readonly IExecutableMetadataReader metadataReader;
    private readonly HoyoReadOnlyPathGuard pathGuard;

    [SupportedOSPlatform("windows")]
    public HoyoPlayGlobalValidator()
        : this(new WindowsAuthenticodeExecutableMetadataReader(), new SystemDriveTypeReader())
    {
    }

    internal HoyoPlayGlobalValidator(
        IExecutableMetadataReader metadataReader,
        IDriveTypeReader driveTypeReader)
    {
        this.metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        pathGuard = new(driveTypeReader ?? throw new ArgumentNullException(nameof(driveTypeReader)));
    }

    public HoyoPlayValidationResult Validate(string? candidateRoot)
    {
        var rootCheck = pathGuard.CheckRoot(candidateRoot);
        if (rootCheck.Status is not HoyoInspectionStatus.Ready)
        {
            return new(rootCheck.Status, rootCheck.Reason);
        }

        var root = rootCheck.CanonicalRoot!;
        try
        {
            var rootLauncherPath = Path.Combine(root, "launcher.exe");
            if (HoyoReadOnlyPathGuard.HasReparsePoint(rootLauncherPath))
            {
                return Review(HoyoInspectionReason.ReparsePointFound);
            }

            if (!File.Exists(rootLauncherPath))
            {
                return Review(HoyoInspectionReason.LaunchTargetMissing);
            }

            var rootSnapshot = FileSnapshot.Capture(rootLauncherPath);
            var rootMetadata = metadataReader.Read(rootLauncherPath);
            var rootReason = ValidateLauncherMetadata(rootMetadata);
            if (rootReason is not HoyoInspectionReason.None)
            {
                return Review(rootReason);
            }

            if (!IsStrictVersion(rootMetadata.ProductVersion))
            {
                return Review(HoyoInspectionReason.ExecutableVersionInvalid);
            }

            var version = rootMetadata.ProductVersion!;
            var versionDirectoryPath = Path.Combine(root, version);
            var versionLauncherPath = Path.Combine(versionDirectoryPath, "launcher.exe");
            if (HoyoReadOnlyPathGuard.ContainsReparsePoint(versionDirectoryPath)
                || HoyoReadOnlyPathGuard.HasReparsePoint(versionLauncherPath))
            {
                return Review(HoyoInspectionReason.ReparsePointFound);
            }

            if (!File.Exists(versionLauncherPath))
            {
                return Review(HoyoInspectionReason.VersionFolderLauncherMissing);
            }

            var versionSnapshot = FileSnapshot.Capture(versionLauncherPath);
            var versionMetadata = metadataReader.Read(versionLauncherPath);
            var versionReason = ValidateLauncherMetadata(versionMetadata);
            if (versionReason is not HoyoInspectionReason.None)
            {
                return Review(versionReason);
            }

            if (!IsStrictVersion(versionMetadata.ProductVersion)
                || !string.Equals(versionMetadata.ProductVersion, version, StringComparison.Ordinal))
            {
                return Review(HoyoInspectionReason.VersionFolderMismatch);
            }

            var rootMetadataSecond = metadataReader.Read(rootLauncherPath);
            var versionMetadataSecond = metadataReader.Read(versionLauncherPath);
            var rootSecond = pathGuard.CheckRoot(root);
            if (rootMetadataSecond != rootMetadata
                || versionMetadataSecond != versionMetadata
                || rootSecond.Status is not HoyoInspectionStatus.Ready
                || !string.Equals(rootSecond.CanonicalRoot, root, StringComparison.OrdinalIgnoreCase)
                || FileSnapshot.Capture(rootLauncherPath) != rootSnapshot
                || FileSnapshot.Capture(versionLauncherPath) != versionSnapshot
                || !File.Exists(rootLauncherPath)
                || !File.Exists(versionLauncherPath)
                || HoyoReadOnlyPathGuard.HasReparsePoint(rootLauncherPath)
                || HoyoReadOnlyPathGuard.ContainsReparsePoint(versionDirectoryPath)
                || HoyoReadOnlyPathGuard.HasReparsePoint(versionLauncherPath))
            {
                return Review(HoyoInspectionReason.TargetChangedDuringInspection);
            }

            return new(
                HoyoInspectionStatus.Ready,
                HoyoInspectionReason.None,
                new ValidatedHoyoPlayInstallation(root, rootLauncherPath, version));
        }
        catch (Exception exception) when (HoyoReadOnlyPathGuard.IsInspectionException(exception))
        {
            return Review(HoyoInspectionReason.InspectionFailed);
        }
    }

    private static HoyoInspectionReason ValidateLauncherMetadata(ExecutableMetadata metadata)
    {
        if (!metadata.HasValidAuthenticodeSignature)
        {
            return HoyoInspectionReason.SignatureInvalid;
        }

        if (!string.Equals(metadata.Publisher?.Trim(), ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
        {
            return HoyoInspectionReason.PublisherMismatch;
        }

        return string.Equals(metadata.ProductName, "HoYoPlay", StringComparison.Ordinal)
            && string.Equals(metadata.FileDescription, "HoYoPlay", StringComparison.Ordinal)
            ? HoyoInspectionReason.None
            : HoyoInspectionReason.ProductIdentityMismatch;
    }

    private static bool IsStrictVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value)
        {
            return false;
        }

        var segments = value.Split('.');
        return segments.Length is >= 2 and <= 4
            && segments.All(segment =>
                segment.Length > 0
                && segment.All(char.IsAsciiDigit)
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static HoyoPlayValidationResult Review(HoyoInspectionReason reason) =>
        new(HoyoInspectionStatus.NeedsReview, reason);

    private sealed record FileSnapshot(long Length, DateTime LastWriteTimeUtc)
    {
        public static FileSnapshot Capture(string path)
        {
            var info = new FileInfo(path);
            info.Refresh();
            return new(info.Length, info.LastWriteTimeUtc);
        }
    }
}
