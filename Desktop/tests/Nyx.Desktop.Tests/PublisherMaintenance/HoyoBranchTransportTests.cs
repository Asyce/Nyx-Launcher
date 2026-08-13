using System.Net;
using System.Net.Http.Headers;
using Nyx.Desktop.Core.PublisherMaintenance;
using Nyx.Desktop.Infrastructure.PublisherMaintenance;

namespace Nyx.Desktop.Tests.PublisherMaintenance;

public sealed class HoyoBranchTransportTests
{
    [Fact]
    public async Task Request_uses_only_fixed_https_batch_endpoint()
    {
        HttpRequestMessage? captured = null;
        using var transport = new HoyoBranchHttpTransport(
            new DelegateHandler((request, _) =>
            {
                captured = request;
                return Task.FromResult(JsonResponse(SanitizedHoyoFixtures.ValidBatch));
            }),
            TimeSpan.FromSeconds(1));

        var body = await transport.FetchAsync(CancellationToken.None);

        Assert.NotEmpty(body.ToArray());
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(HoyoBranchHttpTransport.FixedEndpoint, captured.RequestUri!.AbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttps, captured.RequestUri.Scheme);
        Assert.Null(captured.Content);
        Assert.Equal(["application/json"], captured.Headers.Accept.Select(value => value.MediaType));
        Assert.DoesNotContain(captured.Headers, header =>
            header.Key.Contains("uthor", StringComparison.OrdinalIgnoreCase)
            || header.Key.Contains("ookie", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "game_ids%5B%5D=gopR6Cufr3&game_ids%5B%5D=4ziysqXOQ8&game_ids%5B%5D=U5hbdsT9W7&launcher_id=VYTpXlbWo8",
            captured.RequestUri.Query[1..]);
    }

    [Fact]
    public void Production_handler_disables_redirects_cookies_and_decompression()
    {
        using var handler = HoyoBranchHttpTransport.CreateProductionHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromSeconds(10), HoyoBranchHttpTransport.ProductionTimeout);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "application/json", PublisherCheckFailure.HttpStatus)]
    [InlineData(HttpStatusCode.InternalServerError, "application/json", PublisherCheckFailure.HttpStatus)]
    [InlineData(HttpStatusCode.OK, "text/html", PublisherCheckFailure.ContentType)]
    [InlineData(HttpStatusCode.OK, null, PublisherCheckFailure.ContentType)]
    public async Task Redirect_status_and_non_json_content_fail_closed(
        HttpStatusCode status,
        string? contentType,
        PublisherCheckFailure expected)
    {
        using var transport = new HoyoBranchHttpTransport(
            new DelegateHandler((_, _) =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent("{}"),
                };
                response.Content.Headers.ContentType = contentType is null
                    ? null
                    : new MediaTypeHeaderValue(contentType);
                return Task.FromResult(response);
            }),
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<HoyoTransportException>(
            () => transport.FetchAsync(CancellationToken.None));

        Assert.Equal(expected, exception.Failure);
    }

    [Fact]
    public async Task Body_larger_than_256_kib_is_rejected_without_content_length()
    {
        using var transport = new HoyoBranchHttpTransport(
            new DelegateHandler((_, _) =>
            {
                var content = new StreamContent(new MemoryStream(
                    new byte[HoyoBranchResponseParser.MaximumResponseBytes + 1],
                    writable: false));
                content.Headers.ContentType = new("application/json");
                content.Headers.ContentLength = null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }),
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<HoyoTransportException>(
            () => transport.FetchAsync(CancellationToken.None));

        Assert.Equal(PublisherCheckFailure.ResponseTooLarge, exception.Failure);
    }

    [Fact]
    public async Task Total_timeout_is_enforced()
    {
        using var transport = new HoyoBranchHttpTransport(
            new DelegateHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }),
            TimeSpan.FromMilliseconds(25));

        var exception = await Assert.ThrowsAsync<HoyoTransportException>(
            () => transport.FetchAsync(CancellationToken.None));

        Assert.Equal(PublisherCheckFailure.Timeout, exception.Failure);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mislabeled_as_timeout()
    {
        using var transport = new HoyoBranchHttpTransport(
            new DelegateHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }),
            TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.FetchAsync(cancellation.Token));
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };
        response.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        return response;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
