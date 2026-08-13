using System.Collections;
using Nyx.Desktop.Core.Genshin;
using Nyx.Desktop.Infrastructure.Genshin;

namespace Nyx.Desktop.Tests.Launching;

public sealed class WindowsGenshinCandidateDiscoveryTests
{
    private const string ParentRoot = @"C:\Games\Genshin Impact";
    private const string GameRoot = @"C:\Games\Genshin Impact\Genshin Impact Game";
    private const string UpdaterRoot = @"C:\FakePublisher\HoYoPlay";

    [Fact]
    public void Exact_public_records_propose_candidates_but_disk_inspection_decides_ready()
    {
        var registry = ValidRegistry();
        var inspector = new FakeInspector();
        var discovery = new WindowsGenshinCandidateDiscovery(registry, inspector);

        var result = discovery.Discover();

        Assert.Equal(GameRoot, result.GameRoot);
        Assert.Equal(UpdaterRoot, result.UpdaterRoot);
        Assert.Equal([GenshinRegistryRecord.GenshinImpact, GenshinRegistryRecord.HoYoPlayGlobal], registry.Reads);
        Assert.Equal([GameRoot], inspector.GameCandidates);
        Assert.Equal([UpdaterRoot], inspector.UpdaterCandidates);
    }

    [Theory]
    [InlineData("GameBiz", "other_game")]
    [InlineData("Channel", "2_0")]
    [InlineData("HoYoPlay", "V1")]
    [InlineData("InstallPath", @"C:\Games\Genshin Impact\moved\..")]
    [InlineData("InstallPath", @"\\server\share\Genshin")]
    public void Wrong_or_unsafe_game_registry_evidence_is_ignored_without_disk_inspection(
        string name,
        string value)
    {
        var registry = ValidRegistry();
        registry.Game[name] = value;
        var inspector = new FakeInspector();

        var result = new WindowsGenshinCandidateDiscovery(registry, inspector).Discover();

        Assert.Null(result.GameRoot);
        Assert.Empty(inspector.GameCandidates);
        Assert.Equal(UpdaterRoot, result.UpdaterRoot);
    }

    [Theory]
    [InlineData("GameBiz", "other_game")]
    [InlineData("Region", "china")]
    [InlineData("ExeName", "other.exe")]
    [InlineData("InstallPath", @"C:\FakePublisher\HoYoPlay\child\..")]
    public void Wrong_or_unsafe_updater_registry_evidence_is_ignored_independently(
        string name,
        string value)
    {
        var registry = ValidRegistry();
        registry.Updater[name] = value;
        var inspector = new FakeInspector();

        var result = new WindowsGenshinCandidateDiscovery(registry, inspector).Discover();

        Assert.Null(result.UpdaterRoot);
        Assert.Empty(inspector.UpdaterCandidates);
        Assert.Equal(GameRoot, result.GameRoot);
    }

    [Fact]
    public void Stale_game_record_is_not_returned_when_adapter_validation_fails()
    {
        var registry = ValidRegistry();
        var inspector = new FakeInspector
        {
            GameResult = new(
                GenshinInspectionStatus.NotFound,
                GenshinInspectionReason.DirectoryNotFound,
                GameRoot),
        };

        var result = new WindowsGenshinCandidateDiscovery(registry, inspector).Discover();

        Assert.Null(result.GameRoot);
        Assert.Equal([GameRoot], inspector.GameCandidates);
    }

