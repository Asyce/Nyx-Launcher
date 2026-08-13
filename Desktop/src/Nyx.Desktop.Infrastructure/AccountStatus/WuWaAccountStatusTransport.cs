using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Infrastructure.AccountStatus;

internal interface IWuWaAccountStatusTransport
{
    Task<byte[]> PostAsync(Uri endpoint, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

internal sealed class WuWaAccountStatusTransport : IWuWaAccountStatusTransport, IDisposable
{
    internal const int MaximumResponseBytes = 64 * 1024;
    internal static readonly TimeSpan ProductionTimeout = TimeSpan.FromSeconds(8);
    internal static readonly Uri PlayerInfoEndpoint = new(
        "https://pc-launcher-sdk-api.kurogame.net/game/queryPlayerInfo");
    internal static readonly Uri RoleEndpoint = new(
        "https://pc-launcher-sdk-api.kurogame.net/game/queryRole");

    private readonly HttpClient client;
    private readonly TimeSpan totalTimeout;

    public WuWaAccountStatusTransport()
        : this(CreateProductionHandler(), ProductionTimeout)
    {
    }

    internal WuWaAccountStatusTransport(HttpMessageHandler handler, TimeSpan totalTimeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (totalTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(totalTimeout));
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        this.totalTimeout = totalTimeout;
    }

    public async Task<byte[]> PostAsync(
        Uri endpoint,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(totalTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ReadOnlyMemoryContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new WuWaTransportException(WuWaAccountStatusFailure.InvalidResponse);
            if (response.StatusCode is not HttpStatusCode.OK)
                throw new WuWaTransportException(WuWaAccountStatusFailure.Network);
            if (!string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase))
                throw new WuWaTransportException(WuWaAccountStatusFailure.InvalidResponse);
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw new WuWaTransportException(WuWaAccountStatusFailure.ResponseTooLarge);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var writer = new ArrayBufferWriter<byte>();
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    if (writer.WrittenCount + read > MaximumResponseBytes)
                        throw new WuWaTransportException(WuWaAccountStatusFailure.ResponseTooLarge);
                    writer.Write(buffer.AsSpan(0, read));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            return writer.WrittenMemory.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WuWaTransportException(WuWaAccountStatusFailure.Timeout);
        }
    }

    public void Dispose() => client.Dispose();

    internal static void ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.Equals(PlayerInfoEndpoint) && !endpoint.Equals(RoleEndpoint))
            throw new InvalidOperationException("The account-status endpoint is not allowed.");
    }

    internal static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        UseCookies = false,
        UseProxy = false,
    };
}

internal sealed class WuWaTransportException(WuWaAccountStatusFailure failure) : Exception
{
    public WuWaAccountStatusFailure Failure { get; } = failure;
}
