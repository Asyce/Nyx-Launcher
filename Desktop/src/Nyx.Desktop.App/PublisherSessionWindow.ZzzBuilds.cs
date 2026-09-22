using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherSessionWindow
{
    public async Task<HoyoLabZzzBuildReadResult> ReadZzzBuildsAsync(
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        var entry = PublisherAccountCatalog.Get("zzz");
        if (purpose != PublisherSessionPurpose.Resource || authorizedGameId != "zzz"
            || !PublisherAccountCatalog.IsValidRoleBinding("zzz", expectedBinding))
            return new(HoyoLabZzzBuildReadStatus.NeedsReview);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var controllerKey = "__pengoNyxZzzBuild_" + Guid.NewGuid().ToString("N");
        var serializedKey = JsonSerializer.Serialize(controllerKey);
        try
        {
            await NavigateAsync(entry.ResourceUri!, linked.Token);
            var started = await Browser.CoreWebView2!
                .ExecuteScriptAsync(HoyoLabZzzBuildCapture.CreateScript(controllerKey, expectedBinding))
                .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
            if (started != JsonSerializer.Serialize("started"))
                return new(HoyoLabZzzBuildReadStatus.NeedsReview);

            var deadline = Environment.TickCount64 + (HoyoLabZzzBuildCapture.TimeoutSeconds + 2) * 1000L;
            while (Environment.TickCount64 < deadline)
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await Browser.CoreWebView2.ExecuteScriptAsync(
                    $"window[{serializedKey}]?.result ?? null")
                    .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(2), linked.Token);
                if (result != "null")
                    return HoyoLabZzzBuildCapture.ParseResult(result, expectedBinding);
                await Task.Delay(200, linked.Token);
            }
            return new(HoyoLabZzzBuildReadStatus.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabZzzBuildReadStatus.Canceled);
        }
        catch (TimeoutException)
        {
            return new(HoyoLabZzzBuildReadStatus.TimedOut);
        }
        catch (Exception)
        {
            // No raw official response or account identifier goes into diagnostics.
            return new(HoyoLabZzzBuildReadStatus.NeedsReview);
        }
        finally
        {
            await AbortResourceFetchAsync(controllerKey);
        }
    }
}
