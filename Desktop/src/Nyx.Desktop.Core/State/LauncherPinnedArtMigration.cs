namespace Nyx.Desktop.Core.State;

public enum LauncherPinnedArtMigrationStatus
{
    NotPinned,
    Protected,
    AvailableForProtection,
    Pending,
}

/// <summary>
/// Classifies a saved art pin without changing it. A pin whose old banner art
/// is temporarily unavailable stays pending until that exact variant returns;
/// the launcher must never silently release the user's choice.
/// </summary>
public static class LauncherPinnedArtMigration
{
    public static LauncherPinnedArtMigrationStatus Evaluate(
        GameAppearanceState appearance,
        bool protectedFileValid,
        IEnumerable<string> availableVariantIds)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(availableVariantIds);
        if (!appearance.ArtPinned || string.IsNullOrWhiteSpace(appearance.ArtVariant))
        {
            return LauncherPinnedArtMigrationStatus.NotPinned;
        }
        if (protectedFileValid)
        {
            return LauncherPinnedArtMigrationStatus.Protected;
        }
        return availableVariantIds.Contains(appearance.ArtVariant, StringComparer.Ordinal)
            ? LauncherPinnedArtMigrationStatus.AvailableForProtection
            : LauncherPinnedArtMigrationStatus.Pending;
    }
}