    [Fact]
    public void Only_allowlisted_values_are_observed_even_when_record_contains_private_lookalikes()
    {
        var registry = ValidRegistry(tracking: true);
        registry.Game["UUID"] = "must-not-be-read";
        registry.Game["account_token"] = "must-not-be-read";
        registry.Updater["TelemetryPath"] = "must-not-be-read";

        _ = new WindowsGenshinCandidateDiscovery(registry, new FakeInspector()).Discover();

        Assert.Equal(
            new[] { "Channel", "GameBiz", "HoYoPlay", "InstallPath" },
            registry.Game.AccessedKeys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            new[] { "ExeName", "GameBiz", "InstallPath", "Region" },
            registry.Updater.AccessedKeys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("UUID", registry.Game.AccessedKeys);
        Assert.DoesNotContain("account_token", registry.Game.AccessedKeys);
        Assert.DoesNotContain("TelemetryPath", registry.Updater.AccessedKeys);
    }

    [Fact]
    public void Repeated_discovery_does_not_mutate_registry_or_inspector_fixtures()
    {
        var registry = ValidRegistry();
        var inspector = new FakeInspector();
        var discovery = new WindowsGenshinCandidateDiscovery(registry, inspector);
        var before = registry.Snapshot();

        var first = discovery.Discover();
        var second = discovery.Discover();

        Assert.Equal(first, second);
        Assert.Equal(before, registry.Snapshot());
        Assert.Equal([GameRoot, GameRoot], inspector.GameCandidates);
        Assert.Equal([UpdaterRoot, UpdaterRoot], inspector.UpdaterCandidates);
    }

    private static FakeRegistry ValidRegistry(bool tracking = false) =>
        new(
            new TrackingDictionary(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["InstallPath"] = ParentRoot,
                    ["GameBiz"] = "hk4e_global",
                    ["Channel"] = "1_0",
                    ["HoYoPlay"] = "V2",
                },
                tracking),
            new TrackingDictionary(
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["InstallPath"] = UpdaterRoot,
                    ["ExeName"] = "launcher.exe",
                    ["Region"] = "global",
                    ["GameBiz"] = "hk4e_global",
                },
                tracking));

    private sealed class FakeRegistry(TrackingDictionary game, TrackingDictionary updater)
        : IGenshinRegistryReader
    {
        public TrackingDictionary Game { get; } = game;

        public TrackingDictionary Updater { get; } = updater;

        public List<GenshinRegistryRecord> Reads { get; } = [];

        public IReadOnlyDictionary<string, string?> Read(GenshinRegistryRecord record)
        {
            Reads.Add(record);
            return record is GenshinRegistryRecord.GenshinImpact ? Game : Updater;
        }

        public string Snapshot() =>
            string.Join('|', Game.OrderBy(entry => entry.Key).Concat(Updater.OrderBy(entry => entry.Key)));
    }

    private sealed class FakeInspector : IGenshinCandidateInspector
    {
        public GenshinInspectionResult GameResult { get; set; } = Ready(GameRoot);

        public GenshinInspectionResult UpdaterResult { get; set; } = Ready(UpdaterRoot);

        public List<string> GameCandidates { get; } = [];

        public List<string> UpdaterCandidates { get; } = [];

        public GenshinInspectionResult InspectGame(string root)
        {
            GameCandidates.Add(root);
            return GameResult;
        }

        public GenshinInspectionResult InspectUpdater(string root)
        {
            UpdaterCandidates.Add(root);
            return UpdaterResult;
        }
    }

    private sealed class TrackingDictionary(
        Dictionary<string, string?> values,
        bool tracking) : IReadOnlyDictionary<string, string?>
    {
        public HashSet<string> AccessedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? this[string key]
        {
            get
            {
                Track(key);
                return values[key];
            }
            set => values[key] = value;
        }

        public IEnumerable<string> Keys => values.Keys;

        public IEnumerable<string?> Values => values.Values;

        public int Count => values.Count;

        public bool ContainsKey(string key)
        {
            Track(key);
            return values.ContainsKey(key);
        }

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => values.GetEnumerator();

        public bool TryGetValue(string key, out string? value)
        {
            Track(key);
            return values.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private void Track(string key)
        {
            if (tracking)
            {
                AccessedKeys.Add(key);
            }
        }
    }

    private static GenshinInspectionResult Ready(string root) =>
        new(GenshinInspectionStatus.Ready, GenshinInspectionReason.None, root, "1.0.0");
}
