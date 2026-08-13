using System.ComponentModel;
using System.Reflection;
using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Core.PublisherGames;
using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class WuWaMaintenanceLocatorTests
{
    [Fact]
    public void Exact_public_uninstall_values_produce_at_most_two_normalized_hints()
    {
        using var first = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var second = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var registry = Registry(
            ("DisplayName", "Wuthering Waves"),
            ("InstallPath", first.Root + Path.DirectorySeparatorChar),
            ("LauncherPath", second.PathOf("launcher.exe")));
        var source = new WuWaMaintenanceCandidateSource(registry, first.CreateWuWaAdapter());

        var roots = source.ReadCandidateRoots();

        Assert.Equal([first.Root, second.Root], roots);
        Assert.Equal(1, registry.ReadCount);
    }

    [Fact]
    public void Duplicate_install_and_launcher_hints_collapse_to_one_root()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var source = Source(
            fixture,
            ("DisplayName", "Wuthering Waves"),
            ("InstallPath", fixture.Root),
            ("LauncherPath", fixture.PathOf("launcher.exe")));

        Assert.Equal([fixture.Root], source.ReadCandidateRoots());
    }

    [Fact]
    public void Candidate_source_reads_only_the_three_exact_public_value_names()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var values = new TrackingRegistryValues(new Dictionary<string, object?>
        {
            ["DisplayName"] = "Wuthering Waves",
            ["InstallPath"] = fixture.Root,
            ["LauncherPath"] = fixture.PathOf("launcher.exe"),
            ["UnrelatedPrivateValue"] = "must-not-be-read",
        });
        var source = new WuWaMaintenanceCandidateSource(
            new FakeRegistryReader(values),
            fixture.CreateWuWaAdapter());

        source.ReadCandidateRoots();

        Assert.Equal(["DisplayName", "InstallPath", "LauncherPath"], values.ReadNames);
    }

    [Theory]
    [MemberData(nameof(RejectedRecords))]
    public void Missing_malformed_oversized_or_mismatched_records_produce_no_hints(
        IReadOnlyDictionary<string, object?> values)
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var source = new WuWaMaintenanceCandidateSource(
            new FakeRegistryReader(values),
            fixture.CreateWuWaAdapter());

        Assert.Empty(source.ReadCandidateRoots());
    }

    public static TheoryData<IReadOnlyDictionary<string, object?>> RejectedRecords => new()
    {
        new Dictionary<string, object?>(),
        new Dictionary<string, object?> { ["DisplayName"] = 42, ["InstallPath"] = @"C:\Games\WuWa" },
        new Dictionary<string, object?> { ["DisplayName"] = "Other Game", ["InstallPath"] = @"C:\Games\WuWa" },
        new Dictionary<string, object?> { ["DisplayName"] = new string('x', 129), ["InstallPath"] = @"C:\Games\WuWa" },
        new Dictionary<string, object?> { ["DisplayName"] = "Wuthering Waves", ["InstallPath"] = new string('x', 2049) },
        new Dictionary<string, object?> { ["DisplayName"] = "Wuthering Waves", ["InstallPath"] = 7 },
        new Dictionary<string, object?> { ["DisplayName"] = "Wuthering Waves", ["InstallPath"] = @"\\server\WuWa" },
        new Dictionary<string, object?> { ["DisplayName"] = "Wuthering Waves", ["LauncherPath"] = @"C:\Games\other.exe" },
    };

    [Fact]
    public void Two_fully_valid_registry_hints_are_ambiguous_and_mint_no_request()
    {
        using var first = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var second = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        first.Metadata.Import(second.Metadata);
        var source = Source(
            first,
            ("DisplayName", "Wuthering Waves"),
            ("InstallPath", first.Root),
            ("LauncherPath", second.PathOf("launcher.exe")));

        var result = source.Inspect();

        Assert.Equal(PublisherGameInspectionReason.AmbiguousCandidates, result.Reason);
        Assert.Null(result.MaintenanceTarget);
    }

    [Fact]
    public void Ready_full_proof_is_revalidated_read_only_and_exposes_a_sealed_request()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var starter = new FakeStarter();
        var service = Service(fixture, starter, RunningProcessStatus.NotRunning);
        var before = fixture.Snapshot();

        var result = service.Check();

        Assert.Equal(WuWaOfficialMaintenanceStatus.Ready, result.Status);
        Assert.NotNull(result.Request);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Version_conflict_with_full_proof_is_maintenance_ready_but_never_direct_launch()
    {
        using var fixture = FakePublisherInstall.CreateWuWa();
        var service = Service(fixture, new FakeStarter(), RunningProcessStatus.NotRunning);

        var result = service.Check();

        Assert.Equal(WuWaOfficialMaintenanceStatus.Ready, result.Status);
        Assert.Equal(PublisherGameInspectionReason.VersionConflict, result.InspectionReason);
        Assert.False(result.Request!.AllowsDirectGameLaunch);
        Assert.False(result.Request.AllowsDirectUpdate);
    }

    [Theory]
    [InlineData(RunningProcessStatus.Running, WuWaOfficialMaintenanceStatus.Running)]
    [InlineData(RunningProcessStatus.Uncertain, WuWaOfficialMaintenanceStatus.NeedsReview)]
    public void Exact_running_or_uncertain_launcher_is_reported_without_start(
        RunningProcessStatus running,
        WuWaOfficialMaintenanceStatus expected)
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var starter = new FakeStarter();

        var result = Service(fixture, starter, running).Check();

        Assert.Equal(expected, result.Status);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Missing_registry_record_is_not_found_without_adapter_or_start()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var registry = new FakeRegistryReader(new Dictionary<string, object?>());
        var starter = new FakeStarter();
        var service = CreateService(fixture, registry, starter);

        var result = service.Check();

        Assert.Equal(WuWaOfficialMaintenanceStatus.NotFound, result.Status);
        Assert.Empty(fixture.Metadata.ReadPaths);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public void Partial_fresh_install_needs_review_and_cannot_open()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        fixture.Delete(@"Wuthering Waves Game\Wuthering Waves.exe");
        var starter = new FakeStarter();

        var result = Service(fixture, starter, RunningProcessStatus.NotRunning).Check();

        Assert.Equal(WuWaOfficialMaintenanceStatus.NeedsReview, result.Status);
        Assert.Null(result.Request);
        Assert.Empty(starter.Requests);
    }

    [Fact]
    public async Task Only_explicit_open_dispatches_and_bounded_failure_is_returned()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var starter = new FakeStarter(new Win32Exception(740));
        var service = Service(fixture, starter, RunningProcessStatus.NotRunning);
        var check = service.Check();
        Assert.Empty(starter.Requests);

        var opened = await service.OpenOrObserveCurrentAsync(check.Request!);

        Assert.Equal(WuWaOfficialMaintenanceStatus.Failed, opened.Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Repeated_service_open_waits_then_observes_without_a_second_dispatch()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var starter = new FakeStarter(entered: entered, release: release);
        var service = Service(fixture, starter, RunningProcessStatus.NotRunning);
        var request = service.Check().Request!;

        var first = service.OpenOrObserveCurrentAsync(request);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var repeated = service.OpenOrObserveCurrentAsync(request);
        Assert.False(repeated.IsCompleted);
        release.Set();

        Assert.Equal(
            WuWaOfficialMaintenanceStatus.Opened,
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(
            WuWaOfficialMaintenanceStatus.Ready,
            (await repeated.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Canceled_service_observer_never_dispatches_and_releases_waiting_admission()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var starter = new FakeStarter(entered: entered, release: release);
        var service = Service(fixture, starter, RunningProcessStatus.NotRunning);
        var request = service.Check().Request!;
        var first = service.OpenOrObserveCurrentAsync(request);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var observer = service.OpenOrObserveCurrentAsync(request, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await observer.WaitAsync(TimeSpan.FromSeconds(5)));
        release.Set();

        Assert.Equal(
            WuWaOfficialMaintenanceStatus.Opened,
            (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Single(starter.Requests);
        Assert.Equal(WuWaOfficialMaintenanceStatus.Ready, service.Check().Status);
    }

    [Fact]
    public async Task Opened_is_only_start_admission_until_a_fresh_exact_process_recheck()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var process = new FakeProcessInspector(
            RunningProcessStatus.NotRunning,
            RunningProcessStatus.NotRunning,
            RunningProcessStatus.Running);
        var starter = new FakeStarter();
        var service = CreateService(
            fixture,
            Registry(
                ("DisplayName", "Wuthering Waves"),
                ("InstallPath", fixture.Root)),
            starter,
            process);
        var request = service.Check().Request!;

        var opened = await service.OpenOrObserveCurrentAsync(request);
        var observed = service.Check(opened.Request!);

        Assert.Equal(WuWaOfficialMaintenanceStatus.Opened, opened.Status);
        Assert.Equal(WuWaOfficialMaintenanceStatus.Running, observed.Status);
        Assert.Single(starter.Requests);
    }

    [Fact]
    public async Task Immediate_launcher_exit_rechecks_to_ready_and_can_be_opened_again()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var process = new FakeProcessInspector(
            RunningProcessStatus.NotRunning,
            RunningProcessStatus.NotRunning,
            RunningProcessStatus.NotRunning,
            RunningProcessStatus.NotRunning);
        var starter = new FakeStarter();
        var service = CreateService(
            fixture,
            Registry(
                ("DisplayName", "Wuthering Waves"),
                ("InstallPath", fixture.Root)),
            starter,
            process);
        var request = service.Check().Request!;

        var first = await service.OpenOrObserveCurrentAsync(request);
        var ready = service.Check(first.Request!);
        var second = await service.OpenOrObserveCurrentAsync(ready.Request!);

        Assert.Equal(WuWaOfficialMaintenanceStatus.Opened, first.Status);
        Assert.Equal(WuWaOfficialMaintenanceStatus.Ready, ready.Status);
        Assert.Equal(WuWaOfficialMaintenanceStatus.Opened, second.Status);
        Assert.Equal(2, starter.Requests.Count);
    }

    [Fact]
    public async Task Later_exact_launcher_close_rechecks_from_running_to_ready()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var process = new FakeProcessInspector(
            RunningProcessStatus.NotRunning,
            RunningProcessStatus.NotRunning,
            RunningProcessStatus.Running,
            RunningProcessStatus.NotRunning);
        var service = CreateService(
            fixture,
            Registry(
                ("DisplayName", "Wuthering Waves"),
                ("InstallPath", fixture.Root)),
            new FakeStarter(),
            process);
        var request = service.Check().Request!;
        var opened = await service.OpenOrObserveCurrentAsync(request);

        var running = service.Check(opened.Request!);
        var closed = service.Check(running.Request!);

        Assert.Equal(WuWaOfficialMaintenanceStatus.Running, running.Status);
        Assert.Equal(WuWaOfficialMaintenanceStatus.Ready, closed.Status);
        Assert.NotNull(closed.Request);
    }

    [Fact]
    public void Production_reader_surface_is_exact_Registry32_and_never_enumerates()
    {
        var source = File.ReadAllText(Path.Combine(
            WorkspaceRoot,
            "Desktop",
            "src",
            "Nyx.Desktop.Infrastructure",
            "PublisherGames",
            "WuWaMaintenanceLocator.cs"));

        Assert.Contains("RegistryHive.LocalMachine, RegistryView.Registry32", source, StringComparison.Ordinal);
        Assert.Contains("KRInstall Wuthering Waves Overseas", source, StringComparison.Ordinal);
        Assert.Contains("[\"DisplayName\", \"InstallPath\", \"LauncherPath\"]", source, StringComparison.Ordinal);
        Assert.Contains("writable: false", source, StringComparison.Ordinal);
        Assert.Contains("RegistryValueOptions.DoNotExpandEnvironmentNames", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSubKeyNames", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetValueNames", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry64", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUser", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Direct_launch_locator_returns_only_one_exact_bounded_registry_root()
    {
        using var fixture = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var source = Source(
            fixture,
            ("DisplayName", "Wuthering Waves"),
            ("InstallPath", fixture.Root),
            ("LauncherPath", fixture.PathOf("launcher.exe")));

        var locator = new WuWaInstallRootLocator(source);

        Assert.Equal(fixture.Root, locator.LocateRoot());
    }

    [Fact]
    public void Direct_launch_locator_returns_no_root_for_missing_or_ambiguous_hints()
    {
        using var first = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        using var second = FakePublisherInstall.CreateWuWa(resourceVersion: "3.5.0");
        var missing = new WuWaInstallRootLocator(new WuWaMaintenanceCandidateSource(
            new FakeRegistryReader(new Dictionary<string, object?>()),
            first.CreateWuWaAdapter()));
        var ambiguous = new WuWaInstallRootLocator(new WuWaMaintenanceCandidateSource(
            Registry(
                ("DisplayName", "Wuthering Waves"),
                ("InstallPath", first.Root),
                ("LauncherPath", second.PathOf("launcher.exe"))),
            first.CreateWuWaAdapter()));

        Assert.Null(missing.LocateRoot());
        Assert.Null(ambiguous.LocateRoot());
    }

    [Fact]
    public void Direct_launch_locator_public_surface_accepts_no_path_registry_key_or_game_id()
    {
        var constructor = Assert.Single(typeof(WuWaInstallRootLocator).GetConstructors());
        Assert.Empty(constructor.GetParameters());
        var method = Assert.Single(typeof(WuWaInstallRootLocator).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Equal(nameof(WuWaInstallRootLocator.LocateRoot), method.Name);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void Public_service_surface_has_no_registry_path_or_game_id_input()
    {
        var constructors = typeof(WuWaMaintenanceService).GetConstructors();
        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
        Assert.False(typeof(IWuWaUninstallRegistryReader).IsPublic);
        Assert.False(typeof(WuWaMaintenanceCandidateSource).IsPublic);

        var methods = typeof(WuWaMaintenanceService).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.All(methods, method => Assert.All(method.GetParameters(), parameter =>
            Assert.Contains(
                parameter.ParameterType,
                new[] { typeof(OfficialMaintenanceHandoffRequest), typeof(CancellationToken) })));
    }

    private static WuWaMaintenanceCandidateSource Source(
        FakePublisherInstall fixture,
        params (string Name, object? Value)[] values) =>
        new(Registry(values), fixture.CreateWuWaAdapter());

    private static WuWaMaintenanceService Service(
        FakePublisherInstall fixture,
        FakeStarter starter,
        RunningProcessStatus running) =>
        CreateService(
            fixture,
            Registry(
                ("DisplayName", "Wuthering Waves"),
                ("InstallPath", fixture.Root),
                ("LauncherPath", fixture.PathOf("launcher.exe"))),
            starter,
            running);

    private static WuWaMaintenanceService CreateService(
        FakePublisherInstall fixture,
        FakeRegistryReader registry,
        FakeStarter starter,
        RunningProcessStatus running = RunningProcessStatus.NotRunning)
        => CreateService(fixture, registry, starter, new FakeProcessInspector(running));

    private static WuWaMaintenanceService CreateService(
        FakePublisherInstall fixture,
        FakeRegistryReader registry,
        FakeStarter starter,
        IStrictRunningProcessInspector processInspector)
    {
        var source = new WuWaMaintenanceCandidateSource(registry, fixture.CreateWuWaAdapter());
        var executor = new WuWaOfficialMaintenanceExecutor(
            fixture.CreateWuWaAdapter(),
            processInspector,
            starter,
            new WuWaOfficialLauncherAdmission());
        return new(source, executor);
    }

    private static FakeRegistryReader Registry(params (string Name, object? Value)[] values) =>
        new(values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal));

    private sealed class FakeRegistryReader(IReadOnlyDictionary<string, object?> values)
        : IWuWaUninstallRegistryReader
    {
        public int ReadCount { get; private set; }

        public IReadOnlyDictionary<string, object?> Read()
        {
            ReadCount++;
            return values;
        }
    }

    private sealed class TrackingRegistryValues(IReadOnlyDictionary<string, object?> inner)
        : IReadOnlyDictionary<string, object?>
    {
        public List<string> ReadNames { get; } = [];

        public int Count => inner.Count;

        public IEnumerable<string> Keys => throw new InvalidOperationException("Enumeration is forbidden.");

        public IEnumerable<object?> Values => throw new InvalidOperationException("Enumeration is forbidden.");

        public object? this[string key] => throw new InvalidOperationException("Indexer reads are forbidden.");

        public bool ContainsKey(string key) => throw new InvalidOperationException("ContainsKey is forbidden.");

        public bool TryGetValue(string key, out object? value)
        {
            ReadNames.Add(key);
            return inner.TryGetValue(key, out value);
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            throw new InvalidOperationException("Enumeration is forbidden.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class FakeProcessInspector(params RunningProcessStatus[] statuses)
        : IStrictRunningProcessInspector
    {
        private int index;

        public RunningProcessStatus CheckStrict(string processName, string expectedExecutablePath)
        {
            if (statuses.Length == 0)
            {
                throw new InvalidOperationException("At least one process status is required.");
            }

            var status = statuses[Math.Min(index, statuses.Length - 1)];
            index++;
            return status;
        }
    }

    private sealed class FakeStarter(
        Exception? failure = null,
        ManualResetEventSlim? entered = null,
        ManualResetEventSlim? release = null)
        : IWuWaOfficialMaintenanceProcessStarter
    {
        public List<OfficialMaintenanceHandoffRequest> Requests { get; } = [];

        public void Start(OfficialMaintenanceHandoffRequest request)
        {
            Requests.Add(request);
            entered?.Set();
            release?.Wait(TimeSpan.FromSeconds(5));
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop", "src")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the Nyx workspace root.");
    }
}
