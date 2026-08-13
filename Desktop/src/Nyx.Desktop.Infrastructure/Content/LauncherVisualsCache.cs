using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nyx.Desktop.Infrastructure.Content;

public sealed record LauncherVisualSelection(
    string GameId,
    string Revision,
    string Kind,
    string? Character,
    IReadOnlyList<string> Files);

public sealed class LauncherVisualsCache
{
    public static readonly Uri DefaultManifestUri = new("https://assets.pengo.gg/launcher-visuals-v1.json");
    private static readonly Uri EndfieldVisualUri = new("https://launcher.gryphline.com/api/proxy/web/batch_proxy");
    private const string EndfieldVideoHost = "gl-utils-public.hg-cdn.com";
    private const string EndfieldVideoPathPrefix = "/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/";
    private const string EndfieldRequestJson = "{\"proxy_reqs\":[{\"kind\":\"get_main_bg_image\",\"get_main_bg_image_req\":{\"appcode\":\"YDUTE5gscDZ229CW\",\"language\":\"en-us\",\"channel\":\"6\",\"sub_channel\":\"6\",\"platform\":\"Windows\",\"source\":\"launcher\"}}]}";
    private const int MaximumManifestBytes = 128 * 1024;
    private const int MaximumStateBytes = 16 * 1024;
    private const long MaximumVideoBytes = 40 * 1024 * 1024;
    private const long MaximumImageBytes = 8 * 1024 * 1024;
    private static readonly string[] GameOrder = ["gi", "hsr", "zzz", "wuwa", "ae"];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly byte[] WebmSignature = [0x1a, 0x45, 0xdf, 0xa3];
    private static readonly HashSet<string> Games = new(GameOrder, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions OfficialJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions StateJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string dataRoot;
    private readonly string root;
    private readonly HttpClient http;
    private readonly Uri manifestUri;
    public string? LastFailure { get; private set; }

    public LauncherVisualsCache(string dataDirectory, HttpClient? http = null, Uri? manifestUri = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        dataRoot = Path.GetFullPath(dataDirectory);
        root = Path.Combine(dataRoot, "ContentCache", "LauncherVisuals");
        this.http = http ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        this.manifestUri = manifestUri ?? DefaultManifestUri;
    }

    public async Task<LauncherVisualSelection?> RefreshAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (!Games.Contains(gameId)) return null;
        try
        {
            LastFailure = null;
            if (gameId == "ae") return await RefreshOfficialEndfieldAsync(cancellationToken);
            var manifest = await ReadManifestAsync(cancellationToken);
            if (!manifest.Games.TryGetValue(gameId, out var entry)) return TryLoadLastGood(gameId);
            return await AcquireSelectionAsync(gameId, manifest.Revision, entry, cancellationToken);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            LastFailure = exception.GetType().Name + ": " + exception.Message;
            return TryLoadLastGood(gameId);
        }
    }

    public async Task<IReadOnlyDictionary<string, LauncherVisualSelection>> RefreshAllAsync(
        Action<LauncherVisualSelection>? selectionReady = null,
        CancellationToken cancellationToken = default)
    {
        LastFailure = null;
        var officialEndfield = NotifyAsync(RefreshOfficialEndfieldAsync(cancellationToken));
        Manifest? manifest = null;
        try
        {
            manifest = await ReadManifestAsync(cancellationToken);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            LastFailure = exception.GetType().Name + ": " + exception.Message;
        }

        var tasks = GameOrder.Where(static gameId => gameId != "ae").Select(async gameId =>
        {
            LauncherVisualSelection? selection = null;
            try
            {
                selection = manifest is not null && manifest.Games.TryGetValue(gameId, out var entry)
                    ? await AcquireSelectionAsync(gameId, manifest.Revision, entry, cancellationToken)
                    : TryLoadLastGood(gameId);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                LastFailure = exception.GetType().Name + ": " + exception.Message;
                selection = TryLoadLastGood(gameId);
            }
            if (selection is not null) selectionReady?.Invoke(selection);
            return selection;
        }).Append(officialEndfield).ToArray();
        var selections = await Task.WhenAll(tasks);
        return selections.Where(static selection => selection is not null)
            .ToDictionary(static selection => selection!.GameId, static selection => selection!, StringComparer.Ordinal);

        async Task<LauncherVisualSelection?> NotifyAsync(Task<LauncherVisualSelection?> pending)
        {
            var selection = await pending;
            if (selection is not null) selectionReady?.Invoke(selection);
            return selection;
        }
    }

