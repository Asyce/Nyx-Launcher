using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Buffers.Binary;
using Nyx.Desktop.Core.Content;

namespace Nyx.Desktop.Infrastructure.Content;

public sealed class LauncherBannersCache
{
    public const long MaximumManagedBytes = 150L * 1024 * 1024;
    public string RootDirectory { get; }
    public string ManagedDirectory { get; }
    public string ManagedAssetsDirectory { get; }
    public string LastKnownGoodDirectory { get; }
    public string UserArtDirectory { get; }

    public LauncherBannersCache(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("A cache directory is required.", nameof(rootDirectory));
        RootDirectory = Path.GetFullPath(rootDirectory);
        ManagedDirectory = Path.Combine(RootDirectory, "managed");
        ManagedAssetsDirectory = Path.Combine(ManagedDirectory, "assets");
        LastKnownGoodDirectory = Path.Combine(RootDirectory, "last-known-good");
        UserArtDirectory = Path.Combine(RootDirectory, "user-art");
    }

    public string LastKnownGoodManifestPath => Path.Combine(LastKnownGoodDirectory, "launcher-banners-v1.json");
    public string LastKnownGoodCodesPath => Path.Combine(LastKnownGoodDirectory, "launcher-codes-v1.json");
    public string LastKnownGoodToolsPath => Path.Combine(LastKnownGoodDirectory, "launcher-tools-v1.json");

    public string? TryResolveManagedAsset(LauncherBannersAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var path = Path.Combine(ManagedAssetsDirectory, asset.Sha256 + Extension(asset.Mime));
        if (!IsSafeOwnedCachePath(path, mustExist: true)) return null;
        var result = TryValidateFile(path, asset);
        return result is not null && IsSafeOwnedCachePath(result, mustExist: true) ? result : null;
    }

