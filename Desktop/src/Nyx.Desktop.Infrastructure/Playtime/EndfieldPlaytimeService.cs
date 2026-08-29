using Nyx.Desktop.Core.Playtime;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Sessions;
using NyxPlaytime = Nyx.Desktop.Core.Playtime.EndfieldPlaytime;

namespace Nyx.Desktop.Infrastructure.Playtime;

public enum EndfieldPlaytimeScanStatus
{
    NotScanned,
    Normal,
    Empty,
    Capped,
    Corrupt,
}

public sealed record EndfieldPlaytimeWarnings(
    int UnmatchedMarkers,
    int RejectedMarkers,
    int RejectedOverlaps,
    int UnreadableFiles)
{
    public int Total => UnmatchedMarkers + RejectedMarkers + RejectedOverlaps + UnreadableFiles;

    public override string ToString() => nameof(EndfieldPlaytimeWarnings);
}

public sealed record EndfieldPlaytimeSnapshot(
    EndfieldPlaytimeScanStatus ScanStatus,
    EndfieldPlaytimeStatistics Statistics,
    EndfieldPlaytimeWarnings Warnings,
    int ScannedFiles,
    bool IsScanning,
    bool IsRunning,
    bool HasPendingSession,
    bool SaveFailed)
{
    public override string ToString() => nameof(EndfieldPlaytimeSnapshot);
}

internal sealed record EndfieldPlaytimeScanLimits(
    int MaximumFiles,
    long MaximumBytes,
    long MaximumLines,
    TimeSpan MaximumTime)
{
    public static EndfieldPlaytimeScanLimits Default { get; } = new(
        32,
        64L * 1024 * 1024,
        1_000_000,
        TimeSpan.FromSeconds(10));
}

public sealed class EndfieldPlaytimeService : IDisposable
{
    private readonly object sync = new();
    private readonly Func<EndfieldPlaytimeState, bool> persist;
    private readonly GameSessionRefreshPump sessionRefresh;
    private readonly TimeProvider timeProvider;
    private readonly TimeZoneInfo localTimeZone;
    private readonly EndfieldPlaytimeScanLimits limits;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private EndfieldPlaytimeState state;
    private string? selectedLogRoot;
    private EndfieldPlaytimeScanStatus scanStatus;
    private EndfieldPlaytimeWarnings warnings = new(0, 0, 0, 0);
    private int scannedFiles;
    private bool scanning;
    private bool running;
    private bool observedConfirmedAbsence;
    private bool sawRunningThisLifetime;
    private EndfieldPlaytimePendingStart? pendingConfirmedStart;
    private DateTimeOffset? pendingConfirmedEndUtc;
    private bool saveFailed;
    private bool disposed;

    public EndfieldPlaytimeService(
        EndfieldPlaytimeState initialState,
        Func<EndfieldPlaytimeState, bool> persist,
        GameSessionRefreshPump sessionRefresh,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? localTimeZone = null)
        : this(
            initialState,
            persist,
            sessionRefresh,
            timeProvider,
            localTimeZone,
            EndfieldPlaytimeScanLimits.Default)
    {
    }

    internal EndfieldPlaytimeService(
        EndfieldPlaytimeState initialState,
        Func<EndfieldPlaytimeState, bool> persist,
        GameSessionRefreshPump sessionRefresh,
        TimeProvider? timeProvider,
        TimeZoneInfo? localTimeZone,
        EndfieldPlaytimeScanLimits limits)
    {
        state = initialState ?? throw new ArgumentNullException(nameof(initialState));
        this.persist = persist ?? throw new ArgumentNullException(nameof(persist));
        this.sessionRefresh = sessionRefresh ?? throw new ArgumentNullException(nameof(sessionRefresh));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        this.limits = ValidateLimits(limits);
        sessionRefresh.Refreshed += SessionRefresh_Refreshed;
    }

