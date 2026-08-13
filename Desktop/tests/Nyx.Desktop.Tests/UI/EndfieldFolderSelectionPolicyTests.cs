using Nyx_Desktop_App;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.Sessions;
using Nyx.Desktop.Infrastructure.Sessions;

namespace Nyx.Desktop.Tests.UI;

public sealed class EndfieldFolderSelectionPolicyTests
{
    [Fact]
    public async Task Canceled_attempt_cannot_save_clear_or_refresh()
    {
        var policy = new EndfieldFolderSelectionPolicy();
        var attempt = policy.Begin();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = new Calls();

        var result = await policy.CompleteAsync(
            attempt,
            cancellation.Token,
            identityAccepted: true,
            @"C:\Games\GRYPHLINK",
            calls.Save,
            calls.Clear,
            calls.RefreshAsync);

        Assert.Equal(EndfieldFolderSelectionStatus.Stale, result.Status);
        Assert.False(result.FolderAccepted);
        Assert.Equal(0, calls.SaveCount);
        Assert.Equal(0, calls.ClearCount);
        Assert.Equal(0, calls.RefreshCount);
    }

    [Fact]
    public async Task Valid_current_attempt_saves_then_refreshes()
    {
        var policy = new EndfieldFolderSelectionPolicy();
        var calls = new Calls();

        var result = await policy.CompleteAsync(
            policy.Begin(),
            default,
            identityAccepted: true,
            @"C:\Games\GRYPHLINK",
            calls.Save,
            calls.Clear,
            calls.RefreshAsync);

        Assert.Equal(EndfieldFolderSelectionStatus.Saved, result.Status);
        Assert.True(result.FolderAccepted);
        Assert.False(result.NeedsReview);
        Assert.Equal([@"C:\Games\GRYPHLINK"], calls.SavedPaths);
        Assert.Equal(0, calls.ClearCount);
        Assert.Equal(1, calls.RefreshCount);
    }

    [Fact]
    public async Task Invalid_identity_clears_without_saving_or_refreshing()
    {
        var policy = new EndfieldFolderSelectionPolicy();
        var calls = new Calls();

        var result = await policy.CompleteAsync(
            policy.Begin(),
            default,
            identityAccepted: false,
            @"C:\Wrong",
            calls.Save,
            calls.Clear,
            calls.RefreshAsync);

        Assert.Equal(EndfieldFolderSelectionStatus.InvalidIdentity, result.Status);
        Assert.True(result.NeedsReview);
        Assert.Equal(0, calls.SaveCount);
        Assert.Equal(1, calls.ClearCount);
        Assert.Equal(0, calls.RefreshCount);
    }

    [Fact]
    public async Task Unloaded_old_attempt_cannot_overwrite_a_newer_choice()
    {
        var policy = new EndfieldFolderSelectionPolicy();
        var oldAttempt = policy.Begin();
        policy.CancelAll();
        var currentAttempt = policy.Begin();
        var calls = new Calls();

        var stale = await policy.CompleteAsync(
            oldAttempt,
            default,
            identityAccepted: true,
            @"C:\Old",
            calls.Save,
            calls.Clear,
            calls.RefreshAsync);
        var current = await policy.CompleteAsync(
            currentAttempt,
            default,
            identityAccepted: true,
            @"D:\New",
            calls.Save,
            calls.Clear,
            calls.RefreshAsync);

        Assert.Equal(EndfieldFolderSelectionStatus.Stale, stale.Status);
        Assert.Equal(EndfieldFolderSelectionStatus.Saved, current.Status);
        Assert.Equal([@"D:\New"], calls.SavedPaths);
        Assert.Equal(0, calls.ClearCount);
        Assert.Equal(1, calls.RefreshCount);
    }

    [Fact]
    public async Task Refresh_failure_does_not_relabel_a_proven_saved_folder_as_invalid()
    {
        var policy = new EndfieldFolderSelectionPolicy();
        var calls = new Calls { RefreshFailure = new IOException("refresh failed") };

        var result = await policy.CompleteAsync(
            policy.Begin(),
            default,
            identityAccepted: true,
            @"C:\Games\GRYPHLINK",
            calls.Save,
            calls.Clear,
            calls.RefreshAsync);

        Assert.Equal(EndfieldFolderSelectionStatus.SavedRefreshFailed, result.Status);
        Assert.True(result.FolderAccepted);
        Assert.False(result.NeedsReview);
        Assert.Single(calls.SavedPaths);
        Assert.Equal(0, calls.ClearCount);
        Assert.Equal(1, calls.RefreshCount);
    }

