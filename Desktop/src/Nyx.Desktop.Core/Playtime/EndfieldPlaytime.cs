using System.Collections.ObjectModel;

namespace Nyx.Desktop.Core.Playtime;

public enum EndfieldPlaytimeIntervalKind
{
    Gameplay,
    Launcher,
}

public sealed record EndfieldPlaytimeInterval(
    EndfieldPlaytimeIntervalKind Kind,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string TimeZoneId)
{
    public const int MaximumStoredIntervals = 50_000;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(7);

    public TimeSpan Duration => EndUtc - StartUtc;

    public bool IsValid =>
        Enum.IsDefined(Kind)
        && StartUtc.Offset == TimeSpan.Zero
        && EndUtc.Offset == TimeSpan.Zero
        && EndUtc > StartUtc
        && Duration <= MaximumDuration
        && EndfieldPlaytime.IsKnownTimeZone(TimeZoneId);

    public override string ToString() => nameof(EndfieldPlaytimeInterval);
}

public sealed record EndfieldPlaytimeParseResult(
    IReadOnlyList<EndfieldPlaytimeInterval> Intervals,
    int UnmatchedMarkers,
    int RejectedMarkers,
    bool ChronologyRejected,
    bool Capped = false)
{
    public override string ToString() => nameof(EndfieldPlaytimeParseResult);
}

