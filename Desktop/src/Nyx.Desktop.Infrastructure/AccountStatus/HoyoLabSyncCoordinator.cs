using System.Security.Cryptography;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

public enum HoyoLabManualSyncStatus
{
    Completed,
    NotEnabled,
    NoLocalData,
    InvalidRecoveryCode,
    Conflict,
    DeletionPending,
    LocalStorageUnavailable,
    InvalidCloudData,
    AuthenticationFailed,
    NetworkUnavailable,
    TimedOut,
    RateLimited,
    TooLarge,
    Canceled,
}

public sealed record HoyoLabManualSyncResult(
    HoyoLabManualSyncStatus Status,
    DateTimeOffset? UpdatedAt = null,
    string? RecoveryCode = null,
    DateTimeOffset? RoleDeletionAt = null)
{
    public override string ToString() => nameof(HoyoLabManualSyncResult);
}

public sealed record HoyoLabSyncSummary(
    bool Available,
    bool Enabled,
    int PendingDeletions,
    DateTimeOffset? LastSyncedAt)
{
    public override string ToString() => nameof(HoyoLabSyncSummary);
}

/// <summary>
/// Manual HSR sync and previously authorized deletion only. The publisher service
/// holds its existing operation gate and supplies its atomic generation check.
/// </summary>
public sealed class HoyoLabSyncCoordinator : IDisposable
{
    internal const string ManagedDirectoryName = ".protected-hoyolab-sync";
    // ponytail: eight live slots plus eight detached cleanup slots; finish cleanup before adding more.
    internal const int MaximumRetainedSlots = HoyoLabAccountSlotRules.MaximumSlots * 2;

    private readonly string publisherRoot;
    private readonly string managedRoot;
    private readonly string? slotId;
    private readonly HoyoLabSyncStateStore? currentStore;
    private readonly HoyoLabGameBundleStore? bundles;
    private readonly IPublisherRoleBindingProtector protector;
    private readonly IPublisherRoleBindingFileBoundary files;
    private readonly HoyoLabSyncClient client;
    private readonly TimeProvider clock;
    private readonly Func<Action, bool> tryPublish;
    private int disposed;

    public HoyoLabSyncCoordinator(
        string publisherRoot,
        string? slotId,
        string? protectedSlotRoot,
        Func<Action, bool> tryPublish)
        : this(
            publisherRoot,
            slotId,
            protectedSlotRoot,
            tryPublish,
            new WindowsCurrentUserRoleBindingProtector(),
            new SystemPublisherRoleBindingFileBoundary(),
            new HoyoLabSyncClient(),
            TimeProvider.System)
    {
    }

