using Nyx.Desktop.Core.Diagnostics;

namespace Nyx.Desktop.Infrastructure.Cache;

/// <summary>
/// Measures and clears only Nyx-generated downloaded content. UserAssets,
/// launcher state, and export output are deliberately outside the clear set.
/// </summary>
public sealed class LauncherCacheService
{
    public LauncherCacheService(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        DataDirectory = Path.GetFullPath(dataDirectory);
        ContentCacheDirectory = Path.Combine(DataDirectory, "ContentCache");
        GeneratedDirectory = Path.Combine(ContentCacheDirectory, "managed");
        LastKnownGoodDirectory = Path.Combine(ContentCacheDirectory, "last-known-good");
        UserArtCacheDirectory = Path.Combine(ContentCacheDirectory, "user-art");
        UserAssetsDirectory = Path.Combine(DataDirectory, "UserAssets");
        StatePath = Path.Combine(DataDirectory, "launcher-state-v1.json");
        BackupStatePath = StatePath + ".bak";
        ExportsDirectory = Path.Combine(DataDirectory, "Exports");
    }

    public string DataDirectory { get; }
    public string ContentCacheDirectory { get; }
    public string GeneratedDirectory { get; }
    public string LastKnownGoodDirectory { get; }
    public string UserArtCacheDirectory { get; }
    public string UserAssetsDirectory { get; }
    public string StatePath { get; }
    public string BackupStatePath { get; }
    public string ExportsDirectory { get; }

    public LauncherCacheTotals GetTotals()
    {
        var generated = MeasureDirectories(GeneratedDirectory, LastKnownGoodDirectory);
        var userAssets = MeasureDirectories(UserAssetsDirectory, UserArtCacheDirectory);
        var state = MeasureFiles(StatePath, BackupStatePath);
        var exports = MeasureDirectory(ExportsDirectory);
        return new(generated, userAssets, state, exports);
    }

    public LauncherCacheTotals ClearGeneratedCache()
    {
        ClearDirectoryContents(GeneratedDirectory);
        ClearDirectoryContents(LastKnownGoodDirectory);
        return GetTotals();
    }

    private static long MeasureDirectories(params string[] directories)
    {
        long total = 0;
        foreach (var directory in directories) total += MeasureDirectory(directory);
        return total;
    }

    private static long MeasureDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
            };
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!IsReparse(info)) total += info.Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    private static long MeasureFiles(params string[] files)
    {
        long total = 0;
        foreach (var path in files)
        {
            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (!IsReparse(info)) total += info.Length;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    private static void ClearDirectoryContents(string directory)
    {
        if (!Directory.Exists(directory)) return;
        string[] files;
        string[] children;
        try
        {
            files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            children = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!IsReparse(info)) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var child in children)
        {
            try
            {
                var info = new DirectoryInfo(child);
                if (IsReparse(info)) continue;
                ClearDirectoryContents(child);
                if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsReparse(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;
}
