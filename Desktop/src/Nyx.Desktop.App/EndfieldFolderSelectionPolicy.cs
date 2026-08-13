namespace Nyx_Desktop_App;

internal enum EndfieldFolderSelectionStatus
{
    Stale,
    Saved,
    SavedRefreshFailed,
    InvalidIdentity,
    StorageFailed,
}

internal readonly record struct EndfieldFolderSelectionAttempt(long Generation);

internal readonly record struct EndfieldFolderSelectionResult(
    EndfieldFolderSelectionStatus Status)
{
    public bool FolderAccepted => Status is EndfieldFolderSelectionStatus.Saved
        or EndfieldFolderSelectionStatus.SavedRefreshFailed;

    public bool NeedsReview => Status is EndfieldFolderSelectionStatus.InvalidIdentity
        or EndfieldFolderSelectionStatus.StorageFailed;
}

/// <summary>
/// Owns the generation boundary between an asynchronous folder picker and the
/// one saved Endfield root. Only the newest live attempt may touch settings.
/// A later refresh is advisory and cannot undo a proven successful save.
/// </summary>
internal sealed class EndfieldFolderSelectionPolicy
{
    private readonly object sync = new();
    private long generation;

    public EndfieldFolderSelectionAttempt Begin()
    {
        lock (sync)
        {
            return new(++generation);
        }
    }

    public void CancelAll()
    {
        lock (sync)
        {
            generation++;
        }
    }

    public bool IsCurrent(
        EndfieldFolderSelectionAttempt attempt,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            return !cancellationToken.IsCancellationRequested
                && attempt.Generation == generation;
        }
    }

    public async Task<EndfieldFolderSelectionResult> CompleteAsync(
        EndfieldFolderSelectionAttempt attempt,
        CancellationToken cancellationToken,
        bool identityAccepted,
        string selectedPath,
        Func<string, bool> save,
        Action clear,
        Func<CancellationToken, Task> refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(clear);
        ArgumentNullException.ThrowIfNull(refresh);

        EndfieldFolderSelectionStatus committed;
        lock (sync)
        {
            if (cancellationToken.IsCancellationRequested
                || attempt.Generation != generation)
            {
                return new(EndfieldFolderSelectionStatus.Stale);
            }

            if (!identityAccepted)
            {
                TryClear(clear);
                return new(EndfieldFolderSelectionStatus.InvalidIdentity);
            }

            try
            {
                committed = save(selectedPath)
                    ? EndfieldFolderSelectionStatus.Saved
                    : EndfieldFolderSelectionStatus.StorageFailed;
            }
            catch (Exception)
            {
                committed = EndfieldFolderSelectionStatus.StorageFailed;
            }

            if (committed is EndfieldFolderSelectionStatus.StorageFailed)
            {
                TryClear(clear);
                return new(committed);
            }
        }

        try
        {
            await refresh(cancellationToken).ConfigureAwait(false);
            return new(EndfieldFolderSelectionStatus.Saved);
        }
        catch (Exception)
        {
            return new(EndfieldFolderSelectionStatus.SavedRefreshFailed);
        }
    }

    private static void TryClear(Action clear)
    {
        try
        {
            clear();
        }
        catch (Exception)
        {
            // Failed settings cleanup still never produces an accepted folder.
        }
    }
}

internal enum EndfieldUiActionKind
{
    ChooseFolder,
    OpenMaintenance,
}

/// <summary>
/// Serializes only the two Endfield actions that consume or replace the saved
/// root. Direct game launch intentionally does not enter this admission.
/// </summary>
internal sealed class EndfieldUiActionAdmission
{
    private readonly object sync = new();
    private EndfieldUiActionKind? active;
    private long generation;

    public EndfieldUiActionLease? TryEnter(EndfieldUiActionKind kind)
    {
        lock (sync)
        {
            if (active is not null)
            {
                return null;
            }

            active = kind;
            return new(this, generation);
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            generation++;
            active = null;
        }
    }

    internal void Release(long leaseGeneration)
    {
        lock (sync)
        {
            if (leaseGeneration == generation)
            {
                active = null;
            }
        }
    }
}

internal sealed class EndfieldUiActionLease : IDisposable
{
    private EndfieldUiActionAdmission? owner;
    private readonly long generation;

    internal EndfieldUiActionLease(EndfieldUiActionAdmission owner, long generation)
    {
        this.owner = owner;
        this.generation = generation;
    }

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(generation);
}
