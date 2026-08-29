using System.Collections.ObjectModel;

namespace Nyx.Desktop.Core.Playtime;

public sealed record EndfieldPlaytimeInterval(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string TimeZoneId)
{
    public const int MaximumStoredIntervals = 50_000;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(7);

    public TimeSpan Duration => EndUtc - StartUtc;

    public bool IsValid =>
        StartUtc.Offset == TimeSpan.Zero
        && EndUtc.Offset == TimeSpan.Zero
        && EndUtc > StartUtc
        && Duration <= MaximumDuration
        && EndfieldPlaytime.IsKnownTimeZone(TimeZoneId);

    public override string ToString() => nameof(EndfieldPlaytimeInterval);
}

public sealed record EndfieldDurationBuckets(
    int UnderThirtyMinutes,
    int ThirtyMinutesThroughThreeHours,
    int OverThreeHours);

public sealed record EndfieldGameplayStatistics(
    TimeSpan Total,
    int Sessions,
    int ActiveDays,
    TimeSpan AverageSession,
    TimeSpan AverageActiveDay,
    TimeSpan Shortest,
    TimeSpan Longest,
    int LongestActiveDayStreak,
    EndfieldDurationBuckets DurationBuckets,
    IReadOnlyList<int> LaunchesByLocalHour,
    IReadOnlyDictionary<DayOfWeek, TimeSpan> TimeByLocalWeekday,
    IReadOnlyDictionary<string, TimeSpan> TimeByLocalMonth,
    TimeSpan NightTime);

public sealed record EndfieldPlaytimeStatistics(EndfieldGameplayStatistics Gameplay);

public static class EndfieldPlaytime
{
    public static EndfieldPlaytimeStatistics Calculate(
        IEnumerable<EndfieldPlaytimeInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var gameplay = LimitForStorage(intervals).ToArray();
        var total = gameplay.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration);
        var dates = new HashSet<DateOnly>();
        var weekday = Enum.GetValues<DayOfWeek>()
            .ToDictionary(static day => day, static _ => TimeSpan.Zero);
        var months = new SortedDictionary<string, TimeSpan>(StringComparer.Ordinal);
        var launchHours = new int[24];
        var night = TimeSpan.Zero;

