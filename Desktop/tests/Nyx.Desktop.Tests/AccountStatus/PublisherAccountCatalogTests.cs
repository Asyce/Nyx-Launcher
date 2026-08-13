using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class PublisherAccountCatalogTests
{
    [Fact]
    public void Catalog_CoversExactlyTheFiveCanonicalGames()
    {
        Assert.Equal(["ae", "gi", "hsr", "wuwa", "zzz"],
            PublisherAccountCatalog.All.Select(static entry => entry.GameId).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("gi", "https://act.hoyolab.com/ys/event/signin-sea-v3/index.html?act_id=e202102251931481")]
    [InlineData("hsr", "https://act.hoyolab.com/bbs/event/signin/hkrpg/e202303301540311.html?act_id=e202303301540311&lang=en-us")]
    [InlineData("zzz", "https://act.hoyolab.com/bbs/event/signin/zzz/e202406031448091.html?act_id=e202406031448091&lang=en-us")]
    [InlineData("ae", "https://game.skport.com/endfield/sign-in")]
    public void ExactCheckInUri_AcceptsOnlyTheCompiledUrl(string gameId, string value)
    {
        Assert.True(PublisherAccountCatalog.IsExactCheckInUri(gameId, new Uri(value)));
        Assert.False(PublisherAccountCatalog.IsExactCheckInUri(gameId, new Uri(value + "#changed")));
        Assert.False(PublisherAccountCatalog.IsExactCheckInUri(gameId, new Uri(value + (value.Contains('?') ? "&extra=1" : "?extra=1"))));
    }

    [Fact]
    public void WuWa_HasNoGuessedDailyCheckInUrl()
    {
        var entry = PublisherAccountCatalog.Get("wuwa");
        Assert.False(entry.SupportsDailyCheckIn);
        Assert.Null(entry.CheckInUri);
    }

    [Fact]
    public void Resource_pages_use_the_reviewed_official_surfaces()
    {
        Assert.Equal(
            "https://act.hoyolab.com/app/community-game-records-sea/index.html#/ys/realtime",
            PublisherAccountCatalog.Get("gi").ResourceUri!.AbsoluteUri);
        Assert.Equal(
            "https://act.hoyolab.com/app/community-game-records-sea/rpg/index.html#/hsr",
            PublisherAccountCatalog.Get("hsr").ResourceUri!.AbsoluteUri);
        Assert.Equal(
            "https://act.hoyolab.com/app/zzz-game-record/index.html#/zzz",
            PublisherAccountCatalog.Get("zzz").ResourceUri!.AbsoluteUri);
        Assert.Equal(
            "https://game.skport.com/endfield/game-data?header=0",
            PublisherAccountCatalog.Get("ae").ResourceUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(
        "gi",
        "hk4e_global",
        "https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote",
        "os_usa,os_euro,os_asia,os_cht")]
    [InlineData(
        "hsr",
        "hkrpg_global",
        "https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/note",
        "prod_official_usa,prod_official_eur,prod_official_asia,prod_official_cht")]
    [InlineData(
        "zzz",
        "nap_global",
        "https://sg-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note",
        "prod_gf_us,prod_gf_eu,prod_gf_jp,prod_gf_sg")]
    public void Active_resource_fetch_contracts_are_exact_and_bounded(
        string gameId,
        string gameBusiness,
        string noteEndpoint,
        string regions)
    {
        var contract = PublisherAccountCatalog.GetResourceFetchContract(gameId);

        Assert.Equal(gameBusiness, contract.GameBusiness);
        Assert.Equal(
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken",
            contract.RoleDiscoveryEndpoint.AbsoluteUri);
        Assert.Equal(noteEndpoint, contract.NoteEndpoint.AbsoluteUri);
        Assert.Equal(regions.Split(','), contract.Regions);
        Assert.Equal(nameof(PublisherResourceFetchContract), contract.ToString());
    }

    [Fact]
    public void Active_resource_fetch_contracts_fail_closed_for_every_other_game()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PublisherAccountCatalog.GetResourceFetchContract("ae"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PublisherAccountCatalog.GetResourceFetchContract("wuwa"));
        Assert.False(PublisherAccountCatalog.IsExactResourceRoleDiscoveryRequest(
            "ae",
            new Uri(
                "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_euro"),
            "GET"));
    }

    [Fact]
    public void Genshin_resource_navigation_accepts_only_the_exact_realtime_note_route()
    {
        var approved = new Uri(
            "https://act.hoyolab.com/app/community-game-records-sea/index.html#/ys/realtime");

        Assert.True(PublisherAccountCatalog.IsExactResourcePageUri("gi", approved));
        Assert.True(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "gi",
            approved));

        foreach (var rejected in new[]
        {
            "https://act.hoyolab.com/app/community-game-records-sea/index.html#/ys",
            "https://act.hoyolab.com/app/community-game-records-sea/index.html#/ys/realtime/",
            "https://act.hoyolab.com/app/community-game-records-sea/index.html#/ys/realtime/nearby",
        })
        {
            var uri = new Uri(rejected);
            Assert.False(PublisherAccountCatalog.IsExactResourcePageUri("gi", uri));
            Assert.False(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
                "HoYoLAB",
                PublisherSessionPurpose.Resource,
                "gi",
                uri));
        }
    }

    [Fact]
    public void Endfield_keeps_the_official_protocol_terminal_but_denies_Daily()
    {
        var entry = PublisherAccountCatalog.Get("ae");

        Assert.Equal("https://game.skport.com/endfield/game-data?header=0", entry.ResourceUri?.AbsoluteUri);
        Assert.False(entry.SupportsDailyCheckIn);
        Assert.False(entry.SupportsNumericResource);
        Assert.Equal("https://game.skport.com/endfield/sign-in", entry.CheckInUri?.AbsoluteUri);
    }

    [Fact]
    public void Daily_is_supported_only_for_the_three_selected_HoYo_games()
    {
        var supported = PublisherAccountCatalog.All
            .Where(static entry => entry.SupportsDailyCheckIn)
            .Select(static entry => entry.GameId)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["gi", "hsr", "zzz"], supported);
    }

    [Fact]
    public void Exact_page_matching_normalizes_safe_URI_spelling_but_rejects_scope_changes()
    {
        Assert.True(PublisherAccountCatalog.IsExactCheckInUri(
            "gi",
            new Uri("HTTPS://ACT.HOYOLAB.COM:443/ys/event/signin-sea-v3/index.html?act_id=e202102251931481")));
        Assert.True(PublisherAccountCatalog.IsExactResourcePageUri(
            "zzz",
            new Uri("HTTPS://ACT.HOYOLAB.COM:443/app/zzz-game-record/index.html#/zzz")));
        Assert.False(PublisherAccountCatalog.IsExactResourcePageUri(
            "zzz",
            new Uri("https://act.hoyolab.com/app/zzz-game-record/index.html?extra=1#/zzz")));
        Assert.False(PublisherAccountCatalog.IsExactResourcePageUri(
            "ae",
            new Uri("https://game.skport.com/endfield/game-data?header=1")));
    }

    [Fact]
    public void Zzz_resource_navigation_accepts_only_the_current_official_surface()
    {
        var current = new Uri(
            "https://act.hoyolab.com/app/zzz-game-record/index.html#/zzz");
        var currentDocument = new Uri(
            "https://act.hoyolab.com/app/zzz-game-record/index.html");
        var currentAsset = new Uri(
            "https://act.hoyolab.com/app/zzz-game-record/assets/main.js");
        var superseded = new Uri(
            "https://act.hoyolab.com/app/mihoyo-zzz-game-record/index.html#/zzz");
        var supersededDocument = new Uri(
            "https://act.hoyolab.com/app/mihoyo-zzz-game-record/index.html");
        var supersededAsset = new Uri(
            "https://act.hoyolab.com/app/mihoyo-zzz-game-record/assets/main.js");

        Assert.True(PublisherAccountCatalog.IsExactResourcePageUri("zzz", current));
        Assert.True(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "zzz",
            current));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "zzz",
            currentDocument,
            "GET",
            PublisherWebResourceContext.Document));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "zzz",
            currentAsset,
            "GET",
            PublisherWebResourceContext.Script));

        Assert.False(PublisherAccountCatalog.IsExactResourcePageUri("zzz", superseded));
        Assert.False(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "zzz",
            superseded));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "zzz",
            supersededDocument,
            "GET",
            PublisherWebResourceContext.Document));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "zzz",
            supersededAsset,
            "GET",
            PublisherWebResourceContext.Script));
    }

    [Theory]
    [InlineData("gi", "https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro")]
    [InlineData("hsr", "https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/note?server=prod_official_eur&role_id=123456789")]
    [InlineData("zzz", "https://sg-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note?role_id=123456789&server=prod_gf_eu")]
    public void Resource_response_filter_accepts_only_the_compiled_endpoint_and_bounded_binding_query(
        string gameId,
        string value)
    {
        Assert.True(PublisherAccountCatalog.IsExactResourceResponseUri(gameId, new Uri(value)));
        Assert.False(PublisherAccountCatalog.IsExactResourceResponseUri(gameId, new Uri(value + "&lang=en-us")));
        Assert.False(PublisherAccountCatalog.IsExactResourceResponseUri(gameId, new Uri(value + "#changed")));
        Assert.False(PublisherAccountCatalog.IsExactResourceResponseUri(
            gameId,
            new Uri(value.Replace("https://", "https://evil.example/forward/", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=abc&server=os_euro")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123&server=os_unknown")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123&role_id=456&server=os_euro")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123&server=os_euro&")]
    [InlineData("https://sg-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro")]
    public void Resource_response_filter_rejects_ambiguous_or_unreviewed_bindings(string value)
    {
        Assert.False(PublisherAccountCatalog.IsExactResourceResponseUri("gi", new Uri(value)));
    }

    [Theory]
    [InlineData(
        "hsr",
        "https://bbs-api-os.hoyolab.com/game_record/hkrpg/api/note?role_id=123456789&server=prod_official_eur")]
    [InlineData(
        "zzz",
        "https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note?role_id=123456789&server=prod_gf_eu")]
    public void Resource_response_filter_rejects_superseded_note_hosts(
        string gameId,
        string value)
    {
        Assert.False(PublisherAccountCatalog.IsExactResourceResponseUri(gameId, new Uri(value)));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            new Uri(value),
            "GET",
            PublisherWebResourceContext.Fetch));
    }

    public static TheoryData<string, string> ExactResourceRoleDiscoveryRequests => new()
    {
        {
            "gi",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_usa"
        },
        {
            "gi",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_euro"
        },
        {
            "gi",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_asia"
        },
        {
            "gi",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_cht"
        },
        {
            "hsr",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=prod_official_usa"
        },
        {
            "hsr",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=prod_official_eur"
        },
        {
            "hsr",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=prod_official_asia"
        },
        {
            "hsr",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=prod_official_cht"
        },
        {
            "zzz",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=nap_global&region=prod_gf_us"
        },
        {
            "zzz",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=nap_global&region=prod_gf_eu"
        },
        {
            "zzz",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=nap_global&region=prod_gf_jp"
        },
        {
            "zzz",
            "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=nap_global&region=prod_gf_sg"
        },
    };

    [Theory]
    [MemberData(nameof(ExactResourceRoleDiscoveryRequests))]
    public void Resource_role_discovery_accepts_only_the_compiled_game_biz_and_region(
        string gameId,
        string value)
    {
        var uri = new Uri(value);

        Assert.True(PublisherAccountCatalog.IsExactResourceRoleDiscoveryRequest(gameId, uri, "GET"));
        Assert.True(PublisherAccountCatalog.IsExactResourceRoleDiscoveryRequest(gameId, uri, "OPTIONS"));
        Assert.False(PublisherAccountCatalog.IsExactResourceRoleDiscoveryRequest(gameId, uri, "POST"));
        Assert.False(PublisherAccountCatalog.IsExactResourceRoleDiscoveryRequest(gameId, uri, "HEAD"));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "GET",
            PublisherWebResourceContext.Fetch));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "OPTIONS",
            PublisherWebResourceContext.Other));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "GET",
            PublisherWebResourceContext.Fetch,
            requestBody: new byte[] { 1 }));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "OPTIONS",
            PublisherWebResourceContext.Other,
            requestBody: new byte[] { 1 }));
        foreach (var context in new[]
        {
            PublisherWebResourceContext.Document,
            PublisherWebResourceContext.Script,
            PublisherWebResourceContext.Image,
            PublisherWebResourceContext.Other,
        })
        {
            Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
                "HoYoLAB",
                PublisherSessionPurpose.Resource,
                gameId,
                uri,
                "GET",
                context));
        }
    }

    [Theory]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken")]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hkrpg_global&region=os_euro")]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=prod_official_eur")]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_euro&uid=123")]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&game_biz=hk4e_global&region=os_euro")]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_euro&")]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken/?game_biz=hk4e_global&region=os_euro")]
    [InlineData("gi", "https://api-account-os.hoyolab.com:444/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_euro")]
    [InlineData("gi", "https://api-account-os.hoyolab.com.evil.example/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_euro")]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?game_biz=hk4e_global&region=os_euro#changed")]
    [InlineData("gi", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken?game_biz=hk4e_global&region=os_euro")]
    [InlineData("hsr", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken?game_biz=hkrpg_global&region=prod_official_eur")]
    [InlineData("zzz", "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken?game_biz=nap_global&region=prod_gf_eu")]
    public void Resource_role_discovery_rejects_every_scope_or_query_change(
        string gameId,
        string value)
    {
        var uri = new Uri(value);

        Assert.False(PublisherAccountCatalog.IsExactResourceRoleDiscoveryRequest(gameId, uri, "GET"));
        Assert.False(PublisherAccountCatalog.IsExactResourceRoleDiscoveryRequest(gameId, uri, "OPTIONS"));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "GET",
            PublisherWebResourceContext.Fetch));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "OPTIONS",
            PublisherWebResourceContext.Other));
    }

    [Theory]
    [InlineData("gi", "https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro")]
    [InlineData("hsr", "https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/note?role_id=123456789&server=prod_official_eur")]
    [InlineData("zzz", "https://sg-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note?role_id=123456789&server=prod_gf_eu")]
    public void Resource_note_allows_only_get_and_the_required_hsr_preflight(
        string gameId,
        string value)
    {
        var uri = new Uri(value);

        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "GET",
            PublisherWebResourceContext.Fetch));
        Assert.Equal(
            gameId == "hsr",
            PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "OPTIONS",
            PublisherWebResourceContext.Other));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "POST",
            PublisherWebResourceContext.Fetch));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            gameId,
            uri,
            "GET",
            PublisherWebResourceContext.Fetch,
            requestBody: new byte[] { 1 }));
        foreach (var context in new[]
        {
            PublisherWebResourceContext.Document,
            PublisherWebResourceContext.Script,
            PublisherWebResourceContext.Image,
            PublisherWebResourceContext.Other,
        })
        {
            Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
                "HoYoLAB",
                PublisherSessionPurpose.Resource,
                gameId,
                uri,
                "GET",
                context));
        }
    }

    [Fact]
    public void Hsr_note_preflight_is_an_exact_read_only_cors_handshake()
    {
        var exact = new Uri(
            "https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/note?role_id=123456789&server=prod_official_eur");

        foreach (var context in new[]
        {
            PublisherWebResourceContext.XmlHttpRequest,
            PublisherWebResourceContext.Fetch,
            PublisherWebResourceContext.Other,
        })
        {
            Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
                "HoYoLAB",
                PublisherSessionPurpose.Resource,
                "hsr",
                exact,
                "OPTIONS",
                context));
        }

        foreach (var context in new[]
        {
            PublisherWebResourceContext.Document,
            PublisherWebResourceContext.Script,
            PublisherWebResourceContext.Image,
        })
        {
            Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
                "HoYoLAB",
                PublisherSessionPurpose.Resource,
                "hsr",
                exact,
                "OPTIONS",
                context));
        }

        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "hsr",
            exact,
            "OPTIONS",
            PublisherWebResourceContext.Other,
            requestBody: new byte[] { 1 }));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "hsr",
            new Uri(
                "https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/note?role_id=123456789&server=prod_official_eur&extra=1"),
            "OPTIONS",
            PublisherWebResourceContext.Other));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "hsr",
            new Uri(
                "https://bbs-api-os.hoyolab.com/game_record/hkrpg/api/note?role_id=123456789&server=prod_official_eur"),
            "OPTIONS",
            PublisherWebResourceContext.Other));
    }
}
