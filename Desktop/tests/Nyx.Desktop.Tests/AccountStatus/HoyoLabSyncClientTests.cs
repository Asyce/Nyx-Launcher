using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabSyncClientTests
{
    private const string DisplayCode = "NYX-HOYO-AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-GGGG-HHHH";
    private const string SyncId = "a295b9a2d46ecbdb935fa7961cea87951060e1c973d9289f";
    private const string Token = "520ebcb203323a013f383f2992b37119ae7b864a1abb91a638315f39d63bb79e";
    private const string UpdatedAt = "2026-08-30T00:00:00.000Z";
    private const string Ciphertext =
        "FqIMPqw/OKA4OGLA3kyVV6oO7sv06FGD0EFY3SeorYTTOa7VUd20/WgaZgHD4fJB6mFeab8dVIhkJDt+5JTmlaxwYWIqpRrJiVhapEkzooqHCyecIz/Auw56B39ssxUq75SVt17OyC7hriqbcND0c0EqLeLa866VnGTVdF84aafPDtToRoTKsh7kMp7blG2Oar0IVoHDJJH5+WONy9xSw/J3e2l7ZJLTkKLjBajk/dxAKc5aklDCYQDlFqMYn4WtAOPmdaiEfxHb+OHUU/Z7sfANhx5xai12w83eJORehLJUFP5IUjwptGGMOl0GIGZvedD0jwiTUkUM+Ym2RanNtYu+PYqR2UjsShYEoJDR+eWbroZSM7rgSJwKgFmBuzLfp6X2MkJWYs/uNLKqSfpx51ApDTHiSxM3O5f3FZJRHt8BqdgHiaNAn6TgxH2RKJy+ARFewTAa7kBkeMpOjYO6tvbQNhbPxFCEU3pLd/JHZifbc1QM0UBN1alMqnPbI9qM1Jv2uC9LQQakHfxhVSyv6+7fhIGST+mrFE0RGehomszwtFYds+AWMSRTKP5xYqAiedBOA+ziepefLgfDkmd7eEBVFebYIPCjS8lCQzI97IodXHszdR5des1th8K5RMm3f+RLNR4WUmJfWui4xBrohMfcHjh0aju4NWH5iDjMq57CUgYYV7NBoP563gKwsgZNaHNzbz6ybm5PBkqEzmvaDpo4dQm3DYLtUocEXIBIfViCcCG73Ng+XWOxQok4pD1JWrAFv/sZhwmzHHINFb2yPswfV9oAFPPj8VUoVJctEGv3896jickTB66iZOVrdKJxCKJEUeeCS+ZV54vM/Z5rpR49vpBcsW2mUUYq9RYEfaNXqE/cKF5w/VzLeTnksrmRVd6mSZs1BmPgGoeGokLctgBiF2lKIhhuu47m5IbN70gQj3Ex/IGaLwzEedUcV2qXVIjwKU4TRy3S/sGy0r2F7OPG1XJX4Ev+KleFwVhr7B1wuOGBrBt0lQp2VjD0eA0AYrWu6dN93wTzkfvKiAPjtOCJ4GI545FQP7GbWai41RZwwzJI";

    [Fact]
    public async Task Every_action_uses_the_fixed_post_contract_and_hsr_game()
    {
        var actions = new[] { "push", "pull", "status", "delete", "delete-account" };
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            new { ok = true, updatedAt = UpdatedAt, size = 17 })));
        using var client = CreateClient(handler);
        using var secrets = Secrets();
        var envelope = VectorEnvelope();

        Assert.Equal(HoyoLabSyncFailure.None,
            (await client.PushAsync(secrets, envelope)).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidResponse,
            (await client.PullAsync(secrets)).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidResponse,
            (await client.StatusAsync(secrets)).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidResponse,
            (await client.DeleteAsync(secrets)).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidResponse,
            (await client.DeleteAccountAsync(secrets)).Failure);

        Assert.Equal(actions, handler.Requests.Select(static request => request.Uri.AbsolutePath.Split('/').Last()));
        foreach (var request in handler.Requests)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal("pengo.gg", request.Uri.Host);
            Assert.Equal("/api/account/sync/" + request.Uri.AbsolutePath.Split('/').Last(), request.Uri.AbsolutePath);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal("hoyolab", request.Headers["X-Nyx-Sync-Kind"]);
            Assert.DoesNotContain("Origin", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cookie", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            var expected = request.Uri.AbsolutePath.EndsWith("/push", StringComparison.Ordinal)
                ? new[] { "kind", "syncId", "token", "game", "baseUpdatedAt", "payload" }
                : new[] { "kind", "syncId", "token", "game" };
            Assert.Equal(expected.Order(), root.EnumerateObject().Select(static property => property.Name).Order());
            Assert.Equal("hoyolab", root.GetProperty("kind").GetString());
            Assert.Equal(SyncId, root.GetProperty("syncId").GetString());
            Assert.Equal(Token, root.GetProperty("token").GetString());
            Assert.Equal("hsr", root.GetProperty("game").GetString());
            Assert.False(root.TryGetProperty("force", out _));
            Assert.False(root.TryGetProperty("Origin", out _));
        }
    }

    [Fact]
    public async Task Push_serializes_the_public_vector_and_both_cas_forms()
    {
        foreach (var baseUpdatedAt in new DateTimeOffset?[] { null, DateTimeOffset.Parse(UpdatedAt) })
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
                new { ok = true, updatedAt = UpdatedAt, size = 17 })));
            using var client = CreateClient(handler);
            using var secrets = Secrets();
            var envelope = VectorEnvelope();

            var result = await client.PushAsync(secrets, envelope, baseUpdatedAt);

            Assert.Equal(HoyoLabSyncFailure.None, result.Failure);
            Assert.Single(handler.Requests);
            using var request = JsonDocument.Parse(handler.Requests[0].Body);
            var root = request.RootElement;
            Assert.Equal(
                baseUpdatedAt is null ? JsonValueKind.Null : JsonValueKind.String,
                root.GetProperty("baseUpdatedAt").ValueKind);
            if (baseUpdatedAt is not null)
                Assert.Equal(UpdatedAt, root.GetProperty("baseUpdatedAt").GetString());
            var payload = root.GetProperty("payload");
            Assert.Equal(
                new[] { "format", "kdf", "iv", "ciphertext" }.Order(),
                payload.EnumerateObject().Select(static property => property.Name).Order());
            Assert.Equal(HoyoLabSyncCrypto.Format, payload.GetProperty("format").GetString());
            Assert.Equal("PBKDF2", payload.GetProperty("kdf").GetProperty("name").GetString());
            Assert.Equal("SHA-256", payload.GetProperty("kdf").GetProperty("hash").GetString());
            Assert.Equal(150_000, payload.GetProperty("kdf").GetProperty("iterations").GetInt32());
            Assert.Equal("AAECAwQFBgcICQoL", payload.GetProperty("iv").GetString());
            Assert.Equal(Ciphertext, payload.GetProperty("ciphertext").GetString());
        }
    }

    [Fact]
    public async Task Pull_returns_a_canonical_opaque_vector_payload()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            "{" +
            "\"ok\":true," +
            "\"payload\":{" +
            "\"format\":\"nyx-hoyolab-sync-v1\",\"kdf\":{" +
            "\"name\":\"PBKDF2\",\"hash\":\"SHA-256\",\"iterations\":150000}," +
            "\"iv\":\"AAECAwQFBgcICQoL\",\"ciphertext\":\"" + Ciphertext + "\"}," +
            "\"updatedAt\":\"" + UpdatedAt + "\",\"size\":17}")));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.PullAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.None, result.Failure);
        Assert.Equal(UpdatedAt, result.UpdatedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        Assert.Equal(17, result.Size);
        Assert.Equal(
            "{\"format\":\"nyx-hoyolab-sync-v1\",\"kdf\":{\"name\":\"PBKDF2\",\"hash\":\"SHA-256\",\"iterations\":150000},\"iv\":\"AAECAwQFBgcICQoL\",\"ciphertext\":\"" + Ciphertext + "\"}",
            Encoding.UTF8.GetString(Assert.IsType<byte[]>(result.Payload)));
    }

    [Fact]
    public async Task Status_and_delete_success_schemas_are_action_specific()
    {
        var responses = new Queue<HttpResponseMessage>([
            JsonResponse(new { ok = true, exists = true, updatedAt = UpdatedAt, size = 17 }),
            JsonResponse(new { ok = true, deleted = true }),
            JsonResponse(new { ok = true, deleted = true }),
        ]);
        var handler = new FakeHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var status = await client.StatusAsync(secrets);
        var deleted = await client.DeleteAsync(secrets);
        var accountDeleted = await client.DeleteAccountAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.None, status.Failure);
        Assert.True(status.Exists);
        Assert.Equal(HoyoLabSyncFailure.None, deleted.Failure);
        Assert.Equal(HoyoLabSyncFailure.None, accountDeleted.Failure);
        Assert.Equal("/api/account/sync/status", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("/api/account/sync/delete", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal("/api/account/sync/delete-account", handler.Requests[2].Uri.AbsolutePath);
        Assert.All(handler.Requests.Skip(1), static request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            Assert.Equal(4, document.RootElement.EnumerateObject().Count());
            Assert.False(document.RootElement.TryGetProperty("payload", out _));
        });
    }

    [Fact]
    public async Task Delete_account_is_isolated_from_game_payloads_and_force_overwrite()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(new { ok = true, deleted = true })));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.DeleteAccountAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.None, result.Failure);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://pengo.gg/api/account/sync/delete-account", request.Uri.AbsoluteUri);
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(
            new[] { "kind", "syncId", "token", "game" }.Order(),
            document.RootElement.EnumerateObject().Select(static property => property.Name).Order());
        Assert.DoesNotContain("force", Encoding.UTF8.GetString(request.Body), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reloaded_outbox_deletes_both_scopes_after_current_key_is_removed_without_recovery_code()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-sync-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HoyoLabSyncStateStore(
                directory,
                new CopyProtector(),
                new SystemPublisherRoleBindingFileBoundary(),
                TimeProvider.System);
            using var credential = new HoyoLabSyncCredential(
                SyncId, Convert.FromHexString(Token), Enumerable.Repeat((byte)7, 32).ToArray());
            var key = credential.Key;
            var requestedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.True(store.TrySetCurrentCredential(credential));
            foreach (var scope in new[] { HoyoLabSyncStateStore.HsrScope, HoyoLabSyncStateStore.AllHoyoScope })
            {
                using var deletion = new HoyoLabPendingDeletion(
                    credential.SyncId, credential.Token, scope, "delete-" + scope, requestedAt);
                Assert.True(store.TryEnqueuePendingDeletion(deletion));
            }
            credential.Dispose();
            Assert.All(key.ToArray(), value => Assert.Equal(0, value));
            Assert.True(store.TryClearCurrentCredential());

            using var reloaded = Assert.IsType<HoyoLabSyncState>(store.TryLoad());
            Assert.Null(reloaded.CurrentCredential);
            var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(new { ok = true, deleted = true })));
            using var client = CreateClient(handler);
            foreach (var deletion in reloaded.PendingDeletions)
                Assert.True((await client.DeletePendingAsync(deletion)).IsSuccess);

            Assert.Equal(new[] { "delete-account", "delete" },
                handler.Requests.Select(request => request.Uri.Segments[^1]));
            foreach (var request in handler.Requests)
            {
                using var document = JsonDocument.Parse(request.Body);
                Assert.Equal(new[] { "game", "kind", "syncId", "token" },
                    document.RootElement.EnumerateObject().Select(property => property.Name).Order());
                Assert.Equal(SyncId, document.RootElement.GetProperty("syncId").GetString());
                Assert.Equal(Token, document.RootElement.GetProperty("token").GetString());
                Assert.Equal("hsr", document.RootElement.GetProperty("game").GetString());
                Assert.DoesNotContain("NYX-HOYO", Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reloaded_conditional_cleanup_sends_only_the_copied_revision_and_preserves_it_after_conflict(bool expectAbsent)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nyx-sync-conditioned-delete-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HoyoLabSyncStateStore(directory, new CopyProtector(),
                new SystemPublisherRoleBindingFileBoundary(), TimeProvider.System);
            var requestedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            DateTimeOffset? revision = expectAbsent ? null : DateTimeOffset.Parse(UpdatedAt);
            using var deletion = new HoyoLabPendingDeletion(SyncId, Convert.FromHexString(Token),
                HoyoLabSyncStateStore.AllHoyoScope, "rotation-cleanup", requestedAt,
                requireRevisionMatch: true, expectedRevision: revision);
            Assert.True(store.TryEnqueuePendingDeletion(deletion));
            var restarted = new HoyoLabSyncStateStore(directory, new CopyProtector(),
                new SystemPublisherRoleBindingFileBoundary(), TimeProvider.System);
            using var loaded = Assert.IsType<HoyoLabSyncState>(restarted.TryLoad());
            var pending = Assert.Single(loaded.PendingDeletions);
            var responses = new Queue<HttpResponseMessage>([
                JsonResponse(HttpStatusCode.Conflict,
                    "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"Changed\",\"requestId\":\"test-request\"},\"serverUpdatedAt\":\"" + UpdatedAt + "\"}"),
                JsonResponse(new { ok = true, deleted = true }),
            ]);
            var handler = new FakeHandler((_, _) => Task.FromResult(responses.Dequeue()));
            using var client = CreateClient(handler);
            var conflicted = await client.DeletePendingAsync(pending);
            Assert.True(conflicted.IsConflict);
            Assert.Equal(DateTimeOffset.Parse(UpdatedAt), conflicted.ServerUpdatedAt);
            using (var retained = Assert.IsType<HoyoLabSyncState>(restarted.TryLoad()))
            {
                Assert.True(Assert.Single(retained.PendingDeletions).RequireRevisionMatch);
                Assert.Equal(revision, retained.PendingDeletions[0].ExpectedRevision);
            }
            Assert.True((await client.DeletePendingAsync(pending)).IsSuccess);
            Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
            foreach (var request in handler.Requests)
            {
                Assert.Equal("https://pengo.gg/api/account/sync/delete-account", request.Uri.AbsoluteUri);
                using var document = JsonDocument.Parse(request.Body);
                var body = document.RootElement;
                Assert.Equal(new[] { "baseUpdatedAt", "game", "kind", "syncId", "token" },
                    body.EnumerateObject().Select(property => property.Name).Order());
                Assert.Equal(expectAbsent ? null : UpdatedAt, body.GetProperty("baseUpdatedAt").GetString());
                Assert.Equal("hoyolab", body.GetProperty("kind").GetString());
                Assert.Equal("hsr", body.GetProperty("game").GetString());
                Assert.Equal(SyncId, body.GetProperty("syncId").GetString());
                Assert.Equal(Token, body.GetProperty("token").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("future")]
    [InlineData("offset")]
    [InlineData("submillisecond")]
    [InlineData("before-epoch")]
    public async Task Conditional_cleanup_rejects_invalid_saved_revisions_without_sending(string failure)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(new { ok = true, deleted = true })));
        using var client = CreateClient(handler);
        var requestedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var revision = failure switch
        {
            "future" => requestedAt.AddDays(1),
            "offset" => requestedAt.ToOffset(TimeSpan.FromHours(1)),
            "submillisecond" => requestedAt.AddTicks(1),
            _ => DateTimeOffset.UnixEpoch.AddMilliseconds(-1),
        };
        using var deletion = new HoyoLabPendingDeletion(SyncId, Convert.FromHexString(Token),
            HoyoLabSyncStateStore.AllHoyoScope, "rotation-cleanup", requestedAt,
            requireRevisionMatch: true, expectedRevision: revision);
        Assert.Equal(HoyoLabSyncFailure.InvalidRequest, (await client.DeletePendingAsync(deletion)).Failure);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disposed")]
    [InlineData("future")]
    [InlineData("canceled")]
    public async Task Pending_deletion_rejects_unusable_entries_before_sending(string failure)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(new { ok = true, deleted = true })));
        using var client = CreateClient(handler);
        var requestedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        using var deletion = new HoyoLabPendingDeletion(
            SyncId, Convert.FromHexString(Token), HoyoLabSyncStateStore.HsrScope, "delete-hsr",
            failure == "future" ? requestedAt.AddDays(1) : requestedAt);
        if (failure == "disposed") deletion.Dispose();
        using var cancellation = new CancellationTokenSource();
        if (failure == "canceled") cancellation.Cancel();

        var result = await client.DeletePendingAsync(failure == "null" ? null : deletion, cancellation.Token);

        Assert.Equal(failure == "canceled" ? HoyoLabSyncFailure.Canceled : HoyoLabSyncFailure.InvalidRequest,
            result.Failure);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(404, "Absent")]
    [InlineData(403, "Authentication")]
    [InlineData(429, "RateLimited")]
    [InlineData(500, "RemoteFailure")]
    [InlineData(502, "RemoteFailure")]
    public async Task Http_failures_are_typed_without_remote_text(int status, string expected)
    {
        const string secretText = "remote-body-token-and-sync-id";
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            (HttpStatusCode)status,
            "{\"ok\":false,\"error\":{\"code\":\"remote\",\"message\":\"" + secretText + "\"}}")));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.PullAsync(secrets);

        Assert.Equal(Enum.Parse<HoyoLabSyncFailure>(expected), result.Failure);
        Assert.DoesNotContain(secretText, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyncId, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, "550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("2026-08-30T00:00:00.001Z", "2026-08-30T00:00:00.001Z", "req-abc123")]
    public async Task Stale_write_conflict_preserves_only_the_optional_server_timestamp(
        string? serverUpdatedAt,
        string? expectedTimestamp,
        string requestId)
    {
        var response = "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"remote text with "
            + Token + " and " + SyncId + "\",\"requestId\":\"" + requestId + "\"},\"serverUpdatedAt\":"
            + (serverUpdatedAt is null ? "null" : "\"" + serverUpdatedAt + "\"") + "}";
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.Conflict,
            response)));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.PushAsync(secrets, VectorEnvelope(), DateTimeOffset.Parse(UpdatedAt));

        Assert.Equal(HoyoLabSyncFailure.Conflict, result.Failure);
        Assert.Equal(expectedTimestamp, result.ServerUpdatedAt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyncId, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_and_wrong_or_non_json_content_are_rejected_and_disposed()
    {
        var trackingStream = new TrackingStream(Encoding.UTF8.GetBytes("{}"));
        var responses = new Queue<HttpResponseMessage>([
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Content = JsonContent("{}"),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Content(Encoding.UTF8.GetBytes("{}"), "text/plain"),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(trackingStream),
            },
        ]);
        responses.Last().Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var handler = new FakeHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, (await client.StatusAsync(secrets)).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, (await client.StatusAsync(secrets)).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, (await client.StatusAsync(secrets)).Failure);
        Assert.True(trackingStream.WasDisposed);
    }

    [Theory]
    [InlineData("missing", "{\"ok\":true,\"updatedAt\":\"2026-08-30T00:00:00.000Z\"}")]
    [InlineData("extra", "{\"ok\":true,\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":17,\"extra\":1}")]
    [InlineData("duplicate", "{\"ok\":true,\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":17,\"size\":17}")]
    [InlineData("timestamp", "{\"ok\":true,\"updatedAt\":\"2026-08-30T00:00:00Z\",\"size\":17}")]
    [InlineData("size", "{\"ok\":true,\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":16}")]
    [InlineData("size-type", "{\"ok\":true,\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":\"17\"}")]
    public async Task Push_success_requires_the_exact_schema(string _, string body)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.PushAsync(secrets, VectorEnvelope());

        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, result.Failure);
    }

    [Theory]
    [InlineData("missing", "{\"ok\":true,\"payload\":{},\"updatedAt\":\"2026-08-30T00:00:00.000Z\"}")]
    [InlineData("extra", "{\"ok\":true,\"payload\":{},\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":17,\"extra\":1}")]
    [InlineData("bad-base64", "{\"ok\":true,\"payload\":{\"format\":\"nyx-hoyolab-sync-v1\",\"kdf\":{\"name\":\"PBKDF2\",\"hash\":\"SHA-256\",\"iterations\":150000},\"iv\":\"not-base64\",\"ciphertext\":\"AQ==\"},\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":17}")]
    [InlineData("bad-envelope", "{\"ok\":true,\"payload\":{\"format\":\"wrong\",\"kdf\":{\"name\":\"PBKDF2\",\"hash\":\"SHA-256\",\"iterations\":150000},\"iv\":\"AAECAwQFBgcICQoL\",\"ciphertext\":\"AQ==\"},\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":17}")]
    [InlineData("duplicate", "{\"ok\":true,\"payload\":{},\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":17,\"size\":17}")]
    public async Task Pull_success_requires_a_strict_valid_envelope(string _, string body)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.PullAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, result.Failure);
        Assert.Null(result.Payload);
    }

    [Theory]
    [InlineData("bad-timestamp", "{\"ok\":true,\"exists\":true,\"updatedAt\":\"2026-08-30T00:00:00Z\",\"size\":17}")]
    [InlineData("bad-size", "{\"ok\":true,\"exists\":true,\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":3145745}")]
    [InlineData("extra", "{\"ok\":true,\"exists\":true,\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":17,\"extra\":1}")]
    [InlineData("duplicate", "{\"ok\":true,\"exists\":true,\"updatedAt\":\"2026-08-30T00:00:00.000Z\",\"size\":17,\"size\":17}")]
    public async Task Status_success_requires_valid_metadata(string _, string body)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.StatusAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task Status_success_requires_metadata_even_when_exists_is_false()
    {
        var responses = new Queue<HttpResponseMessage>([
            JsonResponse(new { ok = true, exists = false }),
            JsonResponse(new { ok = true, exists = true }),
        ]);
        var handler = new FakeHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, (await client.StatusAsync(secrets)).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, (await client.StatusAsync(secrets)).Failure);
    }

    [Theory]
    [InlineData("missing", "{\"ok\":true}")]
    [InlineData("wrong", "{\"ok\":false,\"deleted\":true}")]
    [InlineData("extra", "{\"ok\":true,\"deleted\":true,\"extra\":1}")]
    [InlineData("duplicate", "{\"ok\":true,\"deleted\":true,\"deleted\":true}")]
    public async Task Delete_success_requires_the_exact_idempotent_schema(string _, string body)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.DeleteAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, result.Failure);
    }

    [Fact]
    public async Task Conflict_requires_the_strict_safe_error_envelope()
    {
        var bodies = new[]
        {
            "{\"ok\":false,\"error\":{\"code\":\"wrong\",\"message\":\"safe\",\"requestId\":\"req-safe\"},\"serverUpdatedAt\":null}",
            "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\",\"requestId\":\"req-safe\",\"extra\":1},\"serverUpdatedAt\":null}",
            "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\",\"requestId\":\"req-safe\"},\"serverUpdatedAt\":\"2026-08-30T00:00:00Z\"}",
            "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\",\"requestId\":\"req-safe\"},\"serverUpdatedAt\":null,\"extra\":1}",
            "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\"},\"serverUpdatedAt\":null}",
            "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\",\"requestId\":7},\"serverUpdatedAt\":null}",
            "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\",\"requestId\":\"\"},\"serverUpdatedAt\":null}",
            "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\",\"requestId\":\"req-\\u0001\"},\"serverUpdatedAt\":null}",
            "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\",\"requestId\":\"req-safe\",\"requestId\":\"req-other\"},\"serverUpdatedAt\":null}",
        };
        using var secrets = Secrets();
        foreach (var body in bodies)
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.Conflict,
                body)));
            using var client = CreateClient(handler);

            Assert.Equal(HoyoLabSyncFailure.InvalidResponse,
                (await client.PushAsync(secrets, VectorEnvelope())).Failure);
        }
    }

    [Fact]
    public async Task Conflict_rejects_an_overlong_request_id()
    {
        var requestId = new string('x', 129);
        var body = "{\"ok\":false,\"error\":{\"code\":\"stale_write\",\"message\":\"safe\",\"requestId\":\""
            + requestId + "\"},\"serverUpdatedAt\":null}";
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.Conflict,
            body)));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        Assert.Equal(HoyoLabSyncFailure.InvalidResponse,
            (await client.PushAsync(secrets, VectorEnvelope())).Failure);
    }

    [Fact]
    public async Task Request_validation_prevents_send_and_clears_serialized_buffers()
    {
        var cleared = new List<byte[]>();
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            new { ok = true, updatedAt = UpdatedAt, size = 17 })));
        using var client = new HoyoLabSyncClient(
            handler,
            TimeSpan.FromSeconds(1),
            memory => cleared.Add(memory.ToArray()));
        using var secrets = Secrets();

        Assert.Equal(HoyoLabSyncFailure.InvalidRequest,
            (await client.PushAsync(secrets, null)).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidRequest,
            (await client.PushAsync(
                secrets,
                VectorEnvelope(),
                new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.FromHours(1)))).Failure);
        Assert.Equal(HoyoLabSyncFailure.InvalidRequest,
            (await client.PushAsync(
                secrets,
                VectorEnvelope(),
                DateTimeOffset.Parse(UpdatedAt).AddTicks(1))).Failure);
        Assert.Equal(
            HoyoLabSyncFailure.None,
            (await client.PushAsync(secrets, VectorEnvelope())).Failure);
        Assert.Single(handler.Requests);
        Assert.NotEmpty(cleared);
        Assert.All(cleared, static bytes => Assert.All(bytes, static value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task Oversized_push_is_rejected_before_the_handler_runs()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            new { ok = true, updatedAt = UpdatedAt, size = 17 })));
        using var client = CreateClient(handler);
        using var secrets = Secrets();
        var oversized = VectorEnvelope() with
        {
            Ciphertext = Convert.ToBase64String(
                new byte[HoyoLabSyncCrypto.MaximumCiphertextBytes + 3]),
        };

        var result = await client.PushAsync(secrets, oversized);

        Assert.Equal(HoyoLabSyncFailure.RequestTooLarge, result.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_and_timeout_are_typed()
    {
        var handler = new FakeHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(new { ok = true, exists = false });
        });
        using var secrets = Secrets();
        using (var client = CreateClient(handler, TimeSpan.FromSeconds(1)))
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = client.StatusAsync(secrets, cancellation.Token);
            cancellation.Cancel();
            Assert.Equal(HoyoLabSyncFailure.Canceled, (await pending).Failure);
        }

        using var timedClient = CreateClient(handler, TimeSpan.FromMilliseconds(20));
        Assert.Equal(HoyoLabSyncFailure.Timeout, (await timedClient.StatusAsync(secrets)).Failure);
    }

    [Fact]
    public async Task Caller_already_canceled_does_not_send()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(new { ok = true })));
        using var client = CreateClient(handler);
        using var secrets = Secrets();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await client.StatusAsync(secrets, cancellation.Token);

        Assert.Equal(HoyoLabSyncFailure.Canceled, result.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Streamed_response_at_the_exact_cap_is_read_without_overflow()
    {
        var stream = new TrackingStream(new byte[HoyoLabSyncClient.MaximumResponseBytes]);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = HoyoLabSyncClient.MaximumResponseBytes;
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        using var client = CreateClient(handler, TimeSpan.FromSeconds(5));
        using var secrets = Secrets();

        var result = await client.StatusAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.InvalidResponse, result.Failure);
        Assert.True(stream.ReadStarted);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task Response_cap_rejects_forged_length_and_streamed_cap_plus_one_with_cancel()
    {
        var forgedStream = new TrackingStream(Encoding.UTF8.GetBytes("{}"));
        var forgedContent = new StreamContent(forgedStream);
        forgedContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        forgedContent.Headers.ContentLength = HoyoLabSyncClient.MaximumResponseBytes + 1;
        var capPlusOne = new CancellableStream(new byte[HoyoLabSyncClient.MaximumResponseBytes + 1]);
        var capContent = new StreamContent(capPlusOne);
        capContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        capContent.Headers.ContentLength = 1;
        var responses = new Queue<HttpResponseMessage>([
            new HttpResponseMessage(HttpStatusCode.OK) { Content = forgedContent },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = capContent },
        ]);
        var handler = new FakeHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var client = CreateClient(handler, TimeSpan.FromSeconds(5));
        using var secrets = Secrets();

        Assert.Equal(HoyoLabSyncFailure.ResponseTooLarge, (await client.StatusAsync(secrets)).Failure);
        Assert.False(forgedStream.ReadStarted);
        Assert.Equal(HoyoLabSyncFailure.ResponseTooLarge, (await client.StatusAsync(secrets)).Failure);
        Assert.True(capPlusOne.CancellationObserved);
    }

    [Fact]
    public async Task Short_content_length_does_not_bypass_stream_limit()
    {
        var stream = new CancellableStream(new byte[HoyoLabSyncClient.MaximumResponseBytes + 1]);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = 1;
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        using var client = CreateClient(handler, TimeSpan.FromSeconds(5));
        using var secrets = Secrets();

        var result = await client.StatusAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.ResponseTooLarge, result.Failure);
        Assert.True(stream.CancellationObserved);
    }

    [Fact]
    public void Production_handler_has_the_required_network_boundaries()
    {
        using var handler = HoyoLabSyncClient.CreateProductionHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Credentials);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task Client_disposes_response_when_reading_fails()
    {
        var stream = new ThrowingStream();
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        using var client = CreateClient(handler);
        using var secrets = Secrets();

        var result = await client.StatusAsync(secrets);

        Assert.Equal(HoyoLabSyncFailure.Network, result.Failure);
        Assert.True(stream.WasDisposed);
    }

    private static HoyoLabSyncClient CreateClient(
        FakeHandler handler,
        TimeSpan? timeout = null) => new(handler, timeout ?? TimeSpan.FromSeconds(1));

    private static HoyoLabSyncCrypto.DerivedSecrets Secrets()
    {
        Assert.True(HoyoLabSyncCrypto.TryDerive(DisplayCode, out var secrets));
        return Assert.IsType<HoyoLabSyncCrypto.DerivedSecrets>(secrets);
    }

    private static HoyoLabSyncCrypto.Envelope VectorEnvelope() => new(
        HoyoLabSyncCrypto.Format,
        new("PBKDF2", "SHA-256", 150_000),
        "AAECAwQFBgcICQoL",
        Ciphertext);

    private static HttpResponseMessage JsonResponse(object body) => JsonResponse(
        HttpStatusCode.OK,
        JsonSerializer.Serialize(body));

    private static HttpResponseMessage JsonResponse(string body) => JsonResponse(HttpStatusCode.OK, body);

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = JsonContent(body),
        };
    }

    private static HttpContent JsonContent(string body) => Content(
        Encoding.UTF8.GetBytes(body),
        "application/json");

    private static HttpContent Content(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        Dictionary<string, string> Headers,
        byte[] Body,
        string? ContentType);

    private sealed class CopyProtector : IPublisherRoleBindingProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        internal List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers
                .ToDictionary(
                    static header => header.Key,
                    static header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    headers[header.Key] = string.Join(",", header.Value);
            }
            Requests.Add(new(
                request.Method,
                request.RequestUri!,
                headers,
                body,
                request.Content?.Headers.ContentType?.MediaType));
            return await responder(request, cancellationToken);
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal bool ReadStarted { get; private set; }
        internal bool WasDisposed { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            ReadStarted = true;
            return base.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancellableStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal bool CancellationObserved { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.Register(static state => ((CancellableStream)state!).CancellationObserved = true, this);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ThrowingStream : MemoryStream
    {
        internal bool WasDisposed { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("test-only stream failure"));

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
