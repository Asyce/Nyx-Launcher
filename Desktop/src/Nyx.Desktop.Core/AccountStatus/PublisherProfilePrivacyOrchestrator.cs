namespace Nyx.Desktop.Core.AccountStatus;

[Flags]
public enum PublisherBrowsingDataKind
{
    None = 0,
    PasswordAutosave = 1,
    Cookies = 2,
}

/// <summary>
/// Runs the password-only cleanup gate before publisher navigation.
/// Delegates keep WebView2 and test doubles outside the policy layer.
/// </summary>
public sealed class PublisherPasswordNavigationGate : IAsyncDisposable
{
    private readonly SemaphoreSlim preparationGate = new(1, 1);
    private readonly object admissionSync = new();
    private Task? disposal;
    private TaskCompletionSource? operationsDrained;
    private int activeOperations;
    private bool admissionClosed;
    private bool prepared;

    public PublisherPasswordNavigationGate(bool passwordSavingEnabled = false)
    {
        prepared = passwordSavingEnabled;
    }

    public async Task NavigateAsync(
        Func<PublisherBrowsingDataKind, CancellationToken, Task> clearBrowsingData,
        Func<CancellationToken, Task> navigate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clearBrowsingData);
        ArgumentNullException.ThrowIfNull(navigate);
        EnterOperation();
        try
        {
            await EnsurePreparedAsync(clearBrowsingData, cancellationToken);
            await navigate(cancellationToken);
        }
        finally
        {
            ReleaseOperation();
        }
    }

    public async Task ClearSavedPasswordsAsync(
        Func<PublisherBrowsingDataKind, CancellationToken, Task> clearBrowsingData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clearBrowsingData);
        EnterOperation();
        try
        {
            await EnsurePreparedAsync(clearBrowsingData, cancellationToken);
        }
        finally
        {
            ReleaseOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (admissionSync)
        {
            disposal ??= DisposeCoreAsync();
            return new(disposal);
        }
    }

    private async Task EnsurePreparedAsync(
        Func<PublisherBrowsingDataKind, CancellationToken, Task> clearBrowsingData,
        CancellationToken cancellationToken)
    {
        if (prepared) return;
        await preparationGate.WaitAsync(cancellationToken);
        try
        {
            if (prepared) return;
            await clearBrowsingData(
                PublisherBrowsingDataKind.PasswordAutosave,
                cancellationToken);
            prepared = true;
        }
        finally
        {
            preparationGate.Release();
        }
    }

    private void EnterOperation()
    {
        lock (admissionSync)
        {
            ObjectDisposedException.ThrowIf(admissionClosed, this);
            activeOperations++;
        }
    }

    private void ReleaseOperation()
    {
        TaskCompletionSource? drained = null;
        lock (admissionSync)
        {
            activeOperations--;
            if (admissionClosed && activeOperations == 0)
            {
                drained = operationsDrained;
            }
        }

        drained?.TrySetResult();
    }

    private async Task DisposeCoreAsync()
    {
        Task drain;
        lock (admissionSync)
        {
            admissionClosed = true;
            drain = activeOperations == 0
                ? Task.CompletedTask
                : (operationsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await drain.ConfigureAwait(false);
        preparationGate.Dispose();
    }
}

public static class PublisherProfilePrivacyOrchestrator
{
    public static async Task DeleteFullProfileAsync(
        PublisherPasswordStoragePolicy passwordStorage,
        Func<bool, CancellationToken, Task> deleteProfile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passwordStorage);
        ArgumentNullException.ThrowIfNull(deleteProfile);
        passwordStorage.RequireFullProfileCleanup();
        try
        {
            await deleteProfile(true, cancellationToken);
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.FullProfile,
                succeeded: true);
        }
        catch
        {
            passwordStorage.CompleteCleanup(
                PublisherProfileCleanupScope.FullProfile,
                succeeded: false);
            throw;
        }
    }
}
