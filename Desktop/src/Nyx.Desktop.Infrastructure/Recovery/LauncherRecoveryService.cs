using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.Recovery;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Cache;
using Nyx.Desktop.Infrastructure.State;

namespace Nyx.Desktop.Infrastructure.Recovery;

/// <summary>
/// Small, fail-closed recovery boundary. UI and session coordinators supply
/// optional callbacks for discovery/content/export work; state and cache work
/// remains local and deterministic here.
/// </summary>
public sealed class LauncherRecoveryService : ILauncherRecoveryService
{
    private readonly LauncherStateStore stateStore;
    private readonly LauncherCacheService cache;
    private readonly Func<CancellationToken, ValueTask<bool>>? rediscover;
    private readonly Func<string, CancellationToken, ValueTask<bool>>? repair;
    private readonly Func<CancellationToken, ValueTask<bool>>? retryContent;
    private readonly Func<string, CancellationToken, ValueTask<bool>>? retryExport;

    public LauncherRecoveryService(
        LauncherStateStore stateStore,
        LauncherCacheService cache,
        Func<CancellationToken, ValueTask<bool>>? rediscoverInstalls = null,
        Func<string, CancellationToken, ValueTask<bool>>? repairCustomPath = null,
        Func<CancellationToken, ValueTask<bool>>? retryContent = null,
        Func<string, CancellationToken, ValueTask<bool>>? retryExport = null)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        rediscover = rediscoverInstalls;
        repair = repairCustomPath;
        this.retryContent = retryContent;
        this.retryExport = retryExport;
    }

    public async ValueTask<LauncherRecoveryResult> RediscoverInstallsAsync(CancellationToken cancellationToken = default) =>
        await RunCallbackAsync(LauncherRecoveryAction.RediscoverInstalls, rediscover, cancellationToken).ConfigureAwait(false);

    public async ValueTask<LauncherRecoveryResult> RepairCustomPathAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (!IsSafeGameId(gameId)) return Failed(LauncherRecoveryAction.RepairCustomPath, "invalid");
        return repair is null
            ? Failed(LauncherRecoveryAction.RepairCustomPath, "not-configured")
            : await RunCallbackAsync(LauncherRecoveryAction.RepairCustomPath, token => repair(gameId, token), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<LauncherRecoveryResult> ResetSelectedAppearanceAsync(string gameId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSafeGameId(gameId)) return ValueTask.FromResult(Failed(LauncherRecoveryAction.ResetSelectedAppearance, "invalid"));
        try
        {
            stateStore.Update(state =>
            {
                if (!GameCatalog.TryGet(gameId, out _)
                    && state.CustomGames.All(game => !string.Equals(game.Id, gameId, StringComparison.Ordinal)))
                {
                    throw new AppearanceGameNotFoundException();
                }

                return LauncherSettingsStateMerge.ResetAppearance(state, gameId);
            });
            return ValueTask.FromResult(Succeeded(LauncherRecoveryAction.ResetSelectedAppearance));
        }
        catch (AppearanceGameNotFoundException)
        {
            return ValueTask.FromResult(Failed(LauncherRecoveryAction.ResetSelectedAppearance, "not-found"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Failed(LauncherRecoveryAction.ResetSelectedAppearance, "io-failed"));
        }
    }

    public ValueTask<LauncherRecoveryResult> ClearGeneratedCacheAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            cache.ClearGeneratedCache();
            return ValueTask.FromResult(Succeeded(LauncherRecoveryAction.ClearGeneratedCache));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Failed(LauncherRecoveryAction.ClearGeneratedCache, "io-failed"));
        }
    }

    public ValueTask<LauncherRecoveryResult> RestoreLastKnownGoodSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = stateStore.RestoreLastKnownGood();
        return ValueTask.FromResult(result.IsUsable
            ? Succeeded(LauncherRecoveryAction.RestoreLastKnownGoodSettings)
            : Failed(LauncherRecoveryAction.RestoreLastKnownGoodSettings, result.Status is LauncherStateReadStatus.FutureVersion ? "future-version" : "invalid"));
    }

    public async ValueTask<LauncherRecoveryResult> RetryContentAsync(CancellationToken cancellationToken = default) =>
        await RunCallbackAsync(LauncherRecoveryAction.RetryContent, retryContent, cancellationToken).ConfigureAwait(false);

    public async ValueTask<LauncherRecoveryResult> RetryExportAsync(string gameId, CancellationToken cancellationToken = default)
    {
        if (!IsSafeGameId(gameId)) return Failed(LauncherRecoveryAction.RetryExport, "invalid");
        return retryExport is null
            ? Failed(LauncherRecoveryAction.RetryExport, "not-configured")
            : await RunCallbackAsync(LauncherRecoveryAction.RetryExport, token => retryExport(gameId, token), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<LauncherRecoveryResult> OpenOutputFolderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Succeeded(LauncherRecoveryAction.OpenOutputFolder, cache.ExportsDirectory));
    }

    public ValueTask<LauncherRecoveryResult> OpenDataFolderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Succeeded(LauncherRecoveryAction.OpenDataFolder, cache.DataDirectory));
    }

    private static async ValueTask<LauncherRecoveryResult> RunCallbackAsync(
        LauncherRecoveryAction action,
        Func<CancellationToken, ValueTask<bool>>? callback,
        CancellationToken cancellationToken)
    {
        if (callback is null) return Failed(action, "not-configured");
        try
        {
            return await callback(cancellationToken).ConfigureAwait(false)
                ? Succeeded(action)
                : Failed(action, "failed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed(action, "canceled");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed(action, "io-failed");
        }
        catch (Exception)
        {
            return Failed(action, "failed");
        }
    }

    private static LauncherRecoveryResult Succeeded(LauncherRecoveryAction action, string? safeLocation = null) =>
        new(action, true, null, safeLocation);

    private static LauncherRecoveryResult Failed(LauncherRecoveryAction action, string errorCode) =>
        new(action, false, errorCode);

    private static bool IsSafeGameId(string? gameId) =>
        !string.IsNullOrWhiteSpace(gameId)
        && gameId.Length <= 32
        && gameId.All(static character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private sealed class AppearanceGameNotFoundException : Exception
    {
    }
}
