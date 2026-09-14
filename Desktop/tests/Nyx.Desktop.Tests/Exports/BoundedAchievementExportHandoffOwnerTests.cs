using System.Net.Sockets;
using System.Text;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.Exports;

public sealed class BoundedAchievementExportHandoffOwnerTests
{
    [Fact]
    public async Task Close_after_ready_keeps_one_validated_delivery_owner_until_one_use_handoff()
    {
        using var temp = new TemporaryDirectory();
        var provider = new NativeProvider();
        var launcher = new FakeLauncher { Deliver = true };
        var coordinator = new ExportCoordinator(new NoPullProvider(), provider);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(lifetime: TimeSpan.FromSeconds(2)),
            launcher);
        var handoff = owner.TrackAsync("gi", result.JobId);
        var duplicateRegistration = owner.TrackAsync("gi", result.JobId);
        Assert.Same(handoff, duplicateRegistration);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = owner.TrackAsync("hsr", result.JobId);
        });

        await coordinator.ShutDownForLauncherCloseAsync();
        Assert.False(handoff.IsCompleted);
        provider.Complete(Artifact(temp.Write("gi")));

        Assert.Equal(AchievementExportHandoffOutcome.Delivered, await handoff);
        await owner.WaitForActiveAsync();
        Assert.Equal(1, launcher.BrowserCalls);
        Assert.Equal(1, launcher.FallbackCalls);
        Assert.Equal("/genshin/achievements", launcher.LastBrowserUri?.AbsolutePath);
        Assert.Equal(0, provider.Canceled);
        Assert.Equal(1, provider.Disposed);
    }

    [Theory]
    [InlineData("""{"id":1,"status":"partial"}""")]
    [InlineData("""{"id":2,"status":"complete"},{"id":1,"status":"complete"}""")]
    public async Task Invalid_or_partial_output_never_opens_browser_and_keeps_folder_fallback(
        string rows)
    {
        using var temp = new TemporaryDirectory();
        var provider = new NativeProvider();
        var launcher = new FakeLauncher { Deliver = true };
        var coordinator = new ExportCoordinator(new NoPullProvider(), provider);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("hsr", false, true),
            static _ => ValueTask.FromResult(true));
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(),
            launcher);
        var handoff = owner.TrackAsync("hsr", result.JobId);

        await coordinator.ShutDownForLauncherCloseAsync();
        provider.Complete(Artifact(temp.Write("hsr", rows)));

        Assert.Equal(AchievementExportHandoffOutcome.Fallback, await handoff);
        Assert.Equal(0, launcher.BrowserCalls);
        Assert.Equal(1, launcher.FallbackCalls);
    }

    [Fact]
    public async Task Failed_job_with_no_output_never_opens_browser_or_claims_file_fallback()
    {
        var provider = new NativeProvider();
        var launcher = new FakeLauncher { Deliver = true };
        var coordinator = new ExportCoordinator(new NoPullProvider(), provider);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(),
            launcher);
        var handoff = owner.TrackAsync("gi", result.JobId);

        await coordinator.ShutDownForLauncherCloseAsync();
        provider.Fail(new ExportProviderException("capture_closed"));

        Assert.Equal(AchievementExportHandoffOutcome.NotAvailable, await handoff);
        Assert.Equal(0, launcher.BrowserCalls);
        Assert.Equal(0, launcher.FallbackCalls);
    }

    [Fact]
    public async Task Browser_refusal_returns_honest_fallback_without_retry()
    {
        using var temp = new TemporaryDirectory();
        var provider = new NativeProvider();
        var launcher = new FakeLauncher { Deliver = false };
        var coordinator = new ExportCoordinator(new NoPullProvider(), provider);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(),
            launcher);
        var handoff = owner.TrackAsync("gi", result.JobId);
        provider.Complete(Artifact(temp.Write("gi")));

        Assert.Equal(AchievementExportHandoffOutcome.Fallback, await handoff);
        Assert.Equal(1, launcher.BrowserCalls);
        Assert.Equal(1, launcher.FallbackCalls);
    }

    [Fact]
    public async Task Owner_timeout_explicitly_cancels_and_awaits_cleanup_without_opening_anything()
    {
        var provider = new NativeProvider();
        var launcher = new FakeLauncher { Deliver = true };
        var coordinator = new ExportCoordinator(new NoPullProvider(), provider);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("hsr", false, true),
            static _ => ValueTask.FromResult(true));
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(),
            launcher,
            maximumLifetime: TimeSpan.FromMilliseconds(25));

        var outcome = await owner.TrackAsync("hsr", result.JobId);

        Assert.Equal(AchievementExportHandoffOutcome.NotAvailable, outcome);
        Assert.Equal(1, provider.Canceled);
        Assert.Equal(1, provider.Disposed);
        Assert.Equal(0, launcher.BrowserCalls);
        Assert.Equal(0, launcher.FallbackCalls);
    }

    [Fact]
    public async Task Non_native_provider_cannot_register_background_handoff_work()
    {
        var coordinator = new ExportCoordinator(
            new NoPullProvider(),
            new NonNativeProvider());
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("hsr", false, true),
            static _ => ValueTask.FromResult(true));
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(),
            new FakeLauncher());

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = owner.TrackAsync("hsr", result.JobId);
        });
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Completed_registration_is_pruned_and_can_be_registered_again()
    {
        using var temp = new TemporaryDirectory();
        var provider = new NativeProvider();
        var launcher = new FakeLauncher { Deliver = true };
        var coordinator = new ExportCoordinator(new NoPullProvider(), provider);
        var result = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(),
            launcher);
        provider.Complete(Artifact(temp.Write("gi")));

        var first = owner.TrackAsync("gi", result.JobId);
        Assert.Equal(AchievementExportHandoffOutcome.Delivered, await first);
        var second = owner.TrackAsync("gi", result.JobId);

        Assert.NotSame(first, second);
        Assert.Equal(AchievementExportHandoffOutcome.Delivered, await second);
        await owner.DisposeAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Multiple_same_game_handoffs_remain_active_by_job_id()
    {
        using var temp = new TemporaryDirectory();
        var provider = new NativeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher { Deliver = true, BrowserGate = gate.Task };
        var coordinator = new ExportCoordinator(new NoPullProvider(), provider);
        var firstJob = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(),
            launcher);
        provider.Complete(Artifact(temp.Write("gi")));
        var first = owner.TrackAsync("gi", firstJob.JobId);
        await EventuallyAsync(() => launcher.BrowserCalls == 1);

        var secondJob = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("gi", false, true),
            static _ => ValueTask.FromResult(true));
        var second = owner.TrackAsync("gi", secondJob.JobId);
        await EventuallyAsync(() => launcher.BrowserCalls == 2);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        var disposal = owner.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        gate.SetResult();
        Assert.Equal(AchievementExportHandoffOutcome.Delivered, await first);
        Assert.Equal(AchievementExportHandoffOutcome.Delivered, await second);
        await disposal;
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_closes_admission_and_concurrent_repeats_are_safe()
    {
        var coordinator = new ExportCoordinator(new NoPullProvider(), new NativeProvider());
        var owner = new BoundedAchievementExportHandoffOwner(
            coordinator,
            new AchievementImportBridge(),
            new FakeLauncher());

        var disposals = Enumerable.Range(0, 12)
            .Select(_ => owner.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposals);
        await owner.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await owner.TrackAsync("gi", Guid.NewGuid()));
        await coordinator.DisposeAsync();
    }

    private static ExportArtifactMetadata Artifact(string outputPath) => new(
        "achievements",
        1,
        new FileInfo(outputPath).Length,
        "pengo-achievements-v1",
        DateTimeOffset.UtcNow,
        outputPath);

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }

    private sealed class NativeProvider : IAchievementExportProvider
    {
        private readonly TaskCompletionSource<ExportArtifactMetadata> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Canceled;
        public int Disposed;

        public ValueTask<IAchievementExportSession> StartAsync(
            string gameId,
            string? outputPath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAchievementExportSession>(
                new Session(this, cancellationToken));

        public void Complete(ExportArtifactMetadata artifact) =>
            completion.TrySetResult(artifact);

        public void Fail(Exception exception) =>
            completion.TrySetException(exception);

        private sealed class Session :
            ILauncherIndependentAchievementExportSession
        {
            private readonly NativeProvider owner;
            private readonly CancellationTokenSource cancellation;
            private int disposed;

            public Session(NativeProvider owner, CancellationToken token)
            {
                this.owner = owner;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                Completion = owner.completion.Task.WaitAsync(cancellation.Token);
            }

            public Task Ready => Task.CompletedTask;
            public Task<ExportArtifactMetadata> Completion { get; }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                cancellation.Cancel();
                try { await Completion; }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref owner.Canceled);
                }
                catch (Exception)
                {
                }
                cancellation.Dispose();
                Interlocked.Increment(ref owner.Disposed);
            }
        }
    }

    private sealed class NonNativeProvider : IAchievementExportProvider
    {
        public ValueTask<IAchievementExportSession> StartAsync(
            string gameId,
            string? outputPath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAchievementExportSession>(new Session());

        private sealed class Session : IAchievementExportSession
        {
            public Task Ready => Task.CompletedTask;
            public Task<ExportArtifactMetadata> Completion => Task.FromResult(
                new ExportArtifactMetadata(
                    "achievements",
                    1,
                    1,
                    "fixture",
                    DateTimeOffset.UtcNow));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NoPullProvider : IPullExportProvider
    {
        public ValueTask<IPullExportSession> PrepareAsync(
            string gameId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Pulls were not requested.");
    }

    private sealed class FakeLauncher : IAchievementExportHandoffLauncher
    {
        public bool Deliver;
        public int BrowserCalls;
        public int FallbackCalls;
        public Uri? LastBrowserUri;
        public Task? BrowserGate;

        public async ValueTask<bool> OpenBrowserAsync(
            Uri browserUri,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref BrowserCalls);
            LastBrowserUri = browserUri;
            if (!Deliver) return false;
            if (BrowserGate is not null)
                await BrowserGate.WaitAsync(cancellationToken);
            var capability = ParseCapability(browserUri);
            using var client = new TcpClient();
            await client.ConnectAsync(
                "127.0.0.1",
                capability.Port,
                cancellationToken);
            await using var stream = client.GetStream();
            var request =
                $"GET /v1/achievement-import/{capability.Nonce} HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{capability.Port}\r\n" +
                "Origin: https://pengo.gg\r\n\r\n";
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(request),
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            using var reader = new StreamReader(stream, leaveOpen: true);
            var response = await reader.ReadToEndAsync(cancellationToken);
            Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
            return true;
        }

        public ValueTask<bool> OpenFallbackAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FallbackCalls);
            return ValueTask.FromResult(true);
        }

        private static (int Port, string Nonce) ParseCapability(Uri browserUri)
        {
            var values = browserUri.Fragment.TrimStart('#')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(static part => part.Split('=', 2))
                .ToDictionary(
                    static part => part[0],
                    static part => part[1],
                    StringComparer.Ordinal);
            return (int.Parse(values["port"]), values["nonce"]);
        }

    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nyx-bounded-handoff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string game, string rows = """{"id":1,"status":"complete"}""")
        {
            var path = System.IO.Path.Combine(Path, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(
                path,
                $$"""{"kind":"pengo-achievements","version":1,"game":"{{game}}","catalogVersion":"{{game}}-fixture","exportedAt":"2026-07-29T12:00:00Z","achievements":[{{rows}}]}""");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
