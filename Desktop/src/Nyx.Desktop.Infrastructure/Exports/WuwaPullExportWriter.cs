using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

internal sealed class WuwaPullExportWriter(
    string exportRootDirectory,
    PullExportSafetyLimits limits,
    TimeProvider timeProvider)
{
    public async ValueTask<AtomicExportResult> WriteAsync(
        WuwaPullArchive archive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = string.Empty;
        try
        {
            var directory = ResolveOutputDirectory();
            EnsureSafeDestination(directory);
            var baseName = timeProvider.GetUtcNow().ToString(
                "yyyyMMdd'T'HHmmss'Z'",
                System.Globalization.CultureInfo.InvariantCulture);
            var target = string.Empty;
            long bytes;
            for (var suffix = 0; suffix < 100; suffix++)
            {
                var name = suffix == 0
                    ? $"{baseName}-{Guid.NewGuid():N}.wwgf.json"
                    : $"{baseName}-{Guid.NewGuid():N} ({suffix}).wwgf.json";
                var candidate = Path.Combine(directory, name);
                if (!File.Exists(candidate))
                {
                    target = candidate;
                    break;
                }
            }
            if (target.Length == 0)
                throw new PullExportException(PullExportErrorCodes.OutputFailed);

            temporaryPath = Path.Combine(directory, "." + Path.GetFileName(target) + ".tmp");
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (!File.Exists(temporaryPath)) break;
                temporaryPath = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            }

            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                using var bounded = new MaximumLengthWriteStream(file, limits.MaximumOutputBytes);
                WriteWwgf(bounded, archive, cancellationToken);
                await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                bytes = bounded.BytesWritten;
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, target, overwrite: false);
            temporaryPath = string.Empty;
            return new AtomicExportResult(target, bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (PullExportException) { throw; }
        catch (Exception) { throw new PullExportException(PullExportErrorCodes.OutputFailed); }
        finally
        {
            if (temporaryPath.Length != 0)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception) { }
            }
        }
    }

    internal static void WriteWwgf(
        Stream output,
        WuwaPullArchive archive,
        CancellationToken cancellationToken = default)
    {
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WritePropertyName("ww");
        json.WriteStartArray();
        json.WriteStartObject();
        json.WriteString("uid", archive.Uid);
        json.WritePropertyName("list");
        json.WriteStartArray();
        foreach (var record in archive.Records
            .OrderBy(static value => value.Time, StringComparer.Ordinal)
            .ThenBy(static value => value.CardPoolType)
            .ThenBy(static value => value.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            json.WriteStartObject();
            json.WriteString("id", record.Id);
            json.WriteNumber("cardPoolType", record.CardPoolType);
            json.WriteString("resourceId", record.ResourceId);
            json.WriteNumber("qualityLevel", record.QualityLevel);
            json.WriteString("name", record.Name);
            json.WriteString("resourceType", record.ResourceType);
            json.WriteString("time", record.Time);
            json.WriteNumber("count", record.Count);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    private string ResolveOutputDirectory() =>
        Path.Combine(Path.GetFullPath(exportRootDirectory), "Pengo Exports", "Wuthering Waves");

    private void EnsureSafeDestination(string directory)
    {
        try
        {
            var root = Path.GetFullPath(exportRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var destination = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = Path.GetRelativePath(root, destination);
            if (Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new PullExportException(PullExportErrorCodes.OutputFailed);

            EnsureNoReparse(root, requireDirectory: false);
            Directory.CreateDirectory(root);
            EnsureNoReparse(root, requireDirectory: true);
            var current = root;
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                Directory.CreateDirectory(current);
                EnsureNoReparse(current, requireDirectory: true);
            }
        }
        catch (PullExportException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PullExportException(PullExportErrorCodes.OutputFailed);
        }
    }

    private static void EnsureNoReparse(string path, bool requireDirectory)
    {
        var current = Path.GetFullPath(path);
        var ancestors = new Stack<string>();
        while (!string.IsNullOrWhiteSpace(current))
        {
            ancestors.Push(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        foreach (var component in ancestors)
        {
            if (!File.Exists(component) && !Directory.Exists(component))
                continue;
            var attributes = File.GetAttributes(component);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new PullExportException(PullExportErrorCodes.OutputFailed);
        }
        if (requireDirectory)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0)
                throw new PullExportException(PullExportErrorCodes.OutputFailed);
        }
    }

    private sealed class MaximumLengthWriteStream(Stream inner, long maximumBytes) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten += buffer.Length;
        }
        private void EnsureCapacity(int count)
        {
            if (count < 0 || BytesWritten > maximumBytes - count)
                throw new PullExportException(PullExportErrorCodes.SafetyLimit);
        }
        protected override void Dispose(bool disposing) { }
    }
}
