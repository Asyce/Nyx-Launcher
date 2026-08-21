using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.State;

namespace Nyx_Desktop_App.ViewModels;

public sealed record LauncherLayoutProfile(
    double RailExtent,
    double IconSize,
    double ContentWidth,
    double DeckHeight,
    double LaunchWidth)
{
    public const double ItemChrome = 2;
    public const double ItemMargin = 0;
    public const double DesignWidth = 1280;
    public const double DesignHeight = 720;

    public static LauncherLayoutProfile Fixed { get; } = new(
        RailExtent: 102,
        IconSize: 82,
        ContentWidth: 620,
        DeckHeight: 172,
        LaunchWidth: 405);

    public double ItemExtent => IconSize + ItemChrome;

    public double ItemCrossExtent => ItemExtent + (ItemMargin * 2);
}

public static class LauncherOpenLayoutGeometry
{
    public const double LaunchButtonHeight = 110;
    public const double LaunchStatusStripHeight = 20;
}

public static class LauncherBackgroundSourceProjection
{
    public static string? From(LauncherState state, string gameId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var custom = state.CustomGames.FirstOrDefault(game =>
            string.Equals(game.Id, gameId, StringComparison.Ordinal));
        if (custom is null) return null;
        return state.Appearance.TryGetValue(gameId, out var appearance)
            ? appearance.BackgroundPath
            : custom.BackgroundPath;
    }
}
public static class PublisherAccountDisplayProjection
{
    public sealed record CompactResourceText(
        string Label,
        string Value,
        string AutomationText)
    {
        public override string ToString() => nameof(CompactResourceText);
    }

    public static int RemainingRecoverySeconds(PublisherResourceSnapshot resource, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var elapsed = Math.Max(0, (int)Math.Floor((now - resource.ObservedAt).TotalSeconds));
        return Math.Max(0, resource.RecoverySeconds - elapsed);
    }

    public static string FormatResource(PublisherResourceSnapshot resource, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var text = $"{resource.ResourceName.ToUpperInvariant()}  {resource.Current}/{resource.Maximum}";
        if (resource.Reserve is { } reserve) text += $"  ·  RESERVE {reserve}";
        var remaining = RemainingRecoverySeconds(resource, now);
        if (remaining > 0)
        {
            var duration = TimeSpan.FromSeconds(remaining);
            var label = duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}H {duration.Minutes}M"
                : $"{Math.Max(1, duration.Minutes)}M";
            text += $"  ·  FULL {label}";
        }
        if (resource.IsStale) text += "  ·  STALE";
        return text;
    }
    public static CompactResourceText FormatCompactResource(
        PublisherResourceSnapshot resource,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var labels = new List<string>
        {
            resource.ResourceName.ToUpperInvariant(),
        };
        var values = new List<string>
        {
            FormattableString.Invariant($"{resource.Current}/{resource.Maximum}"),
        };
        if (resource.Reserve is { } reserve)
        {
            labels.Add("RESERVE");
            values.Add(reserve.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var remaining = RemainingRecoverySeconds(resource, now);
        if (remaining > 0)
        {
            labels.Add("FULL");
            var duration = TimeSpan.FromSeconds(remaining);
            values.Add(duration.TotalHours >= 1
                ? FormattableString.Invariant(
                    $"{(int)duration.TotalHours}H {duration.Minutes}M")
                : FormattableString.Invariant(
                    $"{Math.Max(1, duration.Minutes)}M"));
        }
        if (resource.IsStale) labels.Add("STALE");
        return new(
            string.Join(" · ", labels),
            string.Join(" · ", values),
            FormatResource(resource, now));
    }
}

public sealed record LauncherResourceMetrics(
    string Primary,
    string? Reserve,
    string? Recovery,
    string? Daily,
    string AutomationText)
{
    public override string ToString() => nameof(LauncherResourceMetrics);
}

public static class LauncherResourceMetricsProjection
{
    public static LauncherResourceMetrics FromPublisher(
        PublisherResourceSnapshot resource,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var remaining = PublisherAccountDisplayProjection.RemainingRecoverySeconds(resource, now);
        return new(
            $"{resource.Current}/{resource.Maximum}",
            resource.Reserve?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            remaining > 0 ? FormatDuration(remaining) : null,
            null,
            PublisherAccountDisplayProjection.FormatResource(resource, now));
    }

    public static LauncherResourceMetrics FromWuWa(WuWaAccountStatusSnapshot resource) =>
        new(
            $"{resource.Energy}/{resource.MaxEnergy}",
            resource.StoreEnergy.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FormatDuration(resource.EnergyRecoverTime),
            $"{resource.Liveness}/{resource.LivenessMaxCount}",
            $"Waveplates {resource.Energy}/{resource.MaxEnergy}; "
                + $"Waveplate Crystal {resource.StoreEnergy}; "
                + $"daily activity {resource.Liveness}/{resource.LivenessMaxCount}");

    public static string? FormatDuration(long seconds)
    {
        if (seconds <= 0 || !WuWaAccountStatusRules.IsValidRecoverySeconds(seconds))
            return null;
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        return hours >= 1
            ? FormattableString.Invariant($"{hours}H {minutes}M")
            : FormattableString.Invariant($"{Math.Max(1, minutes)}M");
    }
}
