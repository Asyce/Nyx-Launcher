using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Nyx.Desktop.Core.PublisherMaintenance;

namespace Nyx.Desktop.Infrastructure.PublisherMaintenance;

internal interface IHoyoBranchTransport
{
    Task<ReadOnlyMemory<byte>> FetchAsync(CancellationToken cancellationToken);
}

internal sealed class HoyoBranchHttpTransport : IHoyoBranchTransport, IDisposable
{
    internal const string FixedEndpoint =
        "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches"
        + "?game_ids%5B%5D=gopR6Cufr3"
        + "&game_ids%5B%5D=4ziysqXOQ8"
        + "&game_ids%5B%5D=U5hbdsT9W7"
        + "&launcher_id=VYTpXlbWo8";

    internal static readonly TimeSpan ProductionTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient client;
    private readonly TimeSpan totalTimeout;

    public HoyoBranchHttpTransport()
        : this(CreateProductionHandler(), ProductionTimeout)
    {
    }

    internal HoyoBranchHttpTransport(HttpMessageHandler handler, TimeSpan totalTimeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (totalTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(totalTimeout));
        }

        client = new(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        this.totalTimeout = totalTimeout;
    }

    public async Task<ReadOnlyMemory<byte>> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(totalTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, FixedEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is not HttpStatusCode.OK)
            {
                throw new HoyoTransportException(PublisherCheckFailure.HttpStatus);
            }

            if (!string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "application/json",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new HoyoTransportException(PublisherCheckFailure.ContentType);
            }

            if (response.Content.Headers.ContentLength is > HoyoBranchResponseParser.MaximumResponseBytes)
            {
                throw new HoyoTransportException(PublisherCheckFailure.ResponseTooLarge);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var writer = new ArrayBufferWriter<byte>();
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (writer.WrittenCount + read > HoyoBranchResponseParser.MaximumResponseBytes)
                    {
                        throw new HoyoTransportException(PublisherCheckFailure.ResponseTooLarge);
                    }

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
            throw new HoyoTransportException(PublisherCheckFailure.Timeout);
        }
    }

    public void Dispose() => client.Dispose();

    internal static SocketsHttpHandler CreateProductionHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        };
}

internal sealed class HoyoTransportException(PublisherCheckFailure failure) : Exception
{
    public PublisherCheckFailure Failure { get; } = failure;
}
