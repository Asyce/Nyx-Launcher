using System.Diagnostics;
using Nyx.Desktop.Core.Diagnostics;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Sessions;

namespace Nyx_Desktop_App;

public sealed partial class MainPage
{
    private readonly long initialRenderStarted = Stopwatch.GetTimestamp();
    private long initialRenderDurationTicks = -1;

    private void RecordInitialRenderDuration()
    {
        if (initialRenderDurationTicks < 0)
            initialRenderDurationTicks = Stopwatch.GetElapsedTime(initialRenderStarted).Ticks;
    }

    private LauncherDiagnosticsSnapshot BuildDiagnosticsSnapshot()
    {
        var state = launcherState.Snapshot;
        var games = Games.Select(game =>
        {
            GameSessionSnapshot snapshot;
            try
            {
                snapshot = sessions.GetSnapshot(game.Id);
            }
            catch (Exception)
            {
                snapshot = new GameSessionSnapshot(
                    game.Id,
                    LocalReadinessEvidence.Unknown,
                    LocalGameStatus.NeedsReview,
                    ExactProcessPresence.Uncertain,
                    false,
                    false,
                    0,
                    0,
                    null,
                    null,
                    null,
                    null,
                    0,
                    0,
                    GameSessionFailureReason.EvidenceUnavailable,
                    false);
            }

            var discovery = snapshot.Readiness switch
            {
                LocalReadinessEvidence.Ready => LauncherDiscoveryResultCategory.Ready,
                LocalReadinessEvidence.NotFound => LauncherDiscoveryResultCategory.Missing,
                LocalReadinessEvidence.NeedsReview => LauncherDiscoveryResultCategory.Invalid,
                _ => LauncherDiscoveryResultCategory.Uncertain,
            };
            var export = state.Export.Games.TryGetValue(game.Id, out var arm)
                ? $"pulls={(arm.PullsArmed ? "armed" : "off")},achievements={(arm.AchievementsArmed ? "armed" : "off")}"
                : "off";
            return new LauncherDiagnosticGame(
                game.Id,
                snapshot.Status.ToString(),
                export,
                discovery,
                snapshot.FailureReason is GameSessionFailureReason.None
                    ? null
                    : snapshot.FailureReason.ToString());
        });

        var timings = new List<LauncherDiagnosticTiming>();
        AddTiming(
            timings,
            "render",
            null,
            initialRenderDurationTicks < 0 ? null : TimeSpan.FromTicks(initialRenderDurationTicks));
        AddTiming(timings, "background", null, launcherVisuals.LastRefreshDuration);
        AddTiming(timings, "banner", null, launcherBanners.LastRefreshDuration);
        AddTiming(timings, "account-restore", null, publisherAccounts.LastAccountRestoreDuration);
        foreach (var game in GameCatalog.All)
        {
            if (publisherAccounts.TryGetResourceRefreshDuration(game.Id, out var refreshDuration))
                AddTiming(timings, "account-refresh", game.Id, refreshDuration);
            if (!sessions.TryGetSnapshot(game.Id, out var snapshot) || snapshot is null) continue;
            AddTiming(timings, "launch", game.Id, snapshot.LastLaunchDetectionDuration);
            AddTiming(timings, "close", game.Id, snapshot.LastCloseDetectionDuration);
        }

        var cache = app.Cache.GetTotals();
        var manifest = launcherBanners.Current;
        return new LauncherDiagnosticsSnapshot(
            typeof(App).Assembly.GetName().Version?.ToString() ?? "dev",
            state.Preferences.FeatureFlags,
            games,
            manifest.Revision,
            manifest.Health.Status,
            cache,
            timings: timings);
    }

    private static void AddTiming(
        ICollection<LauncherDiagnosticTiming> timings,
        string operation,
        string? gameId,
        TimeSpan? duration)
    {
        if (duration is null) return;
        timings.Add(new(
            operation,
            gameId,
            (int)Math.Clamp(Math.Round(duration.Value.TotalMilliseconds), 0, 600_000)));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var value = bytes / 1024d;
        return value < 1024
            ? $"{value:0.0} KB"
            : value / 1024 < 1024
                ? $"{value / 1024:0.0} MB"
                : $"{value / 1024 / 1024:0.0} GB";
    }
}
