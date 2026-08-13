using Nyx.Desktop.Core.Hoyo;
using Nyx.Desktop.Core.Games;
using Nyx.Desktop.Infrastructure.Genshin;
using Nyx.Desktop.Infrastructure.Hoyo;

namespace Nyx.Desktop.Tests.Hoyo;

public sealed class HoyoDiscoveryAndHandoffTests
{
    [Theory]
    [InlineData(HoyoCurrentGameRecord.HsrGlobal, "hsr", "hkrpg_global")]
    [InlineData(HoyoCurrentGameRecord.ZzzGlobal, "zzz", "nap_global")]
    public void Exact_current_record_is_inspected_once(
        HoyoCurrentGameRecord record,
        string gameId,
        string gameBiz)
    {
        var root = CanonicalFixturePath(gameId);
        var reader = new FakeRegistryReader([new(root, gameBiz)]);
        var inspector = new FakeInspector(
            new(gameId, HoyoInspectionStatus.Ready, HoyoInspectionReason.None, root, "1.0.0"));

        var result = new HoyoCurrentUserDiscovery(reader, inspector).Discover(record);

        Assert.Equal(HoyoInspectionStatus.Ready, result.Status);
        Assert.Equal([(gameId, root)], inspector.Calls);
        Assert.Equal([record, record], reader.Calls);
    }

    [Fact]
    public void Missing_current_record_does_not_try_a_legacy_fallback()
    {
        var reader = new FakeRegistryReader([]);
        var inspector = new FakeInspector(
            new("hsr", HoyoInspectionStatus.Ready, HoyoInspectionReason.None));

        var result = new HoyoCurrentUserDiscovery(reader, inspector)
            .Discover(HoyoCurrentGameRecord.HsrGlobal);

        Assert.Equal(HoyoInspectionReason.CurrentRecordMissing, result.Reason);
        Assert.Single(reader.Calls);
        Assert.Empty(inspector.Calls);
    }

    [Fact]
    public void Multiple_current_candidates_are_ambiguous()
    {
        var root = CanonicalFixturePath("hsr");
        var candidates = new[]
        {
            new HoyoRegistryCandidate(root, "hkrpg_global"),
            new HoyoRegistryCandidate(root, "hkrpg_global"),
        };
        var inspector = new FakeInspector(new("hsr", HoyoInspectionStatus.Ready, HoyoInspectionReason.None));

        var result = new HoyoCurrentUserDiscovery(new FakeRegistryReader(candidates), inspector)
            .Discover(HoyoCurrentGameRecord.HsrGlobal);

        Assert.Equal(HoyoInspectionReason.AmbiguousCandidates, result.Reason);
        Assert.Empty(inspector.Calls);
    }

