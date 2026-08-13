using System.Collections.ObjectModel;
using Nyx.Desktop.Core.Games;

namespace Nyx.Desktop.Core.State;

/// <summary>
/// The values a Settings dialog owns. They are merged into the latest state so
/// an older open dialog cannot replace unrelated edits made by another process.
/// </summary>
public sealed record LauncherSettingsEdit
{
    public required string GameId { get; init; }
    public required GameAppearanceState OpenedAppearance { get; init; }
    public required GameAppearanceState Appearance { get; init; }
    public CustomGameDefinition? CustomGame { get; init; }
    public required IReadOnlyList<string> RailOrder { get; init; }
    public required string? OpenedManualInstallRoot { get; init; }
    public required string? ManualInstallRoot { get; init; }
    public required OfficialGameLaunchOptions? OpenedOfficialLaunchOptions { get; init; }
    public required OfficialGameLaunchOptions? OfficialLaunchOptions { get; init; }
    public required bool PublisherPasswordSavingEnabled { get; init; }
    public required bool AutomaticArt { get; init; }
    public required bool RemoteBannerManifest { get; init; }
    public LauncherPanelVisibility? OpenedPanelVisibility { get; init; }
    public LauncherPanelVisibility? PanelVisibility { get; init; }
}

public static class LauncherSettingsStateMerge
{
    public static LauncherState Apply(
        LauncherState latest,
        LauncherState opened,
        LauncherSettingsEdit edit)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(edit);

        var appearances = latest.Appearance.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        if (edit.Appearance != edit.OpenedAppearance)
        {
            var currentAppearance = appearances.TryGetValue(edit.GameId, out var savedAppearance)
                ? savedAppearance
                : new GameAppearanceState();
            appearances[edit.GameId] = MergeAppearance(
                currentAppearance,
                edit.OpenedAppearance,
                edit.Appearance);
        }

        var customs = MergeCustomGame(latest.CustomGames, opened.CustomGames, edit.CustomGame);
        var rail = MergeRailOrder(
            opened.RailOrder,
            edit.RailOrder,
            latest.RailOrder,
            edit.CustomGame?.Id);
        var manualInstallRoots = latest.Preferences.ManualInstallRoots.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        var manualRootChanged = !string.Equals(
            edit.OpenedManualInstallRoot,
            edit.ManualInstallRoot,
            StringComparison.Ordinal);
        if (manualRootChanged)
        {
            var latestRoot = manualInstallRoots.TryGetValue(edit.GameId, out var savedRoot)
                ? savedRoot
                : null;
            var mergedRoot = MergeValue(
                latestRoot,
                edit.OpenedManualInstallRoot,
                edit.ManualInstallRoot);
            if (mergedRoot is null)
            {
                manualInstallRoots.Remove(edit.GameId);
            }
            else
            {
                manualInstallRoots[edit.GameId] = mergedRoot;
            }
        }

        var officialLaunchOptions = latest.OfficialLaunchOptions.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        if (edit.OpenedOfficialLaunchOptions is { } openedLaunchOptions
            && edit.OfficialLaunchOptions is { } editedLaunchOptions
            && !Equals(openedLaunchOptions, editedLaunchOptions))
        {
            var latestLaunchOptions = officialLaunchOptions.TryGetValue(edit.GameId, out var savedOptions)
                ? savedOptions
                : new OfficialGameLaunchOptions();
            officialLaunchOptions[edit.GameId] = latestLaunchOptions with
            {
                RawArguments = MergeValue(
                    latestLaunchOptions.RawArguments,
                    openedLaunchOptions.RawArguments,
                    editedLaunchOptions.RawArguments),
                Enabled = MergeValue(
                    latestLaunchOptions.Enabled,
                    openedLaunchOptions.Enabled,
                    editedLaunchOptions.Enabled),
            };
        }

        var endfieldInstallRoot = latest.Preferences.EndfieldInstallRoot;
        if (edit.GameId == "ae" && manualRootChanged)
        {
            endfieldInstallRoot = MergeValue(
                latest.Preferences.EndfieldInstallRoot,
                edit.OpenedManualInstallRoot,
                edit.ManualInstallRoot);
        }

