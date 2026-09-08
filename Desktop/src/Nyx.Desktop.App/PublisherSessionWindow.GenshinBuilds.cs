using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherSessionWindow
{
    public async Task<HoyoLabGenshinBuildReadResult> ReadGenshinBuildsAsync(
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        var entry = PublisherAccountCatalog.Get("gi");
        if (purpose != PublisherSessionPurpose.Resource || authorizedGameId != "gi"
            || !PublisherAccountCatalog.IsValidRoleBinding("gi", expectedBinding))
            return new(HoyoLabGenshinBuildReadStatus.NeedsReview);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var controllerKey = "__pengoNyxGenshinBuilds_" + Guid.NewGuid().ToString("N");
        var serializedKey = JsonSerializer.Serialize(controllerKey);
        try
        {
            await NavigateAsync(entry.ResourceUri!, linked.Token);
            var started = await Browser.CoreWebView2!
                .ExecuteScriptAsync(HoyoLabGenshinBuildCapture.CreateScript(controllerKey, expectedBinding))
                .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
            if (started != JsonSerializer.Serialize("started"))
                return new(HoyoLabGenshinBuildReadStatus.NeedsReview);

            var deadline = Environment.TickCount64 + (HoyoLabGenshinBuildCapture.TimeoutSeconds + 2) * 1000L;
            while (Environment.TickCount64 < deadline)
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await Browser.CoreWebView2.ExecuteScriptAsync(
                    $"window[{serializedKey}]?.result ?? null")
                    .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(2), linked.Token);
                if (result != "null")
                    return HoyoLabGenshinBuildCapture.ParseResult(result, expectedBinding);
                await Task.Delay(200, linked.Token);
            }
            return new(HoyoLabGenshinBuildReadStatus.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabGenshinBuildReadStatus.Canceled);
        }
        catch (TimeoutException)
        {
            return new(HoyoLabGenshinBuildReadStatus.TimedOut);
        }
        catch (Exception)
        {
            // No raw official response or account identifier goes into diagnostics.
            return new(HoyoLabGenshinBuildReadStatus.NeedsReview);
        }
        finally
        {
            await AbortResourceFetchAsync(controllerKey);
        }
    }
}
