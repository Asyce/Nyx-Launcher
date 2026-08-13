using System.Collections.ObjectModel;
using Nyx.Desktop.Core.Features;

namespace Nyx.Desktop.Core.Diagnostics;

public enum LauncherDiscoveryResultCategory
{
    Ready,
    Running,
    Missing,
    CandidateFound,
    Invalid,
    AccessDenied,
    Uncertain,
    NotConfigured,
    Failed,
}

public sealed record LauncherCacheTotals(
    long GeneratedBytes,
    long UserAssetBytes,
    long StateBytes = 0,
    long ExportBytes = 0)
{
    public long DownloadedBytes => Math.Max(0, GeneratedBytes);
    public long TotalBytes => GeneratedBytes + UserAssetBytes + StateBytes + ExportBytes;
}

public sealed record LauncherDiagnosticGame(
    string GameId,
    string SessionState,
    string ExportState,
    LauncherDiscoveryResultCategory Discovery,
    string? ErrorCode = null)
{
    public LauncherDiagnosticGame Normalize() => this with
    {
        GameId = LauncherDiagnosticsSanitizer.Token(GameId, "unknown-game"),
        SessionState = LauncherDiagnosticsSanitizer.Token(SessionState, "unknown"),
        ExportState = LauncherDiagnosticsSanitizer.Token(ExportState, "unknown"),
        ErrorCode = LauncherDiagnosticsSanitizer.ErrorCode(ErrorCode),
    };
}

public sealed record LauncherDiagnosticsSnapshot
{
    public LauncherDiagnosticsSnapshot(
        string launcherVersion,
        LauncherFeatureFlags featureFlags,
        IEnumerable<LauncherDiagnosticGame>? games = null,
        string? manifestRevision = null,
        string? manifestHealth = null,
        LauncherCacheTotals? cache = null,
        string? lastErrorCode = null)
    {
        LauncherVersion = LauncherDiagnosticsSanitizer.Token(launcherVersion, "unknown-version");
        FeatureFlags = featureFlags ?? LauncherFeatureFlags.Defaults();
        var normalized = (games ?? Array.Empty<LauncherDiagnosticGame>())
            .Where(static game => game is not null)
            .Select(static game => game.Normalize())
            .GroupBy(static game => game.GameId, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        Games = new ReadOnlyCollection<LauncherDiagnosticGame>(normalized);
        ManifestRevision = LauncherDiagnosticsSanitizer.Hash(manifestRevision);
        ManifestHealth = LauncherDiagnosticsSanitizer.Token(manifestHealth, "unknown");
        Cache = cache ?? new LauncherCacheTotals(0, 0);
        LastErrorCode = LauncherDiagnosticsSanitizer.ErrorCode(lastErrorCode);
    }

    public string LauncherVersion { get; }
    public LauncherFeatureFlags FeatureFlags { get; }
    public IReadOnlyList<LauncherDiagnosticGame> Games { get; }
    public string? ManifestRevision { get; }
    public string ManifestHealth { get; }
    public LauncherCacheTotals Cache { get; }
    public string? LastErrorCode { get; }
}

public static class LauncherDiagnosticsSanitizer
{
    private static readonly HashSet<string> SafeErrors = new(StringComparer.Ordinal)
    {
        "unknown", "canceled", "timed-out", "access-denied", "io-failed", "provider-failed",
        "launch-not-admitted", "not-found", "invalid", "uncertain", "network-failed", "cache-cleared",
        "not-configured", "failed", "future-version", "malformed", "unsupported",
    };

    public static string? ErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var token = Token(value, "unknown");
        return SafeErrors.Contains(token) ? token : "unknown";
    }

    public static string Token(string? value, string fallback = "unknown")
    {
        var safeFallback = string.IsNullOrWhiteSpace(fallback)
            ? "unknown"
            : fallback.Trim().ToLowerInvariant();
        if (safeFallback.Length > 64 || safeFallback.Any(static character => !char.IsLetterOrDigit(character) && character != '-' && character != '_' && character != '.'))
        {
            safeFallback = "unknown";
        }
        if (string.IsNullOrWhiteSpace(value)) return safeFallback;
        var text = value.Trim().ToLowerInvariant();
        if (text.Length > 64 || text.Any(static character => !char.IsLetterOrDigit(character) && character != '-' && character != '_' && character != '.')) return safeFallback;
        return text;
    }

    public static string? Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hash = value.Trim();
        return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
    }
}

public static class LauncherDiagnosticsText
{
    public static string FormatForCopy(LauncherDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lines = new List<string>
        {
            "Nyx diagnostics",
            $"launcher-version: {snapshot.LauncherVersion}",
            $"manifest: {snapshot.ManifestHealth}{(snapshot.ManifestRevision is null ? string.Empty : $" ({snapshot.ManifestRevision})")}",
            $"cache-generated-bytes: {snapshot.Cache.GeneratedBytes}",
            $"cache-user-art-bytes: {snapshot.Cache.UserAssetBytes}",
            $"cache-state-bytes: {snapshot.Cache.StateBytes}",
            $"cache-export-bytes: {snapshot.Cache.ExportBytes}",
            $"cache-total-bytes: {snapshot.Cache.TotalBytes}",
            $"feature-flags: {string.Join(',', snapshot.FeatureFlags.AsCapabilityMap().Select(static pair => $"{pair.Key}={pair.Value.ToString().ToLowerInvariant()}"))}",
        };
        foreach (var game in snapshot.Games.OrderBy(static game => game.GameId, StringComparer.Ordinal))
        {
            lines.Add($"game:{game.GameId} discovery={game.Discovery.ToString().ToLowerInvariant()} session={game.SessionState} export={game.ExportState}{(game.ErrorCode is null ? string.Empty : $" error={game.ErrorCode}")}");
        }

        if (snapshot.LastErrorCode is not null) lines.Add($"last-error: {snapshot.LastErrorCode}");
        return string.Join(Environment.NewLine, lines);
    }
}
