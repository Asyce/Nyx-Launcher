using Nyx.Desktop.Core.Playtime;

namespace Nyx.Desktop.Tests.Playtime;

public sealed class EndfieldPlaytimeTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void ParseRecognizesTheFourExactMarkersAndKeepsStreamsSeparate()
    {
        var result = Parse(
            Utc,
            UtcAt(2026, 8, 29, 12),
            ("08-29 10:00:00.000", "Create game process Endfield.exe"),
            ("08-29 10:05:00.000", "enter main"),
            ("08-29 11:00:00.000", "Child process exits"),
            ("08-29 11:05:00.000", "leave main"));

        Assert.False(result.ChronologyRejected);
        Assert.Equal(2, result.Intervals.Count);
        Assert.Equal(TimeSpan.FromHours(1), result.Intervals.Single(value =>
            value.Kind == EndfieldPlaytimeIntervalKind.Gameplay).Duration);
        Assert.Equal(TimeSpan.FromHours(1), result.Intervals.Single(value =>
            value.Kind == EndfieldPlaytimeIntervalKind.Launcher).Duration);
    }

    [Fact]
    public void GameplayRequiresAnExactEndfieldExecutableToken()
    {
        var result = Parse(
            Utc,
            UtcAt(2026, 8, 29, 12),
            ("08-29 10:00:00.000", "Create game process Endfield.exe.bak"),
            ("08-29 11:00:00.000", "Child process exits"));

        Assert.Empty(result.Intervals);

        var wrongCase = Parse(
            Utc,
            UtcAt(2026, 8, 29, 12),
            ("08-29 10:00:00.000", "Create game process endfield.exe"),
            ("08-29 11:00:00.000", "Child process exits"));

        var caseInsensitiveMatch = Assert.Single(wrongCase.Intervals);
        Assert.Equal(EndfieldPlaytimeIntervalKind.Gameplay, caseInsensitiveMatch.Kind);
        Assert.Equal(TimeSpan.FromHours(1), caseInsensitiveMatch.Duration);
    }

    [Fact]
    public void PairingCountsOrphansRepeatedStartsAndEndOfFilePendingStarts()
    {
        var result = Parse(
            Utc,
            UtcAt(2026, 8, 29, 15),
            ("08-29 10:00:00.000", "Create game process Endfield.exe"),
            ("08-29 10:05:00.000", "Create game process Endfield.exe"),
            ("08-29 11:05:00.000", "Child process exits"),
            ("08-29 12:00:00.000", "Child process exits"),
            ("08-29 13:00:00.000", "enter main"),
            ("08-29 14:00:00.000", "leave main"),
            ("08-29 14:30:00.000", "leave main"),
            ("08-29 15:00:00.000", "enter main"));

        Assert.Equal(2, result.Intervals.Count);
        Assert.Equal(4, result.UnmatchedMarkers);
        Assert.Equal(TimeSpan.FromHours(1), result.Intervals.Single(value =>
            value.Kind == EndfieldPlaytimeIntervalKind.Gameplay).Duration);
        Assert.Equal(TimeSpan.FromHours(1), result.Intervals.Single(value =>
            value.Kind == EndfieldPlaytimeIntervalKind.Launcher).Duration);
    }

    [Fact]
    public void MergeDeduplicatesExactStoredAndScannedIntervals()
    {
        var interval = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 8, 29, 10),
            UtcAt(2026, 8, 29, 11));

        var result = EndfieldPlaytime.Merge([interval], [interval]);

        Assert.Single(result.Intervals);
        Assert.Equal(interval, result.Intervals[0]);
        Assert.Equal(0, result.RejectedOverlaps);
    }

    [Fact]
    public void MergeKeepsStoredBoundariesWhenBothScannedBoundariesAreWithinOneMinute()
    {
        var stored = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 8, 29, 10),
            UtcAt(2026, 8, 29, 11));
        var scanned = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 8, 29, 10, 0, 30),
            UtcAt(2026, 8, 29, 11, 0, 30));

        var result = EndfieldPlaytime.Merge([stored], [scanned]);

        Assert.Single(result.Intervals);
        Assert.Equal(stored, result.Intervals[0]);
    }

    [Fact]
    public void MergeRejectsAPartialOverlapAfterBoundaryNormalization()
    {
        var stored = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 8, 29, 10),
            UtcAt(2026, 8, 29, 11));
        var scanned = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 8, 29, 10, 0, 30),
            UtcAt(2026, 8, 29, 11, 30));

        var result = EndfieldPlaytime.Merge([stored], [scanned]);

        Assert.Single(result.Intervals);
        Assert.Equal(1, result.RejectedOverlaps);
    }

    [Fact]
    public void Storage_limit_keeps_the_newest_sessions_across_both_kinds()
    {
        var first = UtcAt(2026, 1, 1, 0);
        var intervals = Enumerable.Range(0, EndfieldPlaytimeInterval.MaximumStoredIntervals + 2)
            .Select(index => Interval(
                index % 2 == 0
                    ? EndfieldPlaytimeIntervalKind.Gameplay
                    : EndfieldPlaytimeIntervalKind.Launcher,
                first.AddMinutes(index),
                first.AddMinutes(index + 1)))
            .ToArray();

        var limited = EndfieldPlaytime.LimitForStorage(intervals);

        Assert.Equal(EndfieldPlaytimeInterval.MaximumStoredIntervals, limited.Count);
        Assert.Equal(first.AddMinutes(2), limited.Min(static value => value.StartUtc));
        Assert.Contains(limited, static value => value.Kind is EndfieldPlaytimeIntervalKind.Gameplay);
        Assert.Contains(limited, static value => value.Kind is EndfieldPlaytimeIntervalKind.Launcher);
    }

    [Fact]
    public void ParseInfersDecemberToJanuaryRotation()
    {
        var result = Parse(
            Utc,
            UtcAt(2027, 1, 1, 1),
            ("12-31 23:55:00.000", "Create game process Endfield.exe"),
            ("01-01 00:05:00.000", "Child process exits"));

        Assert.False(result.ChronologyRejected);
        var interval = Assert.Single(result.Intervals);
        Assert.Equal(UtcAt(2026, 12, 31, 23, 55), interval.StartUtc);
        Assert.Equal(UtcAt(2027, 1, 1, 0, 5), interval.EndUtc);
    }

    [Fact]
    public void ParseAcceptsLeapDayWhenTheInferredYearIsLeap()
    {
        var result = Parse(
            Utc,
            UtcAt(2028, 3, 1, 1),
            ("02-29 23:55:00.000", "Create game process Endfield.exe"),
            ("03-01 00:05:00.000", "Child process exits"));

        Assert.False(result.ChronologyRejected);
        Assert.Equal(UtcAt(2028, 2, 29, 23, 55), Assert.Single(result.Intervals).StartUtc);
    }

    [Fact]
    public void ParseRejectsImpossibleNonMonotonicAndAmbiguousYearChronology()
    {
        var impossible = Parse(
            Utc,
            UtcAt(2026, 3, 1, 1),
            ("02-30 10:00:00.000", "enter main"),
            ("02-30 11:00:00.000", "leave main"));
        Assert.Empty(impossible.Intervals);
        Assert.True(impossible.RejectedMarkers > 0);

        var nonMonotonic = Parse(
            Utc,
            UtcAt(2026, 8, 29, 12),
            ("08-29 10:00:00.000", "enter main"),
            ("08-29 09:00:00.000", "leave main"));
        Assert.Empty(nonMonotonic.Intervals);
        Assert.True(nonMonotonic.ChronologyRejected);

        var ambiguousYear = Parse(
            Utc,
            UtcAt(2026, 1, 1, 0),
            ("07-01 10:00:00.000", "enter main"),
            ("07-01 11:00:00.000", "leave main"));
        Assert.Empty(ambiguousYear.Intervals);
        Assert.True(ambiguousYear.ChronologyRejected);
    }

    [Fact]
    public void ParseRejectsInvalidAndAmbiguousDstLocalTimes()
    {
        var zone = NewYork();
        var invalid = Parse(
            zone,
            UtcAt(2026, 3, 8, 8),
            ("03-08 02:30:00.000", "enter main"),
            ("03-08 03:30:00.000", "leave main"));
        Assert.Empty(invalid.Intervals);
        Assert.True(invalid.ChronologyRejected);

        var ambiguous = Parse(
            zone,
            UtcAt(2026, 11, 1, 8),
            ("11-01 01:30:00.000", "enter main"),
            ("11-01 02:30:00.000", "leave main"));
        Assert.Empty(ambiguous.Intervals);
        Assert.True(ambiguous.ChronologyRejected);
    }

    [Fact]
    public void ParseRejectsZeroAndIntervalsLongerThanSevenDays()
    {
        var zero = Parse(
            Utc,
            UtcAt(2026, 8, 29, 12),
            ("08-29 10:00:00.000", "enter main"),
            ("08-29 10:00:00.000", "leave main"));
        Assert.Empty(zero.Intervals);
        Assert.True(zero.RejectedMarkers > 0);

        var tooLong = Parse(
            Utc,
            UtcAt(2026, 8, 8, 0, 0, 1),
            ("08-01 00:00:00.000", "enter main"),
            ("08-08 00:00:00.001", "leave main"));
        Assert.Empty(tooLong.Intervals);
        Assert.True(tooLong.RejectedMarkers > 0);
    }

    [Fact]
    public void CalculateSeparatesGameplayAndLauncherTotalsAndClassifiesVisits()
    {
        var gameplayInLauncher = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 8, 29, 10),
            UtcAt(2026, 8, 29, 11));
        var gameplayAtLauncherEnd = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 8, 29, 14),
            UtcAt(2026, 8, 29, 14, 30));
        var firstVisit = Interval(
            EndfieldPlaytimeIntervalKind.Launcher,
            UtcAt(2026, 8, 29, 9),
            UtcAt(2026, 8, 29, 12));
        var launcherOnlyVisit = Interval(
            EndfieldPlaytimeIntervalKind.Launcher,
            UtcAt(2026, 8, 29, 13),
            UtcAt(2026, 8, 29, 14));

        var result = EndfieldPlaytime.Calculate([
            gameplayInLauncher,
            gameplayAtLauncherEnd,
            firstVisit,
            launcherOnlyVisit]);

        Assert.Equal(TimeSpan.FromMinutes(90), result.Gameplay.Total);
        Assert.Equal(2, result.Gameplay.Sessions);
        Assert.Equal(TimeSpan.FromHours(4), result.Launcher.Total);
        Assert.Equal(2, result.Launcher.Visits);
        Assert.Equal(1, result.Launcher.GameLaunchVisits);
        Assert.Equal(1, result.Launcher.LauncherOnlyVisits);
    }

    [Fact]
    public void CalculateCountsExactDurationBucketBoundaries()
    {
        var intervals = new[]
        {
            Interval(EndfieldPlaytimeIntervalKind.Gameplay, UtcAt(2026, 8, 1, 10), UtcAt(2026, 8, 1, 10, 29)),
            Interval(EndfieldPlaytimeIntervalKind.Gameplay, UtcAt(2026, 8, 2, 10), UtcAt(2026, 8, 2, 10, 30)),
            Interval(EndfieldPlaytimeIntervalKind.Gameplay, UtcAt(2026, 8, 3, 10), UtcAt(2026, 8, 3, 13, 0)),
            Interval(EndfieldPlaytimeIntervalKind.Gameplay, UtcAt(2026, 8, 4, 10), UtcAt(2026, 8, 4, 13, 1)),
        };

        var buckets = EndfieldPlaytime.Calculate(intervals).Gameplay.DurationBuckets;

        Assert.Equal(1, buckets.UnderThirtyMinutes);
        Assert.Equal(2, buckets.ThirtyMinutesThroughThreeHours);
        Assert.Equal(1, buckets.OverThreeHours);
    }

    [Fact]
    public void CalculateCountsActiveDaysStreaksAndLocalLaunchHours()
    {
        var intervals = new[]
        {
            Interval(EndfieldPlaytimeIntervalKind.Gameplay, UtcAt(2026, 8, 1, 10), UtcAt(2026, 8, 1, 10, 30)),
            Interval(EndfieldPlaytimeIntervalKind.Gameplay, UtcAt(2026, 8, 2, 23), UtcAt(2026, 8, 2, 23, 30)),
            Interval(EndfieldPlaytimeIntervalKind.Gameplay, UtcAt(2026, 8, 4, 5), UtcAt(2026, 8, 4, 5, 30)),
        };

        var gameplay = EndfieldPlaytime.Calculate(intervals).Gameplay;

        Assert.Equal(3, gameplay.ActiveDays);
        Assert.Equal(2, gameplay.LongestActiveDayStreak);
        Assert.Equal(1, gameplay.LaunchesByLocalHour[10]);
        Assert.Equal(1, gameplay.LaunchesByLocalHour[23]);
        Assert.Equal(1, gameplay.LaunchesByLocalHour[5]);
        Assert.Equal(24, gameplay.LaunchesByLocalHour.Count);
    }

    [Fact]
    public void CalculateSplitsWeekdayMonthAndNightDurationsAtLocalBoundaries()
    {
        var interval = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 8, 3, 21, 30),
            UtcAt(2026, 8, 4, 6, 30));

        var gameplay = EndfieldPlaytime.Calculate([interval]).Gameplay;

        Assert.Equal(TimeSpan.FromHours(2.5), gameplay.TimeByLocalWeekday[DayOfWeek.Monday]);
        Assert.Equal(TimeSpan.FromHours(6.5), gameplay.TimeByLocalWeekday[DayOfWeek.Tuesday]);
        Assert.Equal(TimeSpan.FromHours(8), gameplay.NightTime);
        Assert.Equal(TimeSpan.FromHours(9), gameplay.TimeByLocalMonth["2026-08"]);
    }

    [Fact]
    public void CalculateSplitsAcrossMidnightAndCalendarYear()
    {
        var interval = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 12, 31, 23, 30),
            UtcAt(2027, 1, 1, 0, 30));

        var gameplay = EndfieldPlaytime.Calculate([interval]).Gameplay;

        Assert.Equal(TimeSpan.FromMinutes(30), gameplay.TimeByLocalMonth["2026-12"]);
        Assert.Equal(TimeSpan.FromMinutes(30), gameplay.TimeByLocalMonth["2027-01"]);
        Assert.Equal(TimeSpan.FromHours(1), gameplay.Total);
    }

    [Fact]
    public void CalculateUsesElapsedUtcTimeAcrossSpringAndFallDst()
    {
        var zone = NewYork();
        var spring = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            UtcAt(2026, 3, 8, 6, 30),
            UtcAt(2026, 3, 8, 7, 30),
            zone);
        var fall = Interval(
            EndfieldPlaytimeIntervalKind.Gameplay,
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

    [Fact]
    public void PublicToStringValuesDoNotContainRawLinesOrPaths()
    {
        const string raw = "Create game process Endfield.exe C:\\Users\\cedri\\private.log";
        var result = Parse(
            Utc,
            UtcAt(2026, 8, 29, 12),
            ("08-29 10:00:00.000", raw),
            ("08-29 11:00:00.000", "Child process exits"));

        Assert.DoesNotContain(raw, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private.log", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(raw, Assert.Single(result.Intervals).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(raw, EndfieldPlaytime.Merge([], []).ToString(), StringComparison.Ordinal);
    }

    private static EndfieldPlaytimeParseResult Parse(
        TimeZoneInfo zone,
        DateTimeOffset lastWriteUtc,
        params (string Timestamp, string Message)[] entries) =>
        EndfieldPlaytime.ParseFile(
            entries.Select(entry => $"[{entry.Timestamp}] {entry.Message}"),
            lastWriteUtc,
            zone);

    private static EndfieldPlaytimeInterval Interval(
        EndfieldPlaytimeIntervalKind kind,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeZoneInfo? zone = null) =>
        new(kind, startUtc, endUtc, (zone ?? Utc).Id);

    private static DateTimeOffset UtcAt(
        int year,
        int month,
        int day,
        int hour,
        int minute = 0,
        int second = 0,
        int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, TimeSpan.Zero);

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
