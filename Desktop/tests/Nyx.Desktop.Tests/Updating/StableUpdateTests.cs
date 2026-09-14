using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Nyx.Desktop.Core.Updating;
using Nyx.Desktop.Infrastructure.Updating;

namespace Nyx.Desktop.Tests.Updating;

public sealed class StableUpdateTests
{
    [Fact]
    public void Eligibility_is_silent_for_development_unpacked_and_missing_control_runs()
    {
        var root = Path.Combine(Path.GetTempPath(), "NyxStableEligibilityTests", Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local");
        var install = Path.Combine(local, "Programs", "Pengo Nyx");
        var app = Path.Combine(install, "app");
        var control = Path.Combine(install, "control", "Nyx.Desktop.Update.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(control)!);
        Directory.CreateDirectory(app);
        File.WriteAllText(control, "control");
        try
        {
            Assert.Null(StableUpdatePolicy.FindInstalled(app, local, "development", "1.0.0.0"));
            Assert.Null(StableUpdatePolicy.FindInstalled(root, local, "stable", "1.0.0.0"));

            File.Delete(control);
            Assert.Null(StableUpdatePolicy.FindInstalled(app, local, "stable", "1.0.0.0"));

            File.WriteAllText(control, "control");
            var eligible = StableUpdatePolicy.FindInstalled(app, local, "stable", "1.0.0.0");
            Assert.NotNull(eligible);
            Assert.Equal(control, eligible.ControlUpdaterPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Fixed_manifest_check_precedes_prompt_and_decline_never_gets_the_package()
    {
        var fixture = ReleaseFixture();
        var requests = new List<string>();
        using var transport = new StableUpdateTransport(
            new Handler(request =>
            {
                requests.Add(request.RequestUri!.AbsoluteUri);
                return request.RequestUri.AbsoluteUri == StableUpdateTransport.ManifestEndpoint
                    ? JsonResponse(fixture.ManifestBytes)
                    : BytesResponse(fixture.PackageBytes);
            }),
            TimeSpan.FromSeconds(5));

        var update = await transport.CheckAsync("1.0.0.0", CancellationToken.None);

        Assert.NotNull(update);
        Assert.Equal([StableUpdateTransport.ManifestEndpoint], requests);
        Assert.Null(await transport.DownloadIfAcceptedAsync(
            update,
            Path.Combine(Path.GetTempPath(), "unused-staging"),
            () => Task.FromResult(false),
            CancellationToken.None));
        Assert.Equal([StableUpdateTransport.ManifestEndpoint], requests);
    }

    [Fact]
    public async Task Accepted_download_uses_sealed_url_create_new_size_and_hash()
    {
        var fixture = ReleaseFixture();
        var requests = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "NyxStableTransportTests", Guid.NewGuid().ToString("N"));
        using var transport = new StableUpdateTransport(
            new Handler(request =>
            {
                requests.Add(request.RequestUri!.AbsoluteUri);
                return request.RequestUri.AbsoluteUri == StableUpdateTransport.ManifestEndpoint
                    ? JsonResponse(fixture.ManifestBytes)
                    : BytesResponse(fixture.PackageBytes);
            }),
            TimeSpan.FromSeconds(5));
        try
        {
            var update = await transport.CheckAsync("1.0.0.0", CancellationToken.None);
            var download = await transport.DownloadIfAcceptedAsync(
                update!,
                root,
                () => Task.FromResult(true),
                CancellationToken.None);

            Assert.NotNull(download);
            Assert.Equal(
                [StableUpdateTransport.ManifestEndpoint, fixture.Manifest.PackageUrl!],
                requests);
            Assert.Equal(fixture.ManifestBytes, File.ReadAllBytes(download.ManifestPath));
            Assert.Equal(fixture.PackageBytes, File.ReadAllBytes(download.PackagePath));
            var owner = StableUpdateArtifactContract.ParseOwner(File.ReadAllBytes(download.OwnerPath));
            Assert.Equal(Environment.ProcessId, owner.OwnerProcessId);
            Assert.Equal(fixture.Manifest.Version, owner.TargetVersion);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Manifest_redirects_and_non_success_responses_are_rejected(HttpStatusCode status)
    {
        using var transport = new StableUpdateTransport(
            new Handler(_ => new HttpResponseMessage(status)),
            TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            transport.CheckAsync("1.0.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_manifest_is_rejected_from_headers()
    {
        var fixture = ReleaseFixture();
        var response = JsonResponse(fixture.ManifestBytes);
        response.Content.Headers.ContentLength = UpdateManifestReader.MaximumManifestBytes + 1L;
        using var transport = new StableUpdateTransport(
            new Handler(_ => response),
            TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            transport.CheckAsync("1.0.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task Partial_package_is_rejected_and_its_handoff_files_are_removed()
    {
        var fixture = ReleaseFixture();
        var root = Path.Combine(Path.GetTempPath(), "NyxStablePartialTests", Guid.NewGuid().ToString("N"));
        using var transport = new StableUpdateTransport(
            new Handler(request =>
            {
                if (request.RequestUri!.AbsoluteUri == StableUpdateTransport.ManifestEndpoint)
                    return JsonResponse(fixture.ManifestBytes);
                var response = BytesResponse(fixture.PackageBytes[..^1]);
                response.Content.Headers.ContentLength = null;
                return response;
            }),
            TimeSpan.FromSeconds(5));
        try
        {
            var update = await transport.CheckAsync("1.0.0.0", CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(() => transport.DownloadIfAcceptedAsync(
                update!,
                root,
                () => Task.FromResult(true),
                CancellationToken.None));

            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Close_during_manifest_get_cancels_the_owned_check_without_a_package_request()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var transport = new StableUpdateTransport(
            new AsyncHandler(async (_, cancellationToken) =>
            {
                requested.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }),
            TimeSpan.FromSeconds(5));

        var check = transport.CheckAsync("1.0.0.0", cancellation.Token);
        await requested.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    [Fact]
    public async Task Close_during_package_get_removes_only_the_exact_owned_handoff_file()
    {
        var fixture = ReleaseFixture();
        var packageRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "NyxStableCancellationTests", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        using var transport = new StableUpdateTransport(
            new AsyncHandler(async (request, cancellationToken) =>
            {
                if (request.RequestUri!.AbsoluteUri == StableUpdateTransport.ManifestEndpoint)
                    return JsonResponse(fixture.ManifestBytes);
                packageRequested.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException();
            }),
            TimeSpan.FromSeconds(5));
        try
        {
            var update = await transport.CheckAsync("1.0.0.0", CancellationToken.None);
            var download = transport.DownloadIfAcceptedAsync(
                update!,
                root,
                () => Task.FromResult(true),
                cancellation.Token);
            await packageRequested.Task;
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData((int)StableUpdateDownloadCheckpoint.ManifestWritten)]
    [InlineData((int)StableUpdateDownloadCheckpoint.PackageWritten)]
    public async Task Process_death_after_download_checkpoint_leaves_durable_exact_ownership(
        int terminationPointValue)
    {
        var terminationPoint = (StableUpdateDownloadCheckpoint)terminationPointValue;
        const string handoffId = "33333333333333333333333333333333";
        var fixture = ReleaseFixture();
        var root = Path.Combine(Path.GetTempPath(), "NyxStableOwnerTests", Guid.NewGuid().ToString("N"));
        using var transport = new StableUpdateTransport(
            new Handler(request => request.RequestUri!.AbsoluteUri == StableUpdateTransport.ManifestEndpoint
                ? JsonResponse(fixture.ManifestBytes)
                : BytesResponse(fixture.PackageBytes)),
            TimeSpan.FromSeconds(5));
        try
        {
            var update = await transport.CheckAsync("1.0.0.0", CancellationToken.None);

            await Assert.ThrowsAsync<SimulatedTermination>(() => transport.DownloadAsync(
                update!,
                root,
                CancellationToken.None,
                handoffId,
                checkpoint =>
                {
                    if (checkpoint == terminationPoint) throw new SimulatedTermination();
                }));

            var names = StableUpdateArtifactContract.CreateNames(handoffId, fixture.Manifest.Version);
            var ownerPath = Path.Combine(root, names.OwnerFileName);
            var owner = StableUpdateArtifactContract.ParseOwner(File.ReadAllBytes(ownerPath));
            Assert.Equal(Environment.ProcessId, owner.OwnerProcessId);
            Assert.True(owner.OwnerProcessStartedAtFileTime > 0);
            Assert.Equal(fixture.Manifest.Version, owner.TargetVersion);
            Assert.True(File.Exists(Path.Combine(root, names.ManifestFileName)));
            Assert.Equal(
                terminationPoint is StableUpdateDownloadCheckpoint.PackageWritten,
                File.Exists(Path.Combine(root, names.PackageFileName)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Close_while_waiting_for_ready_never_commits_shutdown_or_writes_apply()
    {
        using var cancellation = new CancellationTokenSource();
        var output = new BlockingReader();
        using var input = new StringWriter();
        var committed = false;
        var handshake = StableUpdateHandoffClient.CompleteReadyHandshakeAsync(
            output,
            input,
            () => committed = true,
            cancellation.Token);
        await output.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handshake);
        Assert.False(committed);
        Assert.Equal(string.Empty, input.ToString());
    }

    [Fact]
    public async Task Legacy_workspace_junction_cannot_redirect_flat_handoff_writes_or_cleanup()
    {
        const string handoffId = "00000000000000000000000000000000";
        var fixture = ReleaseFixture();
        var root = Path.Combine(Path.GetTempPath(), "NyxStableJunctionTests", Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "NyxStableJunctionOutside", Guid.NewGuid().ToString("N"));
        var link = Path.Combine(root, $"handoff-{handoffId}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(link, outside);
        using var transport = new StableUpdateTransport(
            new Handler(request => request.RequestUri!.AbsoluteUri == StableUpdateTransport.ManifestEndpoint
                ? JsonResponse(fixture.ManifestBytes)
                : BytesResponse(fixture.PackageBytes)),
            TimeSpan.FromSeconds(5));
        StableUpdateDownload? download = null;
        try
        {
            var update = await transport.CheckAsync("1.0.0.0", CancellationToken.None);
            download = await transport.DownloadAsync(
                update!,
                root,
                CancellationToken.None,
                handoffId);

            Assert.Equal(root, Path.GetDirectoryName(download.ManifestPath));
            Assert.Equal(root, Path.GetDirectoryName(download.PackagePath));
            Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(outside).Select(Path.GetFileName));
        }
        finally
        {
            if (download is not null)
            {
                File.Delete(download.ManifestPath);
                File.Delete(download.PackagePath);
                File.Delete(download.OwnerPath);
            }

            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    private static ReleaseData ReleaseFixture()
    {
        var package = Encoding.UTF8.GetBytes("sealed-package");
        var packageHash = Convert.ToHexStringLower(SHA256.HashData(package));
        var fileHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("app")));
        const string version = "2.0.0.0";
        var packageFile = $"Nyx-Desktop-{version}-win-x64.zip";
        var manifest = new UpdateReleaseManifest(
            1,
            "nyx-desktop",
            "stable",
            version,
            "win-x64",
            packageFile,
            package.Length,
            packageHash,
            "Nyx.Desktop.App.exe",
            $"https://pengo.gg/desktop/updates/stable/{packageFile}",
            [new("Nyx.Desktop.App.exe", 3, fileHash)]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        return new(manifest, bytes, package);
    }

    private static HttpResponseMessage JsonResponse(byte[] bytes)
    {
        var response = BytesResponse(bytes);
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    private sealed record ReleaseData(
        UpdateReleaseManifest Manifest,
        byte[] ManifestBytes,
        byte[] PackageBytes);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class AsyncHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    private sealed class BlockingReader : TextReader
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class SimulatedTermination : Exception
    {
    }
}
