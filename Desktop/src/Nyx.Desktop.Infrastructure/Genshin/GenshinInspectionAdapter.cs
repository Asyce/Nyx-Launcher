using System.Globalization;
using Nyx.Desktop.Core.Genshin;

namespace Nyx.Desktop.Infrastructure.Genshin;

public sealed class GenshinInspectionAdapter
{
    private const int MaximumConfigBytes = 16 * 1024;
    private const int MaximumConfigLines = 128;
    private const int MaximumConfigLineLength = 1024;
    private const string ExpectedPublisher = "COGNOSPHERE PTE. LTD.";

    private static readonly string[] KnownPackageManifests =
    [
        "pkg_version",
        "pkg_version.json",
        "package_version",
    ];

    private static readonly HashSet<string> AllowedConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "channel",
        "sub_channel",
        "cps",
        "game_version",
    };

    private readonly IExecutableMetadataReader metadataReader;
    private readonly IDriveTypeReader driveTypeReader;

    public GenshinInspectionAdapter(IExecutableMetadataReader metadataReader)
        : this(metadataReader, new SystemDriveTypeReader())
    {
    }

    public GenshinInspectionAdapter(
        IExecutableMetadataReader metadataReader,
        IDriveTypeReader driveTypeReader)
    {
        this.metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        this.driveTypeReader = driveTypeReader ?? throw new ArgumentNullException(nameof(driveTypeReader));
    }

    public GenshinInspectionResult InspectGame(
        string? gameRoot,
        GenshinPathOrigin pathOrigin = GenshinPathOrigin.NewCandidate)
    {
        var rootCheck = CheckRoot(gameRoot, pathOrigin);
        if (rootCheck.Result is not null)
        {
            return rootCheck.Result;
        }

        var root = rootCheck.CanonicalRoot!;

        try
        {
            var executablePath = Path.Combine(root, "GenshinImpact.exe");
            if (IsReparsePoint(executablePath))
            {
                return Review(GenshinInspectionReason.ReparsePointFound, root);
            }

            if (!File.Exists(executablePath))
            {
                return Review(GenshinInspectionReason.LaunchTargetMissing, root);
            }

            var dataDirectoryPath = Path.Combine(root, "GenshinImpact_Data");
            if (IsReparsePoint(dataDirectoryPath))
            {
                return Review(GenshinInspectionReason.ReparsePointFound, root);
            }

            if (!Directory.Exists(dataDirectoryPath))
            {
                return Review(GenshinInspectionReason.DataDirectoryMissing, root);
            }

            var manifestPath = KnownPackageManifests
                .Select(name => Path.Combine(root, name))
                .FirstOrDefault(path => File.Exists(path) || IsReparsePoint(path));
            if (manifestPath is null)
            {
                return Review(GenshinInspectionReason.PackageManifestMissing, root);
            }

            var configPath = Path.Combine(root, "config.ini");
            if (IsReparsePoint(manifestPath) || IsReparsePoint(configPath))
            {
                return Review(GenshinInspectionReason.ReparsePointFound, root);
            }

            if (!File.Exists(configPath))
            {
                return Review(GenshinInspectionReason.ConfigMissing, root);
            }

            var config = ReadConfig(configPath);
            if (config.Reason is not GenshinInspectionReason.None)
            {
                return Review(config.Reason, root);
            }

            if (config.Values!["channel"] != "1"
                || config.Values["sub_channel"] != "0"
                || !string.Equals(config.Values["cps"], "mihoyo", StringComparison.OrdinalIgnoreCase))
            {
                return Review(GenshinInspectionReason.ConfigIdentityMismatch, root);
            }

            var gameVersion = config.Values["game_version"];
            if (!IsDottedNumericVersion(gameVersion))
            {
                return Review(GenshinInspectionReason.GameVersionInvalid, root);
            }

            // The observed Genshin executable exposes empty product-name fields. Its identity is
            // therefore the valid publisher signature plus the Genshin-only config and structure above.
            var metadataResult = ValidateSignedExecutable(metadataReader.Read(executablePath), requireHoYoPlayIdentity: false);
            if (metadataResult is not GenshinInspectionReason.None)
            {
                return Review(metadataResult, root);
            }

            return new(GenshinInspectionStatus.Ready, GenshinInspectionReason.None, root, gameVersion);
        }
        catch (Exception exception) when (IsInspectionException(exception))
        {
            return Review(GenshinInspectionReason.InspectionFailed, root);
        }
    }

    public GenshinInspectionResult InspectUpdater(
        string? updaterRoot,
        GenshinPathOrigin pathOrigin = GenshinPathOrigin.NewCandidate)
    {
        var rootCheck = CheckRoot(updaterRoot, pathOrigin);
        if (rootCheck.Result is not null)
        {
            return rootCheck.Result;
        }

        var root = rootCheck.CanonicalRoot!;

        try
        {
            var rootLauncherPath = Path.Combine(root, "launcher.exe");
            if (IsReparsePoint(rootLauncherPath))
            {
                return Review(GenshinInspectionReason.ReparsePointFound, root);
            }

            if (!File.Exists(rootLauncherPath))
            {
                return Review(GenshinInspectionReason.LaunchTargetMissing, root);
            }

            var rootMetadata = metadataReader.Read(rootLauncherPath);
            var rootMetadataResult = ValidateSignedExecutable(rootMetadata, requireHoYoPlayIdentity: true);
            if (rootMetadataResult is not GenshinInspectionReason.None)
            {
                return Review(rootMetadataResult, root);
            }

            if (!TryParseStrictVersion(rootMetadata.ProductVersion, out var launcherVersion))
            {
                return Review(GenshinInspectionReason.ExecutableVersionInvalid, root);
            }

            var versionDirectoryPath = Path.Combine(root, launcherVersion!.ToString());
            var versionLauncherPath = Path.Combine(versionDirectoryPath, "launcher.exe");
            if (ContainsReparsePoint(versionDirectoryPath) || IsReparsePoint(versionLauncherPath))
            {
                return Review(GenshinInspectionReason.ReparsePointFound, root);
            }

            if (!File.Exists(versionLauncherPath))
            {
                return Review(GenshinInspectionReason.VersionFolderLauncherMissing, root);
            }

            var versionMetadata = metadataReader.Read(versionLauncherPath);
            var versionMetadataResult = ValidateSignedExecutable(versionMetadata, requireHoYoPlayIdentity: true);
            if (versionMetadataResult is not GenshinInspectionReason.None)
            {
                return Review(versionMetadataResult, root);
            }

            if (!TryParseStrictVersion(versionMetadata.ProductVersion, out var nestedVersion)
                || nestedVersion != launcherVersion)
            {
                return Review(GenshinInspectionReason.VersionFolderMismatch, root);
            }

            return new(
                GenshinInspectionStatus.Ready,
                GenshinInspectionReason.None,
                root,
                launcherVersion.ToString());
        }
        catch (Exception exception) when (IsInspectionException(exception))
        {
            return Review(GenshinInspectionReason.InspectionFailed, root);
        }
    }

    private (string? CanonicalRoot, GenshinInspectionResult? Result) CheckRoot(
        string? candidateRoot,
        GenshinPathOrigin pathOrigin)
    {
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            return (null, new(GenshinInspectionStatus.NotFound, GenshinInspectionReason.PathNotProvided));
        }

        string root;
        try
        {
            if (!IsFullyQualifiedLocalDrivePath(candidateRoot))
            {
                return (null, Review(GenshinInspectionReason.PathIsNotLocalAndCanonical));
            }

            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateRoot));
            var supplied = Path.TrimEndingDirectorySeparator(candidateRoot);
            if (!string.Equals(root, supplied, StringComparison.OrdinalIgnoreCase))
            {
                return (null, Review(GenshinInspectionReason.PathIsNotLocalAndCanonical));
            }

            var driveRoot = Path.GetPathRoot(root)!;
            if (driveTypeReader.GetDriveType(driveRoot) is not DriveType.Fixed)
            {
                return (null, Review(GenshinInspectionReason.DriveIsNotLocalFixed));
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            return (null, Review(GenshinInspectionReason.PathIsNotLocalAndCanonical));
        }

        if (!Directory.Exists(root))
        {
            return pathOrigin is GenshinPathOrigin.PreviouslySaved
                ? (root, Review(GenshinInspectionReason.SavedDirectoryMissing, root))
                : (root, new(GenshinInspectionStatus.NotFound, GenshinInspectionReason.DirectoryNotFound, root));
        }

        try
        {
            if (ContainsReparsePoint(root))
            {
                return (root, Review(GenshinInspectionReason.ReparsePointFound, root));
            }
        }
        catch (Exception exception) when (IsInspectionException(exception))
        {
            return (root, Review(GenshinInspectionReason.InspectionFailed, root));
        }

        return (root, null);
    }

    private static bool IsFullyQualifiedLocalDrivePath(string path)
    {
        return Path.IsPathFullyQualified(path)
            && !path.StartsWith(@"\\", StringComparison.Ordinal)
            && path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == Path.VolumeSeparatorChar
            && (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar);
    }

    private static bool ContainsReparsePoint(string path)
    {
        var driveRoot = Path.GetPathRoot(path)!;
        var relativePath = Path.GetRelativePath(driveRoot, path);
        var currentPath = driveRoot;

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            var entry = new DirectoryInfo(currentPath);
            if (entry.LinkTarget is not null)
            {
                return true;
            }

            entry.Refresh();
            if (!entry.Exists)
            {
                return false;
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        FileSystemInfo entry = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        if (entry.LinkTarget is not null)
        {
            return true;
        }

        entry.Refresh();
        return entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static ConfigReadResult ReadConfig(string configPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        var boundedBytes = new byte[MaximumConfigBytes + 1];
        var byteCount = 0;
        while (byteCount < boundedBytes.Length)
        {
            var read = stream.Read(boundedBytes, byteCount, boundedBytes.Length - byteCount);
            if (read == 0)
            {
                break;
            }

            byteCount += read;
        }

        if (byteCount > MaximumConfigBytes)
        {
            return new(GenshinInspectionReason.ConfigTooLarge);
        }

        using var boundedStream = new MemoryStream(boundedBytes, 0, byteCount, writable: false);
        using var reader = new StreamReader(boundedStream, detectEncodingFromByteOrderMarks: true);

        var lineCount = 0;
        while (reader.ReadLine() is { } line)
        {
            lineCount++;
            if (lineCount > MaximumConfigLines || line.Length > MaximumConfigLineLength)
            {
                return new(GenshinInspectionReason.ConfigTooLarge);
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#' || trimmed[0] == '[')
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            if (!AllowedConfigKeys.Contains(key))
            {
                continue;
            }

            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length == 0 || !values.TryAdd(key, value))
            {
                return new(GenshinInspectionReason.ConfigMalformed);
            }
        }

        if (!AllowedConfigKeys.All(values.ContainsKey))
        {
            return new(GenshinInspectionReason.ConfigMalformed);
        }

        return new(GenshinInspectionReason.None, values);
    }

    private static GenshinInspectionReason ValidateSignedExecutable(
        ExecutableMetadata metadata,
        bool requireHoYoPlayIdentity)
    {
        if (!metadata.HasValidAuthenticodeSignature)
        {
            return GenshinInspectionReason.SignatureInvalid;
        }

        if (!string.Equals(metadata.Publisher?.Trim(), ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
        {
            return GenshinInspectionReason.PublisherMismatch;
        }

        if (requireHoYoPlayIdentity
            && (!string.Equals(metadata.ProductName?.Trim(), "HoYoPlay", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(metadata.FileDescription?.Trim(), "HoYoPlay", StringComparison.OrdinalIgnoreCase)))
        {
            return GenshinInspectionReason.ProductIdentityMismatch;
        }

        return GenshinInspectionReason.None;
    }

    private static bool IsDottedNumericVersion(string value)
    {
        var segments = value.Split('.');
        return segments.Length is 3 or 4
            && segments.All(segment =>
                segment.Length > 0
                && segment.All(char.IsAsciiDigit)
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static bool TryParseStrictVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value)
            || value.Trim() != value
            || value.Split('.').Length is < 2 or > 4)
        {
            return false;
        }

        return Version.TryParse(value, out version);
    }

    private static bool IsInspectionException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException;
    }

    private static GenshinInspectionResult Review(
        GenshinInspectionReason reason,
        string? root = null)
    {
        return new(GenshinInspectionStatus.NeedsReview, reason, root);
    }

    private sealed record ConfigReadResult(
        GenshinInspectionReason Reason,
        IReadOnlyDictionary<string, string>? Values = null);
}
