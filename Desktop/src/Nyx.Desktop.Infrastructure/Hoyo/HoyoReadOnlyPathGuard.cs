using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Infrastructure.Hoyo;

internal sealed class HoyoReadOnlyPathGuard(IDriveTypeReader driveTypeReader)
{
    private readonly IDriveTypeReader driveTypeReader =
        driveTypeReader ?? throw new ArgumentNullException(nameof(driveTypeReader));

    public RootCheck CheckRoot(string? candidateRoot)
    {
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            return new(null, HoyoInspectionStatus.NotFound, HoyoInspectionReason.PathNotProvided);
        }

        string canonicalRoot;
        try
        {
            if (!IsFullyQualifiedLocalDrivePath(candidateRoot))
            {
                return Review(HoyoInspectionReason.PathIsNotLocalAndCanonical);
            }

            canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateRoot));
            var supplied = Path.TrimEndingDirectorySeparator(candidateRoot);
            if (!string.Equals(canonicalRoot, supplied, StringComparison.OrdinalIgnoreCase))
            {
                return Review(HoyoInspectionReason.PathIsNotLocalAndCanonical);
            }

            var driveRoot = Path.GetPathRoot(canonicalRoot)!;
            if (driveTypeReader.GetDriveType(driveRoot) is not DriveType.Fixed)
            {
                return Review(HoyoInspectionReason.DriveIsNotLocalFixed);
            }
        }
        catch (Exception exception) when (IsInspectionException(exception) || exception is ArgumentException or PathTooLongException)
        {
            return Review(HoyoInspectionReason.PathIsNotLocalAndCanonical);
        }

        if (!Directory.Exists(canonicalRoot))
        {
            return new(canonicalRoot, HoyoInspectionStatus.NotFound, HoyoInspectionReason.DirectoryNotFound);
        }

        try
        {
            if (ContainsReparsePoint(canonicalRoot))
            {
                return new(canonicalRoot, HoyoInspectionStatus.NeedsReview, HoyoInspectionReason.ReparsePointFound);
            }
        }
        catch (Exception exception) when (IsInspectionException(exception))
        {
            return new(canonicalRoot, HoyoInspectionStatus.NeedsReview, HoyoInspectionReason.InspectionFailed);
        }

        return new(canonicalRoot, HoyoInspectionStatus.Ready, HoyoInspectionReason.None);
    }

    public static bool HasReparsePoint(string path)
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

    public static bool ContainsReparsePoint(string path)
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

    public static bool IsInspectionException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException;

    private static bool IsFullyQualifiedLocalDrivePath(string path) =>
        Path.IsPathFullyQualified(path)
        && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
        && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
        && path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == Path.VolumeSeparatorChar
        && path[2] == Path.DirectorySeparatorChar;

    private static RootCheck Review(HoyoInspectionReason reason) =>
        new(null, HoyoInspectionStatus.NeedsReview, reason);
}

internal sealed record RootCheck(
    string? CanonicalRoot,
    HoyoInspectionStatus Status,
    HoyoInspectionReason Reason);
