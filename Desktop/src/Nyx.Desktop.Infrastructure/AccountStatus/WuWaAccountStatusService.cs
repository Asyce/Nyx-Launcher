using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

public sealed class WuWaAccountStatusService : IAsyncDisposable
{
    internal static readonly TimeSpan ProductionRateLimit = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ProductionStaleAfter = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ProductionRedisEmptyRetryDelay = TimeSpan.FromSeconds(1);
    internal const int ProductionRedisEmptyMaximumRetries = 4;

    private readonly object sync = new();
    private readonly IWuWaAccountStatusTransport transport;
    private readonly WuWaLauncherCredentialReader credentials;
    private readonly WuWaAccountStatusResponseParser parser;
    private readonly TimeProvider clock;
    private readonly TimeSpan rateLimit;
    private readonly TimeSpan staleAfter;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private readonly CancellationTokenSource shutdown = new();
    private readonly byte[] bindingKey = RandomNumberGenerator.GetBytes(32);
    private Task<WuWaAccountStatusResult>? inFlight;
    private CancellationTokenSource? activeRefresh;
    private DateTimeOffset? lastRequestAt;
    private SuccessfulObservation? previousSuccess;
    private WuWaAccountStatusResult? current;
    private long sessionGeneration;
    private bool disposed;

    public WuWaAccountStatusService()
        : this(
            new WuWaAccountStatusTransport(),
            new WuWaLauncherCredentialReader(),
            new WuWaAccountStatusResponseParser(),
            TimeProvider.System,
            ProductionRateLimit,
            ProductionStaleAfter,
            DelayAsync)
    {
    }

    internal WuWaAccountStatusService(
        IWuWaAccountStatusTransport transport,
        WuWaLauncherCredentialReader credentials,
        WuWaAccountStatusResponseParser parser,
        TimeProvider clock,
        TimeSpan rateLimit,
        TimeSpan staleAfter,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.parser = parser ?? throw new ArgumentNullException(nameof(parser));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (rateLimit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(rateLimit));
        if (staleAfter <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(staleAfter));
        this.rateLimit = rateLimit;
        this.staleAfter = staleAfter;
        this.retryDelay = retryDelay ?? DelayAsync;
    }

    public WuWaAccountStatusResult? Current
    {
        get { lock (sync) return current; }
    }

    public bool IsRefreshCoolingDown
    {
        get
        {
            lock (sync)
            {
                return lastRequestAt is { } last
                    && clock.GetUtcNow() - last < rateLimit;
            }
        }
    }

    public async Task<WuWaAccountStatusResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return TransientFailure(WuWaAccountStatusFailure.Canceled, clock.GetUtcNow());

        Task<WuWaAccountStatusResult> request;
        var now = clock.GetUtcNow();
        lock (sync)
        {
            if (disposed) return TransientFailure(WuWaAccountStatusFailure.Shutdown, now);
            if (inFlight is { IsCompleted: false })
            {
                request = inFlight;
            }
            else if (lastRequestAt is { } last && now - last < rateLimit)
            {
                // This is a local request floor, not a new publisher result.
                // Keep the useful result already shown instead of replacing it.
                return current ?? TransientFailure(WuWaAccountStatusFailure.RateLimited, now);
            }
            else
            {
                lastRequestAt = now;
                activeRefresh?.Dispose();
                activeRefresh = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                request = FetchAndProjectAsync(sessionGeneration, activeRefresh.Token);
                inFlight = request;
            }
        }

