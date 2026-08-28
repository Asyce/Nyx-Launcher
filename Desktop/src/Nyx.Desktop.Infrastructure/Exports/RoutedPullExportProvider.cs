using Nyx.Desktop.Core.Exports;

namespace Nyx.Desktop.Infrastructure.Exports;

/// <summary>
/// Routes pull preparation to the HoYo provider for HoYo games and creates a
/// short-lived WuWa provider from a caller-supplied, already validated install
/// root. The router never discovers an install root itself.
/// </summary>
public sealed class RoutedPullExportProvider : IPullExportProvider, IDisposable
{
    private readonly IPullExportProvider hoyoProvider;
    private readonly IPullExportProvider endfieldProvider;
    private readonly Func<string?> wuwaInstallRootResolver;
    private readonly Func<string, WuwaPullExportProvider> wuwaProviderFactory;
    private readonly bool ownsHoyoProvider;
    private readonly bool ownsEndfieldProvider;
    private int disposed;

    /// <summary>
    /// Creates the production router. The HoYo provider is owned by this
    /// router; the resolver must return an already validated WuWa root.
    /// </summary>
    public RoutedPullExportProvider(Func<string?> validatedWuwaRootResolver)
    {
        this.wuwaInstallRootResolver = validatedWuwaRootResolver
            ?? throw new ArgumentNullException(nameof(validatedWuwaRootResolver));
        this.hoyoProvider = new HoyoPullExportProvider();
        this.endfieldProvider = new EndfieldPullExportProvider();
        this.wuwaProviderFactory = static root => new WuwaPullExportProvider(root);
        ownsHoyoProvider = true;
        ownsEndfieldProvider = true;
    }

    /// <summary>
    /// Test and composition constructor. By default, an injected HoYo provider
    /// remains owned by its caller.
    /// </summary>
    internal RoutedPullExportProvider(
        IPullExportProvider hoyoProvider,
        Func<string?> validatedWuwaRootResolver,
        Func<string, WuwaPullExportProvider>? wuwaProviderFactory = null,
        bool ownsHoyo = false,
        IPullExportProvider? endfieldProvider = null,
        bool ownsEndfield = false)
    {
        this.hoyoProvider = hoyoProvider ?? throw new ArgumentNullException(nameof(hoyoProvider));
        this.wuwaInstallRootResolver = validatedWuwaRootResolver
            ?? throw new ArgumentNullException(nameof(validatedWuwaRootResolver));
        this.wuwaProviderFactory = wuwaProviderFactory
            ?? (static root => new WuwaPullExportProvider(root));
        this.ownsHoyoProvider = ownsHoyo;
        this.endfieldProvider = endfieldProvider ?? new UnsupportedPullExportProvider();
        this.ownsEndfieldProvider = ownsEndfield;
    }

    public ValueTask<IPullExportSession> PrepareAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        return gameId switch
        {
            "gi" or "hsr" or "zzz" => hoyoProvider.PrepareAsync(gameId, cancellationToken),
            "wuwa" => PrepareWuwaAsync(cancellationToken),
            "ae" => endfieldProvider.PrepareAsync(gameId, cancellationToken),
            _ => ValueTask.FromException<IPullExportSession>(
                new PullExportException(PullExportErrorCodes.UnsupportedGame)),
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (ownsHoyoProvider && hoyoProvider is IDisposable disposable)
            disposable.Dispose();
        if (ownsEndfieldProvider && endfieldProvider is IDisposable endfieldDisposable)
            endfieldDisposable.Dispose();
    }

    private sealed class UnsupportedPullExportProvider : IPullExportProvider
    {
        public ValueTask<IPullExportSession> PrepareAsync(string gameId, CancellationToken cancellationToken) =>
            ValueTask.FromException<IPullExportSession>(
                new PullExportException(PullExportErrorCodes.UnsupportedGame));
    }

    private async ValueTask<IPullExportSession> PrepareWuwaAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? installRoot;
        try
        {
            // Resolve exactly once. The returned value is captured for this
            // preparation and is never re-read or discovered by the router.
            installRoot = wuwaInstallRootResolver();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new PullExportException(PullExportErrorCodes.HistoryNotFound);

        WuwaPullExportProvider? provider = null;
        IPullExportSession? session = null;
        try
        {
            provider = wuwaProviderFactory(installRoot);
            if (provider is null)
                throw new PullExportException(PullExportErrorCodes.HistoryNotFound);

            session = await provider.PrepareAsync("wuwa", cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
                throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
            return new OwnedWuwaPullExportSession(provider, session);
        }
        catch (OperationCanceledException)
        {
            if (session is not null)
                await DisposeSessionAndProviderAsync(session, provider).ConfigureAwait(false);
            else
                provider?.Dispose();
            throw;
        }
        catch (PullExportException)
        {
            if (session is not null)
                await DisposeSessionAndProviderAsync(session, provider).ConfigureAwait(false);
            else
                provider?.Dispose();
            throw;
        }
        catch (Exception)
        {
            if (session is not null)
                await DisposeSessionAndProviderAsync(session, provider).ConfigureAwait(false);
            else
                provider?.Dispose();
            throw new PullExportException(PullExportErrorCodes.HistoryNotFound);
        }
    }

    private static async ValueTask DisposeSessionAndProviderAsync(
        IPullExportSession session,
        WuwaPullExportProvider? provider)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Preserve the original, already-sanitized preparation failure.
        }
        finally
        {
            try { provider?.Dispose(); }
            catch (Exception)
            {
                // Cleanup cannot expose provider diagnostics or replace the
                // original preparation failure.
            }
        }
    }

    private sealed class OwnedWuwaPullExportSession(
        WuwaPullExportProvider provider,
        IPullExportSession inner) : IPullExportSession
    {
        private int disposed;

        public ValueTask<ExportArtifactMetadata> ExportAsync(
            CancellationToken cancellationToken) =>
            inner.ExportAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                provider.Dispose();
            }
        }
    }
}
