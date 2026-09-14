using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Receiver support is deployed; saved per-account Remember choices still apply.
    public static bool GenshinBuildsAvailable => true;

    public async Task<HoyoLabGenshinBuildReadResult> RefreshGenshinBuildsAsync(
        string expectedSlotId,
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        if (!GenshinBuildsAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !PublisherAccountCatalog.IsValidRoleBinding("gi", expectedBinding)
            || !consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabGenshinBuildReadStatus.NotEnabled);

        using var operation = CreateOperation("HoYoLAB", cancellationToken);
        var token = operation.Cancellation.Token;
        var enteredGate = false;
        try
        {
            await hoyoGate.WaitAsync(token);
            enteredGate = true;
            if (!ProfileAccessAllowedAfterGate("HoYoLAB", consentRequired: true, operation)
                || !CanUseGameBundle("gi", operation)
                || operation.HoyoContext?.SlotId != expectedSlotId)
                return new(HoyoLabGenshinBuildReadStatus.NotEnabled);

            var before = genshinGameBundle.TryLoad();
            if (before?.Consents.Builds != true || before.SelectedRole != expectedBinding
                || TryLoadRoleRecord("gi", operation)?.Binding != expectedBinding)
                return new(HoyoLabGenshinBuildReadStatus.NotEnabled);

            await using var window = CreateWindow("HoYoLAB", operation);
            await window.InitializeAsync(
                PublisherAccountCatalog.Get("gi").ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: "gi",
                "Refresh Genshin characters and equipped builds",
                token);
            var proof = await window.GetSessionProofAsync(token);
            if (proof != PublisherSessionProof.Authenticated)
                return new(proof == PublisherSessionProof.LoginRequired
                    ? HoyoLabGenshinBuildReadStatus.LoginRequired
                    : HoyoLabGenshinBuildReadStatus.NeedsReview);

            var result = await window.ReadGenshinBuildsAsync(expectedBinding, token);
            if (result.Status != HoyoLabGenshinBuildReadStatus.Completed || result.Snapshot is null)
                return result;

            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation) || !CanUseGameBundle("gi", operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabGenshinBuildReadStatus.Canceled);
                var current = genshinGameBundle.TryLoad();
                if (current?.Consents.Builds != true || current.SelectedRole != expectedBinding
                    || TryLoadRoleRecord("gi", operation)?.Binding != expectedBinding)
                    return new(HoyoLabGenshinBuildReadStatus.Canceled);
                var observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!genshinGameBundle.TryRecordGenshinBuilds(expectedBinding, result.Snapshot, observedAt, token))
                    return new(HoyoLabGenshinBuildReadStatus.LocalStorageUnavailable);
                if (!CanPublish("HoYoLAB", operation))
                    return new(HoyoLabGenshinBuildReadStatus.Canceled);
            }
            _ = SyncHoyoAfterCaptureAsync("gi", operation, fullRefresh: true);
            Updated?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabGenshinBuildReadStatus.Canceled);
        }
        catch (Exception)
        {
            // A failed refresh retains the last complete build observation.
            return new(HoyoLabGenshinBuildReadStatus.NeedsReview);
        }
        finally
        {
            if (enteredGate) hoyoGate.Release();
        }
    }
}