public sealed record EndfieldPlaytimeMergeResult(
    IReadOnlyList<EndfieldPlaytimeInterval> Intervals,
    int RejectedOverlaps)
{
    public override string ToString() => nameof(EndfieldPlaytimeMergeResult);
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

public sealed record EndfieldLauncherStatistics(
    TimeSpan Total,
    int Visits,
    int GameLaunchVisits,
    int LauncherOnlyVisits);

public sealed record EndfieldPlaytimeStatistics(
    EndfieldGameplayStatistics Gameplay,
    EndfieldLauncherStatistics Launcher);

public static class EndfieldPlaytime
{
    private static readonly TimeSpan BoundaryPreference = TimeSpan.FromSeconds(60);

    public static EndfieldPlaytimeParseResult ParseFile(
        IEnumerable<string> lines,
        DateTimeOffset lastWriteUtc,
        TimeZoneInfo timeZone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (lastWriteUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("The file timestamp must be UTC.", nameof(lastWriteUtc));

        var markers = new List<Marker>();
        var rejected = 0;
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null) continue;
            var markerKind = ClassifyMarker(line);
            if (markerKind is null) continue;
            if (!TryReadTimestamp(line, out var stamp))
            {
                rejected++;
                continue;
            }
            markers.Add(new(stamp, markerKind.Value));
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (markers.Count == 0)
            return new(Array.Empty<EndfieldPlaytimeInterval>(), 0, rejected, false);

        if (!TryResolveChronology(markers, lastWriteUtc, timeZone, cancellationToken, out var resolved))
            return new(Array.Empty<EndfieldPlaytimeInterval>(), 0, rejected + markers.Count, true);

        var intervals = new HashSet<EndfieldPlaytimeInterval>();
        DateTimeOffset? gameplayStart = null;
        DateTimeOffset? launcherStart = null;
        var unmatched = 0;
        var capped = false;
        foreach (var marker in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (marker.Kind)
            {
                case MarkerKind.GameplayStart:
                    if (gameplayStart is not null) unmatched++;
                    gameplayStart = marker.Instant;
                    break;
                case MarkerKind.GameplayEnd:
                    Complete(EndfieldPlaytimeIntervalKind.Gameplay, ref gameplayStart, marker.Instant);
                    break;
                case MarkerKind.LauncherStart:
                    if (launcherStart is not null) unmatched++;
                    launcherStart = marker.Instant;
                    break;
                case MarkerKind.LauncherEnd:
                    Complete(EndfieldPlaytimeIntervalKind.Launcher, ref launcherStart, marker.Instant);
                    break;
            }
        }

        if (gameplayStart is not null) unmatched++;
        if (launcherStart is not null) unmatched++;
        return new(Sort(intervals), unmatched, rejected, false, capped);

        void Complete(
            EndfieldPlaytimeIntervalKind kind,
            ref DateTimeOffset? pending,
            DateTimeOffset end)
        {
            if (pending is not { } start)
            {
                unmatched++;
                return;
            }
            pending = null;
            var interval = new EndfieldPlaytimeInterval(kind, start, end, timeZone.Id);
            if (!interval.IsValid)
            {
                rejected++;
                return;
            }
            if (intervals.Contains(interval)) return;
            if (intervals.Count == EndfieldPlaytimeInterval.MaximumStoredIntervals)
            {
                capped = true;
                return;
            }
            intervals.Add(interval);
        }
    }

    public static EndfieldPlaytimeMergeResult Merge(
        IEnumerable<EndfieldPlaytimeInterval> storedPreferred,
        IEnumerable<EndfieldPlaytimeInterval> scanned,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storedPreferred);
        ArgumentNullException.ThrowIfNull(scanned);

        var rejected = 0;
        var preferred = Prepare(storedPreferred, isPreferred: true);
        var normalizedPreferred = new List<MergeCandidate>(preferred.Count);
        foreach (var candidate in preferred)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (normalizedPreferred.Count > 0
                && Overlaps(normalizedPreferred[^1].Interval, candidate.Interval))
            {
                rejected++;
                continue;
            }
            normalizedPreferred.Add(candidate);
        }

        var gameplayStarts = Boundaries(normalizedPreferred, EndfieldPlaytimeIntervalKind.Gameplay, starts: true, cancellationToken);
        var gameplayEnds = Boundaries(normalizedPreferred, EndfieldPlaytimeIntervalKind.Gameplay, starts: false, cancellationToken);
        var launcherStarts = Boundaries(normalizedPreferred, EndfieldPlaytimeIntervalKind.Launcher, starts: true, cancellationToken);
        var launcherEnds = Boundaries(normalizedPreferred, EndfieldPlaytimeIntervalKind.Launcher, starts: false, cancellationToken);
        var normalizedScanned = new List<MergeCandidate>();
        foreach (var candidate in Prepare(scanned, isPreferred: false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var starts = candidate.Interval.Kind is EndfieldPlaytimeIntervalKind.Gameplay
                ? gameplayStarts
                : launcherStarts;
            var ends = candidate.Interval.Kind is EndfieldPlaytimeIntervalKind.Gameplay
                ? gameplayEnds
                : launcherEnds;
            var normalized = candidate.Interval with
            {
                StartUtc = NearestUniqueBoundary(candidate.Interval.StartUtc, starts)
                    ?? candidate.Interval.StartUtc,
                EndUtc = NearestUniqueBoundary(candidate.Interval.EndUtc, ends)
                    ?? candidate.Interval.EndUtc,
            };
            if (!normalized.IsValid)
            {
                rejected++;
                continue;
            }
            normalizedScanned.Add(candidate with { Interval = normalized });
        }

        var combined = new List<MergeCandidate>(normalizedPreferred.Count + normalizedScanned.Count);
        combined.AddRange(normalizedPreferred);
        combined.AddRange(normalizedScanned);
        combined.Sort(CompareCandidates);
        cancellationToken.ThrowIfCancellationRequested();

        var merged = new List<MergeCandidate>(combined.Count);
        foreach (var candidate in combined)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (merged.Count == 0 || merged[^1].Interval.Kind != candidate.Interval.Kind)
            {
                merged.Add(candidate);
                continue;
            }

            var previous = merged[^1];
            if (Near(previous.Interval.StartUtc, candidate.Interval.StartUtc)
                && Near(previous.Interval.EndUtc, candidate.Interval.EndUtc))
            {
                if (candidate.IsPreferred && !previous.IsPreferred) merged[^1] = candidate;
                continue;
            }
            if (!Overlaps(previous.Interval, candidate.Interval))
            {
                merged.Add(candidate);
                continue;
            }
            if (candidate.IsPreferred && !previous.IsPreferred) merged[^1] = candidate;
            else rejected++;
        }

        return new(merged.Select(static value => value.Interval).ToArray(), rejected);

        List<MergeCandidate> Prepare(
            IEnumerable<EndfieldPlaytimeInterval> source,
            bool isPreferred)
        {
            var unique = new HashSet<EndfieldPlaytimeInterval>();
            foreach (var interval in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (interval.IsValid) unique.Add(interval);
            }
            var prepared = unique.Select(value => new MergeCandidate(value, isPreferred)).ToList();
            prepared.Sort(CompareCandidates);
            cancellationToken.ThrowIfCancellationRequested();
            return prepared;
        }
    }

    public static EndfieldPlaytimeStatistics Calculate(
        IEnumerable<EndfieldPlaytimeInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var valid = Sort(intervals.Where(static interval => interval.IsValid));
        var gameplay = valid.Where(static interval => interval.Kind is EndfieldPlaytimeIntervalKind.Gameplay).ToArray();
        var launcher = valid.Where(static interval => interval.Kind is EndfieldPlaytimeIntervalKind.Launcher).ToArray();

        var total = gameplay.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration);
        var dates = new HashSet<DateOnly>();
        var weekday = Enum.GetValues<DayOfWeek>().ToDictionary(static day => day, static _ => TimeSpan.Zero);
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
                months[month] = months.TryGetValue(month, out var current) ? current + duration : duration;
                if (local.Hour >= 22 || local.Hour < 6) night += duration;
            }
        }

        var orderedDates = dates.Order().ToArray();
        var streak = 0;
        var runningStreak = 0;
        DateOnly? prior = null;
        foreach (var date in orderedDates)
        {
            runningStreak = prior is { } previous && date.DayNumber == previous.DayNumber + 1
                ? runningStreak + 1
                : 1;
            streak = Math.Max(streak, runningStreak);
            prior = date;
        }

        var shortest = gameplay.Length == 0 ? TimeSpan.Zero : gameplay.Min(static value => value.Duration);
        var longestInterval = gameplay.OrderByDescending(static value => value.Duration).FirstOrDefault();
        var buckets = new EndfieldDurationBuckets(
            gameplay.Count(static value => value.Duration < TimeSpan.FromMinutes(30)),
            gameplay.Count(static value => value.Duration >= TimeSpan.FromMinutes(30)
                && value.Duration <= TimeSpan.FromHours(3)),
            gameplay.Count(static value => value.Duration > TimeSpan.FromHours(3)));
        var gameLaunchVisits = 0;
        var gameplayIndex = 0;
        foreach (var visit in launcher)
        {
            while (gameplayIndex < gameplay.Length
                && gameplay[gameplayIndex].StartUtc < visit.StartUtc) gameplayIndex++;
            if (gameplayIndex < gameplay.Length
                && gameplay[gameplayIndex].StartUtc < visit.EndUtc) gameLaunchVisits++;
        }
        var launcherTotal = launcher.Aggregate(TimeSpan.Zero, static (sum, item) => sum + item.Duration);

        return new(
            new(
                total,
                gameplay.Length,
                dates.Count,
                gameplay.Length == 0 ? TimeSpan.Zero : total / gameplay.Length,
                dates.Count == 0 ? TimeSpan.Zero : total / dates.Count,
                shortest,
                longestInterval?.Duration ?? TimeSpan.Zero,
                streak,
                buckets,
                Array.AsReadOnly(launchHours),
                new ReadOnlyDictionary<DayOfWeek, TimeSpan>(weekday),
                new ReadOnlyDictionary<string, TimeSpan>(months),
                night),
            new(
                launcherTotal,
                launcher.Length,
                gameLaunchVisits,
                launcher.Length - gameLaunchVisits));
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

    private static MarkerKind? ClassifyMarker(string line)
    {
        var gameplayStart = line.Contains("Create game process", StringComparison.OrdinalIgnoreCase)
            && ContainsExactFileName(line, "Endfield.exe");
        var gameplayEnd = line.Contains("Child process exits", StringComparison.Ordinal);
        var launcherStart = line.Contains("enter main", StringComparison.Ordinal);
        var launcherEnd = line.Contains("leave main", StringComparison.Ordinal);
        var count = (gameplayStart ? 1 : 0) + (gameplayEnd ? 1 : 0)
            + (launcherStart ? 1 : 0) + (launcherEnd ? 1 : 0);
        if (count != 1) return null;
        if (gameplayStart) return MarkerKind.GameplayStart;
        if (gameplayEnd) return MarkerKind.GameplayEnd;
        if (launcherStart) return MarkerKind.LauncherStart;
        return MarkerKind.LauncherEnd;
    }

    private static bool ContainsExactFileName(string value, string fileName)
    {
        var start = 0;
        while ((start = value.IndexOf(fileName, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = start == 0 ? '\0' : value[start - 1];
            var afterIndex = start + fileName.Length;
            var after = afterIndex == value.Length ? '\0' : value[afterIndex];
            if (!IsFileNameCharacter(before) && !IsFileNameCharacter(after)) return true;
            start++;
        }
        return false;
    }

    private static bool IsFileNameCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '_' or '-' or '.';

    private static bool TryReadTimestamp(string line, out Stamp stamp)
    {
        stamp = default;
        if (line.Length < 20 || line[0] != '[' || line[19] != ']') return false;
        var value = line.AsSpan(1, 18);
        if (value[2] != '-' || value[5] != ' ' || value[8] != ':'
            || value[11] != ':' || value[14] != '.') return false;
        if (!TryDigits(value[0..2], out var month)
            || !TryDigits(value[3..5], out var day)
            || !TryDigits(value[6..8], out var hour)
            || !TryDigits(value[9..11], out var minute)
            || !TryDigits(value[12..14], out var second)
            || !TryDigits(value[15..18], out var millisecond)
            || month is < 1 or > 12
            || day is < 1 or > 31
            || hour is < 0 or > 23
            || minute is < 0 or > 59
            || second is < 0 or > 59) return false;
        stamp = new(month, day, hour, minute, second, millisecond);
        return true;
    }

    private static bool TryDigits(ReadOnlySpan<char> value, out int result)
    {
        result = 0;
        foreach (var character in value)
        {
            if (character is < '0' or > '9') return false;
            result = (result * 10) + (character - '0');
        }
        return true;
    }

    private static bool TryResolveChronology(
        IReadOnlyList<Marker> markers,
        DateTimeOffset lastWriteUtc,
        TimeZoneInfo zone,
        CancellationToken cancellationToken,
        out IReadOnlyList<ResolvedMarker> result)
    {
        var localWriteYear = TimeZoneInfo.ConvertTime(lastWriteUtc, zone).Year;
        var candidates = new List<IReadOnlyList<ResolvedMarker>>();
        foreach (var firstYear in new[] { localWriteYear - 1, localWriteYear })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveFromYear(markers, firstYear, zone, cancellationToken, out var resolved)) continue;
            var last = resolved[^1].Instant;
            if (last > lastWriteUtc + TimeSpan.FromDays(1)
                || last < lastWriteUtc - TimeSpan.FromDays(31)) continue;
            candidates.Add(resolved);
        }

        if (candidates.Count != 1)
        {
            result = Array.Empty<ResolvedMarker>();
            return false;
        }
        result = candidates[0];
        return true;
    }

    private static bool TryResolveFromYear(
        IReadOnlyList<Marker> markers,
        int firstYear,
        TimeZoneInfo zone,
        CancellationToken cancellationToken,
        out IReadOnlyList<ResolvedMarker> result)
    {
        var resolved = new List<ResolvedMarker>(markers.Count);
        var year = firstYear;
        var rollovers = 0;
        Stamp? previousStamp = null;
        DateTimeOffset? previous = null;
        foreach (var marker in markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previousStamp is { } prior && CompareStamp(marker.Stamp, prior) < 0)
            {
                if (prior.Month != 12 || marker.Stamp.Month != 1 || ++rollovers > 1)
                {
                    result = Array.Empty<ResolvedMarker>();
                    return false;
                }
                year++;
            }
            DateTime local;
            try
            {
                local = new(
                    year,
                    marker.Stamp.Month,
                    marker.Stamp.Day,
                    marker.Stamp.Hour,
                    marker.Stamp.Minute,
                    marker.Stamp.Second,
                    marker.Stamp.Millisecond,
                    DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                result = Array.Empty<ResolvedMarker>();
                return false;
            }
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
            {
                result = Array.Empty<ResolvedMarker>();
                return false;
            }
            var instant = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            if (previous is { } earlier && instant < earlier)
            {
                result = Array.Empty<ResolvedMarker>();
                return false;
            }
            resolved.Add(new(instant, marker.Kind));
            previousStamp = marker.Stamp;
            previous = instant;
        }
        result = resolved;
        return true;
    }

    private static int CompareStamp(Stamp left, Stamp right)
    {
        var leftValue = (left.Month, left.Day, left.Hour, left.Minute, left.Second, left.Millisecond);
        var rightValue = (right.Month, right.Day, right.Hour, right.Minute, right.Second, right.Millisecond);
        return leftValue.CompareTo(rightValue);
    }

    private static IReadOnlyList<EndfieldPlaytimeInterval> Sort(
        IEnumerable<EndfieldPlaytimeInterval> intervals) =>
        intervals
            .OrderBy(static value => value.Kind)
            .ThenBy(static value => value.StartUtc)
            .ThenBy(static value => value.EndUtc)
            .ThenBy(static value => value.TimeZoneId, StringComparer.Ordinal)
            .ToArray();

    private static bool Near(DateTimeOffset left, DateTimeOffset right) =>
        (left - right).Duration() <= BoundaryPreference;

    internal static IReadOnlyList<EndfieldPlaytimeInterval> LimitForStorage(
        IEnumerable<EndfieldPlaytimeInterval> intervals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var values = new List<EndfieldPlaytimeInterval>();
        foreach (var interval in intervals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Add(interval);
        }
        if (values.Count > EndfieldPlaytimeInterval.MaximumStoredIntervals)
        {
            values.Sort(static (left, right) =>
            {
                var compared = right.StartUtc.CompareTo(left.StartUtc);
                if (compared != 0) return compared;
                compared = right.EndUtc.CompareTo(left.EndUtc);
                if (compared != 0) return compared;
                compared = left.Kind.CompareTo(right.Kind);
                return compared != 0
                    ? compared
                    : StringComparer.Ordinal.Compare(left.TimeZoneId, right.TimeZoneId);
            });
            values.RemoveRange(
                EndfieldPlaytimeInterval.MaximumStoredIntervals,
                values.Count - EndfieldPlaytimeInterval.MaximumStoredIntervals);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Sort(values);
    }

    private static bool Overlaps(
        EndfieldPlaytimeInterval left,
        EndfieldPlaytimeInterval right) =>
        left.Kind == right.Kind
        && left.StartUtc < right.EndUtc
        && right.StartUtc < left.EndUtc;

    private static DateTimeOffset[] Boundaries(
        IEnumerable<MergeCandidate> candidates,
        EndfieldPlaytimeIntervalKind kind,
        bool starts,
        CancellationToken cancellationToken)
    {
        var values = new HashSet<DateTimeOffset>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Interval.Kind == kind)
                values.Add(starts ? candidate.Interval.StartUtc : candidate.Interval.EndUtc);
        }
        var result = values.ToArray();
        Array.Sort(result);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static int CompareCandidates(MergeCandidate left, MergeCandidate right)
    {
        var compared = left.Interval.Kind.CompareTo(right.Interval.Kind);
        if (compared != 0) return compared;
        compared = left.Interval.StartUtc.CompareTo(right.Interval.StartUtc);
        if (compared != 0) return compared;
        compared = left.Interval.EndUtc.CompareTo(right.Interval.EndUtc);
        if (compared != 0) return compared;
        compared = right.IsPreferred.CompareTo(left.IsPreferred);
        return compared != 0
            ? compared
            : StringComparer.Ordinal.Compare(left.Interval.TimeZoneId, right.Interval.TimeZoneId);
    }

    private static DateTimeOffset? NearestUniqueBoundary(
        DateTimeOffset target,
        DateTimeOffset[] boundaries)
    {
        var index = Array.BinarySearch(boundaries, target);
        if (index >= 0) return boundaries[index];
        index = ~index;
        DateTimeOffset? nearest = null;
        TimeSpan? distance = null;
        Consider(index - 1);
        Consider(index);
        return nearest;

        void Consider(int candidateIndex)
        {
            if ((uint)candidateIndex >= (uint)boundaries.Length) return;
            var candidateDistance = (boundaries[candidateIndex] - target).Duration();
            if (candidateDistance > BoundaryPreference) return;
            if (distance is null || candidateDistance < distance)
            {
                nearest = boundaries[candidateIndex];
                distance = candidateDistance;
            }
            else if (candidateDistance == distance)
            {
                nearest = null;
            }
        }
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> SplitAtLocalBoundaries(
        EndfieldPlaytimeInterval interval,
        TimeZoneInfo zone)
    {
        var points = new SortedSet<DateTimeOffset> { interval.StartUtc, interval.EndUtc };
        var localStart = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(interval.StartUtc, zone).DateTime).AddDays(-1);
        var localEnd = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(interval.EndUtc.AddTicks(-1), zone).DateTime).AddDays(1);
        for (var date = localStart; date <= localEnd; date = date.AddDays(1))
        {
            foreach (var hour in new[] { 0, 6, 22 })
            {
                var local = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
                foreach (var instant in LocalBoundaryInstants(local, zone))
                    if (instant > interval.StartUtc && instant < interval.EndUtc) points.Add(instant);
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
                    if (zone.GetUtcOffset(new DateTimeOffset(middle, TimeSpan.Zero)) == priorOffset) low = middle;
                    else high = middle;
                }
                var transition = new DateTimeOffset(high, TimeSpan.Zero);
                if (transition > interval.StartUtc && transition < interval.EndUtc) points.Add(transition);
            }
            cursor = next;
            priorOffset = nextOffset;
        }

        var ordered = points.ToArray();
        for (var index = 0; index < ordered.Length - 1; index++)
            yield return (ordered[index], ordered[index + 1]);
    }

    private static IEnumerable<DateTimeOffset> LocalBoundaryInstants(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local)) yield break;
        if (zone.IsAmbiguousTime(local))
        {
            foreach (var offset in zone.GetAmbiguousTimeOffsets(local))
                yield return new DateTimeOffset(local, offset).ToUniversalTime();
            yield break;
        }
        yield return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    private enum MarkerKind
    {
        GameplayStart,
        GameplayEnd,
        LauncherStart,
        LauncherEnd,
    }

    private readonly record struct Stamp(
        int Month,
        int Day,
        int Hour,
        int Minute,
        int Second,
        int Millisecond);

    private readonly record struct Marker(Stamp Stamp, MarkerKind Kind);

    private readonly record struct ResolvedMarker(DateTimeOffset Instant, MarkerKind Kind);

    private readonly record struct MergeCandidate(
        EndfieldPlaytimeInterval Interval,
        bool IsPreferred);
}
