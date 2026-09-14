using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.State;

namespace Nyx_Desktop_App;

internal sealed class LauncherStateController
{
    private readonly object gate = new();
    private readonly LauncherStateStore store;
    private LauncherState snapshot;

    public LauncherStateController(LauncherStateStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        var loaded = store.Load();
        snapshot = loaded.State ?? LauncherState.Defaults();
        ReadStatus = loaded.Status;
    }

    public event EventHandler? Changed;

    public LauncherStateReadStatus ReadStatus { get; private set; }

    public bool WritesBlocked => !store.CanSave;

    public string StatePath => store.StatePath;

    public string DataDirectory => Path.GetDirectoryName(store.StatePath)
        ?? throw new InvalidOperationException("Launcher state path has no parent directory.");

    public LauncherState Snapshot
    {
        get
        {
            lock (gate)
            {
                return snapshot;
            }
        }
    }

    public bool TryUpdate(Func<LauncherState, LauncherState> update) =>
        TryUpdate(update, out _);

    public bool TryUpdate(
        Func<LauncherState, LauncherState> update,
        out LauncherStateUpdateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(update);
        LauncherState next;
        lock (gate)
        {
            try
            {
                next = store.Update(update);
            }
            catch (CustomGameExecutableConflictException)
            {
                failure = LauncherStateUpdateFailure.CustomGameExecutableConflict;
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = LauncherStateUpdateFailure.Storage;
                return false;
            }

            snapshot = next;
            ReadStatus = LauncherStateReadStatus.Loaded;
            failure = LauncherStateUpdateFailure.None;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryUpdatePublisherCleanupPending(
        string provider,
        bool cleanupPending,
        bool? accountAccess = null)
    {
        LauncherState next;
        lock (gate)
        {
            try
            {
                next = store.UpdatePublisherCleanupPending(
                    provider,
                    cleanupPending,
                    accountAccess);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return false;
            }

            snapshot = next;
            ReadStatus = LauncherStateReadStatus.Loaded;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryReplaceSettings(
        LauncherState expected,
        LauncherState target,
        out LauncherStateUpdateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(target);
        LauncherState next;
        lock (gate)
        {
            if (!SettingsEqualExceptPlaytime(snapshot, expected))
            {
                failure = LauncherStateUpdateFailure.ConcurrentMutation;
                return false;
            }

            try
            {
                next = store.Update(current =>
                {
                    if (!SettingsEqualExceptPlaytime(current, expected))
                    {
                        throw new LauncherStateMutationConflictException();
                    }

                    return target with
                    {
                        PlaytimeSecondsByGame = current.PlaytimeSecondsByGame,
                    };
                });
            }
            catch (CustomGameExecutableConflictException)
            {
                failure = LauncherStateUpdateFailure.CustomGameExecutableConflict;
                return false;
            }
            catch (LauncherStateMutationConflictException)
            {
                failure = LauncherStateUpdateFailure.ConcurrentMutation;
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = LauncherStateUpdateFailure.Storage;
                return false;
            }

            snapshot = next;
            ReadStatus = LauncherStateReadStatus.Loaded;
            failure = LauncherStateUpdateFailure.None;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryReset(IReadOnlyDictionary<string, long>? playtimeSecondsByGame = null)
    {
        LauncherState expected;
        lock (gate)
        {
            expected = snapshot;
        }

        return TryReset(playtimeSecondsByGame, expected, out _);
    }

    public bool TryReset(
        IReadOnlyDictionary<string, long>? playtimeSecondsByGame,
        LauncherState expected,
        out LauncherStateUpdateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(expected);
        LauncherStateReadResult reset;
        lock (gate)
        {
            if (!SettingsEqualExceptPlaytime(snapshot, expected))
            {
                failure = LauncherStateUpdateFailure.ConcurrentMutation;
                return false;
            }

            try
            {
                reset = store.ResetToDefaults(
                    playtimeSecondsByGame ?? snapshot.PlaytimeSecondsByGame,
                    expected);
            }
            catch (LauncherStateMutationConflictException)
            {
                failure = LauncherStateUpdateFailure.ConcurrentMutation;
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = LauncherStateUpdateFailure.Storage;
                return false;
            }

            snapshot = reset.State!;
            ReadStatus = reset.Status;
            failure = LauncherStateUpdateFailure.None;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public LauncherStateReadResult PrepareLastKnownGoodRestore(
        out PreparedLauncherStateRestore? prepared) =>
        store.PrepareLastKnownGoodRestore(out prepared);

    public bool TryCommitPreparedRestore(
        PreparedLauncherStateRestore prepared,
        LauncherState expected,
        IReadOnlyDictionary<string, long> playtimeSecondsByGame,
        out LauncherStateUpdateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(playtimeSecondsByGame);
        LauncherStateReadResult restored;
        lock (gate)
        {
            if (!SettingsEqualExceptPlaytime(snapshot, expected))
            {
                failure = LauncherStateUpdateFailure.ConcurrentMutation;
                return false;
            }

            try
            {
                restored = store.CommitPreparedLastKnownGoodRestore(
                    prepared,
                    playtimeSecondsByGame,
                    expected);
            }
            catch (LauncherStateMutationConflictException)
            {
                failure = LauncherStateUpdateFailure.ConcurrentMutation;
                return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = LauncherStateUpdateFailure.Storage;
                return false;
            }

            snapshot = restored.State!;
            ReadStatus = restored.Status;
            failure = LauncherStateUpdateFailure.None;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Reloads the validated on-disk state after a recovery action.</summary>
    public bool TryReload()
    {
        var loaded = store.Load();
        if (loaded.State is null)
        {
            return false;
        }

        lock (gate)
        {
            snapshot = LauncherStateMigrations.Normalize(loaded.State);
            ReadStatus = loaded.Status;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool SettingsEqualExceptPlaytime(LauncherState left, LauncherState right)
    {
        var empty = new Dictionary<string, long>(StringComparer.Ordinal);
        return string.Equals(
            LauncherStateMigrations.Write(left with { PlaytimeSecondsByGame = empty }),
            LauncherStateMigrations.Write(right with { PlaytimeSecondsByGame = empty }),
            StringComparison.Ordinal);
    }
}

internal enum LauncherStateUpdateFailure
{
    None,
    Storage,
    CustomGameExecutableConflict,
    ConcurrentMutation,
    SessionBusy,
}
