using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherSessionWindow
{
    public async Task<HoyoLabHsrBuildReadResult> ReadHsrBuildsAsync(
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        var entry = PublisherAccountCatalog.Get("hsr");
        if (purpose != PublisherSessionPurpose.Resource || authorizedGameId != "hsr"
            || !PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedBinding))
            return new(HoyoLabHsrBuildReadStatus.NeedsReview);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var controllerKey = "__pengoNyxHsrBuilds_" + Guid.NewGuid().ToString("N");
        var serializedKey = JsonSerializer.Serialize(controllerKey);
        var stage = "reader-navigation";
        try
        {
            await NavigateAsync(entry.ResourceUri!, linked.Token);
            stage = "reader-script-start";
            var started = await Browser.CoreWebView2!
                .ExecuteScriptAsync(HoyoLabHsrBuildCapture.CreateScript(controllerKey, expectedBinding))
                .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
            if (started != JsonSerializer.Serialize("started"))
                return new(HoyoLabHsrBuildReadStatus.NeedsReview) { Diagnostic = stage };

            stage = "reader-result";
            var deadline = Environment.TickCount64 + (HoyoLabHsrBuildCapture.TimeoutSeconds + 2) * 1000L;
            while (Environment.TickCount64 < deadline)
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await Browser.CoreWebView2.ExecuteScriptAsync(
                    $"window[{serializedKey}]?.result ?? null")
                    .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(2), linked.Token);
                if (result != "null")
                {
                    var parsed = HoyoLabHsrBuildCapture.ParseResult(result, expectedBinding);
                    return parsed.Status == HoyoLabHsrBuildReadStatus.NeedsReview
                        ? parsed with { Diagnostic = await ReadCaptureFailureLocationAsync(controllerKey, linked.Token) }
                        : parsed;
                }
                await Task.Delay(200, linked.Token);
            }
            return new(HoyoLabHsrBuildReadStatus.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabHsrBuildReadStatus.Canceled);
        }
        catch (TimeoutException)
        {
            return new(HoyoLabHsrBuildReadStatus.TimedOut);
        }
        catch (Exception)
        {
            // No raw official response or account identifier goes into diagnostics.
            return new(HoyoLabHsrBuildReadStatus.NeedsReview) { Diagnostic = stage };
        }
        finally
        {
            await AbortResourceFetchAsync(controllerKey);
        }
    }
}
