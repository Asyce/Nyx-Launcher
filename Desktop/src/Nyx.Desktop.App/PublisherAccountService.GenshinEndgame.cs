using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Enabled only after receiver and integrated validation are complete.
    public static bool GenshinEndgameAvailable => false;

    public async Task<HoyoLabGenshinEndgameReadResult> RefreshGenshinEndgameAsync(
        string expectedSlotId,
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        if (!GenshinEndgameAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !PublisherAccountCatalog.IsValidRoleBinding("gi", expectedBinding)
            || !consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabGenshinEndgameReadStatus.NotEnabled);

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
                return new(HoyoLabGenshinEndgameReadStatus.NotEnabled);

            var before = genshinGameBundle.TryLoad();
            if (before?.Consents.Endgame != true || before.SelectedRole != expectedBinding
                || TryLoadRoleRecord("gi", operation)?.Binding != expectedBinding)
                return new(HoyoLabGenshinEndgameReadStatus.NotEnabled);

            await using var window = CreateWindow("HoYoLAB", operation);
            await window.InitializeAsync(
                PublisherAccountCatalog.Get("gi").ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: "gi",
                "Refresh Spiral Abyss records",
                token);
            var proof = await window.GetSessionProofAsync(token);
            if (proof != PublisherSessionProof.Authenticated)
                return new(proof == PublisherSessionProof.LoginRequired
                    ? HoyoLabGenshinEndgameReadStatus.LoginRequired
                    : HoyoLabGenshinEndgameReadStatus.NeedsReview);

            var result = await window.ReadGenshinEndgameAsync(expectedBinding, token);
            if (result.Status != HoyoLabGenshinEndgameReadStatus.Completed || result.Snapshot is null)
                return result;

            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation) || !CanUseGameBundle("gi", operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabGenshinEndgameReadStatus.Canceled);
                var current = genshinGameBundle.TryLoad();
                if (current?.Consents.Endgame != true || current.SelectedRole != expectedBinding
                    || TryLoadRoleRecord("gi", operation)?.Binding != expectedBinding)
                    return new(HoyoLabGenshinEndgameReadStatus.Canceled);
                var observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!genshinGameBundle.TryRecordGenshinEndgame(expectedBinding, result.Snapshot, observedAt, token))
                    return new(HoyoLabGenshinEndgameReadStatus.LocalStorageUnavailable);
                if (!CanPublish("HoYoLAB", operation))
                    return new(HoyoLabGenshinEndgameReadStatus.Canceled);
            }
            _ = SyncHoyoAfterCaptureAsync("gi", operation, fullRefresh: true);
            Updated?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabGenshinEndgameReadStatus.Canceled);
        }
        catch (Exception)
        {
            // A failed refresh retains the last complete Abyss observation.
            return new(HoyoLabGenshinEndgameReadStatus.NeedsReview);
        }
        finally
        {
            if (enteredGate) hoyoGate.Release();
        }
    }
}
