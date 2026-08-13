using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Infrastructure.Exports;

public interface IVerifiedAchievementHelperRunner
{
    ValueTask<IAchievementExportSession> StartAsync(
        AchievementHelperInvocation invocation,
        VerifiedAchievementHelperLaunchBinding helperBinding,
        CancellationToken cancellationToken);
}

/// <summary>
/// Keeps every directory component and the reviewed helper file bound to the
/// exact NTFS identities that were SHA-256 verified. Only this assembly can
/// create a binding, so the process runner is not a general elevated launcher.
/// </summary>
public sealed class VerifiedAchievementHelperLaunchBinding : IDisposable
{
    private readonly PublisherAncestorDirectoryBinding ancestors;
    private readonly FileStream helper;
    private readonly IPublisherFileIdentityReader identityReader;
    private readonly PublisherNtfsFileIdentity identity;
    private bool disposed;

    private VerifiedAchievementHelperLaunchBinding(
        string helperPath,
        PublisherAncestorDirectoryBinding ancestors,
        FileStream helper,
        IPublisherFileIdentityReader identityReader,
        PublisherNtfsFileIdentity identity)
    {
        HelperPath = helperPath;
        this.ancestors = ancestors;
        this.helper = helper;
        this.identityReader = identityReader;
        this.identity = identity;
    }

    public string HelperPath { get; }

    internal static VerifiedAchievementHelperLaunchBinding OpenAndVerify(
        string helperPath,
        byte[] expectedSha256)
    {
        var path = Path.GetFullPath(helperPath);
        var root = Path.GetPathRoot(path)
            ?? throw new InvalidOperationException("The fixed achievement helper failed package verification.");
        PublisherAncestorDirectoryBinding? ancestors = null;
        SafeFileHandle? entry = null;
        FileStream? helper = null;
        try
        {
            ancestors = PublisherAncestorDirectoryBinding.Open(root, path);
            var identityReader = new WindowsPublisherFileIdentityReader();
            entry = PublisherPathIdentity.OpenNonReparseEntry(path);
            var identity = identityReader.Read(entry);
            if (identity.NumberOfLinks != 1)
                throw new IOException("Hard-linked achievement helpers are not accepted.");

            helper = new FileStream(entry, FileAccess.Read, 64 * 1024, isAsync: false);
            entry = null;
            PublisherPathIdentity.EnsurePathMatches(path, identity, identityReader);
            var actual = SHA256.HashData(helper);
            helper.Position = 0;
            PublisherPathIdentity.EnsurePathMatches(path, identity, identityReader);
            if (!CryptographicOperations.FixedTimeEquals(actual, expectedSha256))
                throw new IOException("The achievement helper hash did not match.");

            return new(path, ancestors, helper, identityReader, identity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            helper?.Dispose();
            entry?.Dispose();
            ancestors?.Dispose();
            throw new InvalidOperationException(
                "The fixed achievement helper failed package verification.",
                exception);
        }
        catch
        {
            helper?.Dispose();
            entry?.Dispose();
            ancestors?.Dispose();
            throw;
        }
    }

    internal void EnsurePathStillMatches()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        PublisherPathIdentity.EnsurePathMatches(HelperPath, identity, identityReader);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        helper.Dispose();
        ancestors.Dispose();
    }
}

/// <summary>
/// Constructs one fixed, allowlisted launcher-mode invocation. No caller-supplied
/// executable or argument string crosses this boundary.
/// </summary>
public sealed class VerifiedAchievementHelperBoundary : IAchievementHelperBoundary
{
    public const string ExpectedHelperFileName = "pengo-achievements-launcher.exe";
    private readonly string helperPath;
    private readonly byte[] expectedSha256;
    private readonly IVerifiedAchievementHelperRunner runner;

    public VerifiedAchievementHelperBoundary(
        string helperPath,
        string expectedSha256,
        IVerifiedAchievementHelperRunner runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        if (!Path.IsPathFullyQualified(helperPath)
            || helperPath.StartsWith("\\\\", StringComparison.Ordinal)
            || helperPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || helperPath.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException("The helper must be an absolute local path.", nameof(helperPath));
        this.helperPath = Path.GetFullPath(helperPath);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        if (expectedSha256.Length != 64
            || expectedSha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("The reviewed helper SHA-256 is unavailable.", nameof(expectedSha256));
        this.expectedSha256 = Convert.FromHexString(expectedSha256);
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public ValueTask<IAchievementExportSession> StartAsync(
        string gameId,
        string? outputPath,
        CancellationToken cancellationToken)
    {
        if (gameId is not ("gi" or "hsr"))
            throw new NotSupportedException("Achievement export is not available for this game.");
        if (!string.Equals(Path.GetFileName(helperPath), ExpectedHelperFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The helper path is not the fixed verified helper.");

        using var helperBinding = VerifiedAchievementHelperLaunchBinding.OpenAndVerify(
            helperPath,
            expectedSha256);

        var jobId = Guid.NewGuid().ToString("N");
        var outputRoot = ResolveOutputRoot(outputPath);
        var arguments = new List<string>
        {
            "--launcher",
            "--game", gameId,
            "--kind", "achievements",
            "--job-id", jobId,
            "--cancel", "named-event",
            "--parent-watch", "named-mutex",
            "--ipc", "named-pipe",
        };
        if (outputPath is null)
        {
            arguments.AddRange(["--output-root", "downloads"]);
        }
        else
        {
            arguments.AddRange(["--output-root", "fixed", "--fixed-root", outputRoot]);
        }
        arguments.AddRange(["--timeout-seconds", "300"]);

        var invocation = new AchievementHelperInvocation(
            helperPath,
            arguments.AsReadOnly(),
            gameId,
            jobId,
            outputRoot);
        return runner.StartAsync(invocation, helperBinding, cancellationToken);
    }

    private static string ResolveOutputRoot(string? outputPath)
    {
        if (outputPath is null)
        {
            return Path.Combine(WindowsDocumentsDirectory.Get(), "Pengo Exports");
        }

        if (!Path.IsPathFullyQualified(outputPath)
            || outputPath.StartsWith("\\\\", StringComparison.Ordinal)
            || outputPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || outputPath.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException("The export folder must be an absolute local path.", nameof(outputPath));
        return Path.GetFullPath(outputPath);
    }

}

public static class NdjsonExportStatusParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ExportStatusEvent> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var statuses = new List<ExportStatusEvent>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var status = JsonSerializer.Deserialize<ExportStatusEvent>(line, JsonOptions);
                if (status is null || status.GameId is not ("gi" or "hsr")
                    || status.Kind is not ("pulls" or "achievements" or "job"))
                    continue;
                statuses.Add(status with { ErrorCode = SanitizeCode(status.ErrorCode) });
            }
            catch (JsonException) { }
        }
        return statuses.AsReadOnly();
    }

    private static string? SanitizeCode(string? code) => code switch
    {
        "canceled" or "timed-out" or "access-denied" or "io-failed" or "provider-failed" or "launch-not-admitted" => code,
        _ => null,
    };
}
