using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherAccountService
{
    // Receiver support is deployed; saved per-account Remember choices still apply.
    public static bool HsrBuildsAvailable => true;

    public async Task<HoyoLabHsrBuildReadResult> RefreshHsrBuildsAsync(
        string expectedSlotId,
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken = default)
    {
        if (!HsrBuildsAvailable || !HoyoLabAccountSlotRules.IsValidSlotId(expectedSlotId)
            || !PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedBinding)
            || !consent.IsEnabled("HoYoLAB") || !HasUsableHoyoAccount()
            || !OwnsProfile("HoYoLAB") || disposed)
            return new(HoyoLabHsrBuildReadStatus.NotEnabled);

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
                return new(HoyoLabHsrBuildReadStatus.NotEnabled);

            var before = hoyoGameBundle.TryLoad();
            if (before?.Consents.Builds != true || before.SelectedRole != expectedBinding
                || TryLoadRoleRecord("hsr", operation)?.Binding != expectedBinding)
                return new(HoyoLabHsrBuildReadStatus.NotEnabled);

            await using var window = CreateWindow("HoYoLAB", operation);
            await window.InitializeAsync(
                PublisherAccountCatalog.Get("hsr").ResourceUri!,
                visible: false,
                purpose: PublisherSessionPurpose.Resource,
                gameId: "hsr",
                "Refresh Star Rail characters and equipped builds",
                token);
            var proof = await window.GetSessionProofAsync(token);
            if (proof != PublisherSessionProof.Authenticated)
                return new(proof == PublisherSessionProof.LoginRequired
                    ? HoyoLabHsrBuildReadStatus.LoginRequired
                    : HoyoLabHsrBuildReadStatus.NeedsReview);

            var result = await window.ReadHsrBuildsAsync(expectedBinding, token);
            if (result.Status != HoyoLabHsrBuildReadStatus.Completed || result.Snapshot is null)
                return result;

            lock (sync)
            {
                if (!CanPublish("HoYoLAB", operation) || !CanUseGameBundle("hsr", operation)
                    || operation.HoyoContext?.SlotId != expectedSlotId)
                    return new(HoyoLabHsrBuildReadStatus.Canceled);
                var current = hoyoGameBundle.TryLoad();
                if (current?.Consents.Builds != true || current.SelectedRole != expectedBinding
                    || TryLoadRoleRecord("hsr", operation)?.Binding != expectedBinding)
                    return new(HoyoLabHsrBuildReadStatus.Canceled);
                var observedAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (!hoyoGameBundle.TryRecordHsrBuilds(expectedBinding, result.Snapshot, observedAt, token))
                    return new(HoyoLabHsrBuildReadStatus.LocalStorageUnavailable);
                if (!CanPublish("HoYoLAB", operation))
                    return new(HoyoLabHsrBuildReadStatus.Canceled);
            }
            _ = SyncHoyoAfterCaptureAsync("hsr", operation, fullRefresh: true);
            Updated?.Invoke(this, EventArgs.Empty);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabHsrBuildReadStatus.Canceled);
        }
        catch (Exception)
        {
            // A failed refresh retains the last complete build observation.
            return new(HoyoLabHsrBuildReadStatus.NeedsReview);
        }
        finally
        {
            if (enteredGate) hoyoGate.Release();
        }
    }
}
