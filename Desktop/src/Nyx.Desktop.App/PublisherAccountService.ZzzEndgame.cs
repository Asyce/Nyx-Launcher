using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Enabled only after receiver and integrated validation are complete.
    public static bool ZzzEndgameAvailable => ZzzManualSyncAvailable;

    public async Task<HoyoLabZzzEndgameReadResult> RefreshZzzEndgameAsync(
        string expectedSlotId,
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        if (!ZzzEndgameAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !PublisherAccountCatalog.IsValidRoleBinding("zzz", expectedBinding)
            || !consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabZzzEndgameReadStatus.NotEnabled);

        using var operation = CreateOperation("HoYoLAB", cancellationToken);
        var token = operation.Cancellation.Token;
        var enteredGate = false;
        try
        {
            await hoyoGate.WaitAsync(token);
            enteredGate = true;
            if (!ProfileAccessAllowedAfterGate("HoYoLAB", consentRequired: true, operation)
                || !CanUseGameBundle("zzz", operation)
                || operation.HoyoContext?.SlotId != expectedSlotId)
                return new(HoyoLabZzzEndgameReadStatus.NotEnabled);

            var before = zzzGameBundle.TryLoad();
            if (before?.Consents.Endgame != true || before.SelectedRole != expectedBinding
                || TryLoadRoleRecord("zzz", operation)?.Binding != expectedBinding)
                return new(HoyoLabZzzEndgameReadStatus.NotEnabled);

            await using var window = CreateWindow("HoYoLAB", operation);
            await window.InitializeAsync(
                PublisherAccountCatalog.Get("zzz").ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: "zzz",
                "Refresh Shiyu Defense records",
                token);
            var proof = await window.GetSessionProofAsync(token);
            if (proof != PublisherSessionProof.Authenticated)
                return new(proof == PublisherSessionProof.LoginRequired
                    ? HoyoLabZzzEndgameReadStatus.LoginRequired
                    : HoyoLabZzzEndgameReadStatus.NeedsReview);

            var result = await window.ReadZzzEndgameAsync(expectedBinding, token);
            if (result.Status != HoyoLabZzzEndgameReadStatus.Completed || result.Snapshot is null)
                return result;

            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation) || !CanUseGameBundle("zzz", operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabZzzEndgameReadStatus.Canceled);
                var current = zzzGameBundle.TryLoad();
                if (current?.Consents.Endgame != true || current.SelectedRole != expectedBinding
                    || TryLoadRoleRecord("zzz", operation)?.Binding != expectedBinding)
                    return new(HoyoLabZzzEndgameReadStatus.Canceled);
                var observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!zzzGameBundle.TryRecordZzzEndgame(expectedBinding, result.Snapshot, observedAt, token))
                    return new(HoyoLabZzzEndgameReadStatus.LocalStorageUnavailable);
                if (!CanPublish("HoYoLAB", operation))
                    return new(HoyoLabZzzEndgameReadStatus.Canceled);
            }
            _ = SyncHoyoAfterCaptureAsync("zzz", operation, fullRefresh: true);
            Updated?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabZzzEndgameReadStatus.Canceled);
        }
        catch (Exception)
        {
            // A failed refresh retains the last complete Shiyu observation.
            return new(HoyoLabZzzEndgameReadStatus.NeedsReview);
        }
        finally
        {
            if (enteredGate) hoyoGate.Release();
        }
    }
}
