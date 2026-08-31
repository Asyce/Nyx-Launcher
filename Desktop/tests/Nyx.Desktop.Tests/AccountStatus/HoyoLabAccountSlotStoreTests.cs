using System.Text;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabAccountSlotStoreTests
{
    private const string FirstId = "00112233445566778899aabbccddeeff";
    private const string SecondId = "ffeeddccbbaa99887766554433221100";
    private static readonly DateTimeOffset InitialTime =
        new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Id_label_and_index_rules_are_strict_and_bounded()
    {
        Assert.True(HoyoLabAccountSlotRules.IsValidSlotId(FirstId));
        Assert.False(HoyoLabAccountSlotRules.IsValidSlotId(FirstId.ToUpperInvariant()));
        Assert.False(HoyoLabAccountSlotRules.IsValidSlotId(FirstId[..31]));
        Assert.False(HoyoLabAccountSlotRules.IsValidSlotId(new string('g', 32)));

        Assert.True(HoyoLabAccountSlotRules.TryNormalizeLabel("  My account  ", out var label));
        Assert.Equal("My account", label);
        Assert.False(HoyoLabAccountSlotRules.TryNormalizeLabel("\r\n", out _));
        Assert.False(HoyoLabAccountSlotRules.TryNormalizeLabel("\tAccount", out _));
        Assert.False(HoyoLabAccountSlotRules.TryNormalizeLabel("a\u0001b", out _));
        Assert.False(HoyoLabAccountSlotRules.TryNormalizeLabel(new string('x', 49), out _));
        Assert.False(HoyoLabAccountSlotRules.TryNormalizeLabel(string.Concat(Enumerable.Repeat("😀", 33)), out _));
        Assert.False(HoyoLabAccountSlotRules.TryNormalizeLabel("\ud800", out _));

        var slot = Slot(FirstId);
        Assert.True(HoyoLabAccountSlotRules.IsValidIndex(Index([slot], FirstId)));
        Assert.False(HoyoLabAccountSlotRules.IsValidIndex(Index([slot, slot], FirstId)));
        Assert.False(HoyoLabAccountSlotRules.IsValidIndex(Index([slot], SecondId)));
        Assert.False(HoyoLabAccountSlotRules.IsValidIndex(Index([slot with { RemovalPending = true }], FirstId)));
        Assert.False(HoyoLabAccountSlotRules.IsValidIndex(Index([
            slot with { IsLegacy = true },
            Slot(SecondId) with { IsLegacy = true },
        ], FirstId, legacyFallback: true)));
        Assert.False(HoyoLabAccountSlotRules.IsValidIndex(Index(
            Enumerable.Range(0, 9)
                .Select(i => Slot(i.ToString("x32")))
                .ToArray(),
            null)));
    }

    [Fact]
    public void Empty_initialization_and_protected_round_trip_do_not_expose_id_or_label()
    {
        using var root = new TemporaryRoot();
        var store = new HoyoLabAccountSlotStore(root.Path);
        var initialized = store.TryInitialize();
        Assert.True(initialized.IsReady);
        Assert.Empty(initialized.Index!.Slots);
        Assert.False(initialized.Index.LegacyFallback);

        Assert.True(store.TryCreateSlot("Private local label", out var slot));
        Assert.NotNull(slot);
        Assert.Matches("^[0-9a-f]{32}$", slot!.Id);
        var ciphertext = File.ReadAllBytes(IndexPath(root.Path));
        Assert.Equal(-1, ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(slot.Id)));
        Assert.Equal(-1, ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(slot.Label)));

        var loaded = Assert.IsType<HoyoLabAccountSlotIndex>(
            new HoyoLabAccountSlotStore(root.Path).TryLoad());
        Assert.Equal(slot, Assert.Single(loaded.Slots));
    }

    [Fact]
    public void Create_and_select_is_one_index_commit_and_index_delete_fails_closed()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = Store(root.Path, boundary: boundary, idFactory: () => FirstId);
        Assert.True(store.TryInitialize().IsReady);
        Assert.True(store.TryCreateAndSelectSlot("Account", out var slot));
        Assert.Equal(slot!.Id, store.CurrentIndex!.ActiveSlotId);

        boundary.FailDeletePath = IndexPath(root.Path);
        Assert.False(store.TryDeleteIndex());
        Assert.True(File.Exists(IndexPath(root.Path)));
        boundary.FailDeletePath = null;
        Assert.True(store.TryDeleteIndex());
        Assert.False(File.Exists(IndexPath(root.Path)));
        Assert.Null(store.CurrentIndex);
    }

    [Fact]
    public void Parser_requires_exact_v1_schema_canonical_timestamps_and_bounds()
    {
        var valid = HoyoLabAccountSlotStore.SerializeIndex(Index([Slot(FirstId)], FirstId));
        Assert.True(HoyoLabAccountSlotStore.TryParseIndex(valid, out _));

        Assert.False(Parse("{\"schemaVersion\":2,\"activeSlotId\":null,\"legacyFallback\":false,\"slots\":[]}"));
        Assert.False(Parse("{\"schemaVersion\":1,\"activeSlotId\":null,\"legacyFallback\":false,\"slots\":[],\"extra\":0}"));
        Assert.False(Parse("{\"schemaVersion\":1,\"schemaVersion\":1,\"activeSlotId\":null,\"legacyFallback\":false,\"slots\":[]}"));
        Assert.False(Parse("{\"schemaVersion\":1,\"activeSlotId\":null,\"legacyFallback\":false}"));
        Assert.False(Parse("{\"schemaVersion\":1,\"activeSlotId\":null,\"legacyFallback\":false,\"slots\":[{\"id\":\"00112233445566778899aabbccddeeff\",\"label\":\"x\",\"isLegacy\":false,\"createdAt\":\"2026-08-08T10:00:00Z\",\"updatedAt\":\"2026-08-08T10:00:00Z\",\"removalPending\":false}]}"));
        Assert.False(HoyoLabAccountSlotStore.TryParseIndex(
            new byte[HoyoLabAccountSlotStore.MaximumPlaintextBytes + 1],
            out _));
    }

    [Fact]
    public void Corrupt_future_duplicate_invalid_active_and_reparse_indexes_fail_closed_without_rewrite()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath(root.Path))!);
        var protector = new CopyProtector();
        foreach (var payload in new[]
        {
            "not-json",
            "{\"schemaVersion\":2,\"activeSlotId\":null,\"legacyFallback\":false,\"slots\":[]}",
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"activeSlotId\":null,\"legacyFallback\":false,\"slots\":[]}",
            "{\"schemaVersion\":1,\"activeSlotId\":\"00112233445566778899aabbccddeeff\",\"legacyFallback\":false,\"slots\":[]}",
        })
        {
            File.WriteAllText(IndexPath(root.Path), payload);
            var before = File.ReadAllBytes(IndexPath(root.Path));
            var result = Store(root.Path, protector: protector).TryInitialize();
            Assert.Equal(HoyoLabAccountSlotInitializationState.Unavailable, result.State);
            Assert.Equal(before, File.ReadAllBytes(IndexPath(root.Path)));
        }

        var valid = HoyoLabAccountSlotStore.SerializeIndex(Index([], null));
        File.WriteAllBytes(IndexPath(root.Path), valid);
        var boundary = new FaultBoundary { ReparsePath = IndexPath(root.Path) };
        Assert.Equal(
            HoyoLabAccountSlotInitializationState.Unavailable,
            Store(root.Path, boundary: boundary).TryInitialize().State);
        Assert.Equal(valid, File.ReadAllBytes(IndexPath(root.Path)));
    }

    [Fact]
    public void Legacy_compatibility_safety_is_revoked_if_an_index_entry_appears()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "HoYoLAB"));
        var protector = new CopyProtector { FailProtect = true };
        var store = Store(root.Path, protector: protector, idFactory: () => FirstId);
        Assert.Equal(
            HoyoLabAccountSlotInitializationState.LegacyCompatibility,
            store.TryInitialize().State);
        Assert.True(store.IsLegacyCompatibilityStillSafe());

        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath(root.Path))!);
        File.WriteAllText(IndexPath(root.Path), "corrupt");

        Assert.False(store.IsLegacyCompatibilityStillSafe());
    }

    [Fact]
    public void Failed_atomic_mutation_leaves_prior_index_and_cleans_temporary_file()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var ids = new Queue<string>([FirstId, SecondId]);
        var store = Store(root.Path, boundary: boundary, idFactory: ids.Dequeue);
        Assert.True(store.TryInitialize().IsReady);
        Assert.True(store.TryCreateSlot("First", out var first));
        var prior = File.ReadAllBytes(IndexPath(root.Path));

        boundary.FailMoveOverwrite = true;
        Assert.False(store.TryCreateSlot("Second", out _));
        Assert.Equal(prior, File.ReadAllBytes(IndexPath(root.Path)));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(IndexPath(root.Path))!,
            "index.bin.tmp.*"));
        Assert.Equal(first, Assert.Single(Store(root.Path).TryLoad()!.Slots));
    }

    [Fact]
    public void Legacy_adoption_keeps_profile_and_sources_and_byte_verifies_both_stores()
    {
        using var root = new TemporaryRoot();
        var legacyProfile = Path.Combine(root.Path, "HoYoLAB");
        Directory.CreateDirectory(legacyProfile);
        var roleSource = LegacyFile(root.Path, ".protected-role-bindings", "gi.bin", [1, 2, 3]);
        var snapshotSource = LegacyFile(root.Path, ".protected-resource-snapshots", "gi.bin", [4, 5, 6]);
        var store = Store(root.Path, idFactory: () => FirstId);

        var result = store.TryInitialize();

        Assert.True(result.IsReady);
        var slot = Assert.Single(result.Index!.Slots);
        Assert.True(slot.IsLegacy);
        Assert.True(result.Index.LegacyFallback);
        Assert.Equal(FirstId, result.Index.ActiveSlotId);
        Assert.True(Directory.Exists(legacyProfile));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(roleSource));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(snapshotSource));
        Assert.True(store.TryGetProtectedStateRoot(slot, out var protectedRoot));
        Assert.Equal(
            File.ReadAllBytes(roleSource),
            File.ReadAllBytes(Path.Combine(protectedRoot, ".protected-role-bindings", "gi.bin")));
        Assert.Equal(
            File.ReadAllBytes(snapshotSource),
            File.ReadAllBytes(Path.Combine(protectedRoot, ".protected-resource-snapshots", "gi.bin")));
        Assert.True(store.TryGetWebView2ProfilePath(slot, out var profile));
        Assert.Equal(Path.GetFullPath(legacyProfile), profile);
    }

    [Fact]
    public void Adoption_write_copy_and_destination_mismatch_failures_keep_legacy_data_untouched()
    {
        foreach (var failure in new[] { "write", "copy", "mismatch" })
        {
            using var root = new TemporaryRoot();
            Directory.CreateDirectory(Path.Combine(root.Path, "HoYoLAB"));
            var source = LegacyFile(root.Path, ".protected-role-bindings", "gi.bin", [7, 8, 9]);
            var protector = new CopyProtector { FailProtect = failure == "write" };
            if (failure == "copy")
                File.WriteAllBytes(source, new byte[16 * 1024 + 1]);
            if (failure == "mismatch")
            {
                var destination = Path.Combine(
                    root.Path,
                    "Accounts",
                    "HoYoLAB",
                    FirstId,
                    "Protected",
                    ".protected-role-bindings",
                    "gi.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, [0xff]);
            }
            var expected = File.ReadAllBytes(source);

            var result = Store(root.Path, protector: protector, idFactory: () => FirstId)
                .TryInitialize();

            Assert.Equal(HoyoLabAccountSlotInitializationState.LegacyCompatibility, result.State);
            Assert.True(Directory.Exists(Path.Combine(root.Path, "HoYoLAB")));
            Assert.Equal(expected, File.ReadAllBytes(source));
            Assert.False(File.Exists(IndexPath(root.Path)));
        }
    }

    [Fact]
    public void Failed_legacy_adoption_then_successful_restart_never_adopts_a_root_v2_bundle()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "HoYoLAB"));
        var source = LegacyFile(
            root.Path,
            ".protected-role-bindings",
            "hsr.bin",
            new byte[16 * 1024 + 1]);
        var legacyBundle = Path.Combine(
            root.Path,
            ".protected-hoyolab-game-bundles",
            "hsr-v2.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyBundle)!);
        File.WriteAllBytes(legacyBundle, [1, 2, 3]);

        var failed = Store(root.Path, idFactory: () => FirstId).TryInitialize();
        Assert.Equal(HoyoLabAccountSlotInitializationState.LegacyCompatibility, failed.State);
        Assert.False(File.Exists(IndexPath(root.Path)));

        File.WriteAllBytes(source, [7, 8, 9]);
        var restartedStore = Store(root.Path, idFactory: () => FirstId);
        var restarted = restartedStore.TryInitialize();

        Assert.True(restarted.IsReady);
        var adopted = Assert.Single(restarted.Index!.Slots);
        Assert.True(restartedStore.TryGetProtectedStateRoot(adopted, out var protectedRoot));
        Assert.False(File.Exists(Path.Combine(
            protectedRoot,
            ".protected-hoyolab-game-bundles",
            "hsr-v2.bin")));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(legacyBundle));
    }

    [Fact]
    public void Adoption_rejects_a_regular_file_where_a_legacy_protected_directory_is_expected()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "HoYoLAB"));
        var invalid = Path.Combine(root.Path, ".protected-role-bindings");
        File.WriteAllBytes(invalid, [1, 2, 3]);

        var result = Store(root.Path, idFactory: () => FirstId).TryInitialize();

        Assert.Equal(HoyoLabAccountSlotInitializationState.LegacyCompatibility, result.State);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(invalid));
        Assert.False(File.Exists(IndexPath(root.Path)));
    }

    [Fact]
    public void Slot_paths_are_exact_and_reject_slots_not_in_the_loaded_index()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "HoYoLAB"));
        var ids = new Queue<string>([FirstId, SecondId]);
        var store = Store(root.Path, idFactory: ids.Dequeue);
        var legacy = Assert.Single(store.TryInitialize().Index!.Slots);
        Assert.True(store.TryGetWebView2ProfilePath(legacy, out var legacyProfile));
        Assert.Equal(Path.Combine(root.Path, "HoYoLAB"), legacyProfile);
        Assert.True(store.TryGetProtectedStateRoot(legacy, out var legacyProtected));
        Assert.Equal(
            Path.Combine(root.Path, "Accounts", "HoYoLAB", FirstId, "Protected"),
            legacyProtected);

        Assert.True(store.TryCreateSlot("Second", out var second));
        Assert.True(store.TryGetWebView2ProfilePath(second!, out var newProfile));
        Assert.Equal(
            Path.Combine(root.Path, "Accounts", "HoYoLAB", SecondId, "WebView2"),
            newProfile);
        Assert.False(store.TryGetWebView2ProfilePath(
            Slot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            out _));
    }

    [Fact]
    public void Max_slots_rename_order_removal_pending_and_remove_do_not_auto_select()
    {
        using var root = new TemporaryRoot();
        var time = new MutableTimeProvider(InitialTime);
        var next = 0;
        string NewId() => (++next).ToString("x32");
        var store = Store(root.Path, clock: time, idFactory: NewId);
        Assert.True(store.TryInitialize().IsReady);
        var slots = new List<HoyoLabAccountSlot>();
        for (var i = 0; i < 8; i++)
        {
            Assert.True(store.TryCreateSlot($"Slot {i}", out var slot));
            slots.Add(slot!);
        }
        Assert.False(store.TryCreateSlot("Ninth", out _));

        time.UtcNow = InitialTime.AddMinutes(1);
        Assert.True(store.TryRenameSlot(slots[3].Id, " Renamed "));
        var renamedIndex = store.CurrentIndex!;
        Assert.Equal(slots.Select(slot => slot.Id), renamedIndex.Slots.Select(slot => slot.Id));
        var renamed = renamedIndex.Slots[3];
        Assert.Equal("Renamed", renamed.Label);
        Assert.Equal(slots[3].CreatedAt, renamed.CreatedAt);
        Assert.True(renamed.UpdatedAt > slots[3].UpdatedAt);

        Assert.True(store.TrySetActiveSlot(renamed.Id));
        Assert.True(store.TryMarkRemovalPending(renamed.Id));
        Assert.Null(store.CurrentIndex!.ActiveSlotId);
        Assert.True(store.CurrentIndex.Slots[3].RemovalPending);
        Assert.True(store.TryRemoveSlot(renamed.Id));
        Assert.Null(store.CurrentIndex!.ActiveSlotId);
        Assert.Equal(7, store.CurrentIndex.Slots.Count);
        Assert.DoesNotContain(store.CurrentIndex.Slots, slot => slot.Id == renamed.Id);
    }

    [Fact]
    public void Removing_adopted_legacy_slot_keeps_durable_fallback_and_never_deletes_profile()
    {
        using var root = new TemporaryRoot();
        var legacyProfile = Path.Combine(root.Path, "HoYoLAB");
        Directory.CreateDirectory(legacyProfile);
        var store = Store(root.Path, idFactory: () => FirstId);
        var slot = Assert.Single(store.TryInitialize().Index!.Slots);

        Assert.True(store.TryMarkRemovalPending(slot.Id));
        Assert.True(store.TryRemoveSlot(slot.Id));

        Assert.Empty(store.CurrentIndex!.Slots);
        Assert.Null(store.CurrentIndex.ActiveSlotId);
        Assert.True(store.CurrentIndex.LegacyFallback);
        Assert.True(Directory.Exists(legacyProfile));
        Assert.True(HoyoLabAccountSlotRules.IsValidIndex(store.CurrentIndex));
    }

    [Fact]
    public void Slot_protected_roots_isolate_role_and_resource_records()
    {
        using var root = new TemporaryRoot();
        var ids = new Queue<string>([FirstId, SecondId]);
        var slots = Store(root.Path, idFactory: ids.Dequeue);
        Assert.True(slots.TryInitialize().IsReady);
        Assert.True(slots.TryCreateSlot("One", out var first));
        Assert.True(slots.TryCreateSlot("Two", out var second));
        Assert.True(slots.TryGetProtectedStateRoot(first!, out var firstRoot));
        Assert.True(slots.TryGetProtectedStateRoot(second!, out var secondRoot));
        var protector = new CopyProtector();
        var firstRoles = new PublisherRoleBindingStore(firstRoot, protector);
        var secondRoles = new PublisherRoleBindingStore(secondRoot, protector);
        var firstSnapshots = new PublisherResourceSnapshotStore(firstRoot, protector);
        var secondSnapshots = new PublisherResourceSnapshotStore(secondRoot, protector);
        var firstRole = new PublisherRoleBinding("123456789", "os_euro");
        var secondRole = new PublisherRoleBinding("987654321", "os_euro");
        var firstSnapshot = new PublisherResourceSnapshot(
            "gi", "Original Resin", 10, 200, InitialTime);
        var secondSnapshot = firstSnapshot with { Current = 20 };

        Assert.True(firstRoles.Save("gi", firstRole));
        Assert.True(secondRoles.Save("gi", secondRole));
        Assert.True(firstSnapshots.Save(firstSnapshot, firstRole));
        Assert.True(secondSnapshots.Save(secondSnapshot, secondRole));
        Assert.Equal(firstRole, firstRoles.TryLoad("gi"));
        Assert.Equal(secondRole, secondRoles.TryLoad("gi"));
        Assert.Equal(10, firstSnapshots.TryLoad("gi", firstRole)!.Current);
        Assert.Equal(20, secondSnapshots.TryLoad("gi", secondRole)!.Current);
        Assert.Null(firstSnapshots.TryLoad("gi", secondRole));
    }

    [Fact]
    public async Task Concurrent_store_instances_serialize_mutations_without_lost_updates()
    {
        using var root = new TemporaryRoot();
        Assert.True(Store(root.Path).TryInitialize().IsReady);
        var tasks = Enumerable.Range(1, 8).Select(index => Task.Run(() =>
        {
            var id = index.ToString("x32");
            return Store(root.Path, idFactory: () => id).TryCreateSlot($"Slot {index}", out _);
        }));

        Assert.All(await Task.WhenAll(tasks), Assert.True);
        var loaded = Assert.IsType<HoyoLabAccountSlotIndex>(Store(root.Path).TryLoad());
        Assert.Equal(8, loaded.Slots.Count);
        Assert.Equal(8, loaded.Slots.Select(slot => slot.Id).Distinct().Count());
    }

    [Fact]
    public void Path_helpers_reject_reparse_components_and_max_timestamp_mutation_fails_closed()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = Store(root.Path, boundary: boundary, idFactory: () => FirstId);
        Assert.True(store.TryInitialize().IsReady);
        Assert.True(store.TryCreateSlot("Account", out var slot));
        boundary.ReparsePath = Path.Combine(root.Path, "Accounts", "HoYoLAB", FirstId);
        Assert.False(store.TryGetWebView2ProfilePath(slot!, out _));
        Assert.False(store.TryGetProtectedStateRoot(slot!, out _));
        Assert.False(store.TryGetSlotContainerPath(slot!, out _));

        boundary.ReparsePath = null;
        var maximum = slot! with
        {
            CreatedAt = DateTimeOffset.MaxValue,
            UpdatedAt = DateTimeOffset.MaxValue,
        };
        File.WriteAllBytes(
            IndexPath(root.Path),
            HoyoLabAccountSlotStore.SerializeIndex(Index([maximum], null)));
        Assert.False(Store(root.Path, clock: new MutableTimeProvider(DateTimeOffset.MaxValue))
            .TryRenameSlot(FirstId, "Changed"));
    }

    [Fact]
    public void Removed_slot_requires_both_index_removal_and_absent_container()
    {
        using var root = new TemporaryRoot();
        var store = Store(root.Path, idFactory: () => FirstId);
        Assert.True(store.TryInitialize().IsReady);
        Assert.True(store.IsSlotRemoved(FirstId));
        Assert.True(store.TryCreateSlot("Account", out var slot));
        Assert.False(store.IsSlotRemoved(FirstId));
        Assert.True(store.TryGetSlotContainerPath(slot!, out var container));
        Directory.CreateDirectory(container);
        Assert.True(store.TryMarkRemovalPending(FirstId));
        Assert.False(store.IsSlotRemoved(FirstId));
        Assert.True(store.TryRemoveSlot(FirstId));
        Assert.False(store.IsSlotRemoved(FirstId));
        Directory.Delete(container);
        Assert.True(Store(root.Path).IsSlotRemoved(FirstId));
        Assert.False(store.IsSlotRemoved("../other"));
    }

    [Fact]
    public void Removed_slot_check_fails_closed_on_corrupt_index_or_reparse_chain()
    {
        using var root = new TemporaryRoot();
        var boundary = new FaultBoundary();
        var store = Store(root.Path, boundary: boundary);
        Assert.True(store.TryInitialize().IsReady);
        var original = File.ReadAllBytes(IndexPath(root.Path));
        File.WriteAllBytes(IndexPath(root.Path), [0xFF]);
        Assert.False(store.IsSlotRemoved(FirstId));
        Assert.Equal(new byte[] { 0xFF }, File.ReadAllBytes(IndexPath(root.Path)));
        File.WriteAllBytes(IndexPath(root.Path), original);
        boundary.ReparsePath = Path.Combine(root.Path, "Accounts", "HoYoLAB");
        Assert.False(store.IsSlotRemoved(FirstId));
        Assert.Equal(original, File.ReadAllBytes(IndexPath(root.Path)));
    }

    [Fact]
    public void Removed_slot_check_allows_finished_global_cleanup_without_recreating_index()
    {
        using var root = new TemporaryRoot();
        var store = Store(root.Path);
        Assert.True(store.IsSlotRemoved(FirstId));
        Assert.False(File.Exists(IndexPath(root.Path)));
        var orphan = Path.Combine(root.Path, "Accounts", "HoYoLAB", FirstId);
        Directory.CreateDirectory(orphan);
        Assert.False(store.IsSlotRemoved(FirstId));
        Assert.False(File.Exists(IndexPath(root.Path)));
    }

    private static bool Parse(string json) => HoyoLabAccountSlotStore.TryParseIndex(
        Encoding.UTF8.GetBytes(json),
        out _);

    private static HoyoLabAccountSlot Slot(string id) => new(
        id,
        "Account",
        IsLegacy: false,
        InitialTime,
        InitialTime,
        RemovalPending: false);

    private static HoyoLabAccountSlotIndex Index(
        IReadOnlyList<HoyoLabAccountSlot> slots,
        string? active,
        bool legacyFallback = false) => new(
            HoyoLabAccountSlotRules.SchemaVersion,
            active,
            slots,
            legacyFallback);

    private static HoyoLabAccountSlotStore Store(
        string root,
        CopyProtector? protector = null,
        FaultBoundary? boundary = null,
        TimeProvider? clock = null,
        Func<string>? idFactory = null) => new(
            root,
            protector ?? new CopyProtector(),
            boundary ?? new FaultBoundary(),
            clock ?? new MutableTimeProvider(InitialTime),
            idFactory ?? (() => SecondId));

    private static string IndexPath(string root) =>
        Path.Combine(root, ".protected-hoyolab-slots", "index.bin");

    private static string LegacyFile(string root, string directory, string name, byte[] bytes)
    {
        var path = Path.Combine(root, directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class CopyProtector : IPublisherRoleBindingProtector
    {
        public bool FailProtect { get; set; }
        public byte[] Protect(byte[] plaintext)
        {
            if (FailProtect) throw new System.Security.Cryptography.CryptographicException();
            return [.. plaintext];
        }
        public byte[] Unprotect(byte[] ciphertext) => [.. ciphertext];
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FaultBoundary : IHoyoLabAccountSlotFileBoundary
    {
        private readonly SystemHoyoLabAccountSlotFileBoundary inner = new();
        public string? ReparsePath { get; set; }
        public bool FailMoveOverwrite { get; set; }
        public string? FailDeletePath { get; set; }
        public bool EntryExists(string path) =>
            string.Equals(path, ReparsePath, StringComparison.OrdinalIgnoreCase)
                || inner.EntryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileAttributes GetAttributes(string path) =>
            string.Equals(path, ReparsePath, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : inner.GetAttributes(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
            inner.EnumerateFileSystemEntries(path);
        public FileStream OpenRead(string path) => inner.OpenRead(path);
        public FileStream CreateNewWriteThrough(string path) => inner.CreateNewWriteThrough(path);
        public void MoveNew(string source, string destination) => inner.MoveNew(source, destination);
        public void MoveOverwrite(string source, string destination)
        {
            if (FailMoveOverwrite) throw new IOException("Injected move failure.");
            inner.MoveOverwrite(source, destination);
        }
        public void DeleteFile(string path)
        {
            if (string.Equals(path, FailDeletePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Injected delete failure.");
            inner.DeleteFile(path);
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "nyx-hoyolab-slot-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
