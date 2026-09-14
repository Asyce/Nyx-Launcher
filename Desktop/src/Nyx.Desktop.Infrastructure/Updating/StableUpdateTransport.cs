using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Nyx.Desktop.Core.Updating;

namespace Nyx.Desktop.Infrastructure.Updating;

public sealed record StableUpdateCheck(UpdateReleaseManifest Manifest, byte[] ManifestBytes);

public sealed record StableUpdateDownload(string ManifestPath, string PackagePath, string OwnerPath);

internal enum StableUpdateDownloadCheckpoint
{
    OwnerWritten,
    ManifestWritten,
    PackageWritten,
}

public sealed class StableUpdateTransport : IDisposable
{
    public const string ManifestEndpoint = "https://pengo.gg/desktop/updates/stable/release.json";
    internal static readonly TimeSpan ProductionTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient client;
    private readonly TimeSpan timeout;

    public StableUpdateTransport()
        : this(CreateProductionHandler(), ProductionTimeout)
    {
    }

    internal StableUpdateTransport(HttpMessageHandler handler, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        this.timeout = timeout;
    }

    public async Task<StableUpdateCheck?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutSource.Token).ConfigureAwait(false);
        if (response.StatusCode is not HttpStatusCode.OK
            || !string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength is > UpdateManifestReader.MaximumManifestBytes)
        {
            throw new InvalidDataException("Stable update manifest request failed.");
        }

        var bytes = await ReadBoundedAsync(
            response.Content,
            UpdateManifestReader.MaximumManifestBytes,
            timeoutSource.Token).ConfigureAwait(false);
        var manifest = UpdateManifestReader.Parse(bytes);
        if (!string.Equals(manifest.Channel, "stable", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stable update channel mismatch.");
        }

        return StableUpdatePolicy.IsStrictUpgrade(currentVersion, manifest.Version)
            ? new(manifest, bytes)
            : null;
    }

    public async Task<StableUpdateDownload?> DownloadIfAcceptedAsync(
        StableUpdateCheck update,
        string stagingRoot,
        Func<Task<bool>> confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(confirm);
        if (!await confirm().ConfigureAwait(true)) return null;
        return await DownloadAsync(update, stagingRoot, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<StableUpdateDownload> DownloadAsync(
        StableUpdateCheck update,
        string stagingRoot,
        CancellationToken cancellationToken,
        string? handoffId = null,
        Action<StableUpdateDownloadCheckpoint>? checkpoint = null)
    {
        var parsed = UpdateManifestReader.Parse(update.ManifestBytes);
        if (!string.Equals(parsed.Channel, "stable", StringComparison.Ordinal)
            || parsed.PackageUrl is null)
        {
            throw new InvalidDataException("Stable update manifest changed.");
        }

        var root = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(root);
        if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Stable update staging root is unsafe.");
        }

        handoffId ??= Guid.NewGuid().ToString("N");
        if (!IsGeneratedId(handoffId)) throw new ArgumentException("Invalid handoff id.", nameof(handoffId));
        var names = StableUpdateArtifactContract.CreateNames(handoffId, parsed.Version);
        var ownerPath = Path.Combine(root, names.OwnerFileName);
        var manifestPath = Path.Combine(root, names.ManifestFileName);
        var packagePath = Path.Combine(root, names.PackageFileName);
        var faultInjectionActive = false;
        void Checkpoint(StableUpdateDownloadCheckpoint value)
        {
            if (checkpoint is null) return;
            faultInjectionActive = true;
            checkpoint(value);
            faultInjectionActive = false;
        }

        try
        {
            using (var process = Process.GetCurrentProcess())
            {
                var owner = new StableUpdateArtifactOwner(
                    1,
                    Environment.ProcessId,
                    process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                    parsed.Version);
                await WriteNewFileAsync(
                    ownerPath,
                    StableUpdateArtifactContract.SerializeOwner(owner),
                    cancellationToken).ConfigureAwait(false);
            }
            Checkpoint(StableUpdateDownloadCheckpoint.OwnerWritten);

            await using (var manifestOutput = new FileStream(
                manifestPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await manifestOutput.WriteAsync(update.ManifestBytes, cancellationToken).ConfigureAwait(false);
                await manifestOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                manifestOutput.Flush(flushToDisk: true);
            }
            Checkpoint(StableUpdateDownloadCheckpoint.ManifestWritten);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, parsed.PackageUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);
            if (response.StatusCode is not HttpStatusCode.OK
                || response.Content.Headers.ContentLength is { } length && length != parsed.PackageSize)
            {
                throw new InvalidDataException("Stable update package request failed.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            await using var packageOutput = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long written = 0;
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeoutSource.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    written = checked(written + read);
                    if (written > parsed.PackageSize)
                    {
                        throw new InvalidDataException("Stable update package exceeded its sealed size.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await packageOutput.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            await packageOutput.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
            packageOutput.Flush(flushToDisk: true);
            if (written != parsed.PackageSize
                || !string.Equals(
                    Convert.ToHexStringLower(hash.GetHashAndReset()),
                    parsed.PackageSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Stable update package hash mismatch.");
            }
            Checkpoint(StableUpdateDownloadCheckpoint.PackageWritten);

            return new(manifestPath, packagePath, ownerPath);
        }
        catch when (!faultInjectionActive)
        {
            DeleteExactFile(packagePath);
            DeleteExactFile(manifestPath);
            DeleteExactFile(ownerPath);
            throw;
        }
    }

    public void Dispose() => client.Dispose();

    private static bool IsGeneratedId(string value) =>
        value.Length == 32
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void DeleteExactFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(Math.Min(maximumBytes, 32 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > maximumBytes)
                    throw new InvalidDataException("Stable update response exceeded its byte limit.");
                memory.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        return memory.ToArray();
    }

    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        UseCookies = false,
        UseProxy = false,
    };
}
