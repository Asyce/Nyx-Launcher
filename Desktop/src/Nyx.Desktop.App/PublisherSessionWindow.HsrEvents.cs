using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherSessionWindow
{
    public async Task<HoyoLabHsrEventsReadResult> ReadHsrEventsAsync(
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        var entry = PublisherAccountCatalog.Get("hsr");
        if (purpose != PublisherSessionPurpose.Resource || authorizedGameId != "hsr"
            || !PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedBinding))
            return new(HoyoLabHsrEventsReadStatus.NeedsReview);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var controllerKey = "__pengoNyxHsrEvents_" + Guid.NewGuid().ToString("N");
        var serializedKey = JsonSerializer.Serialize(controllerKey);
        var stage = "reader-navigation";
        try
        {
            await NavigateAsync(entry.ResourceUri!, linked.Token);
            stage = "reader-script-start";
            var started = await Browser.CoreWebView2!
                .ExecuteScriptAsync(HoyoLabHsrEventsCapture.CreateScript(controllerKey, expectedBinding))
                .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
            if (started != JsonSerializer.Serialize("started"))
                return new(HoyoLabHsrEventsReadStatus.NeedsReview) { Diagnostic = stage };

            stage = "reader-result";
            var deadline = Environment.TickCount64 + (HoyoLabHsrEventsCapture.TimeoutSeconds + 2) * 1000L;
            while (Environment.TickCount64 < deadline)
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await Browser.CoreWebView2.ExecuteScriptAsync(
                    $"window[{serializedKey}]?.result ?? null")
                    .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(2), linked.Token);
                if (result != "null")
                {
                    var parsed = HoyoLabHsrEventsCapture.ParseResult(result, expectedBinding);
                    return parsed.Status == HoyoLabHsrEventsReadStatus.NeedsReview
                        ? parsed with { Diagnostic = await ReadCaptureFailureLocationAsync(controllerKey, linked.Token) }
                        : parsed;
                }
                await Task.Delay(200, linked.Token);
            }
            return new(HoyoLabHsrEventsReadStatus.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabHsrEventsReadStatus.Canceled);
        }
        catch (TimeoutException)
        {
            return new(HoyoLabHsrEventsReadStatus.TimedOut);
        }
        catch (Exception)
        {
            // No raw official response or account identifier goes into diagnostics.
            return new(HoyoLabHsrEventsReadStatus.NeedsReview) { Diagnostic = stage };
        }
        finally
        {
            await AbortResourceFetchAsync(controllerKey);
        }
    }
}
