using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

internal enum HoyoLabSyncFailure
{
    None,
    InvalidRequest,
    Absent,
    Authentication,
    Conflict,
    RateLimited,
    RemoteFailure,
    InvalidResponse,
    RequestTooLarge,
    ResponseTooLarge,
    Timeout,
    Canceled,
    Network,
}

internal sealed record HoyoLabSyncOutcome(
    HoyoLabSyncFailure Failure,
    bool Exists = false,
    DateTimeOffset? UpdatedAt = null,
    int? Size = null,
    byte[]? Payload = null,
    DateTimeOffset? ServerUpdatedAt = null)
{
    internal bool IsSuccess => Failure == HoyoLabSyncFailure.None;

    internal bool IsAbsent => Failure == HoyoLabSyncFailure.Absent;

    internal bool IsConflict => Failure == HoyoLabSyncFailure.Conflict;

    public override string ToString() => nameof(HoyoLabSyncOutcome);
}

internal sealed class HoyoLabSyncClient : IDisposable
{
    internal const int MaximumRequestBytes =
        ((HoyoLabSyncCrypto.MaximumCiphertextBytes + 2) / 3) * 4 + 1024;
    internal const int MaximumResponseBytes = MaximumRequestBytes;
    internal static readonly TimeSpan ProductionTimeout = TimeSpan.FromSeconds(10);
    internal static readonly Uri FixedEndpointRoot = new(
        "https://pengo.gg/api/account/sync/",
        UriKind.Absolute);

    private const string SyncKindHeader = "X-Nyx-Sync-Kind";
    private const string SyncKind = "hoyolab";
    private const string Game = "hsr";
    private const int SyncIdCharacters = 48;
    private const int TokenCharacters = 64;
    private const int MinimumCiphertextBytes = 17;
    private const int MaximumCiphertextBytes = HoyoLabSyncCrypto.MaximumCiphertextBytes;
    private const int MaximumCiphertextBase64Characters =
        ((MaximumCiphertextBytes + 2) / 3) * 4;
    private const int MaximumJsonDepth = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly HttpClient client;
    private readonly TimeSpan totalTimeout;
    private readonly Action<ReadOnlyMemory<byte>>? clearedBufferObserver;
    private int disposed;

    public HoyoLabSyncClient()
        : this(CreateProductionHandler(), ProductionTimeout)
    {
    }