        foreach (var interval in gameplay)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(interval.TimeZoneId);
            launchHours[TimeZoneInfo.ConvertTime(interval.StartUtc, zone).Hour]++;
            foreach (var segment in SplitAtLocalBoundaries(interval, zone))
            {
                var local = TimeZoneInfo.ConvertTime(segment.Start, zone);
                var duration = segment.End - segment.Start;
                var date = DateOnly.FromDateTime(local.DateTime);
                dates.Add(date);
                weekday[local.DayOfWeek] += duration;
                var month = $"{local.Year:D4}-{local.Month:D2}";
                months[month] = months.TryGetValue(month, out var current)
                    ? current + duration
                    : duration;
                if (local.Hour >= 22 || local.Hour < 6) night += duration;
            }
        }

        var longestStreak = 0;
        var runningStreak = 0;
        DateOnly? prior = null;
        foreach (var date in dates.Order())
        {
            runningStreak = prior is { } previous && date.DayNumber == previous.DayNumber + 1
                ? runningStreak + 1
                : 1;
            longestStreak = Math.Max(longestStreak, runningStreak);
            prior = date;
        }

        var shortest = gameplay.Length == 0
            ? TimeSpan.Zero
            : gameplay.Min(static value => value.Duration);
        var longest = gameplay.Length == 0
            ? TimeSpan.Zero
            : gameplay.Max(static value => value.Duration);
        var buckets = new EndfieldDurationBuckets(
            gameplay.Count(static value => value.Duration < TimeSpan.FromMinutes(30)),
            gameplay.Count(static value => value.Duration >= TimeSpan.FromMinutes(30)
                && value.Duration <= TimeSpan.FromHours(3)),
            gameplay.Count(static value => value.Duration > TimeSpan.FromHours(3)));

        return new(new(
            total,
            gameplay.Length,
            dates.Count,
            gameplay.Length == 0 ? TimeSpan.Zero : total / gameplay.Length,
            dates.Count == 0 ? TimeSpan.Zero : total / dates.Count,
            shortest,
            longest,
            longestStreak,
            buckets,
            Array.AsReadOnly(launchHours),
            new ReadOnlyDictionary<DayOfWeek, TimeSpan>(weekday),
            new ReadOnlyDictionary<string, TimeSpan>(months),
            night));
    }

    public static bool IsKnownTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) return false;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<EndfieldPlaytimeInterval> LimitForStorage(
        IEnumerable<EndfieldPlaytimeInterval> intervals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var unique = new HashSet<EndfieldPlaytimeInterval>();
        foreach (var interval in intervals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interval.IsValid) unique.Add(interval);
        }

        var ordered = unique
            .OrderBy(static value => value.StartUtc)
            .ThenBy(static value => value.EndUtc)
            .ThenBy(static value => value.TimeZoneId, StringComparer.Ordinal)
            .ToArray();
        var normalized = new List<EndfieldPlaytimeInterval>(ordered.Length);
        foreach (var interval in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (normalized.Count == 0 || normalized[^1].EndUtc <= interval.StartUtc)
                normalized.Add(interval);
        }

        if (normalized.Count > EndfieldPlaytimeInterval.MaximumStoredIntervals)
        {
            normalized.RemoveRange(
                0,
                normalized.Count - EndfieldPlaytimeInterval.MaximumStoredIntervals);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return normalized.ToArray();
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> SplitAtLocalBoundaries(
        EndfieldPlaytimeInterval interval,
        TimeZoneInfo zone)
    {
        var points = new SortedSet<DateTimeOffset> { interval.StartUtc, interval.EndUtc };
        var localStart = DateOnly
            .FromDateTime(TimeZoneInfo.ConvertTime(interval.StartUtc, zone).DateTime)
            .AddDays(-1);
        var localEnd = DateOnly
            .FromDateTime(TimeZoneInfo.ConvertTime(interval.EndUtc.AddTicks(-1), zone).DateTime)
            .AddDays(1);
        for (var date = localStart; date <= localEnd; date = date.AddDays(1))
        {
            foreach (var hour in new[] { 0, 6, 22 })
            {
                var local = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
                foreach (var instant in LocalBoundaryInstants(local, zone))
                {
                    if (instant > interval.StartUtc && instant < interval.EndUtc)
                        points.Add(instant);
                }
            }
        }

        var cursor = new DateTimeOffset(
            interval.StartUtc.UtcDateTime.Date.AddHours(interval.StartUtc.UtcDateTime.Hour),
            TimeSpan.Zero);
        var priorOffset = zone.GetUtcOffset(cursor);
        while (cursor < interval.EndUtc)
        {
            var next = cursor.AddHours(1);
            var nextOffset = zone.GetUtcOffset(next);
            if (nextOffset != priorOffset)
            {
                var low = cursor.UtcTicks;
                var high = next.UtcTicks;
                while (high - low > 1)
                {
                    var middle = low + ((high - low) / 2);
                    if (zone.GetUtcOffset(new DateTimeOffset(middle, TimeSpan.Zero)) == priorOffset)
                        low = middle;
                    else
                        high = middle;
                }
                var transition = new DateTimeOffset(high, TimeSpan.Zero);
                if (transition > interval.StartUtc && transition < interval.EndUtc)
                    points.Add(transition);
            }
            cursor = next;
            priorOffset = nextOffset;
        }

        var ordered = points.ToArray();
        for (var index = 0; index < ordered.Length - 1; index++)
            yield return (ordered[index], ordered[index + 1]);
    }

    private static IEnumerable<DateTimeOffset> LocalBoundaryInstants(
        DateTime local,
        TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local)) yield break;
        if (zone.IsAmbiguousTime(local))
        {
            foreach (var offset in zone.GetAmbiguousTimeOffsets(local))
                yield return new DateTimeOffset(local, offset).ToUniversalTime();
            yield break;
        }
        yield return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(local, zone),
            TimeSpan.Zero);
    }
}
