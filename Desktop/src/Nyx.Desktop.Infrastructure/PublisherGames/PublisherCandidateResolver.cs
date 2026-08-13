using Nyx.Desktop.Core.PublisherGames;

namespace Nyx.Desktop.Infrastructure.PublisherGames;

internal static class PublisherCandidateResolver
{
    private const int MaximumCandidates = 16;

    public static PublisherGameInspectionResult Resolve(
        string gameId,
        IReadOnlyList<string?> candidateRoots,
        Func<string?, PublisherGameInspectionResult> inspect)
    {
        ArgumentNullException.ThrowIfNull(candidateRoots);
        ArgumentNullException.ThrowIfNull(inspect);
        if (candidateRoots.Count > MaximumCandidates)
        {
            return Ambiguous(gameId);
        }

        var results = candidateRoots.Select(inspect).ToArray();
        var validated = results.Where(result => result.HasFullInstallMaintenanceProof).ToArray();
        if (validated.Length > 1)
        {
            return Ambiguous(gameId);
        }

        if (validated.Length == 1)
        {
            return validated[0];
        }

        return results.FirstOrDefault(result => result.Status is PublisherGameInspectionStatus.NeedsReview)
            ?? results.FirstOrDefault()
            ?? new(
                gameId,
                PublisherGameInspectionStatus.NotFound,
                PublisherGameInspectionReason.PathNotProvided,
                PublisherGameVersionState.Unavailable);
    }

    private static PublisherGameInspectionResult Ambiguous(string gameId) =>
        new(
            gameId,
            PublisherGameInspectionStatus.NeedsReview,
            PublisherGameInspectionReason.AmbiguousCandidates,
            PublisherGameVersionState.Unavailable);
}
