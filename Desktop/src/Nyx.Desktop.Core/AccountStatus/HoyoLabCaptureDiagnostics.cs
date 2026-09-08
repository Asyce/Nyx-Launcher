using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>Transient reader locations only; never raw errors, URLs or account data.</summary>
public static class HoyoLabCaptureDiagnostics
{
    public static string FromScriptFrames(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 64) return "reader-validation";
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > 3)
                return "reader-validation";
            var lines = new List<int>();
            foreach (var value in root.EnumerateArray())
            {
                if (!value.TryGetInt32(out var line) || line is < 1 or > 4096)
                    return "reader-validation";
                lines.Add(line);
            }
            return "reader-lines-" + string.Join('-', lines);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return "reader-validation";
        }
    }
}