        try
        {
            return await request.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // One observer canceling must not overwrite or disclose shared state.
            return TransientFailure(WuWaAccountStatusFailure.Canceled, clock.GetUtcNow());
        }
    }

    /// <summary>
    /// Stops the current opt-in session and forgets all advisory account data.
    /// This is deliberately independent from persistence of the user's setting.
    /// </summary>
    public void DisableSession()
    {
        CancellationTokenSource? refresh;
        SuccessfulObservation? prior;
        lock (sync)
        {
            sessionGeneration++;
            refresh = activeRefresh;
            activeRefresh = null;
            inFlight = null;
            lastRequestAt = null;
            current = null;
            prior = previousSuccess;
            previousSuccess = null;
        }

        try { refresh?.Cancel(); }
        catch (ObjectDisposedException) { }
        refresh?.Dispose();
        if (prior is not null) ZeroObservationBindings(prior);
    }

    public async ValueTask DisposeAsync()
    {
        Task<WuWaAccountStatusResult>? request;
        SuccessfulObservation? prior;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            sessionGeneration++;
            request = inFlight;
            prior = previousSuccess;
            previousSuccess = null;
            current = null;
        }
        await shutdown.CancelAsync().ConfigureAwait(false);
        if (request is not null)
        {
            try { await request.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        shutdown.Dispose();
        activeRefresh?.Dispose();
        if (prior is not null) ZeroObservationBindings(prior);
        CryptographicOperations.ZeroMemory(bindingKey);
        if (transport is IDisposable disposable) disposable.Dispose();
    }

    private async Task<WuWaAccountStatusResult> FetchAndProjectAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        var outcome = await FetchAsync(cancellationToken).ConfigureAwait(false);
        return ProjectOutcome(outcome, generation);
    }

    private async Task<FetchOutcome> FetchAsync(CancellationToken cancellationToken)
    {
        byte[]? credentialBinding = null;
        byte[]? accountBinding = null;
        try
        {
            var credentialResult = await credentials.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (credentialResult.Credential is null)
                return new(credentialResult.Failure, clock.GetUtcNow(), null, null, null);

            var oauthCode = credentialResult.Credential.OAuthCode;
            credentialBinding = CreateCredentialBinding(oauthCode);
            var playerPayload = JsonSerializer.SerializeToUtf8Bytes(new { oauthCode });
            byte[]? playerResponse = null;
            try
            {
                playerResponse = await PostWithRedisEmptyRetryAsync(
                    WuWaAccountStatusTransport.PlayerInfoEndpoint,
                    playerPayload,
                    cancellationToken).ConfigureAwait(false);
                if (!parser.TryParsePlayerInfo(playerResponse, out var identity))
                {
                    var isRedisEmpty = parser.IsRedisEmpty(playerResponse);
                    return new(
                        isRedisEmpty
                            ? WuWaAccountStatusFailure.InvalidResponse
                            : parser.IsRejected(playerResponse)
                            ? WuWaAccountStatusFailure.PlayerInfoRejected
                            : WuWaAccountStatusFailure.InvalidResponse,
                        clock.GetUtcNow(),
                        null,
                        credentialBinding,
                        accountBinding);
                }

                accountBinding = CreateAccountBinding(
                    oauthCode,
                    identity!.PlayerId,
                    identity.Region);

                var rolePayload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    oauthCode,
                    playerId = identity.PlayerId,
                    region = identity.Region,
                });
                byte[]? roleResponse = null;
                try
                {
                    roleResponse = await PostWithRedisEmptyRetryAsync(
                        WuWaAccountStatusTransport.RoleEndpoint,
                        rolePayload,
                        cancellationToken).ConfigureAwait(false);
                    if (parser.TryParseRole(roleResponse, identity.Region, out var snapshot))
                        return new(
                            WuWaAccountStatusFailure.None,
                            clock.GetUtcNow(),
                            snapshot,
                            credentialBinding,
                            accountBinding);

                    var isRedisEmpty = parser.IsRedisEmpty(roleResponse);
                    return new(
                        isRedisEmpty
                            ? WuWaAccountStatusFailure.InvalidResponse
                            : parser.IsRejected(roleResponse)
                            ? WuWaAccountStatusFailure.RoleRejected
                            : WuWaAccountStatusFailure.InvalidResponse,
                        clock.GetUtcNow(),
                        null,
                        credentialBinding,
                        accountBinding);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(rolePayload);
                    if (roleResponse is not null) CryptographicOperations.ZeroMemory(roleResponse);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(playerPayload);
                if (playerResponse is not null) CryptographicOperations.ZeroMemory(playerResponse);
            }
        }
        catch (WuWaTransportException exception)
        {
            return new(exception.Failure, clock.GetUtcNow(), null, credentialBinding, accountBinding);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return new(
                WuWaAccountStatusFailure.Shutdown,
                clock.GetUtcNow(),
                null,
                credentialBinding,
                accountBinding);
        }
        catch (OperationCanceledException)
        {
            return new(
                WuWaAccountStatusFailure.Canceled,
                clock.GetUtcNow(),
                null,
                credentialBinding,
                accountBinding);
        }
        catch (HttpRequestException)
        {
            return new(WuWaAccountStatusFailure.Network, clock.GetUtcNow(), null, credentialBinding, accountBinding);
        }
        catch (IOException)
        {
            return new(WuWaAccountStatusFailure.Network, clock.GetUtcNow(), null, credentialBinding, accountBinding);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(
                WuWaAccountStatusFailure.InvalidResponse,
                clock.GetUtcNow(),
                null,
                credentialBinding,
                accountBinding);
        }
    }

    private async Task<byte[]> PostWithRedisEmptyRetryAsync(
        Uri endpoint,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        for (var redisEmptyRetry = 0; ; redisEmptyRetry++)
        {
            var response = await transport.PostAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
            if (!parser.IsRedisEmpty(response)
                || redisEmptyRetry >= ProductionRedisEmptyMaximumRetries)
            {
                return response;
            }

            CryptographicOperations.ZeroMemory(response);
            await retryDelay(ProductionRedisEmptyRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private WuWaAccountStatusResult ProjectOutcome(FetchOutcome outcome, long generation)
    {
        lock (sync)
        {
            if (generation != sessionGeneration || disposed)
            {
                if (outcome.CredentialBinding is not null)
                    CryptographicOperations.ZeroMemory(outcome.CredentialBinding);
                if (outcome.AccountBinding is not null)
                    CryptographicOperations.ZeroMemory(outcome.AccountBinding);
                return TransientFailure(
                    disposed ? WuWaAccountStatusFailure.Shutdown : WuWaAccountStatusFailure.Canceled,
                    outcome.ObservedAt);
            }

            if (outcome.Failure is WuWaAccountStatusFailure.None)
            {
                ClearPreviousSuccessLocked();
                previousSuccess = new(
                    outcome.ObservedAt,
                    outcome.Snapshot!,
                    outcome.CredentialBinding!,
                    outcome.AccountBinding!);
                current = new(
                    outcome.ObservedAt,
                    WuWaAccountStatusFailure.None,
                    outcome.Snapshot,
                    outcome.ObservedAt,
                    false);
                return current;
            }

            var identityFailure = outcome.Failure is
                WuWaAccountStatusFailure.CacheNotFound
                or WuWaAccountStatusFailure.CacheMalformed
                or WuWaAccountStatusFailure.MultipleAccounts
                or WuWaAccountStatusFailure.PlayerInfoRejected
                or WuWaAccountStatusFailure.RoleRejected;
            var sameAccount = !identityFailure
                && previousSuccess is not null
                && (outcome.AccountBinding is not null
                    ? CryptographicOperations.FixedTimeEquals(
                        outcome.AccountBinding,
                        previousSuccess.AccountBinding)
                    : outcome.CredentialBinding is not null
                        && CryptographicOperations.FixedTimeEquals(
                            outcome.CredentialBinding,
                            previousSuccess.CredentialBinding));
            if (!sameAccount) ClearPreviousSuccessLocked();

            if (outcome.CredentialBinding is not null)
                CryptographicOperations.ZeroMemory(outcome.CredentialBinding);
            if (outcome.AccountBinding is not null)
                CryptographicOperations.ZeroMemory(outcome.AccountBinding);
            var prior = sameAccount ? previousSuccess : null;
            current = new(
                outcome.ObservedAt,
                outcome.Failure,
                prior?.Snapshot,
                prior?.ObservedAt,
                prior is not null && (outcome.ObservedAt - prior.ObservedAt >= staleAfter
                    || outcome.Failure is not WuWaAccountStatusFailure.RateLimited));
            return current;
        }
    }

    private byte[] CreateAccountBinding(string oauthCode, string playerId, string region)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, bindingKey);
        AppendBoundedIdentityPart(hash, oauthCode);
        AppendBoundedIdentityPart(hash, playerId);
        AppendBoundedIdentityPart(hash, region);
        return hash.GetHashAndReset();
    }

    private byte[] CreateCredentialBinding(string oauthCode)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, bindingKey);
        AppendBoundedIdentityPart(hash, oauthCode);
        return hash.GetHashAndReset();
    }

    private static void AppendBoundedIdentityPart(IncrementalHash hash, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        try
        {
            hash.AppendData(BitConverter.GetBytes(utf8.Length));
            hash.AppendData(utf8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    private void ClearPreviousSuccessLocked()
    {
        if (previousSuccess is not null) ZeroObservationBindings(previousSuccess);
        previousSuccess = null;
    }

    private static void ZeroObservationBindings(SuccessfulObservation observation)
    {
        CryptographicOperations.ZeroMemory(observation.CredentialBinding);
        CryptographicOperations.ZeroMemory(observation.AccountBinding);
    }

    private static WuWaAccountStatusResult TransientFailure(
        WuWaAccountStatusFailure failure,
        DateTimeOffset checkedAt) =>
        new(checkedAt, failure, null, null, false);

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    private sealed record SuccessfulObservation(
        DateTimeOffset ObservedAt,
        WuWaAccountStatusSnapshot Snapshot,
        byte[] CredentialBinding,
        byte[] AccountBinding);

    private sealed record FetchOutcome(
        WuWaAccountStatusFailure Failure,
        DateTimeOffset ObservedAt,
        WuWaAccountStatusSnapshot? Snapshot,
        byte[]? CredentialBinding,
        byte[]? AccountBinding);
}