    internal HoyoLabSyncClient(
        HttpMessageHandler handler,
        TimeSpan totalTimeout,
        Action<ReadOnlyMemory<byte>>? clearedBufferObserver = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (totalTimeout <= TimeSpan.Zero
            || totalTimeout > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
        client = new(handler, disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        this.totalTimeout = totalTimeout;
        this.clearedBufferObserver = clearedBufferObserver;
    }

    internal Task<HoyoLabSyncOutcome> PushAsync(
        HoyoLabSyncCrypto.DerivedSecrets? secrets,
        HoyoLabSyncCrypto.Envelope? envelope,
        DateTimeOffset? baseUpdatedAt = null,
        CancellationToken cancellationToken = default) => SendAsync(
        SyncAction.Push,
        secrets,
        envelope,
        baseUpdatedAt,
        cancellationToken);

    internal Task<HoyoLabSyncOutcome> PullAsync(
        HoyoLabSyncCrypto.DerivedSecrets? secrets,
        CancellationToken cancellationToken = default) => SendAsync(
        SyncAction.Pull,
        secrets,
        null,
        null,
        cancellationToken);

    internal Task<HoyoLabSyncOutcome> StatusAsync(
        HoyoLabSyncCrypto.DerivedSecrets? secrets,
        CancellationToken cancellationToken = default) => SendAsync(
        SyncAction.Status,
        secrets,
        null,
        null,
        cancellationToken);

    internal Task<HoyoLabSyncOutcome> DeleteAsync(
        HoyoLabSyncCrypto.DerivedSecrets? secrets,
        CancellationToken cancellationToken = default) => SendAsync(
        SyncAction.Delete,
        secrets,
        null,
        null,
        cancellationToken);

    internal Task<HoyoLabSyncOutcome> DeleteAccountAsync(
        HoyoLabSyncCrypto.DerivedSecrets? secrets,
        CancellationToken cancellationToken = default) => SendAsync(
        SyncAction.DeleteAccount,
        secrets,
        null,
        null,
        cancellationToken);

    internal async Task<HoyoLabSyncOutcome> DeletePendingAsync(
        HoyoLabPendingDeletion? deletion,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Canceled();
        if (Volatile.Read(ref disposed) != 0
            || !HoyoLabSyncStateStore.IsValidPendingDeletion(
                deletion,
                DateTimeOffset.UtcNow,
                enforceClock: true))
            return InvalidRequest();

        byte[]? body = null;
        try
        {
            var action = deletion!.Scope == HoyoLabSyncStateStore.HsrScope
                ? SyncAction.Delete
                : SyncAction.DeleteAccount;
            body = SerializeRequest(
                action,
                deletion.SyncId,
                Convert.ToHexStringLower(deletion.Token.Span),
                null,
                null);
            if (body is null) return InvalidRequest();
            if (body.Length > MaximumRequestBytes) return RequestTooLarge();
            if (cancellationToken.IsCancellationRequested) return Canceled();
            return await SendRequestAsync(action, body, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return InvalidRequest();
        }
        finally
        {
            Clear(body);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            client.Dispose();
    }

    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Credentials = null,
        UseCookies = false,
        UseProxy = false,
    };

    private async Task<HoyoLabSyncOutcome> SendAsync(
        SyncAction action,
        HoyoLabSyncCrypto.DerivedSecrets? secrets,
        HoyoLabSyncCrypto.Envelope? envelope,
        DateTimeOffset? baseUpdatedAt,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Canceled();
        if (Volatile.Read(ref disposed) != 0 || secrets is null || secrets.IsDisposed)
            return InvalidRequest();
        if (action != SyncAction.Push && (envelope is not null || baseUpdatedAt is not null))
            return InvalidRequest();
        string? baseTimestamp = null;
        if (action == SyncAction.Push && !TryFormatTimestamp(baseUpdatedAt, out baseTimestamp))
            return InvalidRequest();

        byte[]? envelopeJson = null;
        byte[]? body = null;
        try
        {
            string syncId;
            string token;
            try
            {
                syncId = secrets.SyncId;
                token = Convert.ToHexStringLower(secrets.Token);
            }
            catch (ObjectDisposedException)
            {
                return InvalidRequest();
            }

            if (!IsLowerHex(syncId, SyncIdCharacters) || !IsLowerHex(token, TokenCharacters))
                return InvalidRequest();

            if (action == SyncAction.Push
                && envelope?.Ciphertext is { Length: > MaximumCiphertextBase64Characters })
                return RequestTooLarge();
            if (action == SyncAction.Push
                && (!HoyoLabSyncCrypto.TrySerializeEnvelope(envelope, out envelopeJson)
                    || envelopeJson.Length == 0))
                return InvalidRequest();

            body = SerializeRequest(action, syncId, token, baseTimestamp, envelopeJson);
            if (body is null) return InvalidRequest();
            if (body.Length > MaximumRequestBytes) return RequestTooLarge();
            if (cancellationToken.IsCancellationRequested) return Canceled();
            return await SendRequestAsync(action, body, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Clear(envelopeJson);
            Clear(body);
        }
    }

    private async Task<HoyoLabSyncOutcome> SendRequestAsync(
        SyncAction action,
        byte[] body,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(totalTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointFor(action))
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(SyncKindHeader, SyncKind);

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode)) return InvalidResponse();
            if (!IsJson(response.Content.Headers.ContentType)) return InvalidResponse();
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return ResponseTooLarge();

            var responseBytes = await ReadResponseAsync(response, timeout.Token).ConfigureAwait(false);
            try
            {
                if (responseBytes is null) return ResponseTooLarge();
                return ParseResponse(action, response.StatusCode, responseBytes);
            }
            finally
            {
                Clear(responseBytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Canceled();
        }
        catch (OperationCanceledException)
        {
            return Timeout();
        }
        catch (HttpRequestException)
        {
            return Network();
        }
        catch (ObjectDisposedException)
        {
            return Network();
        }
        catch (InvalidOperationException)
        {
            return Network();
        }
        catch (IOException)
        {
            return Network();
        }
        catch (Exception)
        {
            return Network();
        }
    }

    private async Task<byte[]?> ReadResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(readCancellation.Token)
            .ConfigureAwait(false);
        using var writer = new MemoryStream(Math.Min(MaximumResponseBytes, 16 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    readCancellation.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (writer.Length + read > MaximumResponseBytes)
                {
                    readCancellation.Cancel();
                    return null;
                }
                await writer.WriteAsync(
                    buffer.AsMemory(0, read),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return writer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            Clear(writer);
        }
    }

    private byte[]? SerializeRequest(
        SyncAction action,
        string syncId,
        string token,
        string? baseTimestamp,
        byte[]? envelopeJson)
    {
        using var output = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", SyncKind);
                writer.WriteString("syncId", syncId);
                writer.WriteString("token", token);
                writer.WriteString("game", Game);
                if (action == SyncAction.Push)
                {
                    if (baseTimestamp is null) writer.WriteNull("baseUpdatedAt");
                    else writer.WriteString("baseUpdatedAt", baseTimestamp);
                    writer.WritePropertyName("payload");
                    writer.WriteRawValue(envelopeJson, skipInputValidation: false);
                }
                writer.WriteEndObject();
                writer.Flush();
            }
            return output.ToArray();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            Clear(output);
        }
    }

    private HoyoLabSyncOutcome ParseResponse(
        SyncAction action,
        HttpStatusCode statusCode,
        ReadOnlyMemory<byte> body)
    {
        if (statusCode == HttpStatusCode.NotFound) return Absent();
        if (statusCode == HttpStatusCode.Forbidden) return new(HoyoLabSyncFailure.Authentication);
        if (statusCode == (HttpStatusCode)429) return new(HoyoLabSyncFailure.RateLimited);
        if ((int)statusCode == 409) return ParseConflict(body);
        if ((int)statusCode >= 500) return new(HoyoLabSyncFailure.RemoteFailure);
        if (statusCode != HttpStatusCode.OK) return new(HoyoLabSyncFailure.RemoteFailure);

        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
            var root = document.RootElement;
            return action switch
            {
                SyncAction.Push => ParsePushSuccess(root),
                SyncAction.Pull => ParsePullSuccess(root),
                SyncAction.Status => ParseStatusSuccess(root),
                SyncAction.Delete or SyncAction.DeleteAccount => ParseDeleteSuccess(root),
                _ => InvalidResponse(),
            };
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
        catch (InvalidOperationException)
        {
            return InvalidResponse();
        }
        catch (FormatException)
        {
            return InvalidResponse();
        }
        catch (OverflowException)
        {
            return InvalidResponse();
        }
    }

    private static HoyoLabSyncOutcome ParsePushSuccess(JsonElement root)
    {
        if (!HasExactProperties(root, "ok", "updatedAt", "size")
            || !IsBoolean(root, "ok", expected: true)
            || !TryGetTimestamp(root, "updatedAt", out var updatedAt)
            || !TryGetSize(root, "size", out var size))
            return InvalidResponse();
        return new(HoyoLabSyncFailure.None, true, updatedAt, size);
    }

    private HoyoLabSyncOutcome ParsePullSuccess(JsonElement root)
    {
        if (!HasExactProperties(root, "ok", "payload", "updatedAt", "size")
            || !IsBoolean(root, "ok", expected: true)
            || !TryGetTimestamp(root, "updatedAt", out var updatedAt)
            || !TryGetSize(root, "size", out var size))
            return InvalidResponse();

        var payload = root.GetProperty("payload");
        if (payload.ValueKind != JsonValueKind.Object) return InvalidResponse();
        byte[]? payloadJson = null;
        try
        {
            payloadJson = StrictUtf8.GetBytes(payload.GetRawText());
            if (!HoyoLabSyncCrypto.TryParseEnvelope(payloadJson, out var envelope)
                || !HoyoLabSyncCrypto.TrySerializeEnvelope(envelope, out var canonical))
                return InvalidResponse();
            return new(HoyoLabSyncFailure.None, true, updatedAt, size, canonical);
        }
        finally
        {
            Clear(payloadJson);
        }
    }

    private static HoyoLabSyncOutcome ParseStatusSuccess(JsonElement root)
    {
        if (!IsBoolean(root, "ok", expected: true)
            || !TryGetBoolean(root, "exists", out var exists))
            return InvalidResponse();

        if (!HasExactProperties(root, "ok", "exists", "updatedAt", "size"))
            return InvalidResponse();
        return exists
            && TryGetTimestamp(root, "updatedAt", out var updatedAt)
            && TryGetSize(root, "size", out var size)
            ? new(HoyoLabSyncFailure.None, true, updatedAt, size)
            : InvalidResponse();
    }

    private static HoyoLabSyncOutcome ParseDeleteSuccess(JsonElement root) =>
        HasExactProperties(root, "ok", "deleted")
            && IsBoolean(root, "ok", expected: true)
            && TryGetBoolean(root, "deleted", out _)
            ? new(HoyoLabSyncFailure.None)
            : InvalidResponse();

    private static HoyoLabSyncOutcome ParseConflict(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;
            if (!HasExactProperties(root, "ok", "error", "serverUpdatedAt")
                || !IsBoolean(root, "ok", expected: false)
                || !TryGetConflictError(root.GetProperty("error")))
                return InvalidResponse();
            var serverUpdatedAt = root.GetProperty("serverUpdatedAt");
            if (serverUpdatedAt.ValueKind == JsonValueKind.Null)
                return new(HoyoLabSyncFailure.Conflict);
            return serverUpdatedAt.ValueKind == JsonValueKind.String
                && TryParseTimestamp(serverUpdatedAt.GetString(), out var parsed)
                ? new(HoyoLabSyncFailure.Conflict, ServerUpdatedAt: parsed)
                : InvalidResponse();
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
        catch (InvalidOperationException)
        {
            return InvalidResponse();
        }
        catch (FormatException)
        {
            return InvalidResponse();
        }
    }

    private static bool TryGetConflictError(JsonElement value)
    {
        if (!HasExactProperties(value, "code", "message", "requestId")
            || value.GetProperty("code").ValueKind != JsonValueKind.String
            || !string.Equals(
                value.GetProperty("code").GetString(),
                "stale_write",
                StringComparison.Ordinal)
            || value.GetProperty("message").ValueKind != JsonValueKind.String
            || value.GetProperty("requestId").ValueKind != JsonValueKind.String)
            return false;
        var message = value.GetProperty("message").GetString();
        var requestId = value.GetProperty("requestId").GetString();
        return !string.IsNullOrEmpty(message)
            && message.Length <= 512
            && !string.IsNullOrEmpty(requestId)
            && requestId.Length <= 128
            && requestId.All(character => character is >= '\x20' and <= '\x7e');
    }

    private static bool TryGetTimestamp(
        JsonElement root,
        string property,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        return root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && TryParseTimestamp(value.GetString(), out timestamp);
    }

    private static bool TryGetSize(JsonElement root, string property, out int size)
    {
        size = 0;
        return root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out size)
            && size is >= MinimumCiphertextBytes and <= MaximumCiphertextBytes;
    }

    private static bool TryGetBoolean(JsonElement root, string property, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(property, out var candidate)) return false;
        if (candidate.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        return candidate.ValueKind == JsonValueKind.False;
    }

    private static bool IsBoolean(JsonElement root, string property, bool expected) =>
        root.TryGetProperty(property, out var value)
        && ((expected && value.ValueKind == JsonValueKind.True)
            || (!expected && value.ValueKind == JsonValueKind.False));

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(expected, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            count++;
            if (!names.Remove(property.Name)) return false;
        }
        return count == expected.Length && names.Count == 0;
    }

