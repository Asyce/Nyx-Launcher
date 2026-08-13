using System.Text;

namespace Nyx.Desktop.Infrastructure.Exports;

internal sealed record HoyoPullGameConfiguration(
    string GameId,
    string OutputFolder,
    string LogRelativePath,
    string DataMarker,
    string LocalLowRelativePath,
    Uri Endpoint,
    IReadOnlyList<string> GachaTypes,
    bool RequiresRealGachaType = false)
{
    public static HoyoPullGameConfiguration For(string gameId) => gameId switch
    {
        "gi" => new(
            "gi",
            "Genshin Impact",
            Path.Combine("AppData", "LocalLow", "miHoYo", "Genshin Impact", "output_log.txt"),
            "GenshinImpact_Data",
            Path.Combine("AppData", "LocalLow", "miHoYo", "Genshin Impact"),
            new Uri("https://public-operation-hk4e-sg.hoyoverse.com/gacha_info/api/getGachaLog"),
            ["301", "400", "302", "500", "200", "100"]),
        "hsr" => new(
            "hsr",
            "Honkai Star Rail",
            Path.Combine("AppData", "LocalLow", "Cognosphere", "Star Rail", "Player.log"),
            "StarRail_Data",
            Path.Combine("AppData", "LocalLow", "Cognosphere", "Star Rail"),
            new Uri("https://public-operation-hkrpg-sg.hoyoverse.com/common/gacha_record/api/getGachaLog"),
            ["11", "12", "1", "2", "21", "22"]),
        "zzz" => new(
            "zzz",
            "Zenless Zone Zero",
            Path.Combine("AppData", "LocalLow", "miHoYo", "ZenlessZoneZero", "Player.log"),
            "ZenlessZoneZero_Data",
            Path.Combine("AppData", "LocalLow", "miHoYo", "ZenlessZoneZero"),
            new Uri("https://public-operation-common-sg.hoyoverse.com/common/gacha_record/api/getGachaLog"),
            ["2", "102", "3", "103", "5", "1"],
            RequiresRealGachaType: true),
        _ => throw new Nyx.Desktop.Core.Exports.PullExportException(
            Nyx.Desktop.Core.Exports.PullExportErrorCodes.UnsupportedGame),
    };
}

internal sealed record PullExportSafetyLimits(
    long MaximumCacheBytes = 64L * 1024 * 1024,
    int MaximumLogBytes = 4 * 1024 * 1024,
    int MaximumSourceLogBytes = 32 * 1024 * 1024,
    int MaximumCandidateUrls = 64,
    int MaximumQueryBytes = 16 * 1024,
    int MaximumResponseBytes = 2 * 1024 * 1024,
    int MaximumPagesPerType = 500,
    int MaximumRecords = 60_000,
    long MaximumOutputBytes = 64L * 1024 * 1024,
    int MaximumVersionDirectories = 128,
    int MaximumSearchDirectories = 2_048,
    TimeSpan? TotalDuration = null,
    TimeSpan? RequestTimeout = null,
    TimeSpan? CacheObservationDuration = null,
    TimeSpan? CachePollInterval = null)
{
    public TimeSpan EffectiveTotalDuration => TotalDuration ?? TimeSpan.FromMinutes(15);
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(15);
    public TimeSpan EffectiveCacheObservationDuration => CacheObservationDuration ?? TimeSpan.FromMinutes(10);
    public TimeSpan EffectiveCachePollInterval => CachePollInterval ?? TimeSpan.FromMilliseconds(750);
}

internal interface IHoyoPullCacheLocator
{
    string Locate(HoyoPullGameConfiguration game, CancellationToken cancellationToken);
}

internal sealed class HoyoPullCacheLocator(string userProfile, PullExportSafetyLimits limits) : IHoyoPullCacheLocator
{
    private static readonly string[] InstallChildren = ["Genshin Impact Game", "Star Rail", "ZenlessZoneZero", "Games"];

