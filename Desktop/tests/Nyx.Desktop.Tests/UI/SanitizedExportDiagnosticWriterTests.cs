using System.Text.Json;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.UI;

public sealed class SanitizedExportDiagnosticWriterTests
{
    [Fact]
    public void Writes_only_safe_summary_without_paths_or_account_data()
    {
        using var temporary = new TemporaryDirectory();
        var snapshot = new ExportJobSnapshot(
            Guid.NewGuid(),
            "hsr",
            ExportJobState.Failed,
            new(ExportTaskState.NotRequested),
            new(ExportTaskState.Failed, "hoyolab-response-invalid"),
            DateTimeOffset.UtcNow.AddSeconds(-2),
            DateTimeOffset.UtcNow);

        SanitizedExportDiagnosticWriter.TryWrite(temporary.Path, snapshot);

        var path = Path.Combine(temporary.Path, "Diagnostics", "last-export.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("hsr", root.GetProperty("game").GetString());
        Assert.Equal(
            "hoyolab-response-invalid",
            root.GetProperty("achievements").GetProperty("error").GetString());
        var json = root.GetRawText();
        Assert.DoesNotContain("outputPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uid", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Replaces_unsafe_values_and_ignores_unfinished_jobs()
    {
        using var temporary = new TemporaryDirectory();
        SanitizedExportDiagnosticWriter.TryWrite(
            temporary.Path,
            new ExportJobSnapshot(
                Guid.NewGuid(),
                "unexpected-game",
                ExportJobState.Failed,
                new(ExportTaskState.Failed, "bad secret/value"),
                new(ExportTaskState.NotRequested),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));

        var path = Path.Combine(temporary.Path, "Diagnostics", "last-export.json");
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.Equal("custom", document.RootElement.GetProperty("game").GetString());
            Assert.Equal(
                "unrecognized",
                document.RootElement.GetProperty("pulls").GetProperty("error").GetString());
        }

        File.Delete(path);
        SanitizedExportDiagnosticWriter.TryWrite(
            temporary.Path,
            new ExportJobSnapshot(
                Guid.NewGuid(),
                "hsr",
                ExportJobState.Running,
                new(ExportTaskState.Running),
                new(ExportTaskState.Running),
                DateTimeOffset.UtcNow));
        Assert.False(File.Exists(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nyx-export-diagnostic-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