    internal HoyoLabSyncCoordinator(
        string publisherRoot,
        string? slotId,
        string? protectedSlotRoot,
        Func<Action, bool> tryPublish,
        IPublisherRoleBindingProtector protector,
        IPublisherRoleBindingFileBoundary files,
        HoyoLabSyncClient client,
        TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherRoot);
        if ((slotId is null) != (protectedSlotRoot is null)
            || (slotId is not null && !HoyoLabAccountSlotRules.IsValidSlotId(slotId)))
            throw new ArgumentException("HoYo sync requires an exact managed slot.");
        this.publisherRoot = Path.GetFullPath(publisherRoot);
        managedRoot = Path.Combine(this.publisherRoot, ManagedDirectoryName);
        if (slotId is not null
            && !string.Equals(
                Path.GetFullPath(protectedSlotRoot!),
                HoyoLabAccountSlotStore.ProtectedStateRootFor(this.publisherRoot, slotId),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("HoYo sync requires the slot's own protected data root.");
        this.slotId = slotId;
        this.tryPublish = tryPublish ?? throw new ArgumentNullException(nameof(tryPublish));
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (slotId is not null)
        {
            currentStore = StoreFor(slotId);
            bundles = new(protectedSlotRoot!, protector, files, clock);
        }
    }

    public static string GenerateRecoveryCode() => HoyoLabSyncCrypto.GenerateRecoveryCode();

    public HoyoLabSyncSummary GetSummary()
    {
        if (!TryListStores(out var stores)) return new(false, false, 0, null);
        var pending = 0;
        var enabled = false;
        DateTimeOffset? revision = null;
        foreach (var entry in stores)
        {
            using var state = entry.Store.TryLoad();
            if (state is null)
            {
                if (files.EntryExists(entry.Store.StatePath)) return new(false, false, pending, null);
                continue;
            }
            pending += state.PendingDeletions.Count + state.PendingRoleDeletions.Count;
            if (entry.SlotId != slotId) continue;
            enabled = state.CurrentCredential is not null;
            revision = state.WorkerRevision;
        }
        return new(true, enabled, pending, revision);
    }

    public async Task<HoyoLabManualSyncResult> ConnectAsync(
        string recoveryCode,
        CancellationToken cancellationToken = default)
    {
        if (currentStore is null || bundles is null) return Result(HoyoLabManualSyncStatus.NotEnabled);
        if (!HoyoLabSyncCrypto.TryDerive(recoveryCode, out var derived))
            return Result(HoyoLabManualSyncStatus.InvalidRecoveryCode);
        using var secrets = derived!;
        using var credential = CredentialFor(secrets);
        using (var state = currentStore.TryLoad())
        {
            if (state?.PendingDeletions.Any(item => item.RemoveLocalSlot) == true)
                return Result(HoyoLabManualSyncStatus.DeletionPending);
            if (state?.CurrentCredential is not null)
                return Result(HoyoLabManualSyncStatus.Conflict);
        }
        var identityStatus = CheckIdentity(secrets.SyncId, rejectOtherCurrent: true);
        if (identityStatus != HoyoLabManualSyncStatus.Completed) return Result(identityStatus);
        if (!CanCreateCurrentStore()) return Result(HoyoLabManualSyncStatus.LocalStorageUnavailable);
        if (!Apply(() => currentStore.TrySetCurrentCredential(credential, cancellationToken), cancellationToken))
            return WriteFailure(cancellationToken);
        return await SyncCoreAsync(secrets, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HoyoLabManualSyncResult> SyncNowAsync(
        CancellationToken cancellationToken = default)
    {
        using var state = currentStore?.TryLoad();
        if (state?.CurrentCredential is not { } credential)
            return Result(HoyoLabManualSyncStatus.NotEnabled);
        var identityStatus = CheckIdentity(credential.SyncId, rejectOtherCurrent: true);
        if (identityStatus != HoyoLabManualSyncStatus.Completed) return Result(identityStatus);
        using var secrets = SecretsFor(credential);
        return await SyncCoreAsync(secrets, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HoyoLabManualSyncResult> RotateAsync(
        CancellationToken cancellationToken = default)
    {
        using var state = currentStore?.TryLoad();
        if (state?.CurrentCredential is not { } current || bundles is null)
            return Result(HoyoLabManualSyncStatus.NotEnabled);
        var identityStatus = CheckIdentity(current.SyncId, rejectOtherCurrent: true);
        if (identityStatus != HoyoLabManualSyncStatus.Completed) return Result(identityStatus);
        using var oldSecrets = SecretsFor(current);
        var preparedBundle = await ReadMergedAsync(oldSecrets, cancellationToken).ConfigureAwait(false);
        if (preparedBundle.Status != HoyoLabManualSyncStatus.Completed) return Result(preparedBundle.Status);

        var code = GenerateRecoveryCode();
        if (!HoyoLabSyncCrypto.TryDerive(code, out var derived))
            return Result(HoyoLabManualSyncStatus.InvalidRecoveryCode);
        using var replacement = derived!;
        using var replacementCredential = CredentialFor(replacement);
        identityStatus = CheckIdentity(replacement.SyncId, rejectOtherCurrent: true);
        if (identityStatus != HoyoLabManualSyncStatus.Completed) return Result(identityStatus);
        var target = await RequestAsync(
            () => client.StatusAsync(replacement, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (!target.IsAbsent)
            return Result(target.IsSuccess ? HoyoLabManualSyncStatus.Conflict : Map(target.Failure));

        using var compensation = TokenDeletion(replacementCredential, HoyoLabSyncStateStore.AllHoyoScope);
        using var oldDeletion = TokenDeletion(
            current, HoyoLabSyncStateStore.AllHoyoScope,
            requireRevisionMatch: true, expectedRevision: preparedBundle.UpdatedAt);
        if (!Apply(() => currentStore!.TryEnqueuePendingDeletion(compensation, cancellationToken), cancellationToken))
            return WriteFailure(cancellationToken);
        if (!HoyoLabSyncCrypto.TryEncryptBundle(replacement, preparedBundle.Bundle, UtcNow(), out var envelope))
            return Result(HoyoLabManualSyncStatus.InvalidCloudData);
        var pushed = await RequestAsync(
            () => client.PushAsync(replacement, envelope, null, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (!pushed.IsSuccess) return Result(Map(pushed.Failure));
        if (!Apply(
                () => bundles.TrySave(preparedBundle.Bundle!)
                    && currentStore!.TryRotateCurrentCredential(
                        current,
                        replacementCredential,
                        pushed.UpdatedAt,
                        oldDeletion,
                        compensation,
                        cancellationToken),
                cancellationToken))
            return WriteFailure(cancellationToken);

        // The replacement code is not exposed until promotion; a failed change
        // retains only its protected compensation token and the old live key.
        _ = await RetryDeletionsAsync(cancellationToken).ConfigureAwait(false);
        return IsCurrent(cancellationToken)
            ? new(HoyoLabManualSyncStatus.Completed, pushed.UpdatedAt, code)
            : Result(HoyoLabManualSyncStatus.Canceled);
    }

    public HoyoLabManualSyncResult Detach(
        string? deletionScope = null,
        CancellationToken cancellationToken = default,
        bool removeLocalSlot = false)
    {
        if ((deletionScope is not null
                && deletionScope is not (HoyoLabSyncStateStore.HsrScope or HoyoLabSyncStateStore.AllHoyoScope))
            || (removeLocalSlot && deletionScope != HoyoLabSyncStateStore.AllHoyoScope))
            return Result(HoyoLabManualSyncStatus.Conflict);
        if (!IsCurrent(cancellationToken)) return Result(HoyoLabManualSyncStatus.Canceled);
        using var state = currentStore?.TryLoad();
        if (state?.CurrentCredential is not { } current)
            return currentStore is not null && files.EntryExists(currentStore.StatePath) && state is null
                ? Result(HoyoLabManualSyncStatus.LocalStorageUnavailable)
                : removeLocalSlot && state?.PendingDeletions.Any(item => item.RemoveLocalSlot) != true
                    ? Result(HoyoLabManualSyncStatus.NotEnabled)
                : Result(HoyoLabManualSyncStatus.Completed);
        using var deletion = deletionScope is null ? null : TokenDeletion(current, deletionScope, removeLocalSlot);
        if (!Apply(
                () => currentStore!.TryDetachCurrentCredential(current, deletion, cancellationToken),
                cancellationToken))
            return WriteFailure(cancellationToken);
        TrimEmptyStore(slotId!, currentStore!);
        return Result(HoyoLabManualSyncStatus.Completed);
    }

    public HoyoLabManualSyncResult QueueRoleDeletion(
        PublisherRoleBinding binding,
        CancellationToken cancellationToken = default)
    {
        using var state = currentStore?.TryLoad();
        if (state?.CurrentCredential is not { } current || bundles is null)
            return Result(HoyoLabManualSyncStatus.NotEnabled);
        var previous = state.PendingRoleDeletions.FirstOrDefault(item =>
            item.SyncId == current.SyncId && item.Binding == binding);
        if (previous is not null)
        {
            var removed = RemoveLocalRole(slotId!, previous, cancellationToken);
            return removed == HoyoLabManualSyncStatus.Completed
                ? new(HoyoLabManualSyncStatus.Completed, RoleDeletionAt: previous.DeletedAt)
                : Result(removed);
        }
        if (state.PendingDeletions.Any(item => item.SyncId == current.SyncId))
            return Result(HoyoLabManualSyncStatus.DeletionPending);
        var bundle = bundles.TryLoad();
        var role = bundle?.Roles.SingleOrDefault(item => item.Role.Binding == binding);
        if (role is null) return Result(HoyoLabManualSyncStatus.NoLocalData);
        var now = UtcNow();
        var second = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var deletionAt = HoyoLabGameBundleStore.StrictDeletionTimestamp(
            second,
            new[] { role.Observations.Resources, role.Observations.Achievements }.Max());
        if (deletionAt is null) return Result(HoyoLabManualSyncStatus.Conflict);
        using var deletion = new HoyoLabPendingRoleDeletion(
            current.SyncId,
            current.Token,
            current.Key,
            binding,
            Guid.NewGuid().ToString("N"),
            now,
            role.Observations.Resources,
            role.Observations.Achievements,
            deletionAt.Value);
        if (!Apply(
            () => currentStore!.TryEnqueuePendingRoleDeletion(deletion, cancellationToken),
            cancellationToken))
            return WriteFailure(cancellationToken);
        var localRemoval = RemoveLocalRole(slotId!, deletion, cancellationToken);
        return localRemoval == HoyoLabManualSyncStatus.Completed
            ? new(HoyoLabManualSyncStatus.Completed, RoleDeletionAt: deletionAt)
            : Result(localRemoval);
    }

    public HoyoLabManualSyncResult DetachAllLocal(
        CancellationToken cancellationToken = default)
    {
        if (!TryListStores(out var stores)) return Result(HoyoLabManualSyncStatus.LocalStorageUnavailable);
        foreach (var entry in stores)
        {
            using var state = entry.Store.TryLoad();
            if (state is null)
            {
                if (files.EntryExists(entry.Store.StatePath))
                    return Result(HoyoLabManualSyncStatus.LocalStorageUnavailable);
                continue;
            }
            if (state.CurrentCredential is { } current
                && !Apply(
                    () => entry.Store.TryDetachCurrentCredential(current, null, cancellationToken),
                    cancellationToken))
                return WriteFailure(cancellationToken);
            TrimEmptyStore(entry.SlotId, entry.Store);
        }
        return Result(HoyoLabManualSyncStatus.Completed);
    }

    public async Task<HoyoLabManualSyncResult> RetryDeletionsAsync(
        CancellationToken cancellationToken = default,
        Func<string, bool>? removeLocalSlot = null)
    {
        if (!TryListStores(out var stores)) return Result(HoyoLabManualSyncStatus.LocalStorageUnavailable);
        // Finish all previously requested local cleanup before any network call.
        // An older offline/conflicting cloud request must not keep a later session alive.
        var localStatus = HoyoLabManualSyncStatus.Completed;
        foreach (var entry in stores)
        {
            using var pending = entry.Store.TryLoad();
            if (pending is null)
            {
                if (files.EntryExists(entry.Store.StatePath))
                    localStatus = HoyoLabManualSyncStatus.LocalStorageUnavailable;
                continue;
            }
            foreach (var deletion in pending.PendingDeletions.Where(item => item.RemoveLocalSlot))
            {
                var removed = pending.CurrentCredential?.SyncId == deletion.SyncId
                    ? HoyoLabManualSyncStatus.Conflict
                    : removeLocalSlot is not null
                        && Apply(() => removeLocalSlot(entry.SlotId), cancellationToken)
                        ? HoyoLabManualSyncStatus.Completed
                        : WriteFailure(cancellationToken).Status;
                if (removed != HoyoLabManualSyncStatus.Completed) localStatus = removed;
            }
            foreach (var deletion in pending.PendingRoleDeletions)
            {
                var removed = RemoveLocalRole(entry.SlotId, deletion, cancellationToken);
                if (removed != HoyoLabManualSyncStatus.Completed) localStatus = removed;
            }
        }
        if (localStatus != HoyoLabManualSyncStatus.Completed) return Result(localStatus);
        foreach (var entry in stores)
        {
            using var state = entry.Store.TryLoad();
            if (state is null)
            {
                if (files.EntryExists(entry.Store.StatePath))
                    return Result(HoyoLabManualSyncStatus.LocalStorageUnavailable);
                continue;
            }
            foreach (var deletion in state.PendingDeletions)
            {
                if (state.CurrentCredential?.SyncId == deletion.SyncId)
                    return Result(HoyoLabManualSyncStatus.Conflict);
                if (deletion.RemoveLocalSlot
                    && (removeLocalSlot is null
                        || !Apply(() => removeLocalSlot(entry.SlotId), cancellationToken)))
                    return WriteFailure(cancellationToken);
                var deleted = await RequestAsync(
                    () => client.DeletePendingAsync(deletion, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (!deleted.IsSuccess && !deleted.IsAbsent) return Result(Map(deleted.Failure));
                if (!Apply(
                        () => entry.Store.TryCompletePendingDeletion(deletion.OperationId, cancellationToken),
                        cancellationToken))
                    return WriteFailure(cancellationToken);
            }
            foreach (var deletion in state.PendingRoleDeletions)
            {
                var localRemoval = RemoveLocalRole(entry.SlotId, deletion, cancellationToken);
                if (localRemoval != HoyoLabManualSyncStatus.Completed) return Result(localRemoval);
                var deleted = await DeleteRoleAsync(deletion, cancellationToken).ConfigureAwait(false);
                if (deleted != HoyoLabManualSyncStatus.Completed) return Result(deleted);
                if (!Apply(
                        () => entry.Store.TryCompletePendingRoleDeletion(deletion.OperationId, cancellationToken),
                        cancellationToken))
                    return WriteFailure(cancellationToken);
            }
            TrimEmptyStore(entry.SlotId, entry.Store);
        }
        return Result(HoyoLabManualSyncStatus.Completed);
    }

    private async Task<HoyoLabManualSyncResult> SyncCoreAsync(
        HoyoLabSyncCrypto.DerivedSecrets secrets,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var merged = await ReadMergedAsync(secrets, cancellationToken).ConfigureAwait(false);
            if (merged.Status != HoyoLabManualSyncStatus.Completed) return Result(merged.Status);
            if (!HoyoLabSyncCrypto.TryEncryptBundle(secrets, merged.Bundle, UtcNow(), out var envelope))
                return Result(HoyoLabManualSyncStatus.InvalidCloudData);
            var pushed = await RequestAsync(
                () => client.PushAsync(secrets, envelope, merged.UpdatedAt, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (pushed.IsConflict && attempt == 0) continue;
            if (!pushed.IsSuccess) return Result(Map(pushed.Failure));
            if (!Apply(
                    () => bundles!.TrySave(merged.Bundle!)
                        && currentStore!.TrySetWorkerRevision(pushed.UpdatedAt, cancellationToken),
                    cancellationToken))
                return WriteFailure(cancellationToken);
            return new(HoyoLabManualSyncStatus.Completed, pushed.UpdatedAt);
        }
        return Result(HoyoLabManualSyncStatus.Conflict);
    }

    private async Task<ReadResult> ReadMergedAsync(
        HoyoLabSyncCrypto.DerivedSecrets secrets,
        CancellationToken cancellationToken)
    {
        var local = bundles?.TryLoad();
        if (local is null) return new(HoyoLabManualSyncStatus.NoLocalData);
        var remote = await ReadRemoteAsync(secrets, cancellationToken).ConfigureAwait(false);
        if (remote.Status != HoyoLabManualSyncStatus.Completed) return remote;
        if (remote.Bundle is null) return new(HoyoLabManualSyncStatus.Completed, local);
        var merged = HoyoLabGameBundleMerge.Merge(local, remote.Bundle, UtcNow());
        return merged.Outcome == HoyoLabGameBundleMergeOutcome.Conflict
            ? new(HoyoLabManualSyncStatus.Conflict)
            : new(HoyoLabManualSyncStatus.Completed, merged.Bundle, remote.UpdatedAt);
    }

    private async Task<ReadResult> ReadRemoteAsync(
        HoyoLabSyncCrypto.DerivedSecrets secrets,
        CancellationToken cancellationToken)
    {
        var pulled = await RequestAsync(
            () => client.PullAsync(secrets, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (pulled.IsAbsent) return new(HoyoLabManualSyncStatus.Completed);
            if (!pulled.IsSuccess) return new(Map(pulled.Failure));
            if (pulled.Payload is null
                || !HoyoLabSyncCrypto.TryParseEnvelope(pulled.Payload, out var envelope)
                || !HoyoLabSyncCrypto.TryDecryptBundle(secrets, envelope, UtcNow(), out var bundle))
                return new(HoyoLabManualSyncStatus.InvalidCloudData);
            return new(HoyoLabManualSyncStatus.Completed, bundle, pulled.UpdatedAt);
        }
        finally
        {
            if (pulled.Payload is not null) CryptographicOperations.ZeroMemory(pulled.Payload);
        }
    }

    private async Task<HoyoLabManualSyncStatus> DeleteRoleAsync(
        HoyoLabPendingRoleDeletion deletion,
        CancellationToken cancellationToken)
    {
        using var secrets = new HoyoLabSyncCrypto.DerivedSecrets(
            deletion.SyncId, deletion.Token.ToArray(), deletion.Key.ToArray(), null);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var remote = await ReadRemoteAsync(secrets, cancellationToken).ConfigureAwait(false);
            if (remote.Status != HoyoLabManualSyncStatus.Completed) return remote.Status;
            if (remote.Bundle is null) return HoyoLabManualSyncStatus.Completed;
            var role = remote.Bundle.Roles.SingleOrDefault(item => item.Role.Binding == deletion.Binding);
            if (NewerThanKnown(role?.Observations.Resources, deletion.KnownResourcesAt)
                || NewerThanKnown(role?.Observations.Achievements, deletion.KnownAchievementsAt))
                return HoyoLabManualSyncStatus.Conflict;
            var tombstone = remote.Bundle.RoleTombstones.SingleOrDefault(item => item.Binding == deletion.Binding);
            if (role is null && tombstone?.DeletedAt >= deletion.DeletedAt)
                return HoyoLabManualSyncStatus.Completed;
            var updated = RemoveRoleAt(remote.Bundle, deletion.Binding, deletion.DeletedAt);
            if (!HoyoLabGameBundleRules.IsValid(updated, UtcNow())
                || !HoyoLabSyncCrypto.TryEncryptBundle(secrets, updated, UtcNow(), out var envelope))
                return HoyoLabManualSyncStatus.Conflict;
            var pushed = await RequestAsync(
                () => client.PushAsync(secrets, envelope, remote.UpdatedAt, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (pushed.IsConflict && attempt == 0) continue;
            return pushed.IsSuccess ? HoyoLabManualSyncStatus.Completed : Map(pushed.Failure);
        }
        return HoyoLabManualSyncStatus.Conflict;
    }

    internal static HoyoLabGameBundle RemoveRoleAt(
        HoyoLabGameBundle bundle,
        PublisherRoleBinding binding,
        DateTimeOffset deletedAt)
    {
        var roles = bundle.Roles.Where(item => item.Role.Binding != binding).ToArray();
        var previous = bundle.RoleTombstones.SingleOrDefault(item => item.Binding == binding);
        var tombstone = new HoyoLabRoleTombstone(
            binding, previous?.DeletedAt > deletedAt ? previous.DeletedAt : deletedAt);
        return HoyoLabGameBundleRules.Normalize(bundle with
        {
            Roles = roles,
            SelectedRole = bundle.SelectedRole != binding
                ? bundle.SelectedRole
                : roles.OrderBy(item => item.Role.Binding.Server, StringComparer.Ordinal)
                    .ThenBy(item => item.Role.Binding.RoleId, StringComparer.Ordinal)
                    .Select(item => item.Role.Binding).FirstOrDefault(),
            RoleTombstones = bundle.RoleTombstones.Where(item => item.Binding != binding)
                .Append(tombstone).OrderBy(item => item.DeletedAt)
                .ThenBy(item => item.Binding.Server, StringComparer.Ordinal)
                .ThenBy(item => item.Binding.RoleId, StringComparer.Ordinal).ToArray(),
        });
    }

    private HoyoLabManualSyncStatus RemoveLocalRole(
        string id,
        HoyoLabPendingRoleDeletion deletion,
        CancellationToken cancellationToken)
    {
        var protectedRoot = HoyoLabAccountSlotStore.ProtectedStateRootFor(publisherRoot, id);
        try
        {
            if (!IsCurrent(cancellationToken)) return HoyoLabManualSyncStatus.Canceled;
            if (!IsSafeDirectoryChain(protectedRoot)) return HoyoLabManualSyncStatus.LocalStorageUnavailable;
            if (!Directory.Exists(protectedRoot)) return HoyoLabManualSyncStatus.Completed;
            var localBundles = new HoyoLabGameBundleStore(protectedRoot, protector, files, clock);
            var local = localBundles.TryLoad();
            if (local is null && files.EntryExists(localBundles.BundlePath))
                return HoyoLabManualSyncStatus.LocalStorageUnavailable;
            var role = local?.Roles.SingleOrDefault(item => item.Role.Binding == deletion.Binding);
            if (NewerThanKnown(role?.Observations.Resources, deletion.KnownResourcesAt)
                || NewerThanKnown(role?.Observations.Achievements, deletion.KnownAchievementsAt))
                return HoyoLabManualSyncStatus.Conflict;
            var roles = new PublisherRoleBindingStore(protectedRoot, protector, files);
            var selected = roles.TryLoadRecord(HoyoLabGameBundleRules.GameId);
            if (selected is null && files.EntryExists(roles.BindingPath(HoyoLabGameBundleRules.GameId)))
                return HoyoLabManualSyncStatus.LocalStorageUnavailable;
            var snapshots = new PublisherResourceSnapshotStore(protectedRoot, protector, files);
            var removesSelected = selected?.Binding == deletion.Binding;
            if (removesSelected
                && NewerThanKnown(
                    snapshots.TryLoad(HoyoLabGameBundleRules.GameId, deletion.Binding)?.ObservedAt,
                    deletion.KnownResourcesAt))
                return HoyoLabManualSyncStatus.Conflict;
            var updated = local is null ? null : RemoveRoleAt(local, deletion.Binding, deletion.DeletedAt);
            if (updated is not null && !HoyoLabGameBundleRules.IsValid(updated, UtcNow()))
                return HoyoLabManualSyncStatus.Conflict;
            return Apply(
                () => (updated is null || localBundles.TrySave(updated))
                    && (!removesSelected
                        || (snapshots.Delete(HoyoLabGameBundleRules.GameId)
                            && roles.Delete(HoyoLabGameBundleRules.GameId))),
                cancellationToken)
                ? HoyoLabManualSyncStatus.Completed
                : WriteFailure(cancellationToken).Status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return HoyoLabManualSyncStatus.LocalStorageUnavailable;
        }
    }

    private HoyoLabManualSyncStatus CheckIdentity(string syncId, bool rejectOtherCurrent)
    {
        if (!TryListStores(out var stores)) return HoyoLabManualSyncStatus.LocalStorageUnavailable;
        foreach (var entry in stores)
        {
            using var state = entry.Store.TryLoad();
            if (state is null)
            {
                if (files.EntryExists(entry.Store.StatePath)) return HoyoLabManualSyncStatus.LocalStorageUnavailable;
                continue;
            }
            if (state.PendingDeletions.Any(item => item.SyncId == syncId)
                || state.PendingRoleDeletions.Any(item => item.SyncId == syncId)
                || (entry.SlotId == slotId && state.PendingDeletions.Any(item => item.RemoveLocalSlot)))
                return HoyoLabManualSyncStatus.DeletionPending;
            if (rejectOtherCurrent && entry.SlotId != slotId && state.CurrentCredential?.SyncId == syncId)
                return HoyoLabManualSyncStatus.Conflict;
        }
        return HoyoLabManualSyncStatus.Completed;
    }

    private HoyoLabSyncStateStore StoreFor(string id) => new(
        Path.Combine(managedRoot, id), protector, files, clock);

    private bool TryListStores(out IReadOnlyList<(string SlotId, HoyoLabSyncStateStore Store)> stores)
    {
        stores = [];
        try
        {
            if (!IsSafeDirectoryChain(publisherRoot) || !IsSafeDirectoryChain(managedRoot)) return false;
            if (!Directory.Exists(managedRoot)) return true;
            var entries = Directory.EnumerateFileSystemEntries(managedRoot)
                .Take(MaximumRetainedSlots + 1).ToArray();
            if (entries.Length > MaximumRetainedSlots) return false;
            var result = new List<(string, HoyoLabSyncStateStore)>();
            foreach (var entry in entries)
            {
                var id = Path.GetFileName(entry);
                if (!HoyoLabAccountSlotRules.IsValidSlotId(id)
                    || !Directory.Exists(entry)
                    || !IsSafeDirectoryChain(entry))
                    return false;
                result.Add((id, StoreFor(id)));
            }
            stores = result.OrderBy(item => item.Item1, StringComparer.Ordinal).ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private bool CanCreateCurrentStore()
    {
        if (!TryListStores(out var stores)) return false;
        foreach (var entry in stores) TrimEmptyStore(entry.SlotId, entry.Store);
        return TryListStores(out stores)
            && (stores.Any(item => item.SlotId == slotId) || stores.Count < MaximumRetainedSlots);
    }

    private void TrimEmptyStore(string id, HoyoLabSyncStateStore store)
    {
        if (!Apply(() => store.TryDeleteIfEmpty(), CancellationToken.None)) return;
        try
        {
            var slotRoot = Path.Combine(managedRoot, id);
            var stateRoot = Path.GetDirectoryName(store.StatePath)!;
            if (!IsSafeDirectoryChain(stateRoot)) return;
            if (Directory.Exists(stateRoot) && !Directory.EnumerateFileSystemEntries(stateRoot).Any())
                Directory.Delete(stateRoot);
            if (IsSafeDirectoryChain(slotRoot) && Directory.Exists(slotRoot)
                && !Directory.EnumerateFileSystemEntries(slotRoot).Any())
                Directory.Delete(slotRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Empty directories retain no credential and may be retried later.
        }
    }

    private static bool IsSafeDirectoryChain(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            try
            {
                var attributes = File.GetAttributes(current.FullName);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
                    != FileAttributes.Directory)
                    return false;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }

    private bool Apply(Func<bool> write, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested) return false;
        var saved = false;
        try
        {
            return tryPublish(() => saved = !cancellationToken.IsCancellationRequested && write()) && saved;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private bool IsCurrent(CancellationToken cancellationToken) =>
        Volatile.Read(ref disposed) == 0 && !cancellationToken.IsCancellationRequested && tryPublish(() => { });

    private async Task<HoyoLabSyncOutcome> RequestAsync(
        Func<Task<HoyoLabSyncOutcome>> request,
        CancellationToken cancellationToken)
    {
        Task<HoyoLabSyncOutcome>? pending = null;
        if (Volatile.Read(ref disposed) != 0
            || cancellationToken.IsCancellationRequested
            || !tryPublish(() =>
            {
                if (!cancellationToken.IsCancellationRequested) pending = request();
            })
            || pending is null)
            return new(HoyoLabSyncFailure.Canceled);
        return await pending.ConfigureAwait(false);
    }

    private HoyoLabManualSyncResult WriteFailure(CancellationToken cancellationToken) =>
        Result(IsCurrent(cancellationToken)
            ? HoyoLabManualSyncStatus.LocalStorageUnavailable
            : HoyoLabManualSyncStatus.Canceled);

    private DateTimeOffset UtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(
        clock.GetUtcNow().ToUnixTimeMilliseconds());

    private HoyoLabPendingDeletion TokenDeletion(
        HoyoLabSyncCredential credential,
        string scope,
        bool removeLocalSlot = false,
        bool requireRevisionMatch = false,
        DateTimeOffset? expectedRevision = null) => new(
        credential.SyncId, credential.Token, scope, Guid.NewGuid().ToString("N"), UtcNow(),
        removeLocalSlot, requireRevisionMatch, expectedRevision);

    private static HoyoLabSyncCredential CredentialFor(HoyoLabSyncCrypto.DerivedSecrets secrets)
    {
        var token = secrets.Token.ToArray();
        var key = secrets.Key.ToArray();
        try
        {
            return new(secrets.SyncId, token, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static HoyoLabSyncCrypto.DerivedSecrets SecretsFor(HoyoLabSyncCredential credential) => new(
        credential.SyncId, credential.Token.ToArray(), credential.Key.ToArray(), null);

    private static bool NewerThanKnown(DateTimeOffset? observedAt, DateTimeOffset? knownAt) =>
        observedAt is not null && (knownAt is null || observedAt > knownAt);

    private static HoyoLabManualSyncResult Result(HoyoLabManualSyncStatus status) => new(status);

    private static HoyoLabManualSyncStatus Map(HoyoLabSyncFailure failure) => failure switch
    {
        HoyoLabSyncFailure.None => HoyoLabManualSyncStatus.Completed,
        HoyoLabSyncFailure.Conflict => HoyoLabManualSyncStatus.Conflict,
        HoyoLabSyncFailure.Authentication => HoyoLabManualSyncStatus.AuthenticationFailed,
        HoyoLabSyncFailure.Network or HoyoLabSyncFailure.RemoteFailure => HoyoLabManualSyncStatus.NetworkUnavailable,
        HoyoLabSyncFailure.Timeout => HoyoLabManualSyncStatus.TimedOut,
        HoyoLabSyncFailure.Canceled => HoyoLabManualSyncStatus.Canceled,
        HoyoLabSyncFailure.RateLimited => HoyoLabManualSyncStatus.RateLimited,
        HoyoLabSyncFailure.RequestTooLarge or HoyoLabSyncFailure.ResponseTooLarge => HoyoLabManualSyncStatus.TooLarge,
        _ => HoyoLabManualSyncStatus.InvalidCloudData,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) client.Dispose();
    }

    private sealed record ReadResult(
        HoyoLabManualSyncStatus Status,
        HoyoLabGameBundle? Bundle = null,
        DateTimeOffset? UpdatedAt = null)
    {
        public override string ToString() => nameof(ReadResult);
    }
}