    public string Locate(HoyoPullGameConfiguration game, CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        var logPath = Path.Combine(userProfile, game.LogRelativePath);
        var log = ReadSharedText(logPath, limits.MaximumLogBytes, cancellationToken);
        if (log is not null)
        {
            var installRoot = FindInstallRoot(log, game.DataMarker);
            if (installRoot is not null) roots.Add(installRoot);
        }

        roots.Add(Path.Combine(userProfile, game.LocalLowRelativePath));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var webCaches = FindWebCaches(root, limits.MaximumSearchDirectories, cancellationToken);
            if (webCaches is null) continue;
            var cache = FindNewestCacheData(webCaches, limits.MaximumVersionDirectories, cancellationToken);
            if (cache is not null) return cache;
        }

        throw new Nyx.Desktop.Core.Exports.PullExportException(
            Nyx.Desktop.Core.Exports.PullExportErrorCodes.HistoryNotFound);
    }

    internal static string? FindInstallRoot(string log, string marker)
    {
        var suffixes = new[] { "\\" + marker, "/" + marker };
        var best = -1;
        string? result = null;
        foreach (var suffix in suffixes)
        {
            var end = log.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (end <= best) continue;
            var lineStart = end;
            while (lineStart > 0 && log[lineStart - 1] is not '\r' and not '\n') lineStart--;
            var start = -1;
            for (var index = end - 2; index >= lineStart; index--)
            {
                if (char.IsAsciiLetter(log[index]) && log[index + 1] == ':' && log[index + 2] is '\\' or '/')
                {
                    start = index;
                    break;
                }
            }
            if (start < 0) continue;
            var candidate = log[start..end].Trim();
            if (!Path.IsPathFullyQualified(candidate)) continue;
            try
            {
                result = Path.GetFullPath(candidate);
                best = end;
            }
            catch (Exception) when (candidate.Length <= 32_768) { }
        }
        return result;
    }

    internal static string? FindNewestCacheData(string webCaches, int maximumVersions, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(webCaches)) return null;
        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(webCaches).Take(maximumVersions + 1).ToArray(); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
        var materialized = directories.ToArray();
        if (materialized.Length > maximumVersions) return null;

        foreach (var versionDirectory in materialized
            .Select(static path => new { Path = path, Version = ParseVersion(Path.GetFileName(path)) })
            .OrderByDescending(static item => item.Version))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var name in new[] { "data_2", "data_1" })
            {
                var candidate = Path.Combine(versionDirectory.Path, "Cache", "Cache_Data", name);
                if (IsRegularFile(candidate)) return candidate;
            }
        }
        return null;
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : new Version(0, 0);

    private static string? FindWebCaches(string root, int maximumDirectories, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return null;
        var direct = Path.Combine(root, "webCaches");
        if (Directory.Exists(direct)) return direct;
        foreach (var child in InstallChildren)
        {
            var candidate = Path.Combine(root, child, "webCaches");
            if (Directory.Exists(candidate)) return candidate;
        }

        var seen = 0;
        try
        {
            var pending = new Queue<(string Path, int Depth)>();
            pending.Enqueue((root, 0));
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Dequeue();
                if (current.Depth >= 4) continue;
                foreach (var directory in Directory.EnumerateDirectories(current.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++seen > maximumDirectories) return null;
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(directory); }
                    catch (Exception) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (Path.GetFileName(directory).Equals("webCaches", StringComparison.OrdinalIgnoreCase)) return directory;
                    pending.Enqueue((directory, current.Depth + 1));
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        return null;
    }

    private static string? ReadSharedText(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        if (!IsRegularFile(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            if (stream.Length > maximumBytes) return null;
            var bytes = new byte[(int)stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0) break;
                read += count;
            }
            return Encoding.UTF8.GetString(bytes, 0, read);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception) { return false; }
    }
}