    public static string DefaultLogRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData",
        "LocalLow",
        "Gryphline");

    public EndfieldPlaytimeSnapshot Current
    {
        get
        {
            lock (sync) return Snapshot();
        }
    }

    public async Task<EndfieldPlaytimeSnapshot> ScanAsync(
        string? selectedRoot = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string root;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                scanning = true;
                root = selectedRoot ?? selectedLogRoot ?? DefaultLogRoot;
            }

            ScanResult scanned;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(limits.MaximumTime);
            try
            {
                scanned = await Task.Run(
                    () => Scan(root, localTimeZone, limits, cancellationToken, budget.Token),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (sync) scanning = false;
                throw;
            }

            lock (sync)
            {
                scanning = false;
                ObjectDisposedException.ThrowIf(disposed, this);
                scanStatus = scanned.Status;
                warnings = scanned.Warnings;
                scannedFiles = scanned.ScannedFiles;
                if (scanned.CanonicalRoot is not null
                    && scanned.Status is not EndfieldPlaytimeScanStatus.Corrupt)
                {
                    selectedLogRoot = scanned.CanonicalRoot;
                    var merged = NyxPlaytime.Merge(state.Intervals, scanned.Intervals, cancellationToken);
                    var limited = NyxPlaytime.LimitForStorage(merged.Intervals, cancellationToken);
                    if (limited.Count < merged.Intervals.Count)
                        scanStatus = EndfieldPlaytimeScanStatus.Capped;
                    var next = state with
                    {
                        Intervals = limited,
                    };
                    warnings = warnings with
                    {
                        RejectedOverlaps = warnings.RejectedOverlaps + merged.RejectedOverlaps,
                    };
                    next = ReconcileRestartPending(next, scanned.Intervals);
                    TryPersist(next);
                }
                return Snapshot();
            }
        }
        finally
        {
            scanGate.Release();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pendingConfirmedStart = null;
            pendingConfirmedEndUtc = null;
        }
        sessionRefresh.Refreshed -= SessionRefresh_Refreshed;
    }

    private void SessionRefresh_Refreshed(object? sender, GameSessionsRefreshedEventArgs e)
    {
        if (!e.Snapshots.TryGetValue("ae", out var snapshot)) return;
        lock (sync)
        {
            if (disposed) return;
            if (snapshot.LastProcessEvidence is ExactProcessPresence.Uncertain) return;

            if (pendingConfirmedEndUtc is not null && !TryCommitPendingEnd()) return;

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            if (snapshot.Status is LocalGameStatus.Running
                && snapshot.LastProcessEvidence is ExactProcessPresence.Present)
            {
                running = true;
                if (state.PendingStart is null && observedConfirmedAbsence)
                {
                    pendingConfirmedStart ??= new()
                    {
                        StartedAt = now,
                        TimeZoneId = localTimeZone.Id,
                    };
                    if (TryPersist(state with
                    {
                        PendingStart = pendingConfirmedStart,
                    })) pendingConfirmedStart = null;
                    sawRunningThisLifetime = true;
                }
                else if (!observedConfirmedAbsence && state.PendingStart is not null)
                {
                    sawRunningThisLifetime = true;
                }
                return;
            }

            if (snapshot.LastProcessEvidence is not ExactProcessPresence.Absent) return;
            if (snapshot.Status is LocalGameStatus.Running) return;
            running = false;
            observedConfirmedAbsence = true;
            if (!sawRunningThisLifetime
                || (state.PendingStart is null && pendingConfirmedStart is null)
                || snapshot.LastCloseDetectionDuration is not { } closeDetection)
                return;

            pendingConfirmedEndUtc = now - closeDetection;
            TryCommitPendingEnd();
        }
    }

    private bool TryCommitPendingEnd()
    {
        if (pendingConfirmedEndUtc is not { } end) return true;
        var pending = state.PendingStart ?? pendingConfirmedStart;
        if (pending is null)
        {
            pendingConfirmedStart = null;
            pendingConfirmedEndUtc = null;
            sawRunningThisLifetime = false;
            return true;
        }
        var interval = new EndfieldPlaytimeInterval(
            EndfieldPlaytimeIntervalKind.Gameplay,
            pending.StartedAt,
            end,
            pending.TimeZoneId);
        var nextIntervals = interval.IsValid
            ? NyxPlaytime.Merge(state.Intervals, [interval]).Intervals
            : state.Intervals;
        if (!TryPersist(state with
        {
            Intervals = nextIntervals,
            PendingStart = null,
        })) return false;
        pendingConfirmedStart = null;
        pendingConfirmedEndUtc = null;
        sawRunningThisLifetime = false;
        return true;
    }

    private EndfieldPlaytimeState ReconcileRestartPending(
        EndfieldPlaytimeState candidate,
        IReadOnlyList<EndfieldPlaytimeInterval> scanned)
    {
        if (running || sawRunningThisLifetime || candidate.PendingStart is not { } pending)
            return candidate;
        var historical = scanned
            .Where(static value => value.Kind is EndfieldPlaytimeIntervalKind.Gameplay)
            .Where(value => (value.StartUtc - pending.StartedAt).Duration() <= TimeSpan.FromSeconds(60))
            .OrderBy(static value => value.EndUtc)
            .FirstOrDefault();
        if (historical is null) return candidate;
        var reconciled = historical with
        {
            StartUtc = pending.StartedAt,
            TimeZoneId = pending.TimeZoneId,
        };
        if (!reconciled.IsValid) return candidate;
        var remaining = candidate.Intervals.Where(value =>
            value.Kind != historical.Kind
            || (value.StartUtc - historical.StartUtc).Duration() > TimeSpan.FromSeconds(60)
            || (value.EndUtc - historical.EndUtc).Duration() > TimeSpan.FromSeconds(60));
        return candidate with
        {
            Intervals = NyxPlaytime.Merge([reconciled], remaining).Intervals,
            PendingStart = null,
        };
    }

    private bool TryPersist(EndfieldPlaytimeState candidate)
    {
        var normalized = LauncherStateMigrations.Normalize(candidate);
        if (!persist(normalized))
        {
            saveFailed = true;
            return false;
        }
        state = normalized;
        saveFailed = false;
        return true;
    }

    private EndfieldPlaytimeSnapshot Snapshot() => new(
        scanStatus,
        NyxPlaytime.Calculate(state.Intervals),
        warnings,
        scannedFiles,
        scanning,
        running,
        state.PendingStart is not null,
        saveFailed);

    private static ScanResult Scan(
        string root,
        TimeZoneInfo timeZone,
        EndfieldPlaytimeScanLimits limits,
        CancellationToken callerCancellationToken,
        CancellationToken budgetCancellationToken)
    {
        callerCancellationToken.ThrowIfCancellationRequested();
        if (!TryCanonicalRoot(root, out var canonical))
            return ScanResult.Corrupt;

        var candidates = new PriorityQueue<FileCandidate, long>();
        var directories = new Stack<string>();
        directories.Push(canonical);
        var capped = false;
        var unreadable = 0;

        while (directories.Count > 0)
        {
            callerCancellationToken.ThrowIfCancellationRequested();
            if (budgetCancellationToken.IsCancellationRequested)
            {
                capped = true;
                break;
            }
            var directory = directories.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    callerCancellationToken.ThrowIfCancellationRequested();
                    if (budgetCancellationToken.IsCancellationRequested)
                    {
                        capped = true;
                        break;
                    }
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (Exception exception) when (IsFileError(exception))
                    {
                        unreadable++;
                        continue;
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Push(entry);
                        continue;
                    }
                    var name = Path.GetFileName(entry);
                    if (!name.StartsWith("games", StringComparison.OrdinalIgnoreCase)
                        || !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var candidate = new FileCandidate(
                            entry,
                            new DateTimeOffset(File.GetLastWriteTimeUtc(entry), TimeSpan.Zero));
                        if (candidates.Count < limits.MaximumFiles)
                            candidates.Enqueue(candidate, candidate.LastWriteUtc.UtcTicks);
                        else
                        {
                            capped = true;
                            if (candidates.TryPeek(out _, out var oldest)
                                && candidate.LastWriteUtc.UtcTicks > oldest)
                            {
                                candidates.Dequeue();
                                candidates.Enqueue(candidate, candidate.LastWriteUtc.UtcTicks);
                            }
                        }
                    }
                    catch (Exception exception) when (IsFileError(exception))
                    {
                        unreadable++;
                    }
                }
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                unreadable++;
            }
        }

        var files = new List<FileCandidate>();
        while (candidates.TryDequeue(out var candidate, out _)) files.Add(candidate);
        files.Sort(static (left, right) => right.LastWriteUtc.CompareTo(left.LastWriteUtc));

        long bytes = 0;
        long lines = 0;
        var scannedFiles = 0;
        var unmatched = 0;
        var rejected = 0;
        var overlaps = 0;
        IReadOnlyList<EndfieldPlaytimeInterval> accepted = Array.Empty<EndfieldPlaytimeInterval>();
        foreach (var file in files)
        {
            callerCancellationToken.ThrowIfCancellationRequested();
            if (budgetCancellationToken.IsCancellationRequested)
            {
                capped = true;
                break;
            }
            try
            {
                using var stream = new FileStream(
                    file.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.SequentialScan);
                var openedLength = stream.Length;
                if (openedLength < 0 || openedLength > limits.MaximumBytes - bytes)
                {
                    capped = true;
                    break;
                }
                var openedLastWriteUtc = new DateTimeOffset(
                    File.GetLastWriteTimeUtc(file.Path),
                    TimeSpan.Zero);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                var fileComplete = true;
                IEnumerable<string> ReadLines()
                {
                    while (true)
                    {
                        callerCancellationToken.ThrowIfCancellationRequested();
                        if (budgetCancellationToken.IsCancellationRequested
                            || lines >= limits.MaximumLines)
                        {
                            fileComplete = false;
                            yield break;
                        }
                        var line = reader.ReadLine();
                        if (line is null) yield break;
                        if (stream.Position > openedLength)
                        {
                            fileComplete = false;
                            yield break;
                        }
                        lines++;
                        yield return line;
                    }
                }
                var parsed = NyxPlaytime.ParseFile(
                    ReadLines(),
                    openedLastWriteUtc,
                    timeZone,
                    budgetCancellationToken);
                if (!fileComplete)
                {
                    capped = true;
                    break;
                }
                if (stream.Length != openedLength
                    || new DateTimeOffset(File.GetLastWriteTimeUtc(file.Path), TimeSpan.Zero)
                        != openedLastWriteUtc)
                {
                    capped = true;
                    break;
                }
                bytes += openedLength;
                scannedFiles++;
                unmatched += parsed.UnmatchedMarkers;
                rejected += parsed.RejectedMarkers;
                var merged = NyxPlaytime.Merge(
                    accepted,
                    parsed.Intervals,
                    budgetCancellationToken);
                accepted = NyxPlaytime.LimitForStorage(
                    merged.Intervals,
                    budgetCancellationToken);
                overlaps += merged.RejectedOverlaps;
                if (parsed.Capped
                    || merged.Intervals.Count > EndfieldPlaytimeInterval.MaximumStoredIntervals)
                {
                    capped = true;
                    break;
                }
            }
            catch (OperationCanceledException) when (
                !callerCancellationToken.IsCancellationRequested
                && budgetCancellationToken.IsCancellationRequested)
            {
                capped = true;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                unreadable++;
            }
        }

        var status = capped
            ? EndfieldPlaytimeScanStatus.Capped
            : files.Count == 0
                ? unreadable > 0
                    ? EndfieldPlaytimeScanStatus.Corrupt
                    : EndfieldPlaytimeScanStatus.Empty
                : accepted.Count == 0 && unreadable > 0
                    ? EndfieldPlaytimeScanStatus.Corrupt
                    : accepted.Count == 0
                        ? EndfieldPlaytimeScanStatus.Empty
                        : EndfieldPlaytimeScanStatus.Normal;
        return new(
            status,
            canonical,
            accepted,
            new(unmatched, rejected, overlaps, unreadable),
            scannedFiles);
    }

    private static bool TryCanonicalRoot(string value, out string canonical)
    {
        canonical = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > 2048
                || !Path.IsPathFullyQualified(value)
                || value.StartsWith("\\\\", StringComparison.Ordinal)
                || value.StartsWith("\\\\?\\", StringComparison.Ordinal)
                || value.StartsWith("\\\\.\\", StringComparison.Ordinal)) return false;
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!Directory.Exists(canonical)) return false;
            return (File.GetAttributes(canonical) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            canonical = string.Empty;
            return false;
        }
    }

    private static EndfieldPlaytimeScanLimits ValidateLimits(EndfieldPlaytimeScanLimits value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.MaximumFiles <= 0
            || value.MaximumBytes <= 0
            || value.MaximumLines <= 0
            || value.MaximumTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }

    private static bool IsFileError(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private sealed record FileCandidate(string Path, DateTimeOffset LastWriteUtc);

    private sealed record ScanResult(
        EndfieldPlaytimeScanStatus Status,
        string? CanonicalRoot,
        IReadOnlyList<EndfieldPlaytimeInterval> Intervals,
        EndfieldPlaytimeWarnings Warnings,
        int ScannedFiles)
    {
        public static ScanResult Corrupt { get; } = new(
            EndfieldPlaytimeScanStatus.Corrupt,
            null,
            Array.Empty<EndfieldPlaytimeInterval>(),
            new(0, 0, 0, 1),
            0);
    }
}
