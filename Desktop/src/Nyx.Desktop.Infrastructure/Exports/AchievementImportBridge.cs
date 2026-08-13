using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

public enum AchievementImportDeliveryState
{
    Delivered,
    Expired,
    Canceled,
    Failed,
}

public sealed class AchievementImportBridge
{
    public const int MaximumArtifactBytes = 5 * 1024 * 1024;
    private const int NonceBytes = 32;
    private readonly Uri siteOrigin;
    private readonly TimeSpan lifetime;

    public AchievementImportBridge(
        Uri? siteOrigin = null,
        TimeSpan? lifetime = null)
    {
        this.siteOrigin = siteOrigin ?? new Uri("https://pengo.gg");
        this.lifetime = lifetime ?? TimeSpan.FromMinutes(2);
        if (!IsExactSiteOrigin(this.siteOrigin))
            throw new ArgumentException("The achievement site origin is not approved.", nameof(siteOrigin));
        if (this.lifetime <= TimeSpan.Zero || this.lifetime > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
    }

    public async ValueTask<AchievementImportBridgeSession> StartAsync(
        string gameId,
        string artifactPath,
        CancellationToken cancellationToken = default)
    {
        var route = gameId switch
        {
            "gi" => "/genshin/achievements",
            "hsr" => "/hsr/achievements",
            _ => throw new ExportProviderException("achievement-handoff-unsupported"),
        };
        var artifact = await ReadAndValidateArtifactAsync(
            gameId,
            artifactPath,
            cancellationToken);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start(4);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var nonceBytes = RandomNumberGenerator.GetBytes(NonceBytes);
            string nonce;
            try
            {
                nonce = Convert.ToBase64String(nonceBytes)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonceBytes);
            }
            var browser = new UriBuilder(siteOrigin)
            {
                Path = route,
                Query = string.Empty,
                Fragment = $"nyx-import=v1&port={port}&nonce={nonce}",
            }.Uri;
            return new(
                listener,
                artifact,
                nonce,
                port,
                siteOrigin.GetLeftPart(UriPartial.Authority),
                browser,
                lifetime,
                cancellationToken);
        }
        catch
        {
            listener.Stop();
            CryptographicOperations.ZeroMemory(artifact);
            throw;
        }
    }

    private static async Task<byte[]> ReadAndValidateArtifactAsync(
        string gameId,
        string artifactPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        if (!Path.IsPathFullyQualified(artifactPath)
            || artifactPath.StartsWith("\\\\", StringComparison.Ordinal)
            || artifactPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || artifactPath.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ExportProviderException("achievement-handoff-invalid");
        var path = Path.GetFullPath(artifactPath);
        if (!File.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ExportProviderException("achievement-handoff-invalid");

        byte[] bytes;
        await using (var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            32 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > MaximumArtifactBytes)
                throw new ExportProviderException("achievement-handoff-invalid");
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }
        try
        {
            ValidateArtifact(gameId, bytes);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static void ValidateArtifact(string expectedGameId, ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasExactProperties(
                    root,
                    root.TryGetProperty("accountBinding", out _)
                        ? ["kind", "version", "game", "accountBinding", "catalogVersion", "exportedAt", "achievements"]
                        : ["kind", "version", "game", "catalogVersion", "exportedAt", "achievements"])
                || !root.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != "pengo-achievements"
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionValue)
                || versionValue != 1
                || !root.TryGetProperty("game", out var game)
                || game.ValueKind != JsonValueKind.String
                || !string.Equals(game.GetString(), expectedGameId, StringComparison.Ordinal)
                || !root.TryGetProperty("catalogVersion", out var catalogVersion)
                || catalogVersion.ValueKind != JsonValueKind.String
                || catalogVersion.GetString() is not { Length: >= 1 and <= 80 }
                || !root.TryGetProperty("exportedAt", out var exportedAt)
                || exportedAt.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    exportedAt.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out _)
                || !root.TryGetProperty("achievements", out var rows)
                || rows.ValueKind != JsonValueKind.Array
                || rows.GetArrayLength() > HoyoLabHsrAchievementResultParser.MaximumAchievementCount)
                throw new ExportProviderException("achievement-handoff-invalid");

            if (root.TryGetProperty("accountBinding", out var accountBinding)
                && !IsValidAccountBinding(accountBinding))
                throw new ExportProviderException("achievement-handoff-invalid");
            long previous = 0;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !HasExactProperties(row, ["id", "status"])
                    || !row.TryGetProperty("id", out var idProperty)
                    || idProperty.ValueKind != JsonValueKind.Number
                    || !idProperty.TryGetInt64(out var id)
                    || id <= previous
                    || id > HoyoLabHsrAchievementResultParser.MaximumAchievementId
                    || !row.TryGetProperty("status", out var status)
                    || status.ValueKind != JsonValueKind.String
                    || status.GetString() != "complete")
                    throw new ExportProviderException("achievement-handoff-invalid");
                previous = id;
            }
        }
        catch (ExportProviderException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new ExportProviderException("achievement-handoff-invalid");
        }
    }

    private static bool IsValidAccountBinding(JsonElement binding)
    {
        if (binding.ValueKind != JsonValueKind.Object
            || !HasExactProperties(binding, ["scheme", "value", "region"])
            || !binding.TryGetProperty("scheme", out var scheme)
            || scheme.ValueKind != JsonValueKind.String
            || scheme.GetString() != AchievementAccountBinding.CurrentScheme
            || !binding.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.String
            || !binding.TryGetProperty("region", out var region)
            || region.ValueKind != JsonValueKind.String)
            return false;
        var fingerprint = value.GetString() ?? string.Empty;
        var regionValue = region.GetString() ?? string.Empty;
        return fingerprint.Length is >= 16 and <= 256
            && fingerprint.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            && regionValue.Length is >= 1 and <= 48
            && regionValue.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool HasExactProperties(JsonElement element, IReadOnlyCollection<string> names)
    {
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
                return false;
        }
        return seen.SetEquals(expected);
    }

    private static bool IsExactSiteOrigin(Uri uri) =>
        uri.IsAbsoluteUri
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.AbsolutePath.Trim('/'))
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "pengo.gg", StringComparison.OrdinalIgnoreCase);
}

