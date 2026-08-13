using Nyx.Desktop.Core.Exports;

namespace Nyx_Desktop_App;

internal sealed class PublisherAccountAchievementExportProvider(
    PublisherAccountService accounts) : IAchievementExportProvider
{
    private readonly PublisherAccountService accounts =
        accounts ?? throw new ArgumentNullException(nameof(accounts));

    public ValueTask<IAchievementExportSession> StartAsync(
        string gameId,
        string? outputPath,
        CancellationToken cancellationToken) =>
        gameId == "hsr"
            ? accounts.StartHsrAchievementExportAsync(outputPath, cancellationToken)
            : ValueTask.FromException<IAchievementExportSession>(
                new ExportProviderException("achievement-export-unsupported"));
}
