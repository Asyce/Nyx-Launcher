using System.Text;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class PublisherRoleBindingStoreTests
{
    [Fact]
    public void Role_binding_is_current_user_protected_and_provider_delete_clears_it()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-protected-role-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PublisherRoleBindingStore(root);
            var binding = new PublisherRoleBinding("123456789", "os_euro");

            Assert.True(store.Save("gi", binding));
            var path = Path.Combine(root, ".protected-role-bindings", "gi.bin");
            var ciphertext = File.ReadAllBytes(path);
            Assert.Equal(-1, ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(binding.RoleId)));
            Assert.Equal(binding, store.TryLoad("gi"));

            Assert.True(store.DeleteProvider("HoYoLAB"));
            Assert.Null(store.TryLoad("gi"));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("ae", "123456789", "os_euro")]
    [InlineData("gi", "not-a-uid", "os_euro")]
    [InlineData("gi", "123456789", "attacker")]
    public void Unsupported_or_malformed_bindings_are_never_written(
        string gameId,
        string roleId,
        string server)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nyx-invalid-role-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PublisherRoleBindingStore(root);

            Assert.False(store.Save(gameId, new(roleId, server)));
            Assert.Null(store.TryLoad(gameId));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_ciphertext_and_unprotect_failure_fail_closed()
    {
        var root = NewRoot("nyx-corrupt-role-tests-");
        try
        {
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            var store = new PublisherRoleBindingStore(root);
            Assert.True(store.Save("gi", binding));
            var path = BindingPath(root, "gi");
            File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]);
            Assert.Null(store.TryLoad("gi"));

            var passthrough = new FaultProtector();
            var injectable = new PublisherRoleBindingStore(root, passthrough);
            Assert.True(injectable.Save("gi", binding));
            passthrough.FailUnprotect = true;
            Assert.Null(injectable.TryLoad("gi"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Protect_failure_and_oversized_ciphertext_are_never_persisted()
    {
        var root = NewRoot("nyx-protector-failure-tests-");
        try
        {
            var protector = new FaultProtector { FailProtect = true };
            var store = new PublisherRoleBindingStore(root, protector);
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            Assert.False(store.Save("gi", binding));
            Assert.False(File.Exists(BindingPath(root, "gi")));

            protector.FailProtect = false;
            protector.ProtectedLength = 16 * 1024 + 1;
            Assert.False(store.Save("gi", binding));
            Assert.False(File.Exists(BindingPath(root, "gi")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Oversized_ciphertext_is_rejected_before_unprotect()
    {
        var root = NewRoot("nyx-oversized-role-tests-");
        try
        {
            var path = BindingPath(root, "gi");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[16 * 1024 + 1]);
            var protector = new FaultProtector();

            Assert.Null(new PublisherRoleBindingStore(root, protector).TryLoad("gi"));
            Assert.Equal(0, protector.UnprotectCalls);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Reparse_binding_interrupted_move_and_denied_delete_fail_closed()
    {
        var root = NewRoot("nyx-role-boundary-tests-");
        try
        {
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            var boundary = new FaultFileBoundary();
            var store = new PublisherRoleBindingStore(root, new FaultProtector(), boundary);
            var path = BindingPath(root, "gi");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x01]);

            boundary.ReparsePath = path;
            Assert.Null(store.TryLoad("gi"));
            Assert.False(store.Save("gi", binding));

            boundary.ReparsePath = null;
            boundary.FailMove = true;
            Assert.False(store.Save("gi", binding));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(path)!,
                "gi.bin.tmp.*"));
            Assert.Equal(new byte[] { 0x01 }, File.ReadAllBytes(path));

            boundary.FailMove = false;
            Assert.True(store.Save("gi", binding));
            boundary.FailDelete = true;
            Assert.False(store.Delete("gi"));
            Assert.True(File.Exists(path));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Reparse_quarantine_delete_keeps_durable_pending_marker_until_retry()
    {
        var root = NewRoot("nyx-role-quarantine-tests-");
        try
        {
            var boundary = new FaultFileBoundary();
            var protector = new FaultProtector();
            var bindings = new PublisherRoleBindingStore(root, protector, boundary);
            var snapshots = new PublisherResourceSnapshotStore(root, protector);
            var revocations = new PublisherConsentRevocationStore(root);
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            Assert.True(bindings.Save("gi", binding));

            boundary.ReparsePath = BindingPath(root, "gi");
            Assert.False(PublisherQuarantineCleanupStore.TryClean(
                "HoYoLAB",
                revocations,
                bindings,
                snapshots));
            Assert.True(revocations.IsPending("HoYoLAB"));

            boundary.ReparsePath = null;
            Assert.True(PublisherQuarantineCleanupStore.TryClean(
                "HoYoLAB",
                revocations,
                bindings,
                snapshots));
            Assert.False(revocations.IsPending("HoYoLAB"));
            Assert.Null(bindings.TryLoad("gi"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void V1_binding_loads_as_a_record_with_derived_region_and_can_be_enriched_to_v2()
    {
        var root = NewRoot("nyx-v1-role-record-tests-");
        try
        {
            var store = new PublisherRoleBindingStore(root, new FaultProtector());
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            Assert.True(store.Save("gi", binding));

            var v1 = Assert.IsType<PublisherRoleRecord>(store.TryLoadRecord("gi"));
            Assert.Equal(binding, v1.Binding);
            Assert.Null(v1.Nickname);
            Assert.Equal("Europe", v1.ReadableRegion);
            var identity = HoyoLabAccountIdentity.Create(
                "gi",
                new(
                    "0123456789abcdef0123456789abcdef",
                    "Old account",
                    IsLegacy: false,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    RemovalPending: false),
                v1);
            Assert.Equal("Old account", identity.DisplayName);
            Assert.Equal(binding.RoleId, identity.FullUid);
            Assert.Equal("Europe", identity.ReadableRegion);

            var enriched = v1 with { Nickname = "Lumine" };
            Assert.True(store.SaveRecord("gi", enriched));
            Assert.Equal(enriched, store.TryLoadRecord("gi"));
            Assert.Equal(binding, store.TryLoad("gi"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void V2_record_round_trips_and_rejects_invalid_nickname_or_region()
    {
        var root = NewRoot("nyx-v2-role-record-tests-");
        try
        {
            var store = new PublisherRoleBindingStore(root, new FaultProtector());
            var binding = new PublisherRoleBinding("123456789", "prod_official_eur");
            var record = new PublisherRoleRecord(binding, "Trailblazer", "Europe");
            Assert.True(store.SaveRecord("hsr", record));
            Assert.Equal(record, store.TryLoadRecord("hsr"));

            Assert.False(store.SaveRecord(
                "hsr",
                record with { Nickname = "bad\nname" }));
            Assert.False(store.SaveRecord(
                "hsr",
                record with { ReadableRegion = "Attacker supplied" }));
            Assert.Equal(record, store.TryLoadRecord("hsr"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Ancestor_reparse_fails_closed_and_legacy_save_preserves_v2_metadata()
    {
        var root = NewRoot("nyx-role-ancestor-tests-");
        try
        {
            Directory.CreateDirectory(root);
            var boundary = new FaultFileBoundary { ReparsePath = root };
            var store = new PublisherRoleBindingStore(root, new FaultProtector(), boundary);
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            Assert.False(store.Save("gi", binding));
            Assert.Null(store.TryLoadRecord("gi"));

            boundary.ReparsePath = null;
            var record = new PublisherRoleRecord(binding, "Lumine", "Europe");
            Assert.True(store.SaveRecord("gi", record));
            Assert.True(store.Save("gi", binding));
            Assert.Equal(record, store.TryLoadRecord("gi"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Concurrent_legacy_same_binding_saves_cannot_overwrite_v2_metadata()
    {
        var root = NewRoot("nyx-role-rmw-race-tests-");
        try
        {
            var store = new PublisherRoleBindingStore(root, new FaultProtector());
            var binding = new PublisherRoleBinding("123456789", "os_euro");
            var record = new PublisherRoleRecord(binding, "Lumine", "Europe");
            Assert.True(store.Save("gi", binding));

            var legacySaves = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => store.Save("gi", binding)));
            var recordSaves = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => store.SaveRecord("gi", record)));
            Assert.All(await Task.WhenAll(legacySaves.Concat(recordSaves)), Assert.True);

            Assert.Equal(record, store.TryLoadRecord("gi"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string NewRoot(string prefix) => Path.Combine(
        Path.GetTempPath(),
        prefix + Guid.NewGuid().ToString("N"));

    private static string BindingPath(string root, string gameId) =>
        Path.Combine(root, ".protected-role-bindings", gameId + ".bin");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FaultProtector : IPublisherRoleBindingProtector
    {
        public bool FailProtect { get; set; }
        public bool FailUnprotect { get; set; }
        public int? ProtectedLength { get; set; }
        public int UnprotectCalls { get; private set; }

        public byte[] Protect(byte[] plaintext)
        {
            if (FailProtect) throw new System.Security.Cryptography.CryptographicException();
            return ProtectedLength is { } length ? new byte[length] : [.. plaintext];
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            UnprotectCalls++;
            if (FailUnprotect) throw new System.Security.Cryptography.CryptographicException();
            return [.. ciphertext];
        }
    }

    private sealed class FaultFileBoundary : IPublisherRoleBindingFileBoundary
    {
        private readonly SystemPublisherRoleBindingFileBoundary inner = new();

        public string? ReparsePath { get; set; }
        public bool FailMove { get; set; }
        public bool FailDelete { get; set; }

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public bool EntryExists(string path) =>
            string.Equals(path, ReparsePath, StringComparison.OrdinalIgnoreCase)
                || inner.EntryExists(path);

        public bool Exists(string path) => inner.Exists(path);

        public FileAttributes GetAttributes(string path) =>
            string.Equals(path, ReparsePath, StringComparison.Ordinal)
                ? FileAttributes.ReparsePoint
                : inner.GetAttributes(path);

        public FileStream OpenRead(string path) => inner.OpenRead(path);

        public FileStream CreateNewWriteThrough(string path) =>
            inner.CreateNewWriteThrough(path);

        public void MoveOverwrite(string source, string destination)
        {
            if (FailMove) throw new IOException("Injected interrupted move.");
            inner.MoveOverwrite(source, destination);
        }

        public void Delete(string path)
        {
            if (FailDelete) throw new UnauthorizedAccessException("Injected delete denial.");
            inner.Delete(path);
        }
    }
}
