using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Receiver support is deployed; saved per-account Remember choices still apply.
    public static bool GenshinEventsAvailable => true;

    public async Task<HoyoLabGenshinEventsReadResult> RefreshGenshinEventsAsync(
        string expectedSlotId,
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        if (!GenshinEventsAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !PublisherAccountCatalog.IsValidRoleBinding("gi", expectedBinding)
            || !consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabGenshinEventsReadStatus.NotEnabled);

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
                return new(HoyoLabGenshinEventsReadStatus.NotEnabled);

            var before = genshinGameBundle.TryLoad();
            if (before?.Consents.Events != true || before.SelectedRole != expectedBinding
                || TryLoadRoleRecord("gi", operation)?.Binding != expectedBinding)
                return new(HoyoLabGenshinEventsReadStatus.NotEnabled);

            await using var window = CreateWindow("HoYoLAB", operation);
            await window.InitializeAsync(
                PublisherAccountCatalog.Get("gi").ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: "gi",
                "Refresh Genshin events",
                token);
            var proof = await window.GetSessionProofAsync(token);
            if (proof != PublisherSessionProof.Authenticated)
                return new(proof == PublisherSessionProof.LoginRequired
                    ? HoyoLabGenshinEventsReadStatus.LoginRequired
                    : HoyoLabGenshinEventsReadStatus.NeedsReview);

            var result = await window.ReadGenshinEventsAsync(expectedBinding, token);
            if (result.Status != HoyoLabGenshinEventsReadStatus.Completed || result.Snapshot is null)
                return result;

            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation) || !CanUseGameBundle("gi", operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabGenshinEventsReadStatus.Canceled);
                var current = genshinGameBundle.TryLoad();
                if (current?.Consents.Events != true || current.SelectedRole != expectedBinding
                    || TryLoadRoleRecord("gi", operation)?.Binding != expectedBinding)
                    return new(HoyoLabGenshinEventsReadStatus.Canceled);
                var observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!genshinGameBundle.TryRecordGenshinEvents(expectedBinding, result.Snapshot, observedAt, token))
                    return new(HoyoLabGenshinEventsReadStatus.LocalStorageUnavailable);
                if (!CanPublish("HoYoLAB", operation))
                    return new(HoyoLabGenshinEventsReadStatus.Canceled);
            }
            Updated?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabGenshinEventsReadStatus.Canceled);
        }
        catch (Exception)
        {
            // A failed refresh retains the last complete events observation.
            return new(HoyoLabGenshinEventsReadStatus.NeedsReview);
        }
        finally
        {
            if (enteredGate) hoyoGate.Release();
        }
    }
}
