using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Receiver support is deployed; saved per-account Remember choices still apply.
    public static bool HsrEventsAvailable => true;

    public async Task<HoyoLabHsrEventsReadResult> RefreshHsrEventsAsync(
        string expectedSlotId,
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        if (!HsrEventsAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedBinding)
            || !consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabHsrEventsReadStatus.NotEnabled);

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
                return new(HoyoLabHsrEventsReadStatus.NotEnabled);

            var before = hoyoGameBundle.TryLoad();
            if (before?.Consents.Events != true || before.SelectedRole != expectedBinding
                || TryLoadRoleRecord("hsr", operation)?.Binding != expectedBinding)
                return new(HoyoLabHsrEventsReadStatus.NotEnabled);

            await using var window = CreateWindow("HoYoLAB", operation);
            await window.InitializeAsync(
                PublisherAccountCatalog.Get("hsr").ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: "hsr",
                "Refresh Star Rail events",
                token);
            var proof = await window.GetSessionProofAsync(token);
            if (proof != PublisherSessionProof.Authenticated)
                return new(proof == PublisherSessionProof.LoginRequired
                    ? HoyoLabHsrEventsReadStatus.LoginRequired
                    : HoyoLabHsrEventsReadStatus.NeedsReview);

            var result = await window.ReadHsrEventsAsync(expectedBinding, token);
            if (result.Status != HoyoLabHsrEventsReadStatus.Completed || result.Snapshot is null)
                return result;

            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation) || !CanUseGameBundle("hsr", operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabHsrEventsReadStatus.Canceled);
                var current = hoyoGameBundle.TryLoad();
                if (current?.Consents.Events != true || current.SelectedRole != expectedBinding
                    || TryLoadRoleRecord("hsr", operation)?.Binding != expectedBinding)
                    return new(HoyoLabHsrEventsReadStatus.Canceled);
                var observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!hoyoGameBundle.TryRecordHsrEvents(expectedBinding, result.Snapshot, observedAt, token))
                    return new(HoyoLabHsrEventsReadStatus.LocalStorageUnavailable);
                if (!CanPublish("HoYoLAB", operation))
                    return new(HoyoLabHsrEventsReadStatus.Canceled);
            }
            Updated?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabHsrEventsReadStatus.Canceled);
        }
        catch (Exception)
        {
            // A failed refresh retains the last complete events observation.
            return new(HoyoLabHsrEventsReadStatus.NeedsReview);
        }
        finally
        {
            if (enteredGate) hoyoGate.Release();
        }
    }
}
