using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

public sealed class EndfieldPullExportProvider : IPullExportProvider, IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly IReadOnlyList<EndfieldPullSource> sources;
    private readonly EndfieldPullHistoryLinkReader reader;
    private readonly IPullRequestPacer pacer;
    private readonly EndfieldPullLimits limits;
    private readonly string exportRootDirectory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan totalDuration;
    private readonly TimeSpan pollInterval;
    private readonly byte[] fingerprintKey = RandomNumberGenerator.GetBytes(32);
    private int disposed;

    public EndfieldPullExportProvider()
        : this(
            CreateHttpClient(),
            DefaultSources(),
            WindowsDocumentsDirectory.Get(),
            ownsHttp: true)
    {
    }

    internal EndfieldPullExportProvider(
        HttpClient http,
        IReadOnlyList<EndfieldPullSource> sources,
        string exportRootDirectory,
        IPullRequestPacer? pacer = null,
        EndfieldPullLimits? limits = null,
        TimeProvider? timeProvider = null,
        TimeSpan? totalDuration = null,
        TimeSpan? pollInterval = null,
        bool ownsHttp = false)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        if (this.sources.Count is 0 or > 2) throw new ArgumentOutOfRangeException(nameof(sources));
        this.exportRootDirectory = Path.GetFullPath(exportRootDirectory ?? throw new ArgumentNullException(nameof(exportRootDirectory)));
        this.pacer = pacer ?? new PullRequestPacer();
        this.limits = limits ?? new EndfieldPullLimits();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.totalDuration = totalDuration ?? TimeSpan.FromMinutes(15);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(750);
        if (this.totalDuration <= TimeSpan.Zero || this.totalDuration > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(totalDuration));
        if (this.pollInterval <= TimeSpan.Zero || this.pollInterval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        reader = new();
        this.ownsHttp = ownsHttp;
    }

    public ValueTask<IPullExportSession> PrepareAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!gameId.Equals("ae", StringComparison.Ordinal))
            throw new PullExportException(PullExportErrorCodes.UnsupportedGame);

        var baseline = new Dictionary<string, BaselineObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var observation = reader.Read(source, cancellationToken);
                baseline[source.Path] = new(
                    observation.Stamp,
                    BuildEvidence(observation.Candidates));
            }
            catch (PullExportException exception) when (exception.ErrorCode == PullExportErrorCodes.HistoryNotFound)
            {
                // The first history view can create either source after launch.
            }
        }
        return ValueTask.FromResult<IPullExportSession>(new Session(this, baseline));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CryptographicOperations.ZeroMemory(fingerprintKey);
        if (ownsHttp) http.Dispose();
    }

    private async ValueTask<ExportArtifactMetadata> ExportAsync(
        IReadOnlyDictionary<string, BaselineObservation> baseline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(totalDuration);
        IReadOnlyList<EndfieldPullHistoryCandidate> candidates;
        try
        {
            candidates = await WaitForFreshCandidatesAsync(baseline, budget.Token, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new PullExportException(PullExportErrorCodes.HistoryNotUpdated);
        }

        var api = new EndfieldPullApiClient(http, pacer, limits, timeProvider);
        var archive = await api.DownloadNewestValidAsync(candidates, budget.Token).ConfigureAwait(false);
        var writer = new EndfieldPullExportWriter(exportRootDirectory, timeProvider);
        var output = await writer.WriteAsync(archive, null, budget.Token).ConfigureAwait(false);
        return new(
            "pulls",
            archive.Records.Count,
            output.ByteCount,
            "pengo-pulls v1 JSON",
            timeProvider.GetUtcNow(),
            output.Path);
    }

    private async ValueTask<IReadOnlyList<EndfieldPullHistoryCandidate>> WaitForFreshCandidatesAsync(
        IReadOnlyDictionary<string, BaselineObservation> baseline,
        CancellationToken totalToken,
        CancellationToken callerToken)
    {
        while (true)
        {
            callerToken.ThrowIfCancellationRequested();
            var fresh = new List<(long LastWrite, EndfieldPullHistoryCandidate Candidate)>();
            foreach (var source in sources)
            {
                try
                {
                    var observation = reader.Read(source, totalToken);
                    foreach (var candidate in SelectFreshCandidates(
                        baseline.GetValueOrDefault(source.Path),
                        observation))
                        fresh.Add((observation.Stamp.LastWriteTimeUtcTicks, candidate));
                }
                catch (PullExportException exception) when (
                    exception.ErrorCode is PullExportErrorCodes.HistoryNotFound
                        or PullExportErrorCodes.InvalidHistoryLink
                        or PullExportErrorCodes.HistoryNotUpdated)
                {
                    // The other approved source can still provide the fresh history view.
                }
            }
            if (fresh.Count != 0)
                return fresh
                    .OrderByDescending(static item => item.LastWrite)
                    .ThenByDescending(static item => item.Candidate.StartOffset)
                    .Select(static item => item.Candidate)
                    .ToArray();
            await Task.Delay(pollInterval, totalToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<EndfieldPullHistoryCandidate> SelectFreshCandidates(
        BaselineObservation? baseline,
        EndfieldPullHistoryObservation current)
    {
        if (current.Candidates.Count == 0) return [];
        if (baseline is null) return current.Candidates;
        var sameFile = baseline.Stamp.SameFileAs(current.Stamp);
        if (sameFile
            && baseline.Stamp.Length == current.Stamp.Length
            && baseline.Stamp.LastWriteTimeUtcTicks == current.Stamp.LastWriteTimeUtcTicks)
            return [];

        var replaced = !sameFile || current.Stamp.Length < baseline.Stamp.Length;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<EndfieldPullHistoryCandidate>();
        foreach (var candidate in current.Candidates)
        {
            var fingerprint = Fingerprint(candidate.Credential);
            counts.TryGetValue(fingerprint, out var count);
            counts[fingerprint] = ++count;
            if (replaced
                || candidate.StartOffset >= baseline.Stamp.Length
                || !baseline.Occurrences.TryGetValue(fingerprint, out var oldCount)
                || count > oldCount)
                result.Add(candidate);
        }
        return result;
    }

    private IReadOnlyDictionary<string, int> BuildEvidence(
        IReadOnlyList<EndfieldPullHistoryCandidate> candidates)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var fingerprint = Fingerprint(candidate.Credential);
            result.TryGetValue(fingerprint, out var count);
            result[fingerprint] = count + 1;
        }
        return result;
    }

    private string Fingerprint(EndfieldPullCredential credential)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, fingerprintKey);
        Append(hash, credential.Token);
        Append(hash, credential.ServerId);
        Append(hash, credential.Language);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static IReadOnlyList<EndfieldPullSource> DefaultSources()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("The Windows profile folders are unavailable.");
        return
        [
            new(Path.Combine(local, "PlatformProcess", "Cache", "data_1"), local),
            new(Path.Combine(profile, "AppData", "LocalLow", "Gryphline", "Endfield", "sdklogs", "HGWebview.log"), profile),
        ];
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            UseProxy = false,
        };
        return new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed record BaselineObservation(
        EndfieldPullFileStamp Stamp,
        IReadOnlyDictionary<string, int> Occurrences);

    private sealed class Session(
        EndfieldPullExportProvider owner,
        IReadOnlyDictionary<string, BaselineObservation> baseline) : IPullExportSession
    {
        private readonly object sync = new();
        private IReadOnlyDictionary<string, BaselineObservation> baseline = baseline;
        private bool used;
        private bool disposed;

        public ValueTask<ExportArtifactMetadata> ExportAsync(CancellationToken cancellationToken)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (used) throw new InvalidOperationException("This pull export session has already been used.");
                used = true;
                return owner.ExportAsync(baseline, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (sync)
            {
                if (disposed) return ValueTask.CompletedTask;
                disposed = true;
                baseline = new Dictionary<string, BaselineObservation>();
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class EndfieldPullExportWriter(
    string exportRootDirectory,
    TimeProvider timeProvider)
{
    public async ValueTask<AtomicExportResult> WriteAsync(
        EndfieldPullArchive archive,
        string? requestedPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exportedAt = timeProvider.GetUtcNow().ToUniversalTime();
        var bytes = EndfieldPullContract.Serialize(archive, exportedAt, cancellationToken);
        var temporaryPath = string.Empty;
        try
        {
            EndfieldPullContract.Validate(bytes);
            var target = ResolvePath(requestedPath, exportedAt);
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrWhiteSpace(directory)) throw OutputFailed();
            UigfPullExportWriter.EnsureSafeDestination(exportRootDirectory, directory);
            temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var file = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, target, overwrite: false);
            temporaryPath = string.Empty;
            return new(target, bytes.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (PullExportException) { throw; }
        catch (Exception) { throw OutputFailed(); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (temporaryPath.Length != 0)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception) { }
            }
        }
    }

    private string ResolvePath(string? requestedPath, DateTimeOffset exportedAt)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            try
            {
                var path = Path.GetFullPath(requestedPath);
                if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) throw OutputFailed();
                return path;
            }
            catch (PullExportException) { throw; }
            catch (Exception) { throw OutputFailed(); }
        }
        var stamp = exportedAt.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(
            exportRootDirectory,
            "Pengo Exports",
            "Arknights Endfield",
            $"{stamp}-{Guid.NewGuid():N}.pengo-pulls.json");
    }

    private static PullExportException OutputFailed() =>
        new(PullExportErrorCodes.OutputFailed);
}

