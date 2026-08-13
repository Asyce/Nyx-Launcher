using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

/// <summary>One exact, reviewed WuWa history URL occurrence in Client.log.</summary>
internal sealed record WuwaPullHistoryUrl(
    string PlayerId,
    string RecordId,
    string ServerId,
    string LanguageCode,
    string ResourcesId)
{
    public override string ToString() => nameof(WuwaPullHistoryUrl);
}

internal sealed record WuwaPullHistoryCandidate(
    WuwaPullHistoryUrl Url,
    long StartOffset,
    long EndOffset)
{
    public override string ToString() => nameof(WuwaPullHistoryCandidate);
}

internal sealed record WuwaPullFileStamp(
    ulong VolumeSerialNumber,
    ulong FileIndex,
    long CreationTimeUtcTicks,
    long LastWriteTimeUtcTicks,
    long Length)
{
    public bool SameIdentity(WuwaPullFileStamp other) =>
        VolumeSerialNumber != 0 && FileIndex != 0 && other.VolumeSerialNumber != 0 && other.FileIndex != 0
            ? VolumeSerialNumber == other.VolumeSerialNumber && FileIndex == other.FileIndex
            : CreationTimeUtcTicks == other.CreationTimeUtcTicks;

    public override string ToString() => nameof(WuwaPullFileStamp);
}

internal sealed record WuwaPullHistoryObservation(
    string Path,
    WuwaPullFileStamp Stamp,
    IReadOnlyList<WuwaPullHistoryCandidate> Candidates,
    bool IsMasked)
{
    public override string ToString() => nameof(WuwaPullHistoryObservation);
}

/// <summary>
/// Reads one caller-selected Client.log with a bounded shared handle. It never
/// searches for another install, creates a cache, or returns the raw URL.
/// </summary>
internal sealed class WuwaPullHistoryLinkReader(PullExportSafetyLimits limits)
{
    private const string HistoryPath = "/aki/gacha/index.html";
    private const int MaximumUrlBytes = 16 * 1024;
    private static readonly HashSet<string> RequiredKeys = new(StringComparer.Ordinal)
    {
        "player_id", "record_id", "svr_id", "lang", "resources_id",
    };
    private static readonly HashSet<string> OptionalKeys = new(StringComparer.Ordinal)
    {
        "gacha_id", "gacha_type", "svr_area", "platform",
    };
    private const string AllowedHost = "aki-gm-resources-oversea.aki-game.net";

    public WuwaPullHistoryObservation Read(string path, CancellationToken cancellationToken)
    {
        try
        {
            var (bytes, count, sourceOffset, stamp) = ReadSharedBounded(path, cancellationToken);
            try
            {
                // Real Client.log files can contain binary records around the
                // ASCII history URL. Latin-1 preserves one character per byte;
                // the strict URL parser below still accepts only the reviewed
                // HTTPS host, path, keys, and ASCII/percent-encoded values.
                var text = Encoding.Latin1.GetString(bytes, 0, count);
                var candidates = ExtractCandidates(
                    text,
                    limits.MaximumCandidateUrls,
                    sourceOffset,
                    bytePreservingOffsets: true);
                var isMasked = false;
                if (candidates.Count == 0)
                {
                    DecodeMaskedLogInPlace(bytes, count, cancellationToken);
                    text = Encoding.Latin1.GetString(bytes, 0, count);
                    candidates = ExtractCandidates(
                        text,
                        limits.MaximumCandidateUrls,
                        sourceOffset,
                        bytePreservingOffsets: true);
                    isMasked = candidates.Count > 0;
                }
                return new(path, stamp, candidates, isMasked);
            }
            finally { Array.Clear(bytes); }
        }
        catch (OperationCanceledException) { throw; }
        catch (PullExportException) { throw; }
        catch (Exception)
        {
            throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
        }
    }

    private static void DecodeMaskedLogInPlace(
        byte[] bytes,
        int count,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            if ((index & 0xffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = bytes[index];
            bytes[index] = (byte)(value ^ ((value & 1) != 0 ? 0xa5 : 0xef));
        }
    }

    internal static IReadOnlyList<WuwaPullHistoryCandidate> ExtractCandidates(
        string text,
        int maximumCandidates,
        long sourceOffset = 0,
        bool bytePreservingOffsets = false)
    {
        if (text is null || maximumCandidates < 1) return [];
        var found = new Queue<WuwaPullHistoryCandidate>(maximumCandidates);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var start = text.IndexOf("https://", cursor, StringComparison.Ordinal);
            if (start < 0) break;
            var end = start;
            while (end < text.Length && !IsUrlTerminator(text[end]))
            {
                if (end - start > MaximumUrlBytes)
                {
                    end = -1;
                    break;
                }
                end++;
            }
            cursor = Math.Max(start + 8, end < 0 ? start + 8 : end);
            if (end <= start) continue;
            var raw = text[start..end];
            if (!TryParse(raw, out var url)) continue;
            if (found.Count == maximumCandidates) found.Dequeue();
            var startByte = bytePreservingOffsets
                ? start
                : Encoding.UTF8.GetByteCount(text.AsSpan(0, start));
            var endByte = bytePreservingOffsets
                ? end
                : Encoding.UTF8.GetByteCount(text.AsSpan(0, end));
            found.Enqueue(new(url!, sourceOffset + startByte, sourceOffset + endByte));
        }
        return found.Reverse().ToArray();
    }

