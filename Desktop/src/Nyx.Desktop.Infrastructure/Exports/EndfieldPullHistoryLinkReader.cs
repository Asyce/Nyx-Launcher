using System.Text;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Infrastructure.Exports;

internal sealed record EndfieldPullSource(string Path, string BindingRoot);

internal sealed record EndfieldPullCredential(string Token, string ServerId, string Language)
{
    public override string ToString() => nameof(EndfieldPullCredential);
}

internal sealed record EndfieldPullHistoryCandidate(
    EndfieldPullCredential Credential,
    long StartOffset,
    long EndOffset)
{
    public override string ToString() => nameof(EndfieldPullHistoryCandidate);
}

internal sealed record EndfieldPullFileStamp(
    uint VolumeSerialNumber,
    ulong FileId,
    long LastWriteTimeUtcTicks,
    long Length)
{
    public bool SameFileAs(EndfieldPullFileStamp other) =>
        VolumeSerialNumber == other.VolumeSerialNumber && FileId == other.FileId;
}

internal sealed record EndfieldPullHistoryObservation(
    EndfieldPullSource Source,
    EndfieldPullFileStamp Stamp,
    IReadOnlyList<EndfieldPullHistoryCandidate> Candidates);

/// <summary>Reads only the two caller-owned Endfield history sources and never returns a raw URL.</summary>
internal sealed class EndfieldPullHistoryLinkReader
{
    private const int TailBytes = 8 * 1024 * 1024;
    private const int MaximumUrlBytes = 16 * 1024;
    private const int MaximumCandidates = 64;
    private const uint GenericRead = 0x80000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private static readonly HashSet<string> PageKeys = new(StringComparer.Ordinal)
    {
        "u8_token", "server", "server_id", "lang", "platform", "channel", "subChannel", "pool_id",
    };
    private static readonly HashSet<string> ApiKeys = new(StringComparer.Ordinal)
    {
        "token", "server_id", "lang", "pool_type", "pool_id", "seq_id",
    };

    public EndfieldPullHistoryObservation Read(
        EndfieldPullSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[]? bytes = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source.Path))
                throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
            using var ancestors = PublisherAncestorDirectoryBinding.Open(source.BindingRoot, source.Path);
            using var handle = NativeMethods.CreateFileW(
                source.Path,
                GenericRead,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException();
            var before = CaptureStamp(handle);
            if (before.NumberOfLinks != 1) throw new IOException();

            using var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            var sourceOffset = Math.Max(0, before.Stamp.Length - TailBytes);
            stream.Position = sourceOffset;
            bytes = new byte[checked((int)(before.Stamp.Length - sourceOffset))];
            var count = 0;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var after = CaptureStamp(stream.SafeFileHandle);
            if (before != after || stream.Length != before.Stamp.Length || count != bytes.Length)
                throw new PullExportException(PullExportErrorCodes.HistoryNotUpdated);

            var text = Encoding.Latin1.GetString(bytes, 0, count);
            var candidates = ExtractCandidates(text, sourceOffset);
            return new(source, before.Stamp, candidates);
        }
        catch (OperationCanceledException) { throw; }
        catch (PullExportException) { throw; }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
        }
        catch (Exception)
        {
            throw new PullExportException(PullExportErrorCodes.InvalidHistoryLink);
        }
        finally
        {
            if (bytes is not null) Array.Clear(bytes);
        }
    }

    internal static IReadOnlyList<EndfieldPullHistoryCandidate> ExtractCandidates(
        string text,
        long sourceOffset = 0)
    {
        if (text is null) return [];
        var found = new Queue<EndfieldPullHistoryCandidate>(MaximumCandidates);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var start = text.IndexOf("https://", cursor, StringComparison.Ordinal);
            if (start < 0) break;
            var end = start;
            while (end < text.Length && !IsUrlTerminator(text[end]))
            {
                if (end - start >= MaximumUrlBytes) { end = -1; break; }
                end++;
            }
            cursor = end < 0 ? start + 8 : Math.Max(start + 8, end);
            if (end <= start || !TryParseCandidate(text[start..end], out var credential)) continue;
            if (found.Count == MaximumCandidates) found.Dequeue();
            found.Enqueue(new(credential!, sourceOffset + start, sourceOffset + end));
        }
        return found.Reverse().ToArray();
    }

    private static bool TryParseCandidate(string raw, out EndfieldPullCredential? credential)
    {
        credential = null;
        if (raw.Length is 0 or > MaximumUrlBytes
            || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !uri.Host.Equals("ef-webview.gryphline.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || uri.Query.Length <= 1)
            return false;

        var page = uri.AbsolutePath.Equals("/page/gacha_char", StringComparison.Ordinal);
        var api = uri.AbsolutePath is "/api/record/char" or "/api/record/weapon/pool" or "/api/record/weapon";
        if (!page && !api) return false;
        var allowed = page ? PageKeys : ApiKeys;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in uri.Query[1..].Split('&', StringSplitOptions.None))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0 || equals == segment.Length - 1) return false;
            string key;
            string value;
            try
            {
                key = Uri.UnescapeDataString(segment[..equals]);
                value = Uri.UnescapeDataString(segment[(equals + 1)..].Replace('+', ' '));
            }
            catch (Exception) { return false; }
            if (!allowed.Contains(key) || !values.TryAdd(key, value) || !IsSafeValue(key, value)) return false;
        }

        var tokenKey = page ? "u8_token" : "token";
        if (!values.TryGetValue(tokenKey, out var token)) return false;
        var serverKeys = page ? new[] { "server", "server_id" } : ["server_id"];
        var servers = serverKeys.Where(values.ContainsKey).ToArray();
        if (servers.Length != 1) return false;
        credential = new(token, values[servers[0]], values.GetValueOrDefault("lang", "en-us"));
        return true;
    }

    internal static bool IsSafeValue(string key, string value)
    {
        var maximum = key is "u8_token" or "token" ? 4_096 : 512;
        if (value.Length is 0 || value.Length > maximum
            || value.Any(static character => character > 0x7f || char.IsControl(character) || char.IsWhiteSpace(character)))
            return false;
        if (key is "u8_token" or "token")
            return value.All(static character => character is >= '!' and <= '~' and not '&' and not '#' and not '?' and not '\\');
        return value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '~');
    }

    private static TailStamp CaptureStamp(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out var information)
            || ((FileAttributes)information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException();
        var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        var lastWrite = ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow;
        DateTime lastWriteUtc;
        try { lastWriteUtc = DateTime.FromFileTimeUtc(lastWrite); }
        catch (ArgumentOutOfRangeException) { throw new IOException(); }
        return new(
            new(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                lastWriteUtc.Ticks,
                length),
            information.NumberOfLinks);
    }

    private static bool IsUrlTerminator(char value) =>
        value is '\0' or '\r' or '\n' or ' ' or '\t' or '"' or '\'' or '<' or '>' or ',';

    private sealed record TailStamp(EndfieldPullFileStamp Stamp, uint NumberOfLinks);
}
