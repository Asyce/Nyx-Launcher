using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

public static class SanitizedExportDiagnosticWriter
{
    private const int MaximumCodeLength = 80;

    public static void TryWrite(string dataDirectory, ExportJobSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsFinished) return;

        try
        {
            var diagnosticsDirectory = Path.Combine(
                Path.GetFullPath(dataDirectory),
                "Diagnostics");
            Directory.CreateDirectory(diagnosticsDirectory);
            var destination = Path.Combine(diagnosticsDirectory, "last-export.json");
            var temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                var payload = new
                {
                    schemaVersion = 1,
                    recordedAt = DateTimeOffset.UtcNow,
                    game = SafeGame(snapshot.GameId),
                    job = snapshot.State.ToString(),
                    pulls = SafeTask(snapshot.Pulls),
                    achievements = SafeTask(snapshot.Achievements),
                };
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    JsonSerializer.Serialize(stream, payload);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            // Diagnostics must never alter an export or game launch.
        }
    }

    private static object SafeTask(ExportTaskSnapshot task) => new
    {
        state = task.State.ToString(),
        error = SafeCode(task.ErrorCode),
        itemCount = task.Artifact?.ItemCount,
        byteCount = task.Artifact?.ByteCount,
        format = SafeCode(task.Artifact?.Format),
    };

    private static string SafeGame(string gameId) =>
        gameId is "gi" or "hsr" or "zzz" or "wuwa" or "ae"
            ? gameId
            : "custom";

    private static string? SafeCode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= MaximumCodeLength
            && value.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
                ? value
                : "unrecognized";
    }
}
