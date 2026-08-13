using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

internal interface IVolumeFileSystemReader
{
    string GetFormat(string driveRoot);
}

internal interface IPublisherReparsePointReader
{
    bool ContainsReparsePoint(string path);

    bool PathOrParentsHaveReparsePoint(string path);
}

internal sealed class SystemPublisherReparsePointReader : IPublisherReparsePointReader
{
    public bool ContainsReparsePoint(string path) =>
        PublisherGamePathGuard.ContainsReparsePoint(path);

    public bool PathOrParentsHaveReparsePoint(string path) =>
        PublisherGamePathGuard.PathOrParentsHaveReparsePoint(path);
}

internal sealed class SystemVolumeFileSystemReader : IVolumeFileSystemReader
{
    public string GetFormat(string driveRoot) => new DriveInfo(driveRoot).DriveFormat;
}

internal sealed class PublisherGamePathGuard(
    IDriveTypeReader driveTypeReader,
    IVolumeFileSystemReader fileSystemReader,
    IPublisherReparsePointReader? reparsePointReader = null)
{
    private readonly IDriveTypeReader driveTypeReader =
        driveTypeReader ?? throw new ArgumentNullException(nameof(driveTypeReader));
    private readonly IVolumeFileSystemReader fileSystemReader =
        fileSystemReader ?? throw new ArgumentNullException(nameof(fileSystemReader));
    private readonly IPublisherReparsePointReader reparsePointReader =
        reparsePointReader ?? new SystemPublisherReparsePointReader();

    public RootCheck CheckRoot(string? candidateRoot)
    {
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            return new(null, PublisherGameInspectionStatus.NotFound, PublisherGameInspectionReason.PathNotProvided);
        }

        string root;
        try
        {
            if (!IsFullyQualifiedLocalDrivePath(candidateRoot))
            {
                return Review(PublisherGameInspectionReason.PathIsNotLocalAndCanonical);
            }

            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateRoot));
            if (!string.Equals(
                    root,
                    Path.TrimEndingDirectorySeparator(candidateRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Review(PublisherGameInspectionReason.PathIsNotLocalAndCanonical);
            }

            var driveRoot = Path.GetPathRoot(root)!;
            if (driveTypeReader.GetDriveType(driveRoot) is not DriveType.Fixed)
            {
                return Review(PublisherGameInspectionReason.DriveIsNotLocalFixed);
            }

            if (!string.Equals(fileSystemReader.GetFormat(driveRoot), "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                return Review(PublisherGameInspectionReason.FileSystemIsNotNtfs);
            }
        }
        catch (Exception exception) when (IsInspectionException(exception) || exception is ArgumentException or PathTooLongException)
        {
            return Review(PublisherGameInspectionReason.PathIsNotLocalAndCanonical);
        }

        if (!Directory.Exists(root))
        {
            return new(root, PublisherGameInspectionStatus.NotFound, PublisherGameInspectionReason.DirectoryNotFound);
        }

        try
        {
            if (reparsePointReader.ContainsReparsePoint(root))
            {
                return new(root, PublisherGameInspectionStatus.NeedsReview, PublisherGameInspectionReason.ReparsePointFound);
            }
        }
        catch (Exception exception) when (IsInspectionException(exception))
        {
            return new(root, PublisherGameInspectionStatus.NeedsReview, PublisherGameInspectionReason.InspectionFailed);
        }

        return new(root, PublisherGameInspectionStatus.Ready, PublisherGameInspectionReason.None);
    }

    public static string GetChildPath(string root, string fixedRelativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, fixedRelativePath));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("Fixed evidence path escaped the inspected root.");
        }

        return path;
    }

    public bool HasReparseComponent(string path) =>
        reparsePointReader.ContainsReparsePoint(path);

    public bool PathOrParentsHaveReparseComponent(string path) =>
        reparsePointReader.PathOrParentsHaveReparsePoint(path);

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

    public static bool PathOrParentsHaveReparsePoint(string path)
    {
        var parent = Path.GetDirectoryName(path);
        return (parent is not null && ContainsReparsePoint(parent)) || HasReparsePoint(path);
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

    private static RootCheck Review(PublisherGameInspectionReason reason) =>
        new(null, PublisherGameInspectionStatus.NeedsReview, reason);
}

internal sealed record RootCheck(
    string? CanonicalRoot,
    PublisherGameInspectionStatus Status,
    PublisherGameInspectionReason Reason);

internal sealed record PublisherFileSnapshot(long Length, DateTime LastWriteTimeUtc)
{
    public static PublisherFileSnapshot Capture(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return new(info.Length, info.LastWriteTimeUtc);
    }
}
