using System.Net;
using System.Text;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.Exports;

public sealed class RoutedPullExportProviderTests
{
    [Theory]
    [InlineData("gi")]
    [InlineData("hsr")]
    [InlineData("zzz")]
    public async Task Hoyo_games_route_to_the_injected_provider(string gameId)
    {
        var hoyo = new RecordingPullProvider();
        var resolverCalls = 0;
        using var router = new RoutedPullExportProvider(
            hoyo,
            () =>
            {
                resolverCalls++;
                throw new InvalidOperationException("WuWa root must not be resolved for HoYo games.");
            });

        await using var session = await router.PrepareAsync(gameId, CancellationToken.None);

        Assert.Equal([gameId], hoyo.Games);
        Assert.Equal(0, resolverCalls);
        Assert.NotNull(session);
    }

    [Fact]
    public async Task Endfield_routes_to_the_injected_provider_without_resolving_wuwa()
    {
        var hoyo = new RecordingPullProvider();
        var endfield = new RecordingPullProvider();
        var resolverCalls = 0;
        using var router = new RoutedPullExportProvider(
            hoyo,
            () =>
            {
                resolverCalls++;
                return null;
            },
            endfieldProvider: endfield);

        await using var session = await router.PrepareAsync("ae", CancellationToken.None);

        Assert.Equal(["ae"], endfield.Games);
        Assert.Empty(hoyo.Games);
        Assert.Equal(0, resolverCalls);
    }

    [Fact]
    public async Task Wuwa_captures_one_resolved_root_for_the_preparation()
    {
        using var fixture = new WuwaFixture();
        var alternateRoot = fixture.CreateInstallRoot("alternate");
        var resolverCalls = 0;
        string? factoryRoot = null;
        CountingHandler? handler = null;
        var hoyo = new RecordingPullProvider();
        using var router = new RoutedPullExportProvider(
            hoyo,
            () =>
            {
                resolverCalls++;
                return resolverCalls == 1 ? fixture.Root : alternateRoot;
            },
            root =>
            {
                factoryRoot = root;
                handler = new CountingHandler();
                return fixture.CreateProvider(root, handler);
            });

        await using var session = await router.PrepareAsync("wuwa", CancellationToken.None);

        Assert.Equal(1, resolverCalls);
        Assert.Equal(fixture.Root, factoryRoot);
        Assert.NotNull(handler);
        Assert.Empty(hoyo.Games);
    }

    [Fact]
    public async Task Missing_root_returns_safe_history_not_found_without_creating_provider()
    {
        var hoyo = new RecordingPullProvider();
        var factoryCalls = 0;
        using var router = new RoutedPullExportProvider(
            hoyo,
            () => null,
            _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException("WuWa provider must not be created without a root.");
            });

        var error = await Assert.ThrowsAsync<PullExportException>(async () =>
            await router.PrepareAsync("wuwa", CancellationToken.None));

        Assert.Equal(PullExportErrorCodes.HistoryNotFound, error.ErrorCode);
        Assert.Equal(0, factoryCalls);
        Assert.DoesNotContain("WuWa provider", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown")]
    public async Task Unsupported_games_never_resolve_or_create_wuwa_provider(string gameId)
    {
        var hoyo = new RecordingPullProvider();
        var resolverCalls = 0;
        var factoryCalls = 0;
        using var router = new RoutedPullExportProvider(
            hoyo,
            () =>
            {
                resolverCalls++;
                return "C:\\secret\\wuwa";
            },
            _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException("Unsupported games must not create WuWa providers.");
            });

        var error = await Assert.ThrowsAsync<PullExportException>(async () =>
            await router.PrepareAsync(gameId, CancellationToken.None));

        Assert.Equal(PullExportErrorCodes.UnsupportedGame, error.ErrorCode);
        Assert.Equal(0, resolverCalls);
        Assert.Equal(0, factoryCalls);
        Assert.Empty(hoyo.Games);
    }