    private static bool TryFormatTimestamp(DateTimeOffset? value, out string? formatted)
    {
        formatted = null;
        if (value is null) return true;
        var timestamp = value.Value;
        if (timestamp.Offset != TimeSpan.Zero
            || timestamp < DateTimeOffset.UnixEpoch
            || timestamp.Ticks % TimeSpan.TicksPerMillisecond != 0)
            return false;
        formatted = timestamp.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value is null
            || value.Length != 24
            || !DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp)
            || timestamp.Offset != TimeSpan.Zero
            || timestamp < DateTimeOffset.UnixEpoch)
            return false;
        return timestamp.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture) == value;
    }

    private static Uri EndpointFor(SyncAction action) => new(
        FixedEndpointRoot,
        action switch
        {
            SyncAction.Push => "push",
            SyncAction.Pull => "pull",
            SyncAction.Status => "status",
            SyncAction.Delete => "delete",
            SyncAction.DeleteAccount => "delete-account",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        });

    private static bool IsJson(MediaTypeHeaderValue? contentType) =>
        string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and < 400;

    private static bool IsLowerHex(string? value, int length) => value is not null
        && value.Length == length
        && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    private HoyoLabSyncOutcome InvalidRequest() => new(HoyoLabSyncFailure.InvalidRequest);

    private static HoyoLabSyncOutcome InvalidResponse() => new(HoyoLabSyncFailure.InvalidResponse);

    private static HoyoLabSyncOutcome RequestTooLarge() => new(HoyoLabSyncFailure.RequestTooLarge);

    private static HoyoLabSyncOutcome ResponseTooLarge() => new(HoyoLabSyncFailure.ResponseTooLarge);

    private static HoyoLabSyncOutcome Canceled() => new(HoyoLabSyncFailure.Canceled);

    private static HoyoLabSyncOutcome Timeout() => new(HoyoLabSyncFailure.Timeout);

    private static HoyoLabSyncOutcome Network() => new(HoyoLabSyncFailure.Network);

    private static HoyoLabSyncOutcome Absent() => new(HoyoLabSyncFailure.Absent);

    private void Clear(byte[]? bytes)
    {
        if (bytes is null) return;
        CryptographicOperations.ZeroMemory(bytes);
        clearedBufferObserver?.Invoke(bytes);
    }

    private void Clear(MemoryStream stream)
    {
        if (!stream.TryGetBuffer(out var buffer)) return;
        CryptographicOperations.ZeroMemory(buffer.AsSpan());
        clearedBufferObserver?.Invoke(buffer.AsMemory());
    }

    private enum SyncAction
    {
        Push,
        Pull,
        Status,
        Delete,
        DeleteAccount,
    }
}