internal static class EndfieldPullContract
{
    public const int MaximumBytes = AchievementImportBridge.MaximumArtifactBytes;
    public const int MaximumRecords = 10_000;
    private static readonly HashSet<string> CharacterPoolTypes = new(StringComparer.Ordinal)
    {
        EndfieldPullApiClient.BasicPool,
        EndfieldPullApiClient.BeginnerPool,
        EndfieldPullApiClient.CharteredPool,
        EndfieldPullApiClient.FestJointPool,
    };

    public static byte[] Serialize(
        EndfieldPullArchive archive,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default)
    {
        if (archive.Records.Count > MaximumRecords)
            throw new PullExportException(PullExportErrorCodes.SafetyLimit);
        using var output = new MemoryStream();
        try
        {
            using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("kind", "pengo-pulls");
                json.WriteNumber("version", 1);
                json.WriteString("game", "ae");
                json.WriteString("exportedAt", exportedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                json.WritePropertyName("account");
                json.WriteStartObject();
                json.WriteString("uid", archive.Account.Uid);
                json.WriteString("roleId", archive.Account.RoleId);
                json.WriteString("serverId", archive.Account.ServerId);
                json.WriteString("serverName", archive.Account.ServerName);
                json.WriteEndObject();
                json.WritePropertyName("records");
                json.WriteStartArray();
                foreach (var record in archive.Records
                    .OrderByDescending(static record => record.ObtainedAt)
                    .ThenByDescending(static record => ulong.Parse(record.SeqId, CultureInfo.InvariantCulture))
                    .ThenBy(static record => record.Id, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    json.WriteStartObject();
                    json.WriteString("id", record.Id);
                    json.WriteString("recordType", record.RecordType);
                    json.WriteString("seqId", record.SeqId);
                    json.WriteString("poolId", record.PoolId);
                    json.WriteString("poolName", record.PoolName);
                    json.WriteString("poolType", record.PoolType);
                    json.WriteString("itemId", record.ItemId);
                    json.WriteString("name", record.Name);
                    json.WriteString("itemType", record.ItemType);
                    json.WriteNumber("rarity", record.Rarity);
                    json.WriteString("obtainedAt", record.ObtainedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    json.WriteBoolean("isNew", record.IsNew);
                    json.WriteBoolean("isFree", record.IsFree);
                    if (record.RecordType == "weapon") json.WriteString("batchId", record.BatchId);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
                json.Flush();
            }
            if (output.Length > MaximumBytes)
                throw new PullExportException(PullExportErrorCodes.SafetyLimit);
            return output.ToArray();
        }
        finally
        {
            if (output.TryGetBuffer(out var buffer) && buffer.Array is not null)
                CryptographicOperations.ZeroMemory(buffer.Array);
        }
    }

    public static void Validate(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasExactProperties(root, ["kind", "version", "game", "exportedAt", "account", "records"])
                || RequiredString(root, "kind", 32) != "pengo-pulls"
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionValue)
                || versionValue != 1
                || RequiredString(root, "game", 8) != "ae"
                || !TryUtc(root, "exportedAt", out _)
                || !root.TryGetProperty("account", out var account)
                || account.ValueKind != JsonValueKind.Object
                || !HasExactProperties(account, ["uid", "roleId", "serverId", "serverName"])
                || !IsIdentifier(RequiredString(account, "uid", 128))
                || !IsIdentifier(RequiredString(account, "roleId", 128))
                || !IsIdentifier(RequiredString(account, "serverId", 128))
                || !IsText(RequiredString(account, "serverName", 256))
                || !root.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array
                || records.GetArrayLength() > MaximumRecords)
                throw Invalid();

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in records.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object) throw Invalid();
                var type = RequiredString(value, "recordType", 16);
                var weapon = type == "weapon";
                if (!weapon && type != "character") throw Invalid();
                var expected = weapon
                    ? new[] { "id", "recordType", "seqId", "poolId", "poolName", "poolType", "itemId", "name", "itemType", "rarity", "obtainedAt", "isNew", "isFree", "batchId" }
                    : ["id", "recordType", "seqId", "poolId", "poolName", "poolType", "itemId", "name", "itemType", "rarity", "obtainedAt", "isNew", "isFree"];
                if (!HasExactProperties(value, expected)) throw Invalid();
                var seqId = RequiredString(value, "seqId", 128);
                var poolId = RequiredString(value, "poolId", 128);
                var id = RequiredString(value, "id", 512);
                var poolType = RequiredString(value, "poolType", 32);
                if (!IsIdentifier(seqId)
                    || !ulong.TryParse(seqId, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    || !IsIdentifier(poolId)
                    || !id.Equals($"{type}:{poolId}:{seqId}", StringComparison.Ordinal)
                    || !ids.Add(id)
                    || !IsText(RequiredString(value, "poolName", 256))
                    || !IsIdentifier(RequiredString(value, "itemId", 128))
                    || !IsText(RequiredString(value, "name", 256))
                    || !IsText(RequiredString(value, "itemType", 128))
                    || !value.TryGetProperty("rarity", out var rarity)
                    || rarity.ValueKind != JsonValueKind.Number
                    || !rarity.TryGetInt32(out var rarityValue)
                    || rarityValue is < 1 or > 6
                    || !TryUtc(value, "obtainedAt", out _)
                    || !IsBoolean(value, "isNew")
                    || !IsBoolean(value, "isFree"))
                    throw Invalid();
                if (weapon)
                {
                    if (poolType != EndfieldPullApiClient.ArsenalPool
                        || RequiredString(value, "batchId", 128) != poolId
                        || value.GetProperty("isFree").GetBoolean())
                        throw Invalid();
                }
                else if (!CharacterPoolTypes.Contains(poolType)
                    || RequiredString(value, "itemType", 128) != "character")
                    throw Invalid();
            }
        }
        catch (PullExportException) { throw; }
        catch (JsonException) { throw Invalid(); }
    }

    private static bool HasExactProperties(JsonElement element, IReadOnlyCollection<string> names)
    {
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name) || !seen.Add(property.Name)) return false;
        return seen.SetEquals(expected);
    }

    private static string RequiredString(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || text.Length == 0
            || text.Length > maximumLength)
            throw Invalid();
        return text;
    }

    private static bool IsIdentifier(string value) =>
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':');

    private static bool IsText(string value) =>
        value.All(static character => !char.IsControl(character));

    private static bool IsBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool TryUtc(JsonElement element, string name, out DateTimeOffset result)
    {
        result = default;
        return element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result)
            && result.Offset == TimeSpan.Zero;
    }

    private static PullExportException Invalid() =>
        new(PullExportErrorCodes.UpstreamInvalid);
}
