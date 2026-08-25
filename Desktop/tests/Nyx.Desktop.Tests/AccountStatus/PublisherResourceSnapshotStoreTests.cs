using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Infrastructure.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class PublisherResourceSnapshotStoreTests
{
    [Fact]
    public void Save_load_and_delete_round_trip_only_for_the_exact_role()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-resource-store-" + Guid.NewGuid());
        try
        {
            var store = new PublisherResourceSnapshotStore(root, new CopyProtector());
            var role = new PublisherRoleBinding("123456789", "prod_official_eur");
            var observed = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);
            var snapshot = new PublisherResourceSnapshot(
                "hsr",
                "Trailblaze Power",
                221,
                300,
                observed,
                RecoverySeconds: 4_200,
                Reserve: 840);

            Assert.True(store.Save(snapshot, role));
            var loaded = Assert.IsType<PublisherResourceSnapshot>(store.TryLoad("hsr", role));
            Assert.Equal(snapshot with { IsStale = true }, loaded);
            Assert.Null(store.TryLoad(
                "hsr",
                new PublisherRoleBinding("987654321", "prod_official_eur")));
            Assert.True(store.Delete("hsr"));
            Assert.Null(store.TryLoad("hsr", role));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_rejects_invalid_or_unbounded_snapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-resource-store-" + Guid.NewGuid());
        try
        {
            var store = new PublisherResourceSnapshotStore(root, new CopyProtector());
            var role = new PublisherRoleBinding("123456789", "os_euro");

            Assert.False(store.Save(
                new("gi", "Original Resin", 201, 200, DateTimeOffset.UtcNow),
                role));
            Assert.False(store.Save(
                new("gi", new string('x', 65), 10, 200, DateTimeOffset.UtcNow),
                role));
            Assert.False(store.Save(
                new("gi", "Original Resin", 10, 200, DateTimeOffset.UtcNow, RecoverySeconds: int.MaxValue),
                role));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Refresh_policy_uses_five_minutes_for_selected_and_background_games()
    {
        var now = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

        Assert.True(PublisherResourceRefreshPolicy.IsDue(null, now, selected: true));
        Assert.False(PublisherResourceRefreshPolicy.IsDue(
            now - TimeSpan.FromMinutes(4),
            now,
            selected: true));
        Assert.True(PublisherResourceRefreshPolicy.IsDue(
            now - TimeSpan.FromMinutes(5),
            now,
            selected: true));
        Assert.True(PublisherResourceRefreshPolicy.IsDue(
            now - TimeSpan.FromMinutes(5),
            now,
            selected: false));
        Assert.False(PublisherResourceRefreshPolicy.IsDue(
            now - TimeSpan.FromMinutes(4),
            now,
            selected: false));
        Assert.True(PublisherResourceRefreshPolicy.IsDue(
            now,
            now,
            selected: false,
            force: true));
    }

    [Fact]
    public void Freshness_policy_uses_the_five_minute_boundary_and_rejects_future_observations()
    {
        var now = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

        Assert.True(PublisherResourceRefreshPolicy.IsFresh(
            now - TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1),
            now));
        Assert.False(PublisherResourceRefreshPolicy.IsFresh(
            now - TimeSpan.FromMinutes(5),
            now));
        Assert.False(PublisherResourceRefreshPolicy.IsFresh(
            now + TimeSpan.FromTicks(1),
            now));
    }

    [Fact]
    public void Ancestor_reparse_rejects_resource_load_save_and_delete()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-resource-reparse-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(root);
            var boundary = new ReparseBoundary(root);
            var store = new PublisherResourceSnapshotStore(root, new CopyProtector(), boundary);
            var role = new PublisherRoleBinding("123456789", "os_euro");
            var snapshot = new PublisherResourceSnapshot(
                "gi", "Original Resin", 10, 200, DateTimeOffset.UtcNow);

            Assert.False(store.Save(snapshot, role));
            Assert.Null(store.TryLoad("gi", role));
            Assert.False(store.Delete("gi"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CopyProtector : IPublisherRoleBindingProtector
    {
        public byte[] Protect(byte[] plaintext) => [.. plaintext];
        public byte[] Unprotect(byte[] ciphertext) => [.. ciphertext];
    }

    private sealed class ReparseBoundary(string reparsePath) : IPublisherRoleBindingFileBoundary
    {
        private readonly SystemPublisherRoleBindingFileBoundary inner = new();
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public bool EntryExists(string path) =>
            string.Equals(path, reparsePath, StringComparison.OrdinalIgnoreCase)
                || inner.EntryExists(path);
        public bool Exists(string path) => inner.Exists(path);
        public FileAttributes GetAttributes(string path) =>
            string.Equals(path, reparsePath, StringComparison.OrdinalIgnoreCase)
                ? inner.GetAttributes(path) | FileAttributes.ReparsePoint
                : inner.GetAttributes(path);
        public FileStream OpenRead(string path) => inner.OpenRead(path);
        public FileStream CreateNewWriteThrough(string path) => inner.CreateNewWriteThrough(path);
        public void MoveOverwrite(string source, string destination) =>
            inner.MoveOverwrite(source, destination);
        public void Delete(string path) => inner.Delete(path);
    }
}