        var panelVisibility = latest.Preferences.PanelVisibility.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        if (GameCatalog.All.Any(game => string.Equals(game.Id, edit.GameId, StringComparison.Ordinal))
            && edit.OpenedPanelVisibility is { } openedVisibility
            && edit.PanelVisibility is { } editedVisibility
            && openedVisibility != editedVisibility)
        {
            var currentVisibility = latest.Preferences.VisibilityFor(edit.GameId);
            var mergedVisibility = currentVisibility with
            {
                ShowBanners = MergeValue(
                    currentVisibility.ShowBanners,
                    openedVisibility.ShowBanners,
                    editedVisibility.ShowBanners),
                ShowRedemptionCodes = MergeValue(
                    currentVisibility.ShowRedemptionCodes,
                    openedVisibility.ShowRedemptionCodes,
                    editedVisibility.ShowRedemptionCodes),
                ShowAccountAndExport = MergeValue(
                    currentVisibility.ShowAccountAndExport,
                    openedVisibility.ShowAccountAndExport,
                    editedVisibility.ShowAccountAndExport),
            };
            if (mergedVisibility == new LauncherPanelVisibility()) panelVisibility.Remove(edit.GameId);
            else panelVisibility[edit.GameId] = mergedVisibility;
        }

        return latest with
        {
            Appearance = new ReadOnlyDictionary<string, GameAppearanceState>(appearances),
            CustomGames = customs,
            RailOrder = rail,
            OfficialLaunchOptions = new ReadOnlyDictionary<string, OfficialGameLaunchOptions>(officialLaunchOptions),
            Preferences = latest.Preferences with
            {
                EndfieldInstallRoot = endfieldInstallRoot,
                ManualInstallRoots = new ReadOnlyDictionary<string, string>(manualInstallRoots),
                PanelVisibility = new ReadOnlyDictionary<string, LauncherPanelVisibility>(panelVisibility),
                PublisherPasswordSavingEnabled = MergeValue(
                    latest.Preferences.PublisherPasswordSavingEnabled,
                    opened.Preferences.PublisherPasswordSavingEnabled,
                    edit.PublisherPasswordSavingEnabled),
                FeatureFlags = latest.Preferences.FeatureFlags with
                {
                    AutomaticArt = MergeValue(
                        latest.Preferences.FeatureFlags.AutomaticArt,
                        opened.Preferences.FeatureFlags.AutomaticArt,
                        edit.AutomaticArt),
                    RemoteBannerManifest = MergeValue(
                        latest.Preferences.FeatureFlags.RemoteBannerManifest,
                        opened.Preferences.FeatureFlags.RemoteBannerManifest,
                        edit.RemoteBannerManifest),
                },
            },
        };
    }

    public static LauncherState ResetAppearance(LauncherState latest, string gameId)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var appearances = latest.Appearance.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        appearances.Remove(gameId);
        return latest with
        {
            Appearance = new ReadOnlyDictionary<string, GameAppearanceState>(appearances),
        };
    }

    private static IReadOnlyList<CustomGameDefinition> MergeCustomGame(
        IReadOnlyList<CustomGameDefinition> latest,
        IReadOnlyList<CustomGameDefinition> opened,
        CustomGameDefinition? edited)
    {
        if (edited is null)
        {
            return latest;
        }

        var openedGame = opened.FirstOrDefault(game => string.Equals(game.Id, edited.Id, StringComparison.Ordinal));
        if (openedGame == edited)
        {
            return latest;
        }

        var replaced = false;
        var merged = latest.Select(game =>
        {
            if (!string.Equals(game.Id, edited.Id, StringComparison.Ordinal))
            {
                return game;
            }

            replaced = true;
            return openedGame is null
                ? edited
                : MergeCustomGame(game, openedGame, edited);
        }).ToList();
        if (!replaced && openedGame is null)
        {
            merged.Add(edited);
        }

        var mergedGame = merged.FirstOrDefault(game => string.Equals(game.Id, edited.Id, StringComparison.Ordinal));
        if (mergedGame is not null)
        {
            LauncherCustomGameStateMerge.EnsureExecutableUnique(merged, mergedGame);
        }

        return merged;
    }

    private static CustomGameDefinition MergeCustomGame(
        CustomGameDefinition latest,
        CustomGameDefinition opened,
        CustomGameDefinition edited) => latest with
    {
        Name = MergeValue(latest.Name, opened.Name, edited.Name),
        ExecutablePath = MergeValue(latest.ExecutablePath, opened.ExecutablePath, edited.ExecutablePath),
        IconPath = MergeValue(latest.IconPath, opened.IconPath, edited.IconPath),
        BackgroundPath = MergeValue(latest.BackgroundPath, opened.BackgroundPath, edited.BackgroundPath),
        RuntimePath = MergeValue(latest.RuntimePath, opened.RuntimePath, edited.RuntimePath),
        RawArguments = MergeValue(latest.RawArguments, opened.RawArguments, edited.RawArguments),
        RequestAdministrator = MergeValue(
            latest.RequestAdministrator,
            opened.RequestAdministrator,
            edited.RequestAdministrator),
        CreationOrder = MergeValue(latest.CreationOrder, opened.CreationOrder, edited.CreationOrder),
    };

    private static GameAppearanceState MergeAppearance(
        GameAppearanceState latest,
        GameAppearanceState opened,
        GameAppearanceState edited) => latest with
    {
        IconPath = MergeValue(latest.IconPath, opened.IconPath, edited.IconPath),
        BackgroundPath = MergeValue(latest.BackgroundPath, opened.BackgroundPath, edited.BackgroundPath),
        AutomaticArt = MergeValue(latest.AutomaticArt, opened.AutomaticArt, edited.AutomaticArt),
        ArtScale = MergeValue(latest.ArtScale, opened.ArtScale, edited.ArtScale),
        ArtX = MergeValue(latest.ArtX, opened.ArtX, edited.ArtX),
        ArtY = MergeValue(latest.ArtY, opened.ArtY, edited.ArtY),
        ArtVariant = MergeValue(latest.ArtVariant, opened.ArtVariant, edited.ArtVariant),
        ArtFit = MergeValue(latest.ArtFit, opened.ArtFit, edited.ArtFit),
        ArtPinned = MergeValue(latest.ArtPinned, opened.ArtPinned, edited.ArtPinned),
        PinnedArtFile = MergeValue(latest.PinnedArtFile, opened.PinnedArtFile, edited.PinnedArtFile),
    };

    private static T MergeValue<T>(T latest, T opened, T edited) =>
        EqualityComparer<T>.Default.Equals(opened, edited) ? latest : edited;

    public static LauncherState ResetRailOrder(LauncherState latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        var officialIds = GameCatalog.All.Select(static game => game.Id);
        var customIds = latest.CustomGames
            .OrderBy(static game => game.CreationOrder)
            .ThenBy(static game => game.Id, StringComparer.Ordinal)
            .Select(static game => game.Id);
        var rail = officialIds.Concat(customIds).ToArray();
        return latest with
        {
            RailOrder = Array.AsReadOnly(rail),
            SelectedGameId = rail.Contains(latest.SelectedGameId, StringComparer.Ordinal)
                ? latest.SelectedGameId
                : rail[0],
        };
    }

    public static LauncherState ResetLauncherState(LauncherState current, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(current);
        return confirmed ? LauncherState.Defaults() : current;
    }

    private static IReadOnlyList<string> MergeRailOrder(
        IReadOnlyList<string> opened,
        IReadOnlyList<string> edited,
        IReadOnlyList<string> latest,
        string? locallyRetainedGameId)
    {
        if (opened.SequenceEqual(edited, StringComparer.Ordinal))
        {
            return latest;
        }

        var openedIds = opened.ToHashSet(StringComparer.Ordinal);
        var availableIds = latest.ToHashSet(StringComparer.Ordinal);
        if (locallyRetainedGameId is not null)
        {
            availableIds.Add(locallyRetainedGameId);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = edited
            .Where(id => availableIds.Contains(id) && seen.Add(id))
            .ToList();

        // IDs absent when the dialog opened belong to a concurrent writer.
        // Keep them in that writer's order after the locally ordered entries.
        foreach (var id in latest)
        {
            if (!openedIds.Contains(id) && seen.Add(id))
            {
                merged.Add(id);
            }
        }

        return merged;
    }
}
