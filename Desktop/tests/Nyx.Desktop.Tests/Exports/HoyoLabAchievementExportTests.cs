using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.AccountStatus;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.Exports;

public sealed class HoyoLabAchievementExportTests
{
    private static readonly IReadOnlySet<long> CurrentFixtureIds =
        new HashSet<long>([4010101, 4055301, 4093621]);

    [Fact]
    public void Parser_accepts_only_strict_sorted_id_result()
    {
        var result = HoyoLabHsrAchievementResultParser.Parse(
            ScriptResult("""{"state":"ok","ids":[4010101,4055301,4093621],"region":"prod_official_eur","uid":"123456789"}"""),
            CurrentFixtureIds);

        Assert.Equal([4010101L, 4055301L, 4093621L], result.AchievementIds);
        Assert.Equal("prod_official_eur", result.Role.Server);
        Assert.Equal("123456789", result.Role.RoleId);
    }

    [Fact]
    public void Parser_rejects_a_result_that_does_not_match_the_saved_role()
    {
        var expected = new PublisherRoleBinding("123456789", "prod_official_eur");

        var exception = Assert.Throws<ExportProviderException>(() =>
            HoyoLabHsrAchievementResultParser.Parse(
                ScriptResult("""{"state":"ok","ids":[4010101],"region":"prod_official_asia","uid":"987654321"}"""),
                CurrentFixtureIds,
                expected));

        Assert.Equal("hoyolab-role-selection-required", exception.Code);
    }

    [Theory]
    [InlineData("""{"state":"ok","ids":[2,1],"region":"prod_official_eur","uid":"123"}""")]
    [InlineData("""{"state":"ok","ids":[1,1],"region":"prod_official_eur","uid":"123"}""")]
    [InlineData("""{"state":"ok","ids":[0],"region":"prod_official_eur","uid":"123"}""")]
    [InlineData("""{"state":"ok","ids":[9007199254740992],"region":"prod_official_eur","uid":"123"}""")]
    [InlineData("""{"state":"ok","ids":["1"],"region":"prod_official_eur","uid":"123"}""")]
    [InlineData("""{"state":"ok","ids":[],"region":"unknown","uid":"123"}""")]
    [InlineData("""{"state":"invalid","ids":[],"region":"","uid":""}""")]
    public void Parser_rejects_unfamiliar_or_sensitive_results(string payload)
    {
        var exception = Assert.Throws<ExportProviderException>(
            () => HoyoLabHsrAchievementResultParser.Parse(
                ScriptResult(payload),
                CurrentFixtureIds));

        Assert.Equal("hoyolab-response-invalid", exception.Code);
    }

