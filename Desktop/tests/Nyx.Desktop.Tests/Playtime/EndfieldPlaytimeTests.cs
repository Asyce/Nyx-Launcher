using Nyx.Desktop.Core.Playtime;

namespace Nyx.Desktop.Tests.Playtime;

public sealed class EndfieldPlaytimeTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Interval_accepts_only_bounded_utc_values_with_a_known_time_zone()
    {
        var valid = Interval(UtcAt(2026, 8, 29, 10), UtcAt(2026, 8, 29, 11));

        Assert.True(valid.IsValid);
        Assert.False((valid with { EndUtc = valid.StartUtc }).IsValid);
        Assert.False((valid with { EndUtc = valid.StartUtc.AddDays(7).AddTicks(1) }).IsValid);
        Assert.False((valid with { StartUtc = valid.StartUtc.ToOffset(TimeSpan.FromHours(1)) }).IsValid);
        Assert.False((valid with { TimeZoneId = "not-a-time-zone" }).IsValid);
        Assert.Equal(nameof(EndfieldPlaytimeInterval), valid.ToString());
    }

    [Fact]
    public void Storage_normalization_filters_invalid_duplicate_and_overlapping_values()
    {
        var first = Interval(UtcAt(2026, 8, 29, 10), UtcAt(2026, 8, 29, 11));
        var overlap = Interval(UtcAt(2026, 8, 29, 10, 30), UtcAt(2026, 8, 29, 11, 30));
        var second = Interval(UtcAt(2026, 8, 29, 12), UtcAt(2026, 8, 29, 13));

        var normalized = EndfieldPlaytime.LimitForStorage([
            second,
            first,
            first,
            overlap,
            first with { EndUtc = first.StartUtc },
        ]);

        Assert.Equal([first, second], normalized);
    }

    [Fact]
    public void Storage_limit_keeps_the_newest_complete_sessions()
    {
        var first = UtcAt(2026, 1, 1, 0);
        var intervals = Enumerable.Range(0, EndfieldPlaytimeInterval.MaximumStoredIntervals + 2)
            .Select(index => Interval(
                first.AddMinutes(index * 2),
                first.AddMinutes((index * 2) + 1)));

        var limited = EndfieldPlaytime.LimitForStorage(intervals);

        Assert.Equal(EndfieldPlaytimeInterval.MaximumStoredIntervals, limited.Count);
        Assert.Equal(first.AddMinutes(4), limited[0].StartUtc);
    }

    [Fact]
    public void Calculate_counts_exact_duration_bucket_boundaries()
    {
        var intervals = new[]
        {
            Interval(UtcAt(2026, 8, 1, 10), UtcAt(2026, 8, 1, 10, 29)),
            Interval(UtcAt(2026, 8, 2, 10), UtcAt(2026, 8, 2, 10, 30)),
            Interval(UtcAt(2026, 8, 3, 10), UtcAt(2026, 8, 3, 13)),
            Interval(UtcAt(2026, 8, 4, 10), UtcAt(2026, 8, 4, 13, 1)),
        };

        var gameplay = EndfieldPlaytime.Calculate(intervals).Gameplay;

        Assert.Equal(4, gameplay.Sessions);
        Assert.Equal(new EndfieldDurationBuckets(1, 2, 1), gameplay.DurationBuckets);
        Assert.Equal(TimeSpan.FromMinutes(29), gameplay.Shortest);
        Assert.Equal(TimeSpan.FromMinutes(181), gameplay.Longest);
    }

    [Fact]
    public void Calculate_counts_active_days_streaks_and_local_launch_hours()
    {
        var intervals = new[]
        {
            Interval(UtcAt(2026, 8, 1, 10), UtcAt(2026, 8, 1, 10, 30)),
            Interval(UtcAt(2026, 8, 2, 23), UtcAt(2026, 8, 2, 23, 30)),
            Interval(UtcAt(2026, 8, 4, 5), UtcAt(2026, 8, 4, 5, 30)),
        };

        var gameplay = EndfieldPlaytime.Calculate(intervals).Gameplay;

        Assert.Equal(3, gameplay.ActiveDays);
        Assert.Equal(2, gameplay.LongestActiveDayStreak);
        Assert.Equal(1, gameplay.LaunchesByLocalHour[5]);
        Assert.Equal(1, gameplay.LaunchesByLocalHour[10]);
        Assert.Equal(1, gameplay.LaunchesByLocalHour[23]);
        Assert.Equal(24, gameplay.LaunchesByLocalHour.Count);
    }

    [Fact]
    public void Calculate_splits_weekday_month_and_night_at_local_boundaries()
    {
        var interval = Interval(
            UtcAt(2026, 8, 3, 21, 30),
            UtcAt(2026, 8, 4, 6, 30));

        var gameplay = EndfieldPlaytime.Calculate([interval]).Gameplay;

        Assert.Equal(TimeSpan.FromHours(2.5), gameplay.TimeByLocalWeekday[DayOfWeek.Monday]);
        Assert.Equal(TimeSpan.FromHours(6.5), gameplay.TimeByLocalWeekday[DayOfWeek.Tuesday]);
        Assert.Equal(TimeSpan.FromHours(8), gameplay.NightTime);
        Assert.Equal(TimeSpan.FromHours(9), gameplay.TimeByLocalMonth["2026-08"]);
    }

    [Fact]
    public void Calculate_splits_across_midnight_and_calendar_year()
    {
        var gameplay = EndfieldPlaytime.Calculate([
            Interval(UtcAt(2026, 12, 31, 23, 30), UtcAt(2027, 1, 1, 0, 30)),
        ]).Gameplay;

        Assert.Equal(TimeSpan.FromMinutes(30), gameplay.TimeByLocalMonth["2026-12"]);
        Assert.Equal(TimeSpan.FromMinutes(30), gameplay.TimeByLocalMonth["2027-01"]);
        Assert.Equal(TimeSpan.FromHours(1), gameplay.Total);
        Assert.Equal(2, gameplay.ActiveDays);
    }

    [Fact]
    public void Calculate_uses_elapsed_utc_time_across_spring_and_fall_dst()
    {
        var zone = NewYork();
        var spring = Interval(
            UtcAt(2026, 3, 8, 6, 30),
            UtcAt(2026, 3, 8, 7, 30),
            zone);
        var fall = Interval(
            UtcAt(2026, 11, 1, 4, 30),
            UtcAt(2026, 11, 1, 7, 30),
            zone);

        var springStats = EndfieldPlaytime.Calculate([spring]).Gameplay;
        var fallStats = EndfieldPlaytime.Calculate([fall]).Gameplay;

        Assert.Equal(TimeSpan.FromHours(1), springStats.Total);
        Assert.Equal(TimeSpan.FromHours(1), springStats.NightTime);
        Assert.Equal(TimeSpan.FromHours(3), fallStats.Total);
        Assert.Equal(TimeSpan.FromHours(3), fallStats.NightTime);
    }

    private static EndfieldPlaytimeInterval Interval(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeZoneInfo? zone = null) =>
        new(startUtc, endUtc, (zone ?? Utc).Id);

    private static DateTimeOffset UtcAt(
        int year,
        int month,
        int day,
        int hour,
        int minute = 0,
        int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static TimeZoneInfo NewYork()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }
}