    [Theory]
    [InlineData(HoyoCurrentGameRecord.HsrGlobal, "nap_global")]
    [InlineData(HoyoCurrentGameRecord.ZzzGlobal, "hkrpg_global")]
    [InlineData(HoyoCurrentGameRecord.HsrGlobal, "Hkrpg_global")]
    public void Wrong_or_nonexact_game_biz_is_rejected(
        HoyoCurrentGameRecord record,
        string gameBiz)
    {
        var inspector = new FakeInspector(new("hsr", HoyoInspectionStatus.Ready, HoyoInspectionReason.None));
        var candidate = new HoyoRegistryCandidate(CanonicalFixturePath("game"), gameBiz);

        var result = new HoyoCurrentUserDiscovery(new FakeRegistryReader([candidate]), inspector)
            .Discover(record);

        Assert.Equal(HoyoInspectionReason.CurrentRecordGameBizMismatch, result.Reason);
        Assert.Empty(inspector.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_install_path_is_malformed(string? path)
    {
        var inspector = new FakeInspector(new("zzz", HoyoInspectionStatus.Ready, HoyoInspectionReason.None));
        var candidate = new HoyoRegistryCandidate(path, "nap_global");

        var result = new HoyoCurrentUserDiscovery(new FakeRegistryReader([candidate]), inspector)
            .Discover(HoyoCurrentGameRecord.ZzzGlobal);

        Assert.Equal(HoyoInspectionReason.CurrentRecordMalformed, result.Reason);
        Assert.Empty(inspector.Calls);
    }

    [Fact]
    public void Current_record_pointing_to_missing_directory_is_stale_and_never_falls_back()
    {
        var root = CanonicalFixturePath("moved");
        var reader = new FakeRegistryReader([new(root, "nap_global")]);
        var inspector = new FakeInspector(
            new("zzz", HoyoInspectionStatus.NotFound, HoyoInspectionReason.DirectoryNotFound, root));

        var result = new HoyoCurrentUserDiscovery(reader, inspector)
            .Discover(HoyoCurrentGameRecord.ZzzGlobal);

        Assert.Equal(HoyoInspectionReason.CurrentRecordStale, result.Reason);
        Assert.Equal(2, reader.Calls.Count);
        Assert.Single(inspector.Calls);
    }

    [Fact]
    public async Task Current_record_changed_while_inspection_is_paused_fails_closed()
    {
        var initialRoot = CanonicalFixturePath("hsr-initial");
        var changedRoot = CanonicalFixturePath("hsr-changed");
        var reader = new FakeRegistryReader([new(initialRoot, "hkrpg_global")]);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var inspector = new PausingInspector(entered, release, new(
            "hsr",
            HoyoInspectionStatus.Ready,
            HoyoInspectionReason.None,
            initialRoot,
            "7.0.0"));
        var discovery = new HoyoCurrentUserDiscovery(reader, inspector);

        var discoveryTask = Task.Run(() => discovery.Discover(HoyoCurrentGameRecord.HsrGlobal));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        reader.Candidates = [new(changedRoot, "hkrpg_global")];
        release.Set();
        var result = await discoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HoyoInspectionStatus.NeedsReview, result.Status);
        Assert.Equal(HoyoInspectionReason.TargetChangedDuringInspection, result.Reason);
        Assert.Equal(2, reader.Calls.Count);
        Assert.Single(inspector.Calls);
    }

    [Theory]
    [InlineData("hsr", "--game=hkrpg_global")]
    [InlineData("zzz", "--game=nap_global")]
    public void Handoff_has_one_sealed_exact_page_argument(string gameId, string expectedArgument)
    {
        var installation = new ValidatedHoyoPlayInstallation(
            CanonicalFixturePath("hoyoplay"),
            Path.Combine(CanonicalFixturePath("hoyoplay"), "launcher.exe"),
            "1.8.0.0");

        var request = HoyoPlayHandoffFactory.Create(gameId, installation);

        Assert.Equal(gameId, request.Game.Id);
        Assert.Same(installation, request.Installation);
        Assert.Equal([expectedArgument], request.Arguments);
        Assert.True(request.RequiresUserInteraction);
        Assert.False(request.AllowsDirectUpdate);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)request.Arguments).Add("--extra"));
    }

    [Fact]
    public void Genshin_handoff_is_sealed_visible_and_argument_free()
    {
        var installation = new ValidatedHoyoPlayInstallation(
            CanonicalFixturePath("hoyoplay"),
            Path.Combine(CanonicalFixturePath("hoyoplay"), "launcher.exe"),
            "1.8.0.0");

        var request = HoyoPlayHandoffFactory.Create("gi", installation);

        Assert.Equal("gi", request.Game.Id);
        Assert.Empty(request.Arguments);
        Assert.True(request.RequiresUserInteraction);
        Assert.False(request.AllowsDirectUpdate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("genshin")]
    [InlineData("wuwa")]
    [InlineData("hkrpg_global")]
    [InlineData("--game=nap_global")]
    [InlineData("https://example.invalid")]
    [InlineData(@"C:\lookalike.exe")]
    public void Arbitrary_handoff_targets_are_rejected(string? target)
    {
        var installation = new ValidatedHoyoPlayInstallation(
            CanonicalFixturePath("hoyoplay"),
            Path.Combine(CanonicalFixturePath("hoyoplay"), "launcher.exe"),
            "1.8.0.0");

        Assert.Throws<UnsupportedGameException>(() => HoyoPlayHandoffFactory.Create(target, installation));
    }

    [Fact]
    public void Validation_tokens_and_handoffs_have_no_public_constructors()
    {
        Assert.Empty(typeof(ValidatedHoyoPlayInstallation).GetConstructors());
        Assert.Empty(typeof(HoyoPlayHandoffRequest).GetConstructors());
    }

    [Fact]
    public void Public_hoyo_entry_points_expose_only_production_bound_parameterless_constructors()
    {
        AssertOnlyPublicParameterlessConstructor(typeof(HoyoPlayGlobalValidator));
        AssertOnlyPublicParameterlessConstructor(typeof(HoyoGameIdentityAdapter));
        AssertOnlyPublicParameterlessConstructor(typeof(HoyoCurrentUserDiscovery));
        Assert.False(typeof(IHoyoCurrentUserRegistryReader).IsPublic);
        Assert.False(typeof(IHoyoGameCandidateInspector).IsPublic);
        Assert.False(typeof(HoyoRegistryCandidate).IsPublic);
        Assert.False(typeof(WindowsHoyoCurrentUserRegistryReader).IsPublic);

        var exportedHoyoTypes = typeof(HoyoPlayGlobalValidator).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == "Nyx.Desktop.Infrastructure.Hoyo");
        Assert.DoesNotContain(
            exportedHoyoTypes.SelectMany(type => type.GetConstructors()),
            constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IExecutableMetadataReader)
                || parameter.ParameterType == typeof(IDriveTypeReader)
                || parameter.ParameterType == typeof(IHoyoCurrentUserRegistryReader)
                || parameter.ParameterType == typeof(IHoyoGameCandidateInspector)));
    }

    private static string CanonicalFixturePath(string leaf) =>
        Path.Combine(Path.GetTempPath(), $"nyx-{leaf}");

    private static void AssertOnlyPublicParameterlessConstructor(Type type)
    {
        var constructors = type.GetConstructors();
        var constructor = Assert.Single(constructors);
        Assert.Empty(constructor.GetParameters());
        Assert.DoesNotContain(
            type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public),
            candidate => candidate.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IExecutableMetadataReader)
                || parameter.ParameterType == typeof(IDriveTypeReader)
                || parameter.ParameterType == typeof(IHoyoCurrentUserRegistryReader)
                || parameter.ParameterType == typeof(IHoyoGameCandidateInspector)));
    }

    private sealed class FakeRegistryReader(IReadOnlyList<HoyoRegistryCandidate> candidates)
        : IHoyoCurrentUserRegistryReader
    {
        public IReadOnlyList<HoyoRegistryCandidate> Candidates { get; set; } = candidates;

        public List<HoyoCurrentGameRecord> Calls { get; } = [];

        public IReadOnlyList<HoyoRegistryCandidate> Read(HoyoCurrentGameRecord record)
        {
            Calls.Add(record);
            return Candidates;
        }
    }

    private sealed class FakeInspector(HoyoGameInspectionResult result) : IHoyoGameCandidateInspector
    {
        public List<(string GameId, string Root)> Calls { get; } = [];

        public HoyoGameInspectionResult Inspect(string gameId, string root)
        {
            Calls.Add((gameId, root));
            return result;
        }
    }

    private sealed class PausingInspector(
        ManualResetEventSlim entered,
        ManualResetEventSlim release,
        HoyoGameInspectionResult result) : IHoyoGameCandidateInspector
    {
        public List<(string GameId, string Root)> Calls { get; } = [];

        public HoyoGameInspectionResult Inspect(string gameId, string root)
        {
            Calls.Add((gameId, root));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return result;
        }
    }
}
