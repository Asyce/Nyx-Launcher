using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Enabled only after receiver and integrated validation are complete.
    public static bool HsrEndgameAvailable => false;

    public async Task<HoyoLabHsrEndgameReadResult> RefreshHsrEndgameAsync(
        string expectedSlotId,
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        if (!HsrEndgameAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedBinding)
            || !consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabHsrEndgameReadStatus.NotEnabled);

        using var operation = CreateOperation("HoYoLAB", cancellationToken);
        var token = operation.Cancellation.Token;
        var enteredGate = false;
        try
        {
            await hoyoGate.WaitAsync(token);
            enteredGate = true;
            if (!ProfileAccessAllowedAfterGate("HoYoLAB", consentRequired: true, operation)
                || !CanUseGameBundle("hsr", operation)
                || operation.HoyoContext?.SlotId != expectedSlotId)
                return new(HoyoLabHsrEndgameReadStatus.NotEnabled);

            var before = hoyoGameBundle.TryLoad();
            if (before?.Consents.Endgame != true || before.SelectedRole != expectedBinding
                || TryLoadRoleRecord("hsr", operation)?.Binding != expectedBinding)
                return new(HoyoLabHsrEndgameReadStatus.NotEnabled);

            await using var window = CreateWindow("HoYoLAB", operation);
            await window.InitializeAsync(
                PublisherAccountCatalog.Get("hsr").ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: "hsr",
                "Refresh Star Rail challenge records",
                token);
            var proof = await window.GetSessionProofAsync(token);
            if (proof != PublisherSessionProof.Authenticated)
                return new(proof == PublisherSessionProof.LoginRequired
                    ? HoyoLabHsrEndgameReadStatus.LoginRequired
                    : HoyoLabHsrEndgameReadStatus.NeedsReview);

            var result = await window.ReadHsrEndgameAsync(expectedBinding, token);
            if (result.Status != HoyoLabHsrEndgameReadStatus.Completed || result.Snapshot is null)
                return result;

            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation) || !CanUseGameBundle("hsr", operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabHsrEndgameReadStatus.Canceled);
                var current = hoyoGameBundle.TryLoad();
                if (current?.Consents.Endgame != true || current.SelectedRole != expectedBinding
                    || TryLoadRoleRecord("hsr", operation)?.Binding != expectedBinding)
                    return new(HoyoLabHsrEndgameReadStatus.Canceled);
                var observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!hoyoGameBundle.TryRecordHsrEndgame(expectedBinding, result.Snapshot, observedAt, token))
                    return new(HoyoLabHsrEndgameReadStatus.LocalStorageUnavailable);
                if (!CanPublish("HoYoLAB", operation))
                    return new(HoyoLabHsrEndgameReadStatus.Canceled);
            }
            _ = SyncHoyoAfterCaptureAsync("hsr", operation, fullRefresh: true);
            Updated?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabHsrEndgameReadStatus.Canceled);
        }
        catch (Exception)
        {
            // A failed refresh retains the last complete challenge observation.
            return new(HoyoLabHsrEndgameReadStatus.NeedsReview);
        }
        finally
        {
            if (enteredGate) hoyoGate.Release();
        }
    }
}
