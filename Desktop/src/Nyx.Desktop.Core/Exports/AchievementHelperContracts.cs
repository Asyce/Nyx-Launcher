namespace Nyx.Desktop.Core.Exports;

public sealed record AchievementHelperInvocation(
    string HelperPath,
    IReadOnlyList<string> Arguments,
    string GameId,
    string JobId,
    string OutputRoot);

public sealed record AchievementHelperResult(
    ExportArtifactMetadata Artifact,
    IReadOnlyList<ExportStatusEvent>? Status = null);

/// <summary>Only the infrastructure boundary may create the fixed helper invocation.</summary>
public interface IAchievementHelperBoundary
{
    ValueTask<IAchievementExportSession> StartAsync(
        string gameId,
        string? outputPath,
        CancellationToken cancellationToken);
}

public sealed class AchievementHelperExportProvider : IAchievementExportProvider
{
    private readonly IAchievementHelperBoundary helper;

    public AchievementHelperExportProvider(IAchievementHelperBoundary helper) =>
        this.helper = helper ?? throw new ArgumentNullException(nameof(helper));

    public ValueTask<IAchievementExportSession> StartAsync(
        string gameId,
        string? outputPath,
        CancellationToken cancellationToken) => helper.StartAsync(gameId, outputPath, cancellationToken);
}