public sealed class AchievementImportBridgeSession : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumConnections = 8;
    private readonly TcpListener listener;
    private readonly byte[] artifact;
    private readonly string nonce;
    private readonly int port;
    private readonly string allowedOrigin;
    private readonly CancellationTokenSource lifetime;
    private readonly TaskCompletionSource<AchievementImportDeliveryState> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    private int delivered;
    private int disposed;

    internal AchievementImportBridgeSession(
        TcpListener listener,
        byte[] artifact,
        string nonce,
        int port,
        string allowedOrigin,
        Uri browserUri,
        TimeSpan expiresAfter,
        CancellationToken cancellationToken)
    {
        this.listener = listener;
        this.artifact = artifact;
        this.nonce = nonce;
        this.port = port;
        this.allowedOrigin = allowedOrigin;
        BrowserUri = browserUri;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(expiresAfter);
        worker = RunAsync();
    }

    public Uri BrowserUri { get; }
    public Task<AchievementImportDeliveryState> Completion => completion.Task;

    private async Task RunAsync()
    {
        try
        {
            for (var attempt = 0; attempt < MaximumConnections
                && !lifetime.IsCancellationRequested
                && Volatile.Read(ref delivered) == 0; attempt++)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                client.NoDelay = true;
                await HandleAsync(client, lifetime.Token);
            }
            if (Volatile.Read(ref delivered) != 0)
                completion.TrySetResult(AchievementImportDeliveryState.Delivered);
            else if (lifetime.IsCancellationRequested)
                completion.TrySetResult(AchievementImportDeliveryState.Expired);
            else
                completion.TrySetResult(AchievementImportDeliveryState.Failed);
        }
        catch (OperationCanceledException)
        {
            completion.TrySetResult(
                Volatile.Read(ref disposed) != 0
                    ? AchievementImportDeliveryState.Canceled
                    : AchievementImportDeliveryState.Expired);
        }
        catch
        {
            completion.TrySetResult(AchievementImportDeliveryState.Failed);
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var request = await ReadRequestAsync(stream, cancellationToken);
        if (request is null)
        {
            await WriteEmptyAsync(stream, 400, "Bad Request", includeCors: false, cancellationToken);
            return;
        }
        if (!string.Equals(request.Host, $"127.0.0.1:{port}", StringComparison.Ordinal)
            || !string.Equals(request.Origin, allowedOrigin, StringComparison.Ordinal))
        {
            await WriteEmptyAsync(stream, 403, "Forbidden", includeCors: false, cancellationToken);
            return;
        }
        var expectedPath = $"/v1/achievement-import/{nonce}";
        if (!string.Equals(request.Path, expectedPath, StringComparison.Ordinal))
        {
            await WriteEmptyAsync(stream, 404, "Not Found", includeCors: true, cancellationToken);
            return;
        }

        if (request.Method == "OPTIONS")
        {
            if (!string.Equals(request.AccessControlRequestMethod, "GET", StringComparison.Ordinal)
                || !string.IsNullOrEmpty(request.AccessControlRequestHeaders)
                || request.AccessControlRequestPrivateNetwork is not (null or "true"))
            {
                await WriteEmptyAsync(stream, 403, "Forbidden", includeCors: true, cancellationToken);
                return;
            }
            await WritePreflightAsync(stream, cancellationToken);
            return;
        }
        if (request.Method != "GET")
        {
            await WriteEmptyAsync(stream, 405, "Method Not Allowed", includeCors: true, cancellationToken);
            return;
        }
        if (Interlocked.Exchange(ref delivered, 1) != 0)
        {
            await WriteEmptyAsync(stream, 410, "Gone", includeCors: true, cancellationToken);
            return;
        }

        var headers =
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {artifact.Length}\r\n" +
            CorsHeaders() +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await stream.WriteAsync(artifact, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        lifetime.Cancel();
    }

    private async Task<HttpRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumHeaderBytes + 1];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0) return null;
                length += read;
                var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                if (end < 0) continue;
                if (end + 4 != length) return null;
                var text = Encoding.ASCII.GetString(buffer, 0, end);
                return ParseRequest(text);
            }
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static HttpRequest? ParseRequest(string text)
    {
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 2) return null;
        var requestLine = lines[0].Split(' ', StringSplitOptions.None);
        if (requestLine.Length != 3
            || requestLine[0] is not ("GET" or "OPTIONS")
            || requestLine[1].Length is <= 0 or > 512
            || !requestLine[1].StartsWith("/", StringComparison.Ordinal)
            || requestLine[1].Contains('?')
            || requestLine[1].Contains('#')
            || requestLine[2] != "HTTP/1.1")
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf(':');
            if (separator <= 0 || separator == line.Length - 1) return null;
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (!name.All(static character =>
                    char.IsAsciiLetterOrDigit(character) || character == '-')
                || value.Length > 2048
                || value.Any(static character => char.IsControl(character) && character != '\t')
                || !headers.TryAdd(name, value))
                return null;
        }
        if (headers.ContainsKey("Transfer-Encoding")
            || headers.TryGetValue("Content-Length", out var contentLength)
                && contentLength != "0")
            return null;
        headers.TryGetValue("Host", out var host);
        headers.TryGetValue("Origin", out var origin);
        headers.TryGetValue("Access-Control-Request-Method", out var accessMethod);
        headers.TryGetValue("Access-Control-Request-Headers", out var accessHeaders);
        headers.TryGetValue("Access-Control-Request-Private-Network", out var privateNetwork);
        return new(
            requestLine[0],
            requestLine[1],
            host,
            origin,
            accessMethod,
            accessHeaders,
            privateNetwork);
    }

    private async Task WritePreflightAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var response =
            "HTTP/1.1 204 No Content\r\n" +
            CorsHeaders() +
            "Access-Control-Allow-Methods: GET\r\n" +
            "Access-Control-Allow-Private-Network: true\r\n" +
            "Access-Control-Max-Age: 60\r\n" +
            "Content-Length: 0\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task WriteEmptyAsync(
        NetworkStream stream,
        int status,
        string reason,
        bool includeCors,
        CancellationToken cancellationToken)
    {
        var response =
            $"HTTP/1.1 {status} {reason}\r\n" +
            (includeCors ? CorsHeaders() : string.Empty) +
            "Content-Length: 0\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private string CorsHeaders() =>
        $"Access-Control-Allow-Origin: {allowedOrigin}\r\n" +
        "Vary: Origin, Access-Control-Request-Method, Access-Control-Request-Private-Network\r\n";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        listener.Stop();
        try
        {
            await worker;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(artifact);
            lifetime.Dispose();
        }
    }

    private sealed record HttpRequest(
        string Method,
        string Path,
        string? Host,
        string? Origin,
        string? AccessControlRequestMethod,
        string? AccessControlRequestHeaders,
        string? AccessControlRequestPrivateNetwork);
}
