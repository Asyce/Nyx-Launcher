using Nyx.Desktop.Infrastructure.PublisherGames;

namespace Nyx.Desktop.Tests.PublisherGames;

public sealed class EndfieldInstallRootStoreTests
{
    [Fact]
    public void One_canonical_local_drive_root_round_trips_under_one_fixed_key()
    {
        var values = new Dictionary<string, object>();
        var store = new EndfieldInstallRootStore(values);

        Assert.True(store.TrySave(@"C:\Games\GRYPHLINK\"));

        Assert.Equal(@"C:\Games\GRYPHLINK", store.Load());
        Assert.Single(values);
        Assert.Equal(@"C:\Games\GRYPHLINK", values[EndfieldInstallRootStore.SettingName]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(@"GRYPHLINK")]
    [InlineData(@"\\server\GRYPHLINK")]
    [InlineData(@"\\?\C:\Games\GRYPHLINK")]
    [InlineData(@"\\.\C:\Games\GRYPHLINK")]
    [InlineData(@"C:\Games\..\GRYPHLINK")]
    [InlineData(@"C:\Games\GRYPHLINK ")]
    [InlineData(@"file:///C:/Games/GRYPHLINK")]
    public void Generic_relative_remote_device_noncanonical_or_ambiguous_values_are_rejected(
        string? value)
    {
        var values = new Dictionary<string, object>
        {
            [EndfieldInstallRootStore.SettingName] = @"C:\Old",
            ["unrelated"] = "preserved",
        };
        var store = new EndfieldInstallRootStore(values);

        Assert.False(store.TrySave(value));

        Assert.Null(store.Load());
        Assert.False(values.ContainsKey(EndfieldInstallRootStore.SettingName));
        Assert.Equal("preserved", values["unrelated"]);
    }

    [Fact]
    public void Oversized_or_wrong_typed_persisted_values_are_cleared()
    {
        var values = new Dictionary<string, object>
        {
            [EndfieldInstallRootStore.SettingName] = new string('x', 2049),
        };
        var store = new EndfieldInstallRootStore(values);

        Assert.Null(store.Load());
        Assert.Empty(values);

        values[EndfieldInstallRootStore.SettingName] = 42;
        Assert.Null(store.Load());
        Assert.Empty(values);
    }

    [Fact]
    public void Hostile_settings_fail_closed_without_leaking_a_root()
    {
        var store = new EndfieldInstallRootStore(new ThrowingSettings());

        Assert.Null(store.Load());
        Assert.False(store.TrySave(@"C:\Games\GRYPHLINK"));
        store.Clear();
    }

    [Fact]
    public void Automatic_save_only_fills_an_empty_store_and_never_replaces_manual_choice()
    {
        var values = new Dictionary<string, object>();
        var store = new EndfieldInstallRootStore(values);

        Assert.True(store.TrySaveIfEmpty(@"C:\Games\GRYPHLINK"));
        Assert.False(store.TrySaveIfEmpty(@"D:\Other\GRYPHLINK"));

        Assert.Equal(@"C:\Games\GRYPHLINK", store.Load());
    }

    [Fact]
    public void Invalid_automatic_candidate_never_clears_an_existing_manual_choice()
    {
        var values = new Dictionary<string, object>();
        var store = new EndfieldInstallRootStore(values);
        Assert.True(store.TrySave(@"C:\Games\GRYPHLINK"));

        Assert.False(store.TrySaveIfEmpty(@"relative\GRYPHLINK"));

        Assert.Equal(@"C:\Games\GRYPHLINK", store.Load());
    }

    [Fact]
    public async Task Concurrent_automatic_candidates_commit_exactly_one_root()
    {
        var values = new Dictionary<string, object>();
        var store = new EndfieldInstallRootStore(values);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(index => Task.Run(() => store.TrySaveIfEmpty($@"C:\Games{index}\GRYPHLINK"))));

        Assert.Equal(1, results.Count(saved => saved));
        Assert.Matches(@"^C:\\Games\d+\\GRYPHLINK$", store.Load());
    }

    private sealed class ThrowingSettings : IDictionary<string, object>
    {
        public object this[string key]
        {
            get => throw new InvalidOperationException();
            set => throw new InvalidOperationException();
        }

        public ICollection<string> Keys => throw new InvalidOperationException();
        public ICollection<object> Values => throw new InvalidOperationException();
        public int Count => throw new InvalidOperationException();
        public bool IsReadOnly => false;
        public void Add(string key, object value) => throw new InvalidOperationException();
        public void Add(KeyValuePair<string, object> item) => throw new InvalidOperationException();
        public void Clear() => throw new InvalidOperationException();
        public bool Contains(KeyValuePair<string, object> item) => throw new InvalidOperationException();
        public bool ContainsKey(string key) => throw new InvalidOperationException();
        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) => throw new InvalidOperationException();
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => throw new InvalidOperationException();
        public bool Remove(string key) => throw new InvalidOperationException();
        public bool Remove(KeyValuePair<string, object> item) => throw new InvalidOperationException();
        public bool TryGetValue(string key, out object value) => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