    [Fact]
    public async Task WuWa_provider_is_disposed_when_prepare_fails()
    {
        using var fixture = new WuwaFixture();
        var missingRoot = fixture.Combine("missing-root");
        var handler = new CountingHandler();
        var hoyo = new RecordingPullProvider();
        using var router = new RoutedPullExportProvider(
            hoyo,
            () => missingRoot,
            root => fixture.CreateProvider(root, handler));

        var error = await Assert.ThrowsAsync<PullExportException>(async () =>
            await router.PrepareAsync("wuwa", CancellationToken.None));

        Assert.Equal(PullExportErrorCodes.HistoryNotFound, error.ErrorCode);
        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task WuWa_provider_is_disposed_once_after_session_disposal()
    {
        using var fixture = new WuwaFixture();
        var handler = new CountingHandler();
        var hoyo = new RecordingPullProvider();
        using var router = new RoutedPullExportProvider(
            hoyo,
            () => fixture.Root,
            root => fixture.CreateProvider(root, handler));

        var session = await router.PrepareAsync("wuwa", CancellationToken.None);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public void Injected_hoyo_provider_is_not_disposed_unless_router_owns_it()
    {
        var hoyo = new RecordingPullProvider();
        using (var router = new RoutedPullExportProvider(hoyo, static () => null)) { }
        Assert.Equal(0, hoyo.DisposeCount);

        using (var router = new RoutedPullExportProvider(
            hoyo,
            static () => null,
            ownsHoyo: true)) { }
        Assert.Equal(1, hoyo.DisposeCount);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_resolving_or_creating_provider()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hoyo = new RecordingPullProvider();
        var resolverCalls = 0;
        var factoryCalls = 0;
        using var router = new RoutedPullExportProvider(
            hoyo,
            () =>
            {
                resolverCalls++;
                return "C:\\wuwa";
            },
            _ =>
            {
                factoryCalls++;
                throw new InvalidOperationException();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await router.PrepareAsync("wuwa", cancellation.Token));

        Assert.Equal(0, resolverCalls);
        Assert.Equal(0, factoryCalls);
    }

    private sealed class RecordingPullProvider : IPullExportProvider, IDisposable
    {
        public List<string> Games { get; } = [];
        public int DisposeCount { get; private set; }

        public ValueTask<IPullExportSession> PrepareAsync(
            string gameId,
            CancellationToken cancellationToken)
        {
            Games.Add(gameId);
            return ValueTask.FromResult<IPullExportSession>(new RecordingPullSession());
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class RecordingPullSession : IPullExportSession
    {
        public ValueTask<ExportArtifactMetadata> ExportAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ExportArtifactMetadata(
                "pulls",
                0,
                0,
                "test",
                DateTimeOffset.UtcNow));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed class NoWaitPacer : IWuwaPullRequestPacer
    {
        public ValueTask BeforeRequestAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class WuwaFixture : IDisposable
    {
        private readonly TemporaryDirectory temporary = new();

        public WuwaFixture()
        {
            Root = CreateInstallRoot("primary");
        }

        public string Root { get; }

        public string Combine(string path) => temporary.Combine(path);

        public string CreateInstallRoot(string name)
        {
            var root = temporary.Combine(name);
            var logPath = Path.Combine(
                root,
                "Wuthering Waves Game",
                "Client",
                "Saved",
                "Logs",
                "Client.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, CanonicalHistoryUrl, Encoding.UTF8);
            return root;
        }

        public WuwaPullExportProvider CreateProvider(string root, CountingHandler handler) =>
            new(
                new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
                root,
                temporary.Combine("downloads"),
                new NoWaitPacer(),
                new PullExportSafetyLimits(
                    TotalDuration: TimeSpan.FromSeconds(1),
                    CacheObservationDuration: TimeSpan.FromMilliseconds(50),
                    CachePollInterval: TimeSpan.FromMilliseconds(5)),
                ownsHttpClient: true);

        public void Dispose() => temporary.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nyx-routed-pulls-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(string relativePath) => System.IO.Path.Combine(Path, relativePath);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception) { }
        }
    }

    private const string CanonicalHistoryUrl =
        "https://aki-gm-resources-oversea.aki-game.net/aki/gacha/index.html#/record?" +
        "player_id=100000001&record_id=record-a&svr_id=server-a&lang=en&resources_id=resources-a";
}
