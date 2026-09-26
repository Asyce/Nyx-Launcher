using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Enabled only after receiver and integrated native acceptance are complete.
    public static bool ZzzBuildsAvailable => ZzzManualSyncAvailable;

    public async Task<HoyoLabZzzBuildReadResult> RefreshZzzBuildsAsync(
        string expectedSlotId,
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        if (!ZzzBuildsAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !PublisherAccountCatalog.IsValidRoleBinding("zzz", expectedBinding)
            || !consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabZzzBuildReadStatus.NotEnabled);

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
                return new(HoyoLabZzzBuildReadStatus.NotEnabled);

            var before = zzzGameBundle.TryLoad();
            if (before?.Consents.Builds != true || before.SelectedRole != expectedBinding
                || TryLoadRoleRecord("zzz", operation)?.Binding != expectedBinding)
                return new(HoyoLabZzzBuildReadStatus.NotEnabled);

            await using var window = CreateWindow("HoYoLAB", operation);
            await window.InitializeAsync(
                PublisherAccountCatalog.Get("zzz").ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: "zzz",
                "Refresh ZZZ Agents and equipped builds",
                token);
            var proof = await window.GetSessionProofAsync(token);
            if (proof != PublisherSessionProof.Authenticated)
                return new(proof == PublisherSessionProof.LoginRequired
                    ? HoyoLabZzzBuildReadStatus.LoginRequired
                    : HoyoLabZzzBuildReadStatus.NeedsReview);

            var result = await window.ReadZzzBuildsAsync(expectedBinding, token);
            if (result.Status != HoyoLabZzzBuildReadStatus.Completed || result.Snapshot is null)
                return result;

            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation) || !CanUseGameBundle("zzz", operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabZzzBuildReadStatus.Canceled);
                var current = zzzGameBundle.TryLoad();
                if (current?.Consents.Builds != true || current.SelectedRole != expectedBinding
                    || TryLoadRoleRecord("zzz", operation)?.Binding != expectedBinding)
                    return new(HoyoLabZzzBuildReadStatus.Canceled);
                var observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!zzzGameBundle.TryRecordZzzBuilds(expectedBinding, result.Snapshot, observedAt, token))
                    return new(HoyoLabZzzBuildReadStatus.LocalStorageUnavailable);
                if (!CanPublish("HoYoLAB", operation))
                    return new(HoyoLabZzzBuildReadStatus.Canceled);
            }
            _ = SyncHoyoAfterCaptureAsync("zzz", operation, fullRefresh: true);
            Updated?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabZzzBuildReadStatus.Canceled);
        }
        catch (Exception)
        {
            // A failed refresh retains the last complete build observation.
            return new(HoyoLabZzzBuildReadStatus.NeedsReview);
        }
        finally
        {
            if (enteredGate) hoyoGate.Release();
        }
    }
}
