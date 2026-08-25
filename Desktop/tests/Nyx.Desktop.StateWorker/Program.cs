using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.AccountStatus;
using Nyx.Desktop.Infrastructure.State;

if (args.Length == 1 && string.Equals(args[0], "probe-native-smoke", StringComparison.Ordinal))
{
    _ = typeof(NyxUserDataPaths).Assembly.FullName;
    _ = typeof(HoyoLabAccountSlotStore).Assembly.FullName;
    Console.WriteLine("NYX_STATE_WORKER=READY");
    return 0;
}

if (args.Length == 4 && string.Equals(args[0], "seed-native-smoke", StringComparison.Ordinal))
{
    return SeedNativeSmoke(args[1], args[2], args[3]);
}

if (args.Length != 7 || !string.Equals(args[0], "append", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: append <root> <id> <ready> <go> <acquired> <delay-ms>");
    return 64;
}

try
{
    var root = args[1];
    var id = args[2];
    var readyPath = args[3];
    var goPath = args[4];
    var acquiredPath = args[5];
    var delay = int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture);
    File.WriteAllText(readyPath, id);

    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (!File.Exists(goPath))
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Start barrier timed out.");
        Thread.Sleep(10);
    }

    var store = new LauncherStateStore(root, TimeSpan.FromSeconds(10));
    store.Update(state =>
    {
        File.WriteAllText(acquiredPath, id);
        if (delay > 0) Thread.Sleep(delay);
        var game = new CustomGameDefinition
        {
            Id = id,
            Name = id,
            ExecutablePath = $@"C:\Games\{id}.exe",
            IconPath = $@"C:\Games\{id}.png",
            CreationOrder = DateTime.UtcNow.Ticks,
        };
        return state with
        {
            CustomGames = state.CustomGames.Append(game).ToArray(),
            RailOrder = state.RailOrder.Append(id).ToArray(),
        };
    });
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static int SeedNativeSmoke(string root, string markerPath, string runId)
{
    if (!IsProvenIsolatedSmokeRoot(root, markerPath, runId)
        || HasReparsePointInExistingPath(markerPath)
        || HasNyxProcess())
    {
        Console.Error.WriteLine("NYX_STATE_WORKER=REJECTED CODE=SMOKE_ISOLATION_INVALID");
        return 65;
    }

    try
    {
        var publisherProfilesRoot = Path.Combine(root, "PublisherProfiles");
        var slots = new HoyoLabAccountSlotStore(publisherProfilesRoot);
        if (!slots.TryInitialize().IsReady
            || !slots.TryCreateAndSelectSlot("Native smoke", out var slot)
            || slot is null
            || !slots.TryGetProtectedStateRoot(slot, out var protectedRoot))
        {
            throw new InvalidOperationException();
        }

        var observedAt = DateTimeOffset.UtcNow;
        var bindings = new PublisherRoleBindingStore(protectedRoot);
        var snapshots = new PublisherResourceSnapshotStore(protectedRoot);
        var fixtures = new[]
        {
            (GameId: "gi", Binding: new PublisherRoleBinding("999999999999999991", "os_usa"),
                Snapshot: new PublisherResourceSnapshot("gi", "Original Resin", 137, 200, observedAt)),
            (GameId: "hsr", Binding: new PublisherRoleBinding("999999999999999992", "prod_official_usa"),
                Snapshot: new PublisherResourceSnapshot("hsr", "Trailblaze Power", 211, 300, observedAt)),
            (GameId: "zzz", Binding: new PublisherRoleBinding("999999999999999993", "prod_gf_us"),
                Snapshot: new PublisherResourceSnapshot("zzz", "Battery Charge", 177, 240, observedAt)),
        };

        foreach (var fixture in fixtures)
        {
            if (!bindings.Save(fixture.GameId, fixture.Binding)
                || !snapshots.Save(fixture.Snapshot, fixture.Binding))
            {
                throw new InvalidOperationException();
            }
        }

        var state = LauncherState.Defaults();
        new LauncherStateStore(root).Save(state with
        {
            SelectedGameId = "hsr",
            Preferences = state.Preferences with
            {
                RefreshContentOnStartup = false,
                PublisherPasswordSavingEnabled = false,
                AutomaticDailyCheckInGames = Array.Empty<string>(),
                FeatureFlags = state.Preferences.FeatureFlags with
                {
                    HoyoLabAccountAccess = true,
                },
            },
        });
        return 0;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("NYX_STATE_WORKER=FAILED CODE=SMOKE_FIXTURE_WRITE_FAILED");
        return 1;
    }
}

static bool HasNyxProcess()
{
    try
    {
        return new[] { "Nyx", "Nyx.Desktop", "Nyx.Desktop.App", "Nyx.Desktop.Update" }
            .Any(name => System.Diagnostics.Process.GetProcessesByName(name).Length != 0);
    }
    catch (Exception)
    {
        return true;
    }
}

static bool IsProvenIsolatedSmokeRoot(string root, string markerPath, string runId)
{
    try
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParseExact(runId, "N", out _))
        {
            return false;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var expectedRoot = Path.GetFullPath(NyxUserDataPaths.CanonicalRoot(localAppData));
        var fullRoot = Path.GetFullPath(root);
        var expectedMarker = Path.Combine(expectedRoot, ".nyx-native-smoke-isolated-v1");
        var fullMarker = Path.GetFullPath(markerPath);
        if (!string.Equals(fullRoot, expectedRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fullMarker, expectedMarker, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(fullRoot)
            || !File.Exists(fullMarker)
            || HasReparsePointInExistingPath(fullMarker))
        {
            return false;
        }

        var entries = Directory.EnumerateFileSystemEntries(fullRoot).Take(2).ToArray();
        return entries.Length == 1
            && string.Equals(Path.GetFullPath(entries[0]), fullMarker, StringComparison.OrdinalIgnoreCase)
            && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) == 0
            && (File.GetAttributes(fullMarker) & FileAttributes.ReparsePoint) == 0
            && string.Equals(
                File.ReadAllText(fullMarker),
                $"NYX_NATIVE_SMOKE_ISOLATED_V1:{runId}",
                StringComparison.Ordinal);
    }
    catch (Exception)
    {
        return false;
    }
}

static bool HasReparsePointInExistingPath(string path)
{
    try
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return true;
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        foreach (var segment in fullPath[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) return true;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
    catch (Exception)
    {
        return true;
    }
}
