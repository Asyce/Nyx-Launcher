using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Infrastructure.Launching;

public enum GameScreenshotFolderStatus
{
    Ready,
    Unavailable,
    Unsupported,
}

public sealed record GameScreenshotFolderResult(
    GameScreenshotFolderStatus Status,
    string? FolderPath = null);

public sealed class GameScreenshotFolderResolver
{
    private static readonly IReadOnlyDictionary<string, string> RelativeFolders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gi"] = "ScreenShot",
            ["hsr"] = @"StarRail_Data\ScreenShots",
            ["zzz"] = "ScreenShot",
            ["wuwa"] = @"Wuthering Waves Game\Client\Saved\ScreenShot",
            ["ae"] = "Endfield",
        };

    private readonly Func<string, string?> resolveValidatedRoot;
    private readonly IScreenshotFolderFileSystem fileSystem;

    public GameScreenshotFolderResolver(Func<string, string?> resolveValidatedRoot)
        : this(resolveValidatedRoot, new WindowsScreenshotFolderFileSystem())
    {
    }

    internal GameScreenshotFolderResolver(
        Func<string, string?> resolveValidatedRoot,
        IScreenshotFolderFileSystem fileSystem)
    {
        this.resolveValidatedRoot = resolveValidatedRoot
            ?? throw new ArgumentNullException(nameof(resolveValidatedRoot));
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public GameScreenshotFolderResult Resolve(string gameId)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        if (!RelativeFolders.TryGetValue(gameId, out var relativeFolder))
        {
            return new(GameScreenshotFolderStatus.Unsupported);
        }

        try
        {
            var suppliedRoot = gameId == "ae"
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                : resolveValidatedRoot(gameId);
            if (!TryCanonicalLocalRoot(suppliedRoot, out var root))
            {
                return new(GameScreenshotFolderStatus.Unavailable);
            }

            var folder = Path.GetFullPath(Path.Combine(root!, relativeFolder));
            var relative = Path.GetRelativePath(root!, folder);
            if (Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || fileSystem.ContainsReparsePoint(root!)
                || fileSystem.ContainsReparsePoint(folder)
                || !fileSystem.DirectoryExists(folder))
            {
                return new(GameScreenshotFolderStatus.Unavailable);
            }

            return new(GameScreenshotFolderStatus.Ready, folder);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException
                                              or NotSupportedException
                                              or PathTooLongException)
        {
            return new(GameScreenshotFolderStatus.Unavailable);
        }
    }

    private static bool TryCanonicalLocalRoot(string? supplied, out string? root)
    {
        root = null;
        if (string.IsNullOrWhiteSpace(supplied)
            || !Path.IsPathFullyQualified(supplied)
            || supplied.StartsWith(@"\\", StringComparison.Ordinal)
            || supplied.StartsWith(@"\\?\", StringComparison.Ordinal)
            || supplied.StartsWith(@"\\.\", StringComparison.Ordinal)
            || supplied.Length < 3
            || !char.IsAsciiLetter(supplied[0])
            || supplied[1] != Path.VolumeSeparatorChar
            || supplied[2] != Path.DirectorySeparatorChar)
        {
            return false;
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(supplied));
        if (!string.Equals(
                canonical,
                Path.TrimEndingDirectorySeparator(supplied),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        root = canonical;
        return true;
    }
}

internal interface IScreenshotFolderFileSystem
{
    bool DirectoryExists(string path);

    bool ContainsReparsePoint(string path);
}

internal sealed class WindowsScreenshotFolderFileSystem : IScreenshotFolderFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool ContainsReparsePoint(string path) =>
        PublisherGamePathGuard.ContainsReparsePoint(path);
}
