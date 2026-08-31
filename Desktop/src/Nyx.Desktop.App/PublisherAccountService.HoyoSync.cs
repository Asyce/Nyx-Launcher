using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Receiver-first gate. Flip only after the authorized production receiver
    // and My HoYo route have passed live verification.
    public static bool HoyoLabManualSyncAvailable => true;

    public async Task<HoyoLabSyncSummary> GetHsrSyncSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        if (!HoyoLabManualSyncAvailable || !ownsHoyoProfile || disposed) return new(false, false, 0, null);
        using var operation = CreateOperation("HoYoLAB", cancellationToken);
        await hoyoGate.WaitAsync(operation.Cancellation.Token);
        try
        {
            var context = operation.HoyoContext;
            using var coordinator = context is { LegacyCompatibility: false, SlotId: not null }
                && OwnsProfile("HoYoLAB") && CanUseHsrGameBundle(operation) && CanPublish("HoYoLAB", operation)
                ? CreateHoyoSyncCoordinator(operation)
                : new HoyoLabSyncCoordinator(
                    root, null, null, publish => TryPublishHoyoSyncCleanup(operation, publish));
            return coordinator.GetSummary();
        }
        finally
        {
            hoyoGate.Release();
        }
    }

    public Task<HoyoLabManualSyncResult> ConnectHsrSyncAsync(
        string slotId,
        string recoveryCode,
        CancellationToken cancellationToken = default) =>
        RunHsrSyncAsync(slotId, (coordinator, token) => coordinator.ConnectAsync(recoveryCode, token), cancellationToken);

    public Task<HoyoLabManualSyncResult> SyncHsrNowAsync(string slotId, CancellationToken cancellationToken = default) =>
        RunHsrSyncAsync(slotId, (coordinator, token) => coordinator.SyncNowAsync(token), cancellationToken);

    public Task<HoyoLabManualSyncResult> StopHsrSyncAsync(string slotId, CancellationToken cancellationToken = default) =>
        RunHsrSyncAsync(slotId, (coordinator, token) => Task.FromResult(coordinator.Detach(cancellationToken: token)), cancellationToken);

    public Task<HoyoLabManualSyncResult> RotateHsrSyncCodeAsync(string slotId, CancellationToken cancellationToken = default) =>
        RunHsrSyncAsync(slotId, (coordinator, token) => coordinator.RotateAsync(token), cancellationToken);

    public Task<HoyoLabManualSyncResult> DeleteHsrCloudCopyAsync(string slotId, CancellationToken cancellationToken = default) =>
        RunHsrSyncAsync(slotId, async (coordinator, token) =>
        {
            var detached = coordinator.Detach(HoyoLabSyncStateStore.HsrScope, token);
            return detached.Status == HoyoLabManualSyncStatus.Completed
                ? await coordinator.RetryDeletionsAsync(token, TryRemoveHoyoSlotLocally)
                : detached;
        }, cancellationToken);

    public Task<HoyoLabManualSyncResult> DeleteHsrSyncedRoleAsync(
        string slotId,
        PublisherRoleBinding binding,
        CancellationToken cancellationToken = default) =>
        RunHsrSyncAsync(slotId, async (coordinator, token) =>
        {
            var queued = coordinator.QueueRoleDeletion(binding, token);
            RefreshHsrAfterSyncDeletion();
            return queued.Status == HoyoLabManualSyncStatus.Completed
                ? await coordinator.RetryDeletionsAsync(token, TryRemoveHoyoSlotLocally)
                : queued;
        }, cancellationToken, rotateSession: true);

    public async Task<HoyoLabManualSyncResult> RemoveHoyoLabAccountEverywhereAsync(
        string slotId,
        CancellationToken cancellationToken = default)
    {
        if (!HoyoLabManualSyncAvailable) return new(HoyoLabManualSyncStatus.NotEnabled);
        try
        {
            if (!await ForgetHoyoLabAccountCoreAsync(slotId, removeEverywhere: true, cancellationToken))
                return new(HoyoLabManualSyncStatus.LocalStorageUnavailable);
            return await RetryHoyoLabSyncDeletionsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabManualSyncStatus.Canceled);
        }
    }

    public async Task<HoyoLabManualSyncResult> RetryHoyoLabSyncDeletionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!HoyoLabManualSyncAvailable || !ownsHoyoProfile || disposed)
            return new(HoyoLabManualSyncStatus.NotEnabled);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, cancellationToken);
        try
        {
            await hoyoGate.WaitAsync(cancellation.Token);
            try
            {
                using var coordinator = new HoyoLabSyncCoordinator(root, null, null, publish =>
                {
                    lock (sync)
                    {
                        if (disposed || !ownsHoyoProfile || cancellation.IsCancellationRequested) return false;
                        publish();
                        return true;
                    }
                });
                var result = await coordinator.RetryDeletionsAsync(cancellation.Token, TryRemoveHoyoSlotLocally);
                RefreshHsrAfterSyncDeletion();
                Updated?.Invoke(this, EventArgs.Empty);
                return result;
            }
            finally
            {
                hoyoGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabManualSyncStatus.Canceled);
        }
    }

    private async Task<HoyoLabManualSyncResult> RunHsrSyncAsync(
        string expectedSlotId,
        Func<HoyoLabSyncCoordinator, CancellationToken, Task<HoyoLabManualSyncResult>> action,
        CancellationToken cancellationToken,
        bool rotateSession = false)
    {
        if (!HoyoLabManualSyncAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !consent.IsEnabled("HoYoLAB")
            || !HasUsableHoyoAccount() || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabManualSyncStatus.NotEnabled);
        CancellationTokenSource? previousSession = null;
        PublisherOperation operation;
        if (rotateSession)
        {
            var rotated = BeginRotatedOperation("HoYoLAB", cancellationToken);
            previousSession = rotated.PreviousSession;
            operation = rotated.Operation;
        }
        else
        {
            operation = CreateOperation("HoYoLAB", cancellationToken);
        }
        using (operation)
        {
            var enteredGate = false;
            try
            {
                if (previousSession is not null) await previousSession.CancelAsync();
                await hoyoGate.WaitAsync(operation.Cancellation.Token);
                enteredGate = true;
                if (!ProfileAccessAllowedAfterGate("HoYoLAB", consentRequired: true, operation)
                    || !CanUseHsrGameBundle(operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabManualSyncStatus.NotEnabled);
                _ = TryMigrateHsrBundleFromV1(operation);
                using var coordinator = CreateHoyoSyncCoordinator(operation);
                var result = await action(coordinator, operation.Cancellation.Token);
                lock (sync)
                {
                    if (!CanPublish("HoYoLAB", operation)) return new(HoyoLabManualSyncStatus.Canceled);
                }
                Updated?.Invoke(this, EventArgs.Empty);
                return result;
            }
            catch (OperationCanceledException)
            {
                return new(HoyoLabManualSyncStatus.Canceled);
            }
            finally
            {
                if (enteredGate) hoyoGate.Release();
                previousSession?.Dispose();
            }
        }
    }

    private HoyoLabSyncCoordinator CreateHoyoSyncCoordinator(PublisherOperation operation)
    {
        var context = operation.HoyoContext!;
        return new(root, context.SlotId, context.ProtectedStateRoot, publish =>
        {
            lock (sync)
            {
                if (!OwnsProfile("HoYoLAB") || !CanPublish("HoYoLAB", operation) || disposed) return false;
                publish();
                return true;
            }
        });
    }

    private bool TryPublishHoyoSyncCleanup(PublisherOperation operation, Action publish)
    {
        lock (sync)
        {
            if (disposed || !ownsHoyoProfile || operation.Cancellation.IsCancellationRequested
                || !hoyoGeneration.IsCurrent(operation.Generation))
                return false;
            publish();
            return true;
        }
    }

    private bool TryDetachCapturedHoyoSyncState(
        PublisherOperation operation,
        bool finishCapturedCleanup = false)
    {
        if (operation.HoyoContext is not { SlotId: not null, LegacyCompatibility: false } context)
            return true;
        using var coordinator = new HoyoLabSyncCoordinator(
            root, context.SlotId, context.ProtectedStateRoot, publish =>
            {
                if (!finishCapturedCleanup) return TryPublishHoyoSyncCleanup(operation, publish);
                // The existing provider gate still owns this exact retired slot.
                // Finish its local-only cleanup after irreversible profile deletion.
                lock (sync)
                {
                    if (!ownsHoyoProfile) return false;
                    publish();
                    return true;
                }
            });
        return coordinator.Detach(
            cancellationToken: finishCapturedCleanup ? CancellationToken.None : operation.Cancellation.Token).Status
            == HoyoLabManualSyncStatus.Completed;
    }

    private bool TryRemoveHoyoSlotLocally(string slotId)
    {
        if (!HoyoLabAccountSlotRules.IsValidSlotId(slotId) || !OwnsProfile("HoYoLAB")) return false;
        var target = hoyoSlots.TryLoad()?.Slots.SingleOrDefault(slot => slot.Id == slotId);
        if (target is null) return hoyoSlots.IsSlotRemoved(slotId);
        if (!target.RemovalPending && !hoyoSlots.TryMarkRemovalPending(target.Id)) return false;
        var wasActive = string.Equals(activeHoyoSlot?.Id, target.Id, StringComparison.Ordinal);
        RefreshActiveHoyoSlot();
        target = FindHoyoSlot(slotId);
        if (target is null || !target.RemovalPending) return false;
        if (wasActive)
        {
            ClearProviderState("HoYoLAB");
            SetConnection("HoYoLAB", PublisherConnectionState.NotConnected);
        }
        // Explicit account removal also clears the imported account's original
        // session/snapshots; retaining v1 for migration is not a deletion exemption.
        if (target.IsLegacy
            && (!hoyoSlots.TryGetWebView2ProfilePath(target, out var legacyProfile)
                || !TryDeleteExactDirectory(legacyProfile)
                || !new PublisherResourceSnapshotStore(root).DeleteProvider("HoYoLAB")
                || !new PublisherRoleBindingStore(root).DeleteProvider("HoYoLAB")
                || !new HoyoLabGameBundleStore(root).TryDelete()))
            return false;
        if (!hoyoSlots.TryGetSlotContainerPath(target, out var containerPath)
            || !TryDeleteManagedDirectory(containerPath))
            return false;
        if (!hoyoSlots.TryRemoveSlot(target.Id)) return false;
        RefreshActiveHoyoSlot();
        Updated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RefreshHsrAfterSyncDeletion()
    {
        lock (sync)
        {
            if (roleBindings.TryLoad(HoyoLabGameBundleRules.GameId) is not null) return;
            resources.Remove(HoyoLabGameBundleRules.GameId);
            resourceStates.Remove(HoyoLabGameBundleRules.GameId);
            resourceDiagnostics.Remove(HoyoLabGameBundleRules.GameId);
            checkIns.Remove(HoyoLabGameBundleRules.GameId);
        }
    }
}