    private async Task<LauncherVisualSelection?> RefreshOfficialEndfieldAsync(CancellationToken cancellationToken)
    {
        try
        {
            var asset = await ReadOfficialEndfieldAssetAsync(cancellationToken);
            var file = await AcquireOfficialEndfieldAsync(asset, cancellationToken);
            var selection = new LauncherVisualSelection("ae", file.Sha256, "video", null, [file.Path]);
            SaveLastGood(selection, [file]);
            RemoveSupersededFiles("ae", selection.Files);
            return selection;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            LastFailure = exception.GetType().Name + ": " + exception.Message;
            return TryLoadLastGood("ae");
        }
    }

    private async Task<Uri> ReadOfficialEndfieldAssetAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, EndfieldVisualUri)
        {
            Content = new StringContent(EndfieldRequestJson, Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        RejectRedirect(response, EndfieldVisualUri);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength is > MaximumManifestBytes)
            throw new InvalidDataException("Official Endfield launcher response is invalid.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(stream, buffer, MaximumManifestBytes, cancellationToken);
        var dto = JsonSerializer.Deserialize<OfficialBatchResponseDto>(buffer.ToArray(), OfficialJson);
        if (dto?.ProxyRsps is not [var item]
            || item is null
            || item.Kind != "get_main_bg_image"
            || item.GetMainBgImageRsp?.MainBgImage?.VideoUrl is not { } rawUrl
            || string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Host, EndfieldVideoHost, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(url.UserInfo)
            || !url.IsDefaultPort
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment)
            || !url.AbsolutePath.StartsWith(EndfieldVideoPathPrefix, StringComparison.Ordinal)
            || !url.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Official Endfield launcher response is invalid.");
        return url;
    }

    private async Task<AcquiredFile> AcquireOfficialEndfieldAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        RejectRedirect(response, url);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "video/mp4", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength is not { } size
            || size is <= 0 or > MaximumVideoBytes)
            throw new InvalidDataException("Official Endfield launcher video is invalid.");

        var directory = GameRoot("ae");
        EnsureSafeCachePath(directory);
        Directory.CreateDirectory(directory);
        EnsureSafeCachePath(directory);
        var temporary = Path.Combine(directory, ".official.tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            EnsureSafeCachePath(temporary);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            EnsureSafeCachePath(temporary);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await CopyBoundedAsync(source, target, size, cancellationToken, requireExactLength: true, hasher);
                await target.FlushAsync(cancellationToken);
            }
            var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            EnsureSafeCachePath(temporary);
            if (!IsRegularFile(temporary)
                || new FileInfo(temporary).Length != size
                || !HasExpectedSignature(temporary, "video/mp4")
                || !await HashMatchesAsync(temporary, hash, cancellationToken))
                throw new InvalidDataException("Official Endfield launcher video verification failed.");
            var destination = Path.Combine(directory, hash + ".mp4");
            EnsureSafeCachePath(temporary);
            EnsureSafeCachePath(destination);
            if (File.Exists(destination)
                && IsRegularFile(destination)
                && new FileInfo(destination).Length == size
                && await HashMatchesAsync(destination, hash, cancellationToken))
                return new(destination, size, hash);
            EnsureSafeCachePath(temporary);
            EnsureSafeCachePath(destination);
            File.Move(temporary, destination, overwrite: true);
            return new(destination, size, hash);
        }
        finally
        {
            try { EnsureSafeCachePath(temporary); if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool IsExpectedFailure(Exception exception) => exception is HttpRequestException
        or IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or JsonException
        or OperationCanceledException;

    private static void RejectRedirect(HttpResponseMessage response, Uri requested)
    {
        if (response.RequestMessage?.RequestUri is { } final && final != requested)
            throw new InvalidDataException("Launcher visual redirect was refused.");
    }

    public LauncherVisualSelection? TryLoadLastGood(string gameId)
    {
        if (!Games.Contains(gameId)) return null;
        try
        {
            var statePath = StatePath(gameId);
            var gameRoot = GameRoot(gameId);
            EnsureSafeCachePath(gameRoot);
            EnsureSafeCachePath(statePath);
            if (!Directory.Exists(gameRoot)
                || IsReparsePoint(gameRoot)
                || !File.Exists(statePath)
                || !IsRegularFile(statePath)
                || new FileInfo(statePath).Length is <= 0 or > MaximumStateBytes) return null;
            byte[] stateBytes;
            EnsureSafeCachePath(statePath);
            using (var stateStream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stateStream.Length is <= 0 or > MaximumStateBytes) return null;
                stateBytes = new byte[(int)stateStream.Length];
                stateStream.ReadExactly(stateBytes);
                if (stateStream.ReadByte() != -1 || !IsRegularFile(statePath)) return null;
            }
            var state = JsonSerializer.Deserialize<CacheState>(stateBytes, StateJson);
            if (state is null
                || state.GameId != gameId
                || !IsHash(state.Revision)
                || state.Kind is not ("video" or "image" or "gallery")
                || state.Files is null
                || state.Files.Count != (state.Kind == "gallery" ? 3 : 1)
                || state.Files.Any(static name => string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
                || state.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Count
                || CleanCharacter(state.Character) != state.Character) return null;

            var metadata = state.FileMetadata;
            if (metadata is null)
            {
                metadata = [];
                foreach (var name in state.Files)
                {
                    if (!TryParseContentAddressedName(name, out var hash, out _)) return null;
                    var path = Path.Combine(gameRoot, name);
                    if (!File.Exists(path)) return null;
                    metadata.Add(new() { Name = name, Size = new FileInfo(path).Length, Sha256 = hash });
                }
            }
            else if (metadata.Count != state.Files.Count
                || metadata.Any(static file => file is null)
                || !metadata.Select(static file => file!.Name!).SequenceEqual(state.Files, StringComparer.Ordinal)) return null;

            var files = new string[metadata.Count];
            for (var index = 0; index < metadata.Count; index++)
            {
                if (!TryValidateCachedFile(gameId, state.Revision!, state.Kind, metadata[index], out var path))
                    return null;
                files[index] = path;
            }
            return new(gameId, state.Revision!, state.Kind, state.Character, files);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or CryptographicException)
        {
            return null;
        }
    }

    private bool TryValidateCachedFile(
        string gameId,
        string revision,
        string kind,
        CacheFileState? file,
        out string path)
    {
        path = string.Empty;
        if (file?.Name is not { } name
            || file.Sha256 is not { } hash
            || file.Size is not { } size
            || !TryParseContentAddressedName(name, out var nameHash, out var extension)
            || hash != nameHash
            || size <= 0
            || (kind == "video" && extension is not (".mp4" or ".webm"))
            || (kind is "image" or "gallery" && extension is not (".webp" or ".png"))
            || size > (kind == "video" ? MaximumVideoBytes : MaximumImageBytes)
            || (gameId == "ae" && kind == "video" && (extension != ".mp4" || revision != hash))) return false;

        path = Path.Combine(GameRoot(gameId), name);
        EnsureSafeCachePath(path);
        if (!File.Exists(path) || !IsRegularFile(path)) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        var mediaType = extension switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".webp" => "image/webp",
            _ => "image/png",
        };
        if (stream.Length != size || !HasExpectedSignature(stream, mediaType)) return false;
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return actual == hash && IsRegularFile(path) && new FileInfo(path).Length == size;
    }

    private static bool TryParseContentAddressedName(string name, out string hash, out string extension)
    {
        extension = Path.GetExtension(name);
        hash = Path.GetFileNameWithoutExtension(name);
        return Path.GetFileName(name) == name
            && IsLowerHash(hash)
            && name == hash + extension
            && extension is ".mp4" or ".webm" or ".webp" or ".png";
    }

    private static bool IsRegularFile(string path) => !IsReparsePoint(path)
        && (File.GetAttributes(path) & FileAttributes.Directory) == 0;

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private void EnsureSafeCachePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var isRoot = string.Equals(fullPath, dataRoot, comparison);
        if (!Directory.Exists(dataRoot)
            || !isRoot && !fullPath.StartsWith(prefix, comparison))
            throw new InvalidDataException("Launcher visual cache path is invalid.");

        var current = dataRoot;
        ValidateComponent(current, mustBeDirectory: true);
        var relative = Path.GetRelativePath(dataRoot, fullPath);
        if (relative == ".") return;
        var components = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            ValidateComponent(current, mustBeDirectory: index < components.Length - 1);
        }

        static void ValidateComponent(string component, bool mustBeDirectory)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(component); }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || mustBeDirectory && (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("Launcher visual cache path is invalid.");
        }
    }

    private async Task<Manifest> ReadManifestAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        RejectRedirect(response, manifestUri);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength is > MaximumManifestBytes)
            throw new InvalidDataException("Launcher visual manifest is invalid.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(stream, buffer, MaximumManifestBytes, cancellationToken);
        var dto = JsonSerializer.Deserialize<ManifestDto>(buffer.ToArray(), ManifestJson)
            ?? throw new InvalidDataException("Launcher visual manifest is empty.");
        if (dto.Schema != 1 || !IsHash(dto.Revision) || dto.Games is null)
            throw new InvalidDataException("Launcher visual manifest is invalid.");

        var games = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var pair in dto.Games)
        {
            if (!Games.Contains(pair.Key) || pair.Value is null) continue;
            var kind = pair.Value.Kind;
            if (kind is not ("video" or "image" or "gallery") || pair.Value.Assets is null) continue;
            var assets = pair.Value.Assets.Select(ParseAsset).ToArray();
            if (assets.Length == 0
                || (kind is "video" or "image" && assets.Length != 1)
                || (kind == "gallery" && assets.Length != 3)) continue;
            games[pair.Key] = new(kind, CleanCharacter(pair.Value.Character), assets);
        }
        return new(dto.Revision!, games);
    }

    private static Asset ParseAsset(AssetDto? dto)
    {
        var size = dto?.Size.GetValueOrDefault() ?? 0;
        if (dto is null
            || !IsLowerHash(dto.Sha256)
            || size <= 0
            || dto.MediaType is not ("video/webm" or "video/mp4" or "image/webp" or "image/png"))
            throw new InvalidDataException("Launcher visual asset is invalid.");
        var limit = dto.MediaType is "video/webm" or "video/mp4" ? MaximumVideoBytes : MaximumImageBytes;
        if (size > limit) throw new InvalidDataException("Launcher visual asset is too large.");
        var extension = dto.MediaType switch
        {
            "video/webm" => ".webm",
            "video/mp4" => ".mp4",
            "image/webp" => ".webp",
            _ => ".png",
        };
        var path = "/launcher-visuals/" + dto.Sha256 + extension;
        if (dto.Url is null
            || (dto.Url != "https://assets.pengo.gg" + path
                && dto.Url != "https://assets.pengo.gg:443" + path)
            || !Uri.TryCreate(dto.Url, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps
            || url.Host != "assets.pengo.gg"
            || !string.IsNullOrEmpty(url.UserInfo)
            || !url.IsDefaultPort
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment)
            || url.AbsolutePath != path)
            throw new InvalidDataException("Launcher visual asset is invalid.");
        return new(url, dto.Sha256!.ToLowerInvariant(), size, dto.MediaType, extension);
    }

    private async Task<LauncherVisualSelection> AcquireSelectionAsync(
        string gameId,
        string revision,
        Entry entry,
        CancellationToken cancellationToken)
    {
        var files = new List<AcquiredFile>(entry.Assets.Count);
        foreach (var asset in entry.Assets)
        {
            files.Add(await AcquireAsync(gameId, asset, cancellationToken));
        }
        var selection = new LauncherVisualSelection(
            gameId,
            revision,
            entry.Kind,
            entry.Character,
            files.Select(static file => file.Path).ToArray());
        SaveLastGood(selection, files);
        RemoveSupersededFiles(gameId, selection.Files);
        return selection;
    }

    private async Task<AcquiredFile> AcquireAsync(string gameId, Asset asset, CancellationToken cancellationToken)
    {
        var directory = GameRoot(gameId);
        EnsureSafeCachePath(directory);
        Directory.CreateDirectory(directory);
        EnsureSafeCachePath(directory);
        var destination = Path.Combine(directory, asset.Sha256 + asset.Extension);
        EnsureSafeCachePath(destination);
        if (File.Exists(destination) && IsRegularFile(destination)
            && new FileInfo(destination).Length == asset.Size
            && await HashMatchesAsync(destination, asset.Sha256, cancellationToken))
            return new(destination, asset.Size, asset.Sha256);

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            RejectRedirect(response, asset.Url);
            response.EnsureSuccessStatusCode();
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, asset.MediaType, StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException("Launcher visual size does not match its manifest.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            EnsureSafeCachePath(temporary);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await CopyBoundedAsync(source, target, asset.Size, cancellationToken, requireExactLength: true);
                await target.FlushAsync(cancellationToken);
            }
            EnsureSafeCachePath(temporary);
            if (!HasExpectedSignature(temporary, asset.MediaType)
                || !await HashMatchesAsync(temporary, asset.Sha256, cancellationToken))
                throw new InvalidDataException("Launcher visual hash does not match its manifest.");
            EnsureSafeCachePath(temporary);
            EnsureSafeCachePath(destination);
            File.Move(temporary, destination, overwrite: true);
            return new(destination, asset.Size, asset.Sha256);
        }
        finally
        {
            try { EnsureSafeCachePath(temporary); if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private void SaveLastGood(LauncherVisualSelection selection, IReadOnlyList<AcquiredFile> files)
    {
        var directory = GameRoot(selection.GameId);
        EnsureSafeCachePath(directory);
        Directory.CreateDirectory(directory);
        EnsureSafeCachePath(directory);
        if (IsReparsePoint(directory)
            || selection.Files.Count != files.Count
            || !selection.Files.SequenceEqual(files.Select(static file => file.Path), StringComparer.Ordinal))
            throw new InvalidDataException("Launcher visual cache state is invalid.");
        var state = new CacheState
        {
            GameId = selection.GameId,
            Revision = selection.Revision,
            Kind = selection.Kind,
            Character = selection.Character,
            Files = selection.Files.Select(static file => Path.GetFileName(file)!).ToList(),
            FileMetadata = files.Select(static file => new CacheFileState
            {
                Name = Path.GetFileName(file.Path),
                Size = file.Size,
                Sha256 = file.Sha256,
            }).ToList(),
        };
        for (var index = 0; index < state.FileMetadata.Count; index++)
        {
            if (!TryValidateCachedFile(selection.GameId, selection.Revision, selection.Kind, state.FileMetadata[index], out var path)
                || path != selection.Files[index])
                throw new InvalidDataException("Launcher visual cache state is invalid.");
        }
        var destination = StatePath(selection.GameId);
        EnsureSafeCachePath(destination);
        if (File.Exists(destination) && !IsRegularFile(destination))
            throw new InvalidDataException("Launcher visual cache state is invalid.");
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            EnsureSafeCachePath(temporary);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, StateJson));
            EnsureSafeCachePath(temporary);
            EnsureSafeCachePath(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { EnsureSafeCachePath(temporary); if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private void RemoveSupersededFiles(string gameId, IReadOnlyList<string> selected)
    {
        EnsureSafeCachePath(GameRoot(gameId));
        var keep = selected.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        EnsureSafeCachePath(GameRoot(gameId));
        foreach (var path in Directory.EnumerateFiles(GameRoot(gameId)))
        {
            if (Path.GetFileName(path) == "state.json" || keep.Contains(Path.GetFullPath(path))) continue;
            try { EnsureSafeCachePath(path); File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExpectedSignature(string path, string mediaType)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
        return HasExpectedSignature(stream, mediaType);
    }

    private static bool HasExpectedSignature(Stream stream, string mediaType)
    {
        Span<byte> header = stackalloc byte[12];
        var read = stream.Read(header);
        stream.Position = 0;
        return mediaType switch
        {
            "image/png" => read >= 8 && header[..8].SequenceEqual(PngSignature),
            "image/webp" => read >= 12
                && header[..4].SequenceEqual("RIFF"u8)
                && header[8..12].SequenceEqual("WEBP"u8),
            "video/mp4" => read >= 8
                && header[4..8].SequenceEqual("ftyp"u8)
                && BinaryPrimitives.ReadUInt32BigEndian(header[..4]) is >= 8 and var boxSize
                && boxSize <= stream.Length,
            "video/webm" => read >= 4 && header[..4].SequenceEqual(WebmSignature),
            _ => false,
        };
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream target,
        long maximum,
        CancellationToken cancellationToken,
        bool requireExactLength = false,
        IncrementalHash? hash = null)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maximum) throw new InvalidDataException("Downloaded content exceeded its declared limit.");
            hash?.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (requireExactLength && total != maximum)
            throw new InvalidDataException("Downloaded content was incomplete.");
    }

    private string GameRoot(string gameId) => Path.Combine(root, gameId);
    private string StatePath(string gameId) => Path.Combine(GameRoot(gameId), "state.json");
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsLowerHash(string? value) => IsHash(value) && value == value!.ToLowerInvariant();
    private static string? CleanCharacter(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim().Length <= 80 && value.Trim().All(c => !char.IsControl(c)) ? value.Trim() : null;

    private sealed record Manifest(string Revision, IReadOnlyDictionary<string, Entry> Games);
    private sealed record Entry(string Kind, string? Character, IReadOnlyList<Asset> Assets);
    private sealed record Asset(Uri Url, string Sha256, long Size, string MediaType, string Extension);
    private sealed record AcquiredFile(string Path, long Size, string Sha256);
    private sealed class ManifestDto { public int Schema { get; set; } public string? Revision { get; set; } public Dictionary<string, EntryDto?>? Games { get; set; } }
    private sealed class EntryDto { public string? Kind { get; set; } public string? Character { get; set; } public List<AssetDto?>? Assets { get; set; } }
    private sealed class AssetDto { public string? Url { get; set; } public string? Sha256 { get; set; } public long? Size { get; set; } public string? MediaType { get; set; } }
    private sealed class CacheState { public string? GameId { get; set; } public string? Revision { get; set; } public string? Kind { get; set; } public string? Character { get; set; } public List<string>? Files { get; set; } public List<CacheFileState>? FileMetadata { get; set; } }
    private sealed class CacheFileState { public string? Name { get; set; } public long? Size { get; set; } public string? Sha256 { get; set; } }
    private sealed class OfficialBatchResponseDto { public List<OfficialProxyResponseDto?>? ProxyRsps { get; set; } }
    private sealed class OfficialProxyResponseDto { public string? Kind { get; set; } public OfficialMainBackgroundResponseDto? GetMainBgImageRsp { get; set; } }
    private sealed class OfficialMainBackgroundResponseDto { public string? DataVersion { get; set; } public OfficialMainBackgroundDto? MainBgImage { get; set; } }
    private sealed class OfficialMainBackgroundDto { public string? Url { get; set; } public string? Md5 { get; set; } public string? VideoUrl { get; set; } }
}
