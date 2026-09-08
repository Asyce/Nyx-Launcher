using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx_Desktop_App;

public sealed partial class PublisherSessionWindow
{
    private async Task<string> ReadCaptureFailureLocationAsync(string controllerKey, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await Browser.CoreWebView2!.ExecuteScriptAsync(
                $"window[{JsonSerializer.Serialize(controllerKey)}]?.failureFrames ?? []")
                .AsTask(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            return HoyoLabCaptureDiagnostics.FromScriptFrames(raw);
        }
        catch (Exception)
        {
            return "reader-validation";
        }
    }
}