    public string? TryResolveBundledAsset(LauncherBannersAsset asset, string bundledAssetsDirectory)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(bundledAssetsDirectory)) return null;
        const string prefix = "/launcher-art/";
        if (!asset.Path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var root = Path.GetFullPath(bundledAssetsDirectory);
        var relative = asset.Path[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsSafeContainedPath(root, path, mustExist: true)) return null;
        if (!string.Equals(Path.GetFileName(path), asset.Sha256 + Extension(asset.Mime), StringComparison.OrdinalIgnoreCase)) return null;
        var result = TryValidateFile(path, asset);
        return result is not null && IsSafeContainedPath(root, result, mustExist: true) ? result : null;
    }

    public LauncherBannersManifest? TryLoadLastKnownGood(DateTimeOffset observedAt, string? bundledAssetsDirectory = null)
    {
        try
        {
            if (!IsSafeOwnedCachePath(LastKnownGoodManifestPath, mustExist: true)) return null;
            var payload = File.ReadAllBytes(LastKnownGoodManifestPath);
            if (!IsSafeOwnedCachePath(LastKnownGoodManifestPath, mustExist: true)) return null;
            var manifest = LauncherBannersManifestParser.Parse(payload, fallback: true, observedAt);
            if (!string.Equals(manifest.Revision, ComputeSemanticRevision(payload), StringComparison.Ordinal)) return null;
            if (AllDisplayAssets(manifest).Any(asset =>
                (bundledAssetsDirectory is null || TryResolveBundledAsset(asset, bundledAssetsDirectory) is null)
                && TryResolveManagedAsset(asset) is null)) return null;
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    public LauncherCodesManifest? TryLoadLastKnownGoodCodes(DateTimeOffset observedAt)
    {
        try
        {
            if (!IsSafeOwnedCachePath(LastKnownGoodCodesPath, mustExist: true)) return null;
            var payload = File.ReadAllBytes(LastKnownGoodCodesPath);
            if (!IsSafeOwnedCachePath(LastKnownGoodCodesPath, mustExist: true)) return null;
            var manifest = LauncherBannersManifestParser.ParseCodes(payload, fallback: true, observedAt);
            return string.Equals(manifest.Revision, ComputeCodesRevision(payload), StringComparison.Ordinal) ? manifest : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public LauncherToolsManifest? TryLoadLastKnownGoodTools(DateTimeOffset observedAt)
    {
        try
        {
            if (!IsSafeOwnedCachePath(LastKnownGoodToolsPath, mustExist: true)) return null;
            var payload = File.ReadAllBytes(LastKnownGoodToolsPath);
            if (!IsSafeOwnedCachePath(LastKnownGoodToolsPath, mustExist: true)) return null;
            return LauncherBannersManifestParser.ParseTools(payload, fallback: true, observedAt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public async Task PromoteCodesAsync(
        LauncherCodesManifest manifest,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(ReadRevision(payload), ComputeCodesRevision(payload), StringComparison.Ordinal))
            throw new InvalidDataException("Launcher codes revision does not match its content.");
        var existing = TryLoadLastKnownGoodCodes(manifest.GeneratedAt);
        if (existing is not null && manifest.GeneratedAt <= existing.GeneratedAt)
            throw new InvalidDataException("Launcher codes generation did not advance.");
        await AtomicWriteOwnedAsync(LastKnownGoodCodesPath, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task PromoteToolsAsync(
        LauncherToolsManifest manifest,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);
        var parsed = LauncherBannersManifestParser.ParseTools(payload, fallback: false, manifest.GeneratedAt);
        if (!ToolsMatch(parsed, manifest))
            throw new InvalidDataException("Launcher tools do not match their parsed content.");
        var existing = TryLoadLastKnownGoodTools(
            DateTimeOffset.MaxValue - LauncherBannersManifestParser.MaximumFutureSkew);
        if (existing is not null)
        {
            if (manifest.GeneratedAt < existing.GeneratedAt)
                throw new InvalidDataException("Launcher tools generation moved backwards.");
            if (manifest.GeneratedAt == existing.GeneratedAt)
            {
                if (!ToolsMatch(manifest, existing))
                    throw new InvalidDataException("Launcher tools changed without a newer generation.");
                throw new InvalidDataException("Launcher tools generation did not advance.");
            }
        }
        await AtomicWriteOwnedAsync(LastKnownGoodToolsPath, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task PromoteAsync(
        LauncherBannersManifest manifest,
        byte[] payload,
        ILauncherBannersTransport transport,
        string? bundledAssetsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(transport);
        if (!string.Equals(ReadRevision(payload), ComputeSemanticRevision(payload), StringComparison.Ordinal))
            throw new InvalidDataException("Launcher manifest revision does not match its content.");
        EnsureOwnedDirectory(ManagedAssetsDirectory);
        EnsureOwnedDirectory(LastKnownGoodDirectory);
        var downloads = AllDisplayAssets(manifest)
            .DistinctBy(static asset => (asset.Sha256, asset.Mime))
            .Where(asset => asset.Url is not null)
            .Where(asset => bundledAssetsDirectory is null || TryResolveBundledAsset(asset, bundledAssetsDirectory) is null)
            .Where(asset => TryResolveManagedAsset(asset) is null)
            .ToArray();
        var existingBytes = ManagedAssetBytes();
        long requiredBytes;
        try
        {
            requiredBytes = downloads.Aggregate(0L, static (total, asset) => checked(total + asset.Size));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Launcher assets exceed the managed cache limit.", exception);
        }
        if (requiredBytes > MaximumManagedBytes || existingBytes > MaximumManagedBytes - requiredBytes)
            throw new InvalidDataException("Launcher assets exceed the managed cache limit.");

        var stagingDirectory = Path.Combine(ManagedDirectory, $".{manifest.Revision}.{Guid.NewGuid():N}.staging");
        var staged = new List<(string Source, string Destination)>();
        var installed = new List<string>();
        var revisionPath = Path.Combine(ManagedDirectory, $"{manifest.Revision}.json");
        var revisionWritten = false;
        var committed = false;
        EnsureOwnedDirectory(stagingDirectory);
        try
        {
            foreach (var asset in downloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await transport.GetAssetAsync(asset.Url!, LauncherBannersTransport.MaximumAssetBytes, cancellationToken).ConfigureAwait(false);
                ValidateAssetBytes(asset, bytes);
                var fileName = asset.Sha256 + Extension(asset.Mime);
                var source = Path.Combine(stagingDirectory, fileName);
                var destination = Path.Combine(ManagedAssetsDirectory, fileName);
                await AtomicWriteOwnedAsync(source, bytes, cancellationToken).ConfigureAwait(false);
                staged.Add((source, destination));
            }

            foreach (var asset in staged)
            {
                EnsureSafeOwnedPath(asset.Source, mustExist: true);
                EnsureSafeOwnedPath(asset.Destination, mustExist: false);
                File.Move(asset.Source, asset.Destination, overwrite: true);
                EnsureSafeOwnedPath(asset.Destination, mustExist: true);
                installed.Add(asset.Destination);
            }
            await AtomicWriteOwnedAsync(revisionPath, payload, cancellationToken).ConfigureAwait(false);
            revisionWritten = true;
            await AtomicWriteOwnedAsync(LastKnownGoodManifestPath, payload, cancellationToken).ConfigureAwait(false);
            committed = true;
            PruneManagedCache(activeManifest: manifest, now: DateTimeOffset.UtcNow);
        }
        finally
        {
            CleanupStagingDirectory(stagingDirectory);
            if (!committed)
            {
                foreach (var path in installed) TryDeleteOwned(path);
                if (revisionWritten) TryDeleteOwned(revisionPath);
            }
        }
    }

    internal async Task<bool> HydrateAssetsAsync(
        LauncherBannersManifest manifest,
        ILauncherBannersTransport transport,
        string? bundledAssetsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(transport);
        EnsureOwnedDirectory(ManagedAssetsDirectory);
        var downloads = AllDisplayAssets(manifest)
            .DistinctBy(static asset => (asset.Sha256, asset.Mime))
            .Where(asset => asset.Url is not null)
            .Where(asset => bundledAssetsDirectory is null || TryResolveBundledAsset(asset, bundledAssetsDirectory) is null)
            .Where(asset => TryResolveManagedAsset(asset) is null)
            .ToArray();
        if (downloads.Length == 0) return false;

        var existingBytes = ManagedAssetBytes();
        long requiredBytes;
        try
        {
            requiredBytes = downloads.Aggregate(0L, static (total, asset) => checked(total + asset.Size));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Launcher assets exceed the managed cache limit.", exception);
        }
        if (requiredBytes > MaximumManagedBytes || existingBytes > MaximumManagedBytes - requiredBytes)
            throw new InvalidDataException("Launcher assets exceed the managed cache limit.");

        var stagingDirectory = Path.Combine(ManagedDirectory, $".{manifest.Revision}.{Guid.NewGuid():N}.staging");
        var staged = new List<(string Source, string Destination)>();
        var installed = new List<string>();
        var committed = false;
        EnsureOwnedDirectory(stagingDirectory);
        try
        {
            foreach (var asset in downloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await transport.GetAssetAsync(asset.Url!, LauncherBannersTransport.MaximumAssetBytes, cancellationToken).ConfigureAwait(false);
                ValidateAssetBytes(asset, bytes);
                var fileName = asset.Sha256 + Extension(asset.Mime);
                var source = Path.Combine(stagingDirectory, fileName);
                var destination = Path.Combine(ManagedAssetsDirectory, fileName);
                await AtomicWriteOwnedAsync(source, bytes, cancellationToken).ConfigureAwait(false);
                staged.Add((source, destination));
            }

            foreach (var asset in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSafeOwnedPath(asset.Source, mustExist: true);
                EnsureSafeOwnedPath(asset.Destination, mustExist: false);
                File.Move(asset.Source, asset.Destination, overwrite: true);
                EnsureSafeOwnedPath(asset.Destination, mustExist: true);
                installed.Add(asset.Destination);
            }
            committed = true;
            PruneManagedCache(activeManifest: manifest, now: DateTimeOffset.UtcNow);
            return true;
        }
        finally
        {
            CleanupStagingDirectory(stagingDirectory);
            if (!committed)
            {
                foreach (var path in installed) TryDeleteOwned(path);
            }
        }
    }

    public int PruneManagedCache(long maximumBytes = MaximumManagedBytes, LauncherBannersManifest? activeManifest = null, DateTimeOffset? now = null)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (!Directory.Exists(ManagedDirectory)) return 0;
        EnsureSafeOwnedPath(ManagedDirectory, mustExist: true);
        foreach (var temporary in Directory.EnumerateFiles(ManagedDirectory, ".*.tmp", SearchOption.TopDirectoryOnly))
        {
            EnsureSafeOwnedPath(temporary, mustExist: true);
            TryDeleteOwned(temporary);
        }
        foreach (var staging in Directory.EnumerateDirectories(ManagedDirectory, ".*.staging", SearchOption.TopDirectoryOnly))
        {
            EnsureSafeOwnedPath(staging, mustExist: true);
            if (!CleanupStagingDirectory(staging)) throw new InvalidDataException("Unsafe launcher staging directory.");
        }
        if (!Directory.Exists(ManagedAssetsDirectory)) return 0;
        EnsureSafeOwnedPath(ManagedAssetsDirectory, mustExist: true);
        if (activeManifest is not null)
        {
            var liveHashes = AllDisplayAssets(activeManifest)
                .Select(asset => asset.Sha256)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(ManagedAssetsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureSafeOwnedPath(file, mustExist: true);
                var hash = Path.GetFileNameWithoutExtension(file);
                if (!liveHashes.Contains(hash)) TryDeleteOwned(file);
            }
        }
        var files = Directory.EnumerateFiles(ManagedAssetsDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(file => { EnsureSafeOwnedPath(file, mustExist: true); return file; })
            .Select(file => new FileInfo(file))
            .Where(file => file.Exists)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.FullName, StringComparer.Ordinal)
            .ToList();
        long total = files.Sum(file => file.Length);
        var removed = 0;
        foreach (var file in files)
        {
            if (total <= maximumBytes) break;
            total -= file.Length;
            TryDeleteOwned(file.FullName);
            removed++;
        }
        return removed;
    }

    private long ManagedAssetBytes()
    {
        if (!Directory.Exists(ManagedAssetsDirectory)) return 0;
        try
        {
            EnsureSafeOwnedPath(ManagedAssetsDirectory, mustExist: true);
            return Directory.EnumerateFiles(ManagedAssetsDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => { EnsureSafeOwnedPath(path, mustExist: true); return path; })
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .Aggregate(0L, static (total, file) => checked(total + file.Length));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Launcher managed cache size is invalid.", exception);
        }
    }

    private static string? TryValidateFile(string path, LauncherBannersAsset asset)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var fullPath = Path.GetFullPath(path);
            var bytes = File.ReadAllBytes(fullPath);
            ValidateAssetBytes(asset, bytes);
            return fullPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static void ValidateAssetBytes(LauncherBannersAsset asset, byte[] bytes)
    {
        if (bytes.Length != asset.Size || bytes.Length == 0) throw new InvalidDataException("Launcher asset size did not match the manifest.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Launcher asset hash did not match the manifest.");
        if (asset.Mime == "image/png" && !(bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))) throw new InvalidDataException("Launcher asset MIME did not match the bytes.");
        if (asset.Mime == "image/webp" && !(bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))) throw new InvalidDataException("Launcher asset MIME did not match the bytes.");
        var dimensions = ReadDimensions(bytes, asset.Mime);
        if (dimensions is null || dimensions.Value.Width != asset.Dimensions.Width || dimensions.Value.Height != asset.Dimensions.Height) throw new InvalidDataException("Launcher asset dimensions did not match the manifest.");
    }

    private static (int Width, int Height)? ReadDimensions(byte[] bytes, string mime)
    {
        if (mime == "image/png" && bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            return width is > 0 and <= 4096 && height is > 0 and <= 4096 ? ((int)width, (int)height) : null;
        }
        if (mime != "image/webp" || bytes.Length < 30 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return null;
        var kind = System.Text.Encoding.ASCII.GetString(bytes, 12, 4);
        if (kind == "VP8X")
        {
            var widthMinusOne = bytes[24] | bytes[25] << 8 | bytes[26] << 16;
            var heightMinusOne = bytes[27] | bytes[28] << 8 | bytes[29] << 16;
            return (1 + widthMinusOne, 1 + heightMinusOne);
        }
        if (kind == "VP8 " && bytes.Length >= 30 && bytes[23] == 0x9d && bytes[24] == 0x01 && bytes[25] == 0x2a) return (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26, 2)) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28, 2)) & 0x3fff);
        if (kind == "VP8L" && bytes.Length >= 25 && bytes[21] == 0x2f)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(21, 4));
            return (1 + (int)(bits >> 8 & 0x3fff), 1 + (int)(bits >> 22 & 0x3fff));
        }
        return null;
    }

    private static string Extension(string mime) => mime == "image/png" ? ".png" : ".webp";

    private static bool ToolsMatch(LauncherToolsManifest left, LauncherToolsManifest right) =>
        left.SchemaVersion == right.SchemaVersion
        && left.GeneratedAt == right.GeneratedAt
        && left.Tools.Count == right.Tools.Count
        && left.Tools.Zip(right.Tools).All(pair =>
            string.Equals(pair.First.Game, pair.Second.Game, StringComparison.Ordinal)
            && string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal)
            && string.Equals(pair.First.Label, pair.Second.Label, StringComparison.Ordinal)
            && string.Equals(pair.First.Url.OriginalString, pair.Second.Url.OriginalString, StringComparison.Ordinal));

    internal static string ComputeSemanticRevision(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var root = JsonNode.Parse(payload)?.AsObject() ?? throw new InvalidDataException("Invalid launcher manifest JSON.");
        var games = root["games"]?.DeepClone()?.AsObject() ?? throw new InvalidDataException("Launcher manifest games are missing.");
        foreach (var game in games)
        {
            if (game.Value is JsonObject gameObject
                && gameObject["current"] is JsonObject current
                && current["remaining"] is JsonObject remaining)
                remaining.Remove("durationSeconds");
        }
        var semantic = new JsonObject
        {
            ["schemaVersion"] = root["schemaVersion"]?.DeepClone(),
            ["health"] = new JsonObject
            {
                ["status"] = "pending",
                ["games"] = root["health"]?["games"]?.DeepClone(),
            },
            ["games"] = games,
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            using var document = JsonDocument.Parse(semantic.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            WriteStable(writer, document.RootElement);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    internal static string ComputeCodesRevision(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", root.GetProperty("schemaVersion").GetInt32());
            writer.WriteString("generatedAt", root.GetProperty("generatedAt").GetString());
            writer.WriteStartObject("games");
            foreach (var game in new[] { "gi", "hsr", "zzz", "wuwa", "ae" })
            {
                writer.WriteStartArray(game);
                foreach (var code in root.GetProperty("games").GetProperty(game).EnumerateArray())
                {
                    writer.WriteStartObject();
                    writer.WriteString("code", code.GetProperty("code").GetString());
                    writer.WriteString("added", code.GetProperty("added").GetString());
                    writer.WriteNumber("amount", code.TryGetProperty("amount", out var amount) ? amount.GetInt32() : 0);
                    writer.WriteString("currency", code.TryGetProperty("currency", out var currency) ? currency.GetString() : string.Empty);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string ReadRevision(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.String
            ? revision.GetString() ?? string.Empty
            : string.Empty;
    }

    private static void WriteStable(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteStable(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteStable(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(element.GetRawText(), skipInputValidation: true); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            default: writer.WriteNullValue(); break;
        }
    }

    private static IEnumerable<LauncherBannersAsset> AllDisplayAssets(LauncherBannersManifest manifest) =>
        manifest.Games.Values.SelectMany(game =>
            (game.Current?.Variants ?? [])
            .Concat((game.Current?.Characters ?? []).Select(character => character.Icon).OfType<LauncherBannersAsset>())
            .Concat(game.Current?.Characters.SelectMany(character => character.Variants) ?? [])
            .Concat(game.Upcoming.SelectMany(phase => phase.Characters).Select(character => character.Icon).OfType<LauncherBannersAsset>())
            .Concat(game.Upcoming.SelectMany(phase => phase.Characters).SelectMany(character => character.Variants)));

    private async Task AtomicWriteOwnedAsync(string target, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)!;
        EnsureOwnedDirectory(directory);
        EnsureSafeOwnedPath(target, mustExist: false);
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            EnsureSafeOwnedPath(temp, mustExist: false);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            EnsureSafeOwnedPath(temp, mustExist: true);
            EnsureSafeOwnedPath(target, mustExist: false);
            File.Move(temp, target, overwrite: true);
            EnsureSafeOwnedPath(target, mustExist: true);
        }
        finally
        {
            TryDeleteOwned(temp);
        }
    }

    private bool CleanupStagingDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return true;
            if (!IsSafeOwnedCachePath(directory, mustExist: true)) return false;
            if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any()) return false;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsSafeOwnedCachePath(file, mustExist: true)) return false;
                TryDeleteOwned(file);
            }
            if (!IsSafeOwnedCachePath(directory, mustExist: true)) return false;
            Directory.Delete(directory, recursive: false);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void EnsureOwnedDirectory(string directory)
    {
        EnsureSafeOwnedPath(directory, mustExist: false);
        Directory.CreateDirectory(directory);
        EnsureSafeOwnedPath(directory, mustExist: true);
    }

    private void EnsureSafeOwnedPath(string path, bool mustExist)
    {
        if (!IsSafeOwnedCachePath(path, mustExist)) throw new InvalidDataException("Unsafe launcher cache path.");
    }

    internal bool IsSafeOwnedCachePath(string path, bool mustExist)
        => IsSafeContainedPath(RootDirectory, path, mustExist);

    private static bool IsSafeContainedPath(string rootDirectory, string path, bool mustExist)
    {
        try
        {
            var root = Path.GetFullPath(rootDirectory);
            var full = Path.GetFullPath(path);
            if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            var current = root;
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            var relative = Path.GetRelativePath(root, full);
            if (relative != ".")
            {
                foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                {
                    current = Path.Combine(current, part);
                    if (!File.Exists(current) && !Directory.Exists(current)) break;
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
                }
            }
            return !mustExist || File.Exists(full) || Directory.Exists(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private void TryDeleteOwned(string path)
    {
        try
        {
            if (!File.Exists(path) || !IsSafeOwnedCachePath(path, mustExist: true)) return;
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

}