    private static bool TryParse(string raw, out WuwaPullHistoryUrl? result)
    {
        result = null;
        if (raw.Length == 0 || raw.Length > MaximumUrlBytes || !raw.StartsWith("https://", StringComparison.Ordinal))
            return false;

        var hash = raw.IndexOf('#');
        if (hash <= "https://".Length || raw.IndexOf('#', hash + 1) >= 0)
            return false;
        var basePart = raw[..hash];
        if (!Uri.TryCreate(basePart, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || !uri.AbsolutePath.Equals(HistoryPath, StringComparison.Ordinal)
            || uri.Query.Length != 0)
            return false;

        var fragment = raw[(hash + 1)..];
        if (!fragment.StartsWith("/record?", StringComparison.Ordinal))
            return false;
        var query = fragment["/record?".Length..];
        if (query.Length == 0 || query.Contains('#', StringComparison.Ordinal)) return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in query.Split('&', StringSplitOptions.None))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0 || equals == segment.Length - 1) return false;
            var key = segment[..equals];
            if (!IsPlainKey(key) || (!RequiredKeys.Contains(key) && !OptionalKeys.Contains(key)))
                return false;
            if (!values.TryAdd(key, string.Empty)) return false;
            var rawValue = segment[(equals + 1)..];
            if (!TryPercentDecode(rawValue, out var value)
                || !IsSafeValue(value, key)) return false;
            values[key] = value;
        }

        if (RequiredKeys.Any(key => !values.TryGetValue(key, out var value) || value.Length == 0))
            return false;

        result = new(
            values["player_id"],
            values["record_id"],
            values["svr_id"],
            values["lang"],
            values["resources_id"]);
        return true;
    }

    private static bool IsPlainKey(string key) =>
        key.Length is > 0 and <= 32
        && key.All(static value => (value is >= 'a' and <= 'z') || (value is >= '0' and <= '9') || value == '_');

    private static bool IsSafeValue(string value, string key)
    {
        var maximum = key == "lang" ? 32 : 512;
        if (value.Length == 0 || value.Length > maximum) return false;
        if (value.Any(static value => char.IsControl(value) || char.IsWhiteSpace(value) || value is '#' or '&' or '?' or '/' or '\\'))
            return false;
        return value.All(static value => (value is >= 'A' and <= 'Z')
            || (value is >= 'a' and <= 'z')
            || (value is >= '0' and <= '9')
            || value is '-' or '_' or '.' or ':' or '~');
    }

    private static bool TryPercentDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Length > MaximumUrlBytes) return false;
        var bytes = new List<byte>(value.Length);
        try
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '%')
                {
                    if (index + 2 >= value.Length || !TryHex(value[index + 1], out var high) || !TryHex(value[index + 2], out var low))
                        return false;
                    bytes.Add((byte)((high << 4) | low));
                    index += 2;
                }
                else
                {
                    if (character > 0x7f) return false;
                    bytes.Add((byte)character);
                }
            }
            decoded = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException) { return false; }
        finally { bytes.Clear(); }
    }

    private static bool TryHex(char value, out int number)
    {
        number = value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };
        return number >= 0;
    }

    private (byte[] Bytes, int Count, long SourceOffset, WuwaPullFileStamp Stamp) ReadSharedBounded(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRegularPlainFile(sourcePath))
            throw new PullExportException(PullExportErrorCodes.HistoryNotFound);

        byte[]? bytes = null;
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            var initialLength = source.Length;
            if (initialLength > limits.MaximumSourceLogBytes || initialLength > int.MaxValue)
                throw new PullExportException(PullExportErrorCodes.CacheTooLarge);
            var sourceOffset = Math.Max(0, initialLength - limits.MaximumLogBytes);
            source.Position = sourceOffset;
            bytes = new byte[(int)(initialLength - sourceOffset)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) break;
                offset += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var finalLength = source.Length;
            if (finalLength > limits.MaximumSourceLogBytes || finalLength != initialLength)
                throw new PullExportException(finalLength > limits.MaximumSourceLogBytes
                    ? PullExportErrorCodes.CacheTooLarge
                    : PullExportErrorCodes.HistoryNotUpdated);
            var stamp = ReadStamp(source.SafeFileHandle, sourcePath, finalLength);
            var result = bytes;
            bytes = null;
            return (result, offset, sourceOffset, stamp);
        }
        finally
        {
            if (bytes is not null) Array.Clear(bytes);
        }
    }

    internal static bool IsRegularPlainFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception) { return false; }
    }

    internal static bool IsUrlTerminator(char value) =>
        value is '\0' or '\r' or '\n' or ' ' or '\t' or '"' or '\'' or '<' or '>' or ',';

    private static WuwaPullFileStamp ReadStamp(SafeFileHandle handle, string path, long length)
    {
        if (OperatingSystem.IsWindows() && GetFileInformationByHandle(handle, out var information))
        {
            return new(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                File.GetCreationTimeUtc(path).Ticks,
                FileTimeTicks(information.LastWriteTime),
                length);
        }

        return new(
            0,
            0,
            File.GetCreationTimeUtc(path).Ticks,
            File.GetLastWriteTimeUtc(path).Ticks,
            length);
    }

    private static long FileTimeTicks(NativeFileTime value) =>
        ((long)value.High << 32) | value.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
