using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherSessionWindow
{
    public async Task<HoyoLabHsrEndgameReadResult> ReadHsrEndgameAsync(
        PublisherRoleBinding expectedBinding,
        CancellationToken cancellationToken)
    {
        var entry = PublisherAccountCatalog.Get("hsr");
        if (purpose != PublisherSessionPurpose.Resource || authorizedGameId != "hsr"
            || !PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedBinding))
            return new(HoyoLabHsrEndgameReadStatus.NeedsReview);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var controllerKey = "__pengoNyxHsrEndgame_" + Guid.NewGuid().ToString("N");
        var serializedKey = JsonSerializer.Serialize(controllerKey);
        try
        {
            await NavigateAsync(entry.ResourceUri!, linked.Token);
            var started = await Browser.CoreWebView2!
                .ExecuteScriptAsync(HoyoLabHsrEndgameCapture.CreateScript(controllerKey, expectedBinding))
                .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
            if (started != JsonSerializer.Serialize("started"))
                return new(HoyoLabHsrEndgameReadStatus.NeedsReview);

            var deadline = Environment.TickCount64 + (HoyoLabHsrEndgameCapture.TimeoutSeconds + 2) * 1000L;
            while (Environment.TickCount64 < deadline)
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = await Browser.CoreWebView2.ExecuteScriptAsync(
                    $"window[{serializedKey}]?.result ?? null")
                    .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(2), linked.Token);
                if (result != "null")
                    return HoyoLabHsrEndgameCapture.ParseResult(result, expectedBinding);
                await Task.Delay(200, linked.Token);
            }
            return new(HoyoLabHsrEndgameReadStatus.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new(HoyoLabHsrEndgameReadStatus.Canceled);
        }
        catch (TimeoutException)
        {
            return new(HoyoLabHsrEndgameReadStatus.TimedOut);
        }
        catch (Exception)
        {
            // No raw official response or account identifier goes into diagnostics.
            return new(HoyoLabHsrEndgameReadStatus.NeedsReview);
        }
        finally
        {
            await AbortResourceFetchAsync(controllerKey);
        }
    }
}
