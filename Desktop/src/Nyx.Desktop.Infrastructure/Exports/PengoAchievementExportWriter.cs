using System.Text.Json;
using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

public interface IAchievementExportPublishAuthority :
    IExportArtifactHandoffAuthority
{
    bool TryPublish(Action publish);
}

public sealed class UnconditionalAchievementExportPublishAuthority :
    IAchievementExportPublishAuthority
{
    public static UnconditionalAchievementExportPublishAuthority Instance { get; } = new();

    private UnconditionalAchievementExportPublishAuthority()
    {
    }

    public bool IsCurrent => true;

    public bool TryPublish(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        publish();
        return true;
    }
}

public sealed class PengoAchievementExportWriter(
    PengoAchievementCatalogReader catalogReader,
    TimeProvider? timeProvider = null,
    Func<string>? uniqueSuffix = null)
{
    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    private readonly PengoAchievementCatalogReader catalogReader =
        catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<string> uniqueSuffix = uniqueSuffix ?? (() => Guid.NewGuid().ToString("N")[..8]);

    public async ValueTask<ExportArtifactMetadata> WriteAsync(
        string gameId,
        string catalogVersion,
        IReadOnlyList<long> achievementIds,
        AchievementAccountBinding? accountBinding,
        string? outputRoot,
        IAchievementExportPublishAuthority publishAuthority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishAuthority);
        if (gameId is not ("gi" or "hsr"))
            throw new ExportProviderException("achievement-export-unsupported");
        if (string.IsNullOrWhiteSpace(catalogVersion) || catalogVersion.Length > 80)
            throw new ExportProviderException("achievement-catalog-invalid");
        ArgumentNullException.ThrowIfNull(achievementIds);
        if (achievementIds.Count > HoyoLabHsrAchievementResultParser.MaximumAchievementCount
            || !IsStrictlyIncreasing(achievementIds))
            throw new ExportProviderException("achievement-response-invalid");
        if (accountBinding is not null && !IsValidBinding(accountBinding))
            throw new ExportProviderException("achievement-binding-unavailable");
        if (gameId == "hsr")
        {
            var catalog = await catalogReader.ReadCurrentHsrAsync(
                catalogVersion,
                cancellationToken);
            if (achievementIds.Any(id => !catalog.AchievementIds.Contains(id)))
                throw new ExportProviderException("achievement-catalog-id-unknown");
        }

        var root = ResolveOutputRoot(outputRoot);
        var gameFolder = Path.Combine(root, gameId == "gi" ? "Genshin Impact" : "Honkai Star Rail");
        Directory.CreateDirectory(gameFolder);
        var exportedAt = timeProvider.GetUtcNow();
        var suffix = uniqueSuffix();
        if (suffix.Length is < 4 or > 32
            || suffix.Any(static character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidOperationException("The export filename suffix is invalid.");
        var filename = $"pengo-achievements-{exportedAt:yyyyMMddTHHmmssZ}-{suffix}.json";
        var target = Path.Combine(gameFolder, filename);
        var temporary = Path.Combine(gameFolder, $".{filename}.{Guid.NewGuid():N}.tmp");
        var targetPublished = false;
        var resultAdmitted = false;

        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(stream, JsonWriterOptions);
                writer.WriteStartObject();
                writer.WriteString("kind", "pengo-achievements");
                writer.WriteNumber("version", 1);
                writer.WriteString("game", gameId);
                if (accountBinding is not null)
                {
                    writer.WriteStartObject("accountBinding");
                    writer.WriteString("scheme", accountBinding.Scheme);
                    writer.WriteString("value", accountBinding.Value);
                    writer.WriteString("region", accountBinding.Region);
                    writer.WriteEndObject();
                }
                writer.WriteString("catalogVersion", catalogVersion);
                writer.WriteString("exportedAt", exportedAt);
                writer.WriteStartArray("achievements");
                foreach (var id in achievementIds)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", id);
                    writer.WriteString("status", "complete");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            var bytes = new FileInfo(temporary).Length;
            var artifact = new ExportArtifactMetadata(
                "achievements",
                achievementIds.Count,
                bytes,
                "pengo-achievements-v1",
                exportedAt,
                target,
                publishAuthority);
            if (!publishAuthority.TryPublish(() =>
                {
                    File.Move(temporary, target, overwrite: false);
                    targetPublished = true;
                }))
                throw new ExportProviderException("achievement-publish-not-authorized");
            if (!targetPublished)
                throw new InvalidOperationException(
                    "The achievement publish authority admitted no artifact.");
            resultAdmitted = true;
            return artifact;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // A failed cleanup never changes the export result.
            }
            if (targetPublished && !resultAdmitted)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                }
                catch
                {
                    // This exact writer-owned orphan is never returned or handed off.
                }
            }
        }
    }

    private static bool IsStrictlyIncreasing(IReadOnlyList<long> ids)
    {
        long previous = 0;
        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            if (id <= previous || id > HoyoLabHsrAchievementResultParser.MaximumAchievementId)
                return false;
            previous = id;
        }
        return true;
    }

    private static bool IsValidBinding(AchievementAccountBinding binding) =>
        string.Equals(
            binding.Scheme,
            AchievementAccountBinding.CurrentScheme,
            StringComparison.Ordinal)
        && binding.Value.Length is >= 16 and <= 256
        && binding.Value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        && binding.Region.Length is >= 1 and <= 48
        && binding.Region.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ResolveOutputRoot(string? outputRoot)
    {
        if (outputRoot is null)
            return Path.Combine(WindowsDocumentsDirectory.Get(), "Pengo Exports");
        if (!Path.IsPathFullyQualified(outputRoot)
            || outputRoot.StartsWith("\\\\", StringComparison.Ordinal)
            || outputRoot.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || outputRoot.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw new ArgumentException("The export folder must be an absolute local path.", nameof(outputRoot));
        return Path.GetFullPath(outputRoot);
    }
}