    [Fact]
    public async Task Storage_failure_clears_and_never_refreshes()
    {
        var policy = new EndfieldFolderSelectionPolicy();
        var calls = new Calls { SaveResult = false };

        var result = await policy.CompleteAsync(
            policy.Begin(),
            default,
            identityAccepted: true,
            @"C:\Games\GRYPHLINK",
            calls.Save,
            calls.Clear,
            calls.RefreshAsync);

        Assert.Equal(EndfieldFolderSelectionStatus.StorageFailed, result.Status);
        Assert.True(result.NeedsReview);
        Assert.Equal(1, calls.SaveCount);
        Assert.Equal(1, calls.ClearCount);
        Assert.Equal(0, calls.RefreshCount);
    }

    private sealed class Calls
    {
        public bool SaveResult { get; init; } = true;

        public Exception? RefreshFailure { get; init; }

        public List<string> SavedPaths { get; } = [];

        public int SaveCount { get; private set; }

        public int ClearCount { get; private set; }

        public int RefreshCount { get; private set; }

        public bool Save(string path)
        {
            SaveCount++;
            if (SaveResult)
            {
                SavedPaths.Add(path);
            }

            return SaveResult;
        }

        public void Clear() => ClearCount++;

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return RefreshFailure is null
                ? Task.CompletedTask
                : Task.FromException(RefreshFailure);
        }
    }
}

public sealed class EndfieldUiActionAdmissionTests
{
    [Fact]
    public void Folder_choice_blocks_maintenance_until_its_lease_releases()
    {
        var admission = new EndfieldUiActionAdmission();
        var folder = admission.TryEnter(EndfieldUiActionKind.ChooseFolder);

        Assert.NotNull(folder);
        Assert.Null(admission.TryEnter(EndfieldUiActionKind.OpenMaintenance));

        folder.Dispose();
        using var maintenance = admission.TryEnter(EndfieldUiActionKind.OpenMaintenance);
        Assert.NotNull(maintenance);
    }

    [Fact]
    public void Maintenance_blocks_folder_choice_until_its_lease_releases()
    {
        var admission = new EndfieldUiActionAdmission();
        var maintenance = admission.TryEnter(EndfieldUiActionKind.OpenMaintenance);

        Assert.NotNull(maintenance);
        Assert.Null(admission.TryEnter(EndfieldUiActionKind.ChooseFolder));

        maintenance.Dispose();
        using var folder = admission.TryEnter(EndfieldUiActionKind.ChooseFolder);
        Assert.NotNull(folder);
    }

    [Fact]
    public void Unload_reset_invalidates_old_lease_without_releasing_a_new_action()
    {
        var admission = new EndfieldUiActionAdmission();
        var oldFolder = admission.TryEnter(EndfieldUiActionKind.ChooseFolder);
        admission.Reset();
        var currentMaintenance = admission.TryEnter(EndfieldUiActionKind.OpenMaintenance);

        oldFolder!.Dispose();

        Assert.NotNull(currentMaintenance);
        Assert.Null(admission.TryEnter(EndfieldUiActionKind.ChooseFolder));
        currentMaintenance!.Dispose();
        using var finalFolder = admission.TryEnter(EndfieldUiActionKind.ChooseFolder);
        Assert.NotNull(finalFolder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_game_dispatch_remains_independent_while_ui_action_is_held(
        bool holdMaintenance)
    {
        var admission = new EndfieldUiActionAdmission();
        using var held = admission.TryEnter(holdMaintenance
            ? EndfieldUiActionKind.OpenMaintenance
            : EndfieldUiActionKind.ChooseFolder);
        var adapter = new PublisherGameSessionAdapter(
            "ae",
            () => @"C:\Games\GRYPHLINK",
            _ => new(PublisherGameLaunchStatus.Ready),
            _ => new(PublisherGameLaunchStatus.Running));

        var launch = await adapter.RequestValidatedLaunchAsync(default);

        Assert.NotNull(held);
        Assert.Equal(GameLaunchDispatchStatus.Accepted, launch.Status);
    }
}
