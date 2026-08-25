using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Nyx.Desktop.Core.State;

namespace Nyx.Desktop.Infrastructure.State;

/// <summary>Per-user state store with same-volume temporary replacement and backup recovery.</summary>
public sealed class LauncherStateStore
{
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string statePath;
    private readonly string backupPath;
    private readonly string lockPath;
    private readonly TimeSpan lockTimeout;

    public LauncherStateStore(string? dataDirectory = null, TimeSpan? lockTimeout = null)
    {
        if (dataDirectory is null)
        {
            dataDirectory = NyxUserDataRootMigration.PrepareCanonicalRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        else
        {
            Directory.CreateDirectory(dataDirectory);
        }
        this.lockTimeout = lockTimeout ?? DefaultLockTimeout;
        if (this.lockTimeout <= TimeSpan.Zero || this.lockTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }

        statePath = Path.GetFullPath(Path.Combine(dataDirectory, "launcher-state-v1.json"));
        backupPath = statePath + ".bak";
        lockPath = statePath + ".lock";
    }

    public string StatePath => statePath;
    public string BackupPath => backupPath;

    /// <summary>
    /// Ordinary settings writes are allowed only when there is no primary file
    /// yet or the existing primary file is understood by this version of Nyx.
    /// Recovery methods deliberately use a separate path.
    /// </summary>
    public bool CanSave
    {
        get
        {
            try
            {
                using var stateLock = AcquireStateLock();
                return PrimaryIsWritable();
            }
            catch (StateLockTimeoutException)
            {
                return false;
            }
        }
    }

    public LauncherStateReadResult Load()
    {
        using var stateLock = AcquireStateLock();
        return LoadCore();
    }

    private LauncherStateReadResult LoadCore()
    {
        var primary = ReadFile(statePath);
        LauncherStateReadResult? primaryResult = null;
        if (primary is not null)
        {
            primaryResult = LauncherStateMigrations.Read(primary);
        }

        var backup = ReadFile(backupPath);
        LauncherStateReadResult? backupResult = null;
        if (backup is not null)
        {
            backupResult = LauncherStateMigrations.Read(backup);
        }

        if (primaryResult?.IsUsable == true)
        {
            return primaryResult with
            {
                State = ReconcilePublisherCleanupPending(
                    primaryResult.State!,
                    backupResult?.State),
            };
        }

        if (backupResult?.IsUsable == true)
        {
            return backupResult with { Status = LauncherStateReadStatus.Recovered };
        }

        return primaryResult ?? new(LauncherStateReadStatus.DefaultsUsed, LauncherState.Defaults());
    }

    /// <summary>
    /// Restores the validated last-known-good payload without treating the
    /// current (possibly malformed) primary file as a new backup.
    /// </summary>
    public LauncherStateReadResult RestoreLastKnownGood()
    {
        using var stateLock = AcquireStateLock();
        var payload = ReadFile(backupPath);
        if (payload is null)
        {
            return new(LauncherStateReadStatus.Malformed, null, "No last-known-good settings backup exists.");
        }

        var result = LauncherStateMigrations.Read(payload);
        if (!result.IsUsable)
        {
            return result;
        }

        var tempPath = statePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            PreservePrimaryForRecovery();
            WriteAndFlush(tempPath, LauncherStateMigrations.Write(result.State!));
            ReplaceAtomically(tempPath, statePath);
            return result with { Status = LauncherStateReadStatus.Recovered };
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public void Save(LauncherState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var stateLock = AcquireStateLock();
        var primary = ReadWritablePrimary();
        var next = ReconcilePublisherCleanupPending(
            LauncherStateMigrations.Normalize(state),
            primary.State);
        SaveCore(next, primary.Exists);
    }

    /// <summary>
    /// Applies one edit to the latest primary state while holding the
    /// cross-process state-root lock. The callback must not perform I/O.
    /// </summary>
    public LauncherState Update(Func<LauncherState, LauncherState> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        using var stateLock = AcquireStateLock();
        var primary = ReadWritablePrimary();
        var next = LauncherStateMigrations.Normalize(
            update(primary.State) ?? throw new InvalidOperationException("A launcher state update returned null."));
        next = ReconcilePublisherCleanupPending(next, primary.State);
        SaveCore(next, primary.Exists);
        return next;
    }

    /// <summary>
    /// Persists the safety-critical publisher-cleanup bit to both state copies
    /// while holding the one cross-process state lock. Setting writes the backup
    /// first; clearing writes it last. A torn write therefore remains pending
    /// when the two files are reconciled on the next load.
    /// </summary>
    public LauncherState UpdatePublisherCleanupPending(
        string provider,
        bool cleanupPending,
        bool? accountAccess = null)
    {
        if (provider is not ("HoYoLAB" or "SKPORT"))
            throw new ArgumentOutOfRangeException(nameof(provider));

        using var stateLock = AcquireStateLock();
        var primaryPayload = ReadFile(statePath);
        var backupPayload = ReadFile(backupPath);
        var primaryResult = primaryPayload is null
            ? null
            : LauncherStateMigrations.Read(primaryPayload);
        var backupResult = backupPayload is null
            ? null
            : LauncherStateMigrations.Read(backupPayload);
        var primaryExists = File.Exists(statePath) || Directory.Exists(statePath);
        var backupExists = File.Exists(backupPath) || Directory.Exists(backupPath);

        var current = primaryResult?.State is { } primaryState
            ? ReconcilePublisherCleanupPending(primaryState, backupResult?.State)
            : backupResult?.State ?? LauncherState.Defaults();
        var next = LauncherStateMigrations.Normalize(SetPublisherCleanupPending(
            current,
            provider,
            cleanupPending,
            accountAccess));

        if (cleanupPending)
        {
            if (primaryPayload is not null && primaryResult?.IsUsable != true)
            {
                // The primary may contain newer or recoverable user data. Do
                // not mutate either state copy until explicit recovery chooses
                // which payload is authoritative.
                throw new IOException(
                    "Launcher settings require explicit recovery before publisher cleanup can be recorded.");
            }

            if (backupExists && backupResult?.IsUsable != true)
            {
                // Never destroy a malformed or future-version recovery copy.
                // Persist the fail-closed bit to a usable primary when possible,
                // then report that redundant persistence was incomplete.
                if (!primaryExists || primaryResult?.IsUsable == true)
                    WriteStateReplacing(statePath, next);
                throw new IOException(
                    "The launcher settings backup requires explicit recovery and was preserved.");
            }

            WriteStateReplacing(backupPath, next);
            if (primaryExists && primaryResult?.IsUsable != true)
            {
                throw new IOException(
                    "Launcher settings require explicit recovery before the primary copy can be replaced.");
            }

            WriteStateReplacing(statePath, next);
        }
        else
        {
            if (backupExists && backupResult?.IsUsable != true)
            {
                throw new IOException(
                    "The launcher settings backup requires explicit recovery before cleanup can be cleared.");
            }

            if (primaryExists && primaryResult?.IsUsable != true)
            {
                throw new IOException(
                    "Launcher settings require explicit recovery before cleanup can be cleared.");
            }

            WriteStateReplacing(statePath, next);
            WriteStateReplacing(backupPath, next);
        }

        return next;
    }

    private void SaveCore(LauncherState state, bool promotePrimary)
    {
        var flags = state.Preferences.FeatureFlags;
        if (flags.HoyoLabAccountCleanupPending || flags.SkportAccountCleanupPending)
        {
            // While cleanup is pending, ordinary settings edits must not rotate
            // a stale false primary over the only true backup. Converge both
            // copies using the same fail-closed ordering as a critical set.
            WriteStateReplacing(backupPath, state);
            WriteStateReplacing(statePath, state);
            return;
        }

        var payload = LauncherStateMigrations.Write(state);
        var tempPath = statePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            WriteAndFlush(tempPath, payload);
            if (promotePrimary)
            {
                CopyReplacing(statePath, backupPath);
            }

            ReplaceAtomically(tempPath, statePath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static LauncherState ReconcilePublisherCleanupPending(
        LauncherState preferred,
        LauncherState? other)
    {
        if (other is null) return preferred;
        var preferredFlags = preferred.Preferences.FeatureFlags;
        var otherFlags = other.Preferences.FeatureFlags;
        var hoyoPending = preferredFlags.HoyoLabAccountCleanupPending
            || otherFlags.HoyoLabAccountCleanupPending;
        var skportPending = preferredFlags.SkportAccountCleanupPending
            || otherFlags.SkportAccountCleanupPending;
        return preferred with
        {
            Preferences = preferred.Preferences with
            {
                FeatureFlags = preferredFlags with
                {
                    HoyoLabAccountAccess = hoyoPending
                        ? preferredFlags.HoyoLabAccountAccess
                            && otherFlags.HoyoLabAccountAccess
                        : preferredFlags.HoyoLabAccountAccess,
                    SkportAccountAccess = skportPending
                        ? preferredFlags.SkportAccountAccess
                            && otherFlags.SkportAccountAccess
                        : preferredFlags.SkportAccountAccess,
                    HoyoLabAccountCleanupPending = hoyoPending,
                    SkportAccountCleanupPending = skportPending,
                },
            },
        };
    }

    private static LauncherState SetPublisherCleanupPending(
        LauncherState state,
        string provider,
        bool cleanupPending,
        bool? accountAccess) => state with
        {
            Preferences = state.Preferences with
            {
                FeatureFlags = provider switch
                {
                    "HoYoLAB" => state.Preferences.FeatureFlags with
                    {
                        HoyoLabAccountAccess = accountAccess
                            ?? state.Preferences.FeatureFlags.HoyoLabAccountAccess,
                        HoyoLabAccountCleanupPending = cleanupPending,
                    },
                    "SKPORT" => state.Preferences.FeatureFlags with
                    {
                        SkportAccountAccess = accountAccess
                            ?? state.Preferences.FeatureFlags.SkportAccountAccess,
                        SkportAccountCleanupPending = cleanupPending,
                    },
                    _ => throw new ArgumentOutOfRangeException(nameof(provider)),
                },
            },
        };

    private static void WriteStateReplacing(string path, LauncherState state)
    {
        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            WriteAndFlush(tempPath, LauncherStateMigrations.Write(state));
            ReplaceAtomically(tempPath, path);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Explicitly replaces an unusable primary with defaults without promoting
    /// the unusable payload to the last-known-good backup.
    /// </summary>
    public LauncherStateReadResult ResetToDefaults()
    {
        using var stateLock = AcquireStateLock();
        var defaults = LauncherState.Defaults();
        var tempPath = statePath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            if (File.Exists(statePath))
            {
                if (PrimaryIsWritable())
                {
                    CopyReplacing(statePath, backupPath);
                }
                else
                {
                    PreservePrimaryForRecovery();
                }
            }

            WriteAndFlush(tempPath, LauncherStateMigrations.Write(defaults));
            ReplaceAtomically(tempPath, statePath);
            return new(LauncherStateReadStatus.Loaded, defaults);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private IDisposable AcquireStateLock()
    {
        var processGate = Locks.GetOrAdd(statePath, static _ => new object());
        var stopwatch = Stopwatch.StartNew();
        if (!Monitor.TryEnter(processGate, lockTimeout))
        {
            throw LockTimeoutException();
        }

        try
        {
            IOException? lastSharingFailure = null;
            while (stopwatch.Elapsed < lockTimeout)
            {
                try
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.None);
                    return new StateLockLease(processGate, stream);
                }
                catch (IOException exception)
                {
                    lastSharingFailure = exception;
                    var remaining = lockTimeout - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;
                    Thread.Sleep(remaining < TimeSpan.FromMilliseconds(20)
                        ? remaining
                        : TimeSpan.FromMilliseconds(20));
                }
            }

            throw LockTimeoutException(lastSharingFailure);
        }
        catch
        {
            Monitor.Exit(processGate);
            throw;
        }
    }

    private StateLockTimeoutException LockTimeoutException(Exception? inner = null) =>
        new(
            $"Launcher settings are busy; the state lock was not acquired within {lockTimeout.TotalMilliseconds:0} ms.",
            inner ?? new TimeoutException("The launcher state lock timed out."));

    private sealed class StateLockTimeoutException(string message, Exception inner)
        : IOException(message, inner);

    private sealed class StateLockLease(object processGate, FileStream stream) : IDisposable
    {
        private FileStream? stream = stream;

        public void Dispose()
        {
            var ownedStream = Interlocked.Exchange(ref stream, null);
            if (ownedStream is null) return;
            ownedStream.Dispose();
            Monitor.Exit(processGate);
        }
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private (bool Exists, LauncherState State) ReadWritablePrimary()
    {
        if (!File.Exists(statePath))
        {
            return (false, ReadUsableState(backupPath) ?? LauncherState.Defaults());
        }

        var payload = ReadFile(statePath);
        var result = payload is null ? null : LauncherStateMigrations.Read(payload);
        if (result?.State is null)
        {
            throw new IOException(
                "Launcher settings require explicit recovery before they can be replaced.");
        }

        return (true, ReconcilePublisherCleanupPending(
            result.State,
            ReadUsableState(backupPath)));
    }

    private static LauncherState? ReadUsableState(string path)
    {
        var payload = ReadFile(path);
        return payload is null ? null : LauncherStateMigrations.Read(payload).State;
    }

    private bool PrimaryIsWritable()
    {
        if (!File.Exists(statePath))
        {
            return true;
        }

        var payload = ReadFile(statePath);
        return payload is not null && LauncherStateMigrations.Read(payload).IsUsable;
    }

    private void PreservePrimaryForRecovery()
    {
        if (!File.Exists(statePath))
        {
            return;
        }

        var recoveryPath = statePath
            + ".recovery."
            + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "."
            + Guid.NewGuid().ToString("N");
        File.Copy(statePath, recoveryPath, overwrite: false);
    }

    private static void WriteAndFlush(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void CopyReplacing(string source, string destination)
    {
        var backupTemp = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, backupTemp, overwrite: false);
            ReplaceAtomically(backupTemp, destination);
        }
        finally { TryDelete(backupTemp); }
    }

    private static void ReplaceAtomically(string source, string destination)
    {
        if (File.Exists(destination))
        {
            try
            {
                File.Replace(source, destination, destination + ".previous", ignoreMetadataErrors: true);
                TryDelete(destination + ".previous");
                return;
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
        }

        File.Move(source, destination, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Moves the one legacy data root by same-volume rename. It never merges,
/// overwrites, follows links, or deletes user files.
/// </summary>
public static class NyxUserDataRootMigration
{
    private const int MaximumEntries = 100_000;

    public static string PrepareCanonicalRoot(string localApplicationData)
    {
        var local = RequireAbsoluteLocal(localApplicationData);
        var canonical = NyxUserDataPaths.CanonicalRoot(local);
        var legacy = NyxUserDataPaths.LegacyRoot(local);

        AuditExistingComponents(local);
        if (File.Exists(canonical) || File.Exists(legacy))
        {
            throw new IOException("A Nyx user-data root is not a directory.");
        }

        var canonicalExists = Directory.Exists(canonical);
        var legacyExists = Directory.Exists(legacy);
        if (canonicalExists)
        {
            AuditTree(canonical);
            if (legacyExists)
            {
                AuditTree(legacy);
                throw new IOException("Both Nyx user-data roots exist; automatic merge is refused.");
            }

            return canonical;
        }

        if (!legacyExists)
        {
            CreateDirectoryTree(canonical);
            return canonical;
        }

        AuditTree(legacy);
        CreateDirectoryTree(Path.GetDirectoryName(canonical)!);
        try
        {
            Directory.Move(legacy, canonical);
            try
            {
                AuditTree(canonical);
            }
            catch
            {
                if (Directory.Exists(canonical) && !Directory.Exists(legacy))
                {
                    Directory.Move(canonical, legacy);
                }

                throw;
            }
        }
        catch
        {
            // Directory.Move is atomic on this fixed volume. A failed move leaves
            // the legacy root in place; the caller must not start with defaults.
            throw;
        }

        return canonical;
    }

    private static string RequireAbsoluteLocal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new IOException("The user-data base is not absolute.");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(full);
        if (root is null || root.Length != 3 || root[1] != ':' || root[2] != Path.DirectorySeparatorChar)
            throw new IOException("The user-data base is not on a local drive.");
        return full;
    }

    private static void CreateDirectoryTree(string path)
    {
        var full = RequireAbsoluteLocal(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current)) throw new IOException("A user-data path component is a file.");
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            RejectReparse(current);
        }
    }

    private static void AuditExistingComponents(string path)
    {
        var full = RequireAbsoluteLocal(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        RejectReparse(current);
        foreach (var segment in full[root.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            RejectReparse(current);
        }
    }

    private static void AuditTree(string root)
    {
        AuditExistingComponents(root);
        var pending = new Stack<string>();
        pending.Push(root);
        var discovered = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (++discovered > MaximumEntries) throw new IOException("The user-data tree is too large to audit.");
            RejectReparse(current);
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                if (++discovered > MaximumEntries) throw new IOException("The user-data tree is too large to audit.");
                var attributes = File.GetAttributes(child);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("A user-data path component is a reparse point.");
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(child);
            }
        }
    }

    private static void RejectReparse(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("A user-data path component is a reparse point.");
    }
}
