namespace Nyx.Desktop.Core.Recovery;

public enum LauncherRecoveryAction
{
    RediscoverInstalls,
    RepairCustomPath,
    ResetSelectedAppearance,
    ClearGeneratedCache,
    RestoreLastKnownGoodSettings,
    RetryContent,
    RetryExport,
    OpenOutputFolder,
    OpenDataFolder,
}

public sealed record LauncherRecoveryResult(
    LauncherRecoveryAction Action,
    bool Succeeded,
    string? ErrorCode = null,
    string? SafeLocation = null);

public interface ILauncherRecoveryService
{
    ValueTask<LauncherRecoveryResult> RediscoverInstallsAsync(CancellationToken cancellationToken = default);
    ValueTask<LauncherRecoveryResult> RepairCustomPathAsync(string gameId, CancellationToken cancellationToken = default);
    ValueTask<LauncherRecoveryResult> ResetSelectedAppearanceAsync(string gameId, CancellationToken cancellationToken = default);
    ValueTask<LauncherRecoveryResult> ClearGeneratedCacheAsync(CancellationToken cancellationToken = default);
    ValueTask<LauncherRecoveryResult> RestoreLastKnownGoodSettingsAsync(CancellationToken cancellationToken = default);
    ValueTask<LauncherRecoveryResult> RetryContentAsync(CancellationToken cancellationToken = default);
    ValueTask<LauncherRecoveryResult> RetryExportAsync(string gameId, CancellationToken cancellationToken = default);
    ValueTask<LauncherRecoveryResult> OpenOutputFolderAsync(CancellationToken cancellationToken = default);
    ValueTask<LauncherRecoveryResult> OpenDataFolderAsync(CancellationToken cancellationToken = default);
}