    [Theory]
    [InlineData("login-required", "hoyolab-login-required")]
    [InlineData("timed-out", "timed-out")]
    [InlineData("login-request", "hoyolab-login-request-failed")]
    [InlineData("login-processing", "hoyolab-login-processing-failed")]
    [InlineData("login-response", "hoyolab-login-response-invalid")]
    [InlineData("login-envelope", "hoyolab-login-envelope-invalid")]
    [InlineData("login-retcode", "hoyolab-login-retcode-failed")]
    [InlineData("login-data", "hoyolab-login-data-invalid")]
    [InlineData("login-binding", "hoyolab-login-binding-invalid")]
    [InlineData("role-request", "hoyolab-role-request-failed")]
    [InlineData("role-processing", "hoyolab-role-processing-failed")]
    [InlineData("role-response", "hoyolab-role-response-invalid")]
    [InlineData("role-envelope", "hoyolab-role-envelope-invalid")]
    [InlineData("role-retcode", "hoyolab-role-retcode-failed")]
    [InlineData("role-data", "hoyolab-role-data-invalid")]
    [InlineData("role-shape", "hoyolab-role-shape-invalid")]
    [InlineData("role-row", "hoyolab-role-row-invalid")]
    [InlineData("role-duplicate", "hoyolab-role-duplicate-invalid")]
    [InlineData("role-none", "hoyolab-role-none")]
    [InlineData("role-multiple", "hoyolab-role-selection-required")]
    [InlineData("role-changed", "hoyolab-role-selection-required")]
    [InlineData("session-chunks", "hoyolab-session-chunks-unavailable")]
    [InlineData("session-require", "hoyolab-session-require-unavailable")]
    [InlineData("session-vue", "hoyolab-session-vue-unavailable")]
    [InlineData("session-missing", "hoyolab-session-client-unavailable")]
    [InlineData("session-account", "hoyolab-session-account-unavailable")]
    [InlineData("session-role", "hoyolab-session-role-unavailable")]
    [InlineData("session-role-setter", "hoyolab-session-role-setter-unavailable")]
    [InlineData("session-role-bind", "hoyolab-session-role-bind-failed")]
    [InlineData("session-role-region", "hoyolab-session-role-region-mismatch")]
    [InlineData("session-role-uid", "hoyolab-session-role-uid-mismatch")]
    [InlineData("list-request", "hoyolab-list-request-failed")]
    [InlineData("list-client", "hoyolab-list-client-unavailable")]
    [InlineData("list-processing", "hoyolab-list-processing-failed")]
    [InlineData("list-response", "hoyolab-list-response-invalid")]
    [InlineData("list-envelope", "hoyolab-list-envelope-invalid")]
    [InlineData("list-retcode", "hoyolab-list-retcode-failed")]
    [InlineData("list-data", "hoyolab-list-data-invalid")]
    [InlineData("list-shape", "hoyolab-list-shape-invalid")]
    [InlineData("list-row", "hoyolab-list-row-invalid")]
    [InlineData("list-duplicate", "hoyolab-list-duplicate-invalid")]
    public void Parser_maps_only_reviewed_safe_failures(string state, string expectedCode)
    {
        var exception = Assert.Throws<ExportProviderException>(
            () => HoyoLabHsrAchievementResultParser.Parse(
                ScriptResult($$"""{"state":"{{state}}","ids":[],"region":"","uid":""}"""),
                CurrentFixtureIds));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Theory]
    [InlineData("login-retcode:-100", "hoyolab-login-retcode--100")]
    [InlineData("login-retcode:0", "hoyolab-login-retcode-0")]
    [InlineData("list-retcode:-100", "hoyolab-list-retcode--100")]
    public void Parser_preserves_only_bounded_numeric_retcode_diagnostics(
        string state,
        string expectedCode)
    {
        var exception = Assert.Throws<ExportProviderException>(
            () => HoyoLabHsrAchievementResultParser.Parse(
                ScriptResult($$"""{"state":"{{state}}","ids":[],"region":"","uid":""}"""),
                CurrentFixtureIds));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task Writer_creates_atomic_pengo_v1_file_without_account_data()
    {
        using var temp = new TemporaryDirectory();
        var instant = new DateTimeOffset(2026, 7, 27, 2, 0, 0, TimeSpan.Zero);
        var writer = new PengoAchievementExportWriter(
            new PengoAchievementCatalogReader(PackagedCatalogPath),
            new FixedTimeProvider(instant),
            () => "safe1234");

        var artifact = await writer.WriteAsync(
            "hsr",
            AchievementCatalogVersions.StarRail,
            [4010101, 4055301],
            new(
                AchievementAccountBinding.CurrentScheme,
                "fixture_binding_value_1234",
                "prod_official_eur"),
            temp.Path,
            UnconditionalAchievementExportPublishAuthority.Instance,
            CancellationToken.None);

        Assert.Equal(2, artifact.ItemCount);
        Assert.Equal("pengo-achievements-v1", artifact.Format);
        Assert.NotNull(artifact.OutputPath);
        Assert.True(File.Exists(artifact.OutputPath));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(artifact.OutputPath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(artifact.OutputPath));
        var root = document.RootElement;
        Assert.Equal("pengo-achievements", root.GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("hsr", root.GetProperty("game").GetString());
        Assert.Equal(AchievementCatalogVersions.StarRail, root.GetProperty("catalogVersion").GetString());
        var binding = root.GetProperty("accountBinding");
        Assert.Equal(AchievementAccountBinding.CurrentScheme, binding.GetProperty("scheme").GetString());
        Assert.Equal("fixture_binding_value_1234", binding.GetProperty("value").GetString());
        Assert.Equal("prod_official_eur", binding.GetProperty("region").GetString());
        Assert.Equal(2, root.GetProperty("achievements").GetArrayLength());
        Assert.False(root.TryGetProperty("uid", out _));
        Assert.False(root.TryGetProperty("region", out _));
        Assert.False(root.TryGetProperty("cookie", out _));
    }

    [Fact]
    public async Task Checked_in_current_catalog_accepts_current_hsr_ids()
    {
        var catalog = await new PengoAchievementCatalogReader(PackagedCatalogPath)
            .ReadCurrentHsrAsync(
                AchievementCatalogVersions.StarRail,
                CancellationToken.None);

        Assert.Equal("hsr", catalog.GameId);
        Assert.Equal(AchievementCatalogVersions.StarRail, catalog.ExportVersion);
        Assert.Equal(1869, catalog.AchievementIds.Count);
        Assert.Contains(4010101, catalog.AchievementIds);
        Assert.Contains(4055301, catalog.AchievementIds);
        Assert.Contains(4093621, catalog.AchievementIds);
    }

    [Theory]
    [InlineData("missing", "achievement-catalog-invalid")]
    [InlineData("malformed", "achievement-catalog-invalid")]
    [InlineData("stale", "achievement-catalog-stale")]
    [InlineData("unknown-id", "achievement-catalog-id-unknown")]
    public async Task Catalog_failure_happens_before_any_artifact_or_handoff_admission(
        string failureKind,
        string expectedCode)
    {
        using var catalogRoot = new TemporaryDirectory();
        using var outputRoot = new TemporaryDirectory();
        var catalogPath = Path.Combine(catalogRoot.Path, "catalog.json");
        if (failureKind == "malformed")
            await File.WriteAllTextAsync(catalogPath, """{"schemaVersion":""");
        else if (failureKind == "stale")
            await File.WriteAllTextAsync(catalogPath, Catalog("4.3", 4010101));
        else if (failureKind == "unknown-id")
            await File.WriteAllTextAsync(catalogPath, Catalog("4.4", 4010101));

        var writer = new PengoAchievementExportWriter(
            new PengoAchievementCatalogReader(catalogPath),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-27T02:00:00Z")),
            () => "safe1234");
        var ids = failureKind == "unknown-id"
            ? new long[] { 4010101, 4055301 }
            : [4010101];

        var failure = await Assert.ThrowsAsync<ExportProviderException>(
            async () => await writer.WriteAsync(
                "hsr",
                AchievementCatalogVersions.StarRail,
                ids,
                new(
                    AchievementAccountBinding.CurrentScheme,
                    "fixture_binding_value_1234",
                    "prod_official_eur"),
                outputRoot.Path,
                UnconditionalAchievementExportPublishAuthority.Instance,
                CancellationToken.None));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Empty(Directory.GetFileSystemEntries(
            outputRoot.Path,
            "*",
            SearchOption.AllDirectories));
        var impossibleArtifact = Path.Combine(
            outputRoot.Path,
            "Honkai Star Rail",
            "pengo-achievements-never-created.json");
        var handoffFailure = await Assert.ThrowsAsync<ExportProviderException>(
            async () => await new AchievementImportBridge().StartAsync(
                "hsr",
                impossibleArtifact));
        Assert.Equal("achievement-handoff-invalid", handoffFailure.Code);
    }

    [Fact]
    public async Task Revocation_after_final_write_denies_atomic_publish_and_leaves_no_artifact()
    {
        using var outputRoot = new TemporaryDirectory();
        var authority = new RejectAfterFinalWriteAuthority(outputRoot.Path);
        var writer = new PengoAchievementExportWriter(
            new PengoAchievementCatalogReader(PackagedCatalogPath),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-27T02:00:00Z")),
            () => "race1234");

        var failure = await Assert.ThrowsAsync<ExportProviderException>(
            async () => await writer.WriteAsync(
                "hsr",
                AchievementCatalogVersions.StarRail,
                [4010101, 4055301],
                new(
                    AchievementAccountBinding.CurrentScheme,
                    "fixture_binding_value_1234",
                    "prod_official_eur"),
                outputRoot.Path,
                authority,
                CancellationToken.None));

        Assert.Equal("achievement-publish-not-authorized", failure.Code);
        Assert.True(authority.SawCompleteClosedTemporary);
        Assert.Empty(Directory.GetFiles(
            outputRoot.Path,
            "*.tmp",
            SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(
            outputRoot.Path,
            "*.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Revocation_after_publish_suppresses_coordinator_success_and_handoff()
    {
        using var outputRoot = new TemporaryDirectory();
        var authority = new MutablePublishAuthority();
        var writer = new PengoAchievementExportWriter(
            new PengoAchievementCatalogReader(PackagedCatalogPath),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-27T02:00:00Z")),
            () => "race5678");
        var artifact = await writer.WriteAsync(
            "hsr",
            AchievementCatalogVersions.StarRail,
            [4010101],
            new(
                AchievementAccountBinding.CurrentScheme,
                "fixture_binding_value_1234",
                "prod_official_eur"),
            outputRoot.Path,
            authority,
            CancellationToken.None);
        await using var coordinator = new ExportCoordinator(
            new UnusedPullProvider(),
            new ArtifactProvider(artifact));

        var launch = await coordinator.RunForLaunchAsync(
            new ExportArmSnapshot("hsr", PullsArmed: false, AchievementsArmed: true),
            static _ => ValueTask.FromResult(true));
        var completed = await coordinator.WaitForCompletionAsync(launch.JobId);
        Assert.Equal(ExportTaskState.Succeeded, completed.Achievements.State);

        authority.Revoke();
        var revoked = coordinator.GetSnapshot(launch.JobId);
        var browserAdmissions = 0;
        if (revoked.Achievements.State is ExportTaskState.Succeeded
            && revoked.Achievements.Artifact is
            {
                IsHandoffCurrent: true,
                OutputPath: { Length: > 0 },
            })
            browserAdmissions++;

        Assert.Equal(ExportJobState.Failed, revoked.State);
        Assert.Equal(ExportTaskState.Failed, revoked.Achievements.State);
        Assert.Equal(
            "achievement-publish-not-authorized",
            revoked.Achievements.ErrorCode);
        Assert.Null(revoked.Achievements.Artifact);
        Assert.Equal(0, browserAdmissions);
        Assert.Empty(Directory.GetFiles(
            outputRoot.Path,
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Account_binding_is_stable_per_install_and_changes_for_another_account()
    {
        using var temp = new TemporaryDirectory();
        var secret = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        var first = new AchievementAccountBindingStore(
            temp.Path,
            new XorProtector(),
            () => secret.ToArray());
        var role = new PublisherRoleBinding("123456789", "prod_official_eur");

        var original = first.Derive("hsr", role);
        var reopened = new AchievementAccountBindingStore(
            temp.Path,
            new XorProtector(),
            () => throw new InvalidOperationException("The stored key should be reused."));
        var repeated = reopened.Derive("hsr", role);
        var otherAccount = reopened.Derive(
            "hsr",
            new PublisherRoleBinding("987654321", "prod_official_eur"));

        Assert.Equal(AchievementAccountBinding.CurrentScheme, original.Scheme);
        Assert.Equal(original, repeated);
        Assert.NotEqual(original.Value, otherAccount.Value);
        Assert.Equal("prod_official_eur", original.Region);
        Assert.DoesNotContain("123456789", original.Value, StringComparison.Ordinal);
        Assert.Equal(nameof(AchievementAccountBinding), original.ToString());
        var protectedBytes = File.ReadAllBytes(Path.Combine(
            temp.Path,
            "achievement-account-binding-key.bin"));
        Assert.False(protectedBytes.AsSpan().SequenceEqual(secret));
    }

    [Fact]
    public void Corrupt_stored_binding_key_fails_closed_instead_of_changing_identity()
    {
        using var temp = new TemporaryDirectory();
        File.WriteAllBytes(
            Path.Combine(temp.Path, "achievement-account-binding-key.bin"),
            [1, 2, 3]);
        var store = new AchievementAccountBindingStore(
            temp.Path,
            new XorProtector(),
            () => Enumerable.Repeat((byte)7, 32).ToArray());

        var failure = Assert.Throws<ExportProviderException>(() => store.Derive(
            "hsr",
            new PublisherRoleBinding("123456789", "prod_official_eur")));

        Assert.Equal("achievement-binding-unavailable", failure.Code);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(
            temp.Path,
            "achievement-account-binding-key.bin")));
    }

    [Fact]
    public async Task Router_sends_each_supported_game_to_its_reviewed_provider()
    {
        var genshin = new RecordingProvider();
        var starRail = new RecordingProvider();
        var router = new RoutedAchievementExportProvider(genshin, starRail);

        await router.StartAsync("gi", null, CancellationToken.None);
        await router.StartAsync("hsr", "C:\\safe", CancellationToken.None);
        var unsupported = await Assert.ThrowsAsync<ExportProviderException>(
            async () => await router.StartAsync("zzz", null, CancellationToken.None));

        Assert.Equal(["gi"], genshin.Games);
        Assert.Equal(["hsr"], starRail.Games);
        Assert.Equal("achievement-export-unsupported", unsupported.Code);
    }

    [Fact]
    public async Task Router_uses_the_saved_hsr_source_without_changing_genshin()
    {
        var game = new RecordingProvider();
        var hoyoLab = new RecordingProvider();
        var selectedSource = AchievementExportSources.Game;
        var router = new RoutedAchievementExportProvider(
            game,
            hoyoLab,
            _ => selectedSource);

        await router.StartAsync("gi", null, CancellationToken.None);
        await router.StartAsync("hsr", null, CancellationToken.None);
        selectedSource = AchievementExportSources.HoyoLab;
        await router.StartAsync("hsr", null, CancellationToken.None);

        Assert.Equal(["gi", "hsr"], game.Games);
        Assert.Equal(["hsr"], hoyoLab.Games);
    }

    [Fact]
    public void Exact_hsr_page_and_read_only_apis_are_allowlisted()
    {
        var page = PublisherAccountCatalog.GetAchievementPageUri("hsr");
        Assert.Equal(
            "https://act.hoyolab.com/sr/event/cultivation-tool/index.html?game_biz=hkrpg_global&hyl_auth_required=true#/tools/achievement",
            page.AbsoluteUri);
        Assert.True(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            page));
        Assert.True(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "hsr",
            page));
        var document = new Uri(page.GetLeftPart(UriPartial.Query));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            document,
            "GET",
            PublisherWebResourceContext.Document));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "hsr",
            document,
            "GET",
            PublisherWebResourceContext.Document));
        Assert.True(AllowedApi(
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=prod_official_eur"));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            new Uri(
                "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=prod_official_eur"),
            "OPTIONS",
            PublisherWebResourceContext.Fetch));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            new Uri("https://sg-act-public-api.hoyolab.com/common/badge/v1/login/account"),
            "POST",
            PublisherWebResourceContext.Fetch));
        Assert.True(AllowedApi(
            "https://sg-public-api.hoyolab.com/common/badge/v1/login/info?game_biz=hkrpg_global&lang=en-us&ts=1785700000000"));
        Assert.True(AllowedApi(
            "https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123456789&show_hide=false&need_all=true"));
        Assert.True(AllowedApi(
            "https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game=hkrpg&game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123456789&show_hide=false&need_all=true"));
        Assert.True(AllowedApi(
            "https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game=hkrpg&game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123456789&show_hide=false&need_all=true"));
        Assert.True(AllowedApi(
            "https://sg-public-data-api.hoyoverse.com/device-fp/api/getExtList?platform=4&app_name=hkrpg_global"));
        Assert.False(AllowedApi(
            "https://bbs-api-os.hoyolab.com/community/painter/wapi/circle/channel/guide/material"));
    }

    [Fact]
    public void Achievement_list_request_must_match_the_saved_role()
    {
        var role = new PublisherRoleBinding("123456789", "prod_official_eur");
        Assert.True(PublisherAccountCatalog.IsExactHsrAchievementListRequestForRole(
            new Uri("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123456789&show_hide=false&need_all=true"),
            "GET",
            role));
        Assert.True(PublisherAccountCatalog.IsExactHsrAchievementListRequestForRole(
            new Uri("https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game=hkrpg&game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123456789&show_hide=false&need_all=true"),
            "GET",
            role));
        Assert.False(PublisherAccountCatalog.IsExactHsrAchievementListRequestForRole(
            new Uri("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game_biz=hkrpg_global&badge_region=prod_official_asia&badge_uid=987654321&show_hide=false&need_all=true"),
            "GET",
            role));
    }

    [Theory]
    [InlineData("https://act.hoyolab.com/sr/event/cultivation-tool/index.html")]
    [InlineData("https://act.hoyolab.com/sr/event/cultivation-tool/index.html?game_biz=hkrpg_global&hyl_auth_required=true")]
    [InlineData("https://act.hoyolab.com/sr/event/cultivation-tool/index.html?game_biz=hkrpg_global#/tools/achievement")]
    [InlineData("https://act.hoyolab.com/sr/event/cultivation-tool/index.html?game_biz=hkrpg_global&hyl_auth_required=true#/tools/suggestion")]
    [InlineData("https://act.hoyolab.com/sr/event/cultivation-tool/other.html")]
    [InlineData("https://act.hoyolab.com.evil.example/sr/event/cultivation-tool/index.html")]
    [InlineData("http://act.hoyolab.com/sr/event/cultivation-tool/index.html")]
    public void Unreviewed_hsr_pages_are_denied(string raw)
    {
        foreach (var purpose in new[]
        {
            PublisherSessionPurpose.Achievements,
            PublisherSessionPurpose.Connect,
        })
        {
            Assert.False(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
                "HoYoLAB",
                purpose,
                "hsr",
                new Uri(raw)));
        }
    }

    [Theory]
    [InlineData("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken")]
    [InlineData("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=prod_official_eur")]
    [InlineData("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=unknown")]
    [InlineData("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=prod_official_eur&uid=secret")]
    [InlineData("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken?game_biz=hkrpg_global&region=prod_official_eur")]
    [InlineData("https://sg-act-public-api.hoyolab.com/common/badge/v1/login/account?uid=secret")]
    [InlineData("https://sg-public-api.hoyolab.com/common/badge/v1/login/info?game_biz=hkrpg_global&lang=en-us")]
    [InlineData("https://sg-public-api.hoyolab.com/common/badge/v1/login/info?game_biz=hkrpg_global&lang=fr-fr&ts=1785700000000")]
    [InlineData("https://sg-public-api.hoyolab.com/common/badge/v1/login/info?game_biz=hkrpg_global&lang=en-us&ts=now")]
    [InlineData("https://sg-public-api.hoyolab.com/common/badge/v1/login/info?game_biz=hkrpg_global&lang=en-us&ts=1785700000000&uid=secret")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=0123&show_hide=false&need_all=true")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game_biz=hkrpg_global&badge_region=unknown&badge_uid=123&show_hide=false&need_all=true")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123&show_hide=false")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game=genshin&game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123&show_hide=false&need_all=true")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game=hkrpg&t=now&game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123&show_hide=false&need_all=true")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game=hkrpg&noSessionRetry=false&game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123&show_hide=false&need_all=true")]
    [InlineData("https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game=hkrpg&game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=0123&show_hide=false&need_all=true")]
    [InlineData("https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list?game=genshin&game_biz=hkrpg_global&badge_region=prod_official_eur&badge_uid=123&show_hide=false&need_all=true")]
    public void Unreviewed_hsr_api_shapes_are_denied(string raw)
    {
        Assert.False(AllowedApi(raw));
    }

    [Fact]
    public void Retired_cookie_token_role_discovery_is_denied_for_hsr_achievement_session()
    {
        var retired = new Uri(
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken?game_biz=hkrpg_global&region=prod_official_eur");

        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            retired,
            "GET",
            PublisherWebResourceContext.Fetch));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            retired,
            "OPTIONS",
            PublisherWebResourceContext.Other));
    }

    [Theory]
    [InlineData("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesBy%43ookieToken?game_biz=hkrpg_global&region=prod_official_eur")]
    [InlineData("https://api-account-os.hoyolab.com/binding/api/getUserGameRolesBy%4Ctoken?game_biz=hkrpg_global&region=prod_official_eur&uid=secret")]
    [InlineData("https://api-account-os.hoyolab.com/binding/api/unrelated")]
    public void Account_api_host_denies_encoded_or_unrelated_paths(string raw)
    {
        var uri = new Uri(raw);

        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            uri,
            "GET",
            PublisherWebResourceContext.Fetch));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            uri,
            "OPTIONS",
            PublisherWebResourceContext.Fetch));
    }

    [Fact]
    public void Achievement_session_denies_unreviewed_writes()
    {
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            new Uri("https://sg-act-public-api.hoyolab.com/common/badge/v1/login/account"),
            "DELETE",
            PublisherWebResourceContext.Fetch));
    }

    private static bool AllowedApi(string raw) =>
        PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            new Uri(raw),
            "GET",
            PublisherWebResourceContext.Fetch);

    private static string ScriptResult(string payload) => JsonSerializer.Serialize(payload);

    private static string PackagedCatalogPath => Path.Combine(
        AppContext.BaseDirectory,
        "Catalog",
        "hsr-catalog.json");

    private static string Catalog(string version, params long[] ids)
    {
        var achievements = string.Join(
            ",",
            ids.Select(id =>
                $$"""{"id":"{{id}}","categoryId":"hsr-1","name":"Fixture {{id}}","description":"","reward":5,"rarity":"Low","version":"1.0","sortOrder":1}"""));
        return $$"""
            {
              "schemaVersion":1,
              "game":"hsr",
              "catalogVersion":"{{version}}",
              "releasedVersion":"{{version}}",
              "generatedAt":"2026-07-27T04:06:34.043Z",
              "dataTimestamp":"2026-07-21T19:00:05.000Z",
              "source":{},
              "categoryCount":1,
              "achievementCount":{{ids.Length}},
              "count":{{ids.Length}},
              "categories":[{}],
              "achievements":[{{achievements}}],
              "rewardCurrency":{}
            }
            """;
    }

    private sealed class RecordingProvider : IAchievementExportProvider
    {
        public List<string> Games { get; } = [];

        public ValueTask<IAchievementExportSession> StartAsync(
            string gameId,
            string? outputPath,
            CancellationToken cancellationToken)
        {
            Games.Add(gameId);
            return ValueTask.FromResult<IAchievementExportSession>(new CompletedSession());
        }
    }

    private sealed class ArtifactProvider(ExportArtifactMetadata artifact) :
        IAchievementExportProvider
    {
        public ValueTask<IAchievementExportSession> StartAsync(
            string gameId,
            string? outputPath,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAchievementExportSession>(
                new ArtifactSession(artifact));
    }

    private sealed class ArtifactSession(ExportArtifactMetadata artifact) :
        IAchievementExportSession
    {
        public Task Ready => Task.CompletedTask;
        public Task<ExportArtifactMetadata> Completion => Task.FromResult(artifact);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedPullProvider : IPullExportProvider
    {
        public ValueTask<IPullExportSession> PrepareAsync(
            string gameId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IPullExportSession>(
                new InvalidOperationException("Pull export was not requested."));
    }

    private sealed class RejectAfterFinalWriteAuthority(string outputRoot) :
        IAchievementExportPublishAuthority
    {
        public bool IsCurrent => false;
        public bool SawCompleteClosedTemporary { get; private set; }

        public bool TryPublish(Action publish)
        {
            var temporary = Assert.Single(Directory.GetFiles(
                outputRoot,
                "*.tmp",
                SearchOption.AllDirectories));
            using var document = JsonDocument.Parse(File.ReadAllText(temporary));
            SawCompleteClosedTemporary =
                document.RootElement.GetProperty("kind").GetString()
                    == "pengo-achievements";
            return false;
        }
    }

    private sealed class MutablePublishAuthority : IAchievementExportPublishAuthority
    {
        private int current = 1;

        public bool IsCurrent => Volatile.Read(ref current) == 1;

        public bool TryPublish(Action publish)
        {
            if (!IsCurrent) return false;
            publish();
            return true;
        }

        public void Revoke() => Interlocked.Exchange(ref current, 0);
    }

    private sealed class CompletedSession : IAchievementExportSession
    {
        public Task Ready => Task.CompletedTask;
        public Task<ExportArtifactMetadata> Completion => Task.FromResult(
            new ExportArtifactMetadata(
                "achievements",
                0,
                0,
                "pengo-achievements-v1",
                DateTimeOffset.UnixEpoch));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class XorProtector : IPublisherRoleBindingProtector
    {
        public byte[] Protect(byte[] plaintext) => Transform(plaintext);
        public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext);

        private static byte[] Transform(byte[] input) =>
            input.Select(static value => (byte)(value ^ 0xa5)).ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nyx-hsr-achievement-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
