using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

internal sealed record AtomicExportResult(string Path, long ByteCount);

internal sealed class UigfPullExportWriter(
    string exportRootDirectory,
    PullExportSafetyLimits limits,
    TimeProvider timeProvider)
{
    public async ValueTask<AtomicExportResult> WriteAsync(
        HoyoPullArchive archive,
        string? requestedPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var usesContractName = string.IsNullOrWhiteSpace(requestedPath);
        var basePath = ResolveBasePath(archive.Game, requestedPath);
        var directory = Path.GetDirectoryName(basePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new PullExportException(PullExportErrorCodes.OutputFailed);
        var temporaryPath = string.Empty;
        try
        {
            EnsureSafeDestination(exportRootDirectory, directory);
            temporaryPath = Path.Combine(directory, "." + Path.GetFileName(basePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            long bytes;
            await using (var file = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                using var bounded = new MaximumLengthWriteStream(file, limits.MaximumOutputBytes);
                WriteUigf(bounded, archive, timeProvider.GetUtcNow(), cancellationToken);
                await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                bytes = bounded.BytesWritten;
            }

            cancellationToken.ThrowIfCancellationRequested();
            for (var suffix = 0; suffix < 100; suffix++)
            {
                var target = suffix == 0
                    ? basePath
                    : usesContractName
                        ? ResolveContractBasePath(archive.Game)
                        : WithSuffix(basePath, suffix);
                try
                {
                    File.Move(temporaryPath, target, overwrite: false);
                    temporaryPath = string.Empty;
                    return new AtomicExportResult(target, bytes);
                }
                catch (IOException) when (File.Exists(target)) { }
            }
            throw new PullExportException(PullExportErrorCodes.OutputFailed);
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

    internal static void WriteUigf(
        Stream output,
        HoyoPullArchive archive,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default)
    {
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WritePropertyName("info");
        json.WriteStartObject();
        json.WriteNumber("export_timestamp", exportedAt.ToUnixTimeSeconds());
        json.WriteString("export_app", "Pengo Nyx");
        json.WriteString("export_app_version", typeof(UigfPullExportWriter).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        json.WriteString("version", "v4.2");
        json.WriteEndObject();

        json.WritePropertyName(archive.Game.GameId switch
        {
            "gi" => "hk4e",
            "hsr" => "hkrpg",
            "zzz" => "nap",
            _ => throw new PullExportException(PullExportErrorCodes.UnsupportedGame),
        });
        json.WriteStartArray();
        json.WriteStartObject();
        json.WriteString("uid", archive.Uid);
        json.WriteNumber("timezone", archive.Timezone);
        json.WriteString("lang", archive.Language);
        json.WritePropertyName("list");
        json.WriteStartArray();
        foreach (var record in archive.Records
            .OrderByDescending(static value => value.Id.Length)
            .ThenByDescending(static value => value.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            json.WriteStartObject();
            if (archive.Game.GameId == "gi")
                json.WriteString("uigf_gacha_type", ToUigfGenshinType(record.GachaType));
            else
                json.WriteString("gacha_id", record.GachaId);
            json.WriteString("gacha_type", record.GachaType);
            json.WriteString("item_id", record.ItemId);
            json.WriteString("count", record.Count);
            json.WriteString("time", record.Time);
            json.WriteString("name", record.Name);
            json.WriteString("item_type", record.ItemType);
            json.WriteString("rank_type", record.RankType);
            json.WriteString("id", record.Id);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    private string ResolveBasePath(HoyoPullGameConfiguration game, string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            try
            {
                var full = Path.GetFullPath(requestedPath);
                if (!Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase))
                    throw new PullExportException(PullExportErrorCodes.OutputFailed);
                return full;
            }
            catch (PullExportException) { throw; }
            catch (Exception) { throw new PullExportException(PullExportErrorCodes.OutputFailed); }
        }

        return ResolveContractBasePath(game);
    }

    private string ResolveContractBasePath(HoyoPullGameConfiguration game)
    {
        var stamp = timeProvider.GetUtcNow().ToString(
            "yyyyMMdd'T'HHmmss'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        return Path.Combine(
            exportRootDirectory,
            "Pengo Exports",
            game.OutputFolder,
            $"{stamp}-{nonce}.uigf.json");
    }

    internal static void EnsureSafeDestination(string exportRootDirectory, string directory)
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
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PullExportException(PullExportErrorCodes.OutputFailed);
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
            if (!File.Exists(component) && !Directory.Exists(component)) continue;
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                throw new PullExportException(PullExportErrorCodes.OutputFailed);
        }
        if (requireDirectory
            && (File.GetAttributes(path) & FileAttributes.Directory) == 0)
            throw new PullExportException(PullExportErrorCodes.OutputFailed);
    }

    private static string WithSuffix(string path, int suffix) =>
        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + $" ({suffix})" + Path.GetExtension(path));

    private static string ToUigfGenshinType(string value) => value == "400" ? "301" : value;

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

public static class WindowsDocumentsDirectory
{
    public static string Get()
    {
        var resolved = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException("The Windows Documents known folder is unavailable.");
        return Path.GetFullPath(resolved);
    }
}
