using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class PublisherAccountHardeningTests
{
    [Fact]
    public void Publisher_account_consent_is_default_off_independent_and_unknown_fails_closed()
    {
        var gate = new PublisherAccountConsentGate();

        Assert.False(gate.IsEnabled("HoYoLAB"));
        Assert.False(gate.IsEnabled("SKPORT"));
        Assert.False(gate.IsEnabled("lookalike"));
        Assert.True(gate.Set("HoYoLAB", enabled: true));
        Assert.True(gate.IsEnabled("HoYoLAB"));
        Assert.False(gate.IsEnabled("SKPORT"));
        Assert.False(gate.Set("lookalike", enabled: true));
        Assert.False(gate.IsEnabled("lookalike"));
        Assert.True(gate.Set("HoYoLAB", enabled: false));
        Assert.False(gate.IsEnabled("HoYoLAB"));
    }

    [Fact]
    public void Endfield_identity_parser_accepts_a_bounded_official_response()
    {
        Assert.True(PublisherEndfieldAccountIdentityParser.TryParseBindingResponse(
            Encoding.UTF8.GetBytes(
                """{"code":0,"data":{"list":[{"appCode":"endfield","bindingList":[{"isDefault":true,"roles":[{"roleId":"123456789","serverId":"prod-eu"}],"defaultRole":{"roleId":"123456789","serverId":"prod-eu"}}]}]}}"""),
            out var identity));
        Assert.NotNull(identity);
        Assert.Equal("123456789 · prod-eu", identity.DisplayText);

        Assert.False(PublisherEndfieldAccountIdentityParser.TryParseBindingResponse(
            Encoding.UTF8.GetBytes("""{"state":"login","identity":null}"""),
            out var loginIdentity));
        Assert.Null(loginIdentity);
    }

    [Fact]
    public void Endfield_identity_parser_accepts_the_current_official_binding_shapes()
    {
        var list = Encoding.UTF8.GetBytes(
            """{"code":0,"data":{"serverDefaultBinding":{"3":{"uid":"game-1","roleId":"123456789"}},"list":[{"appCode":"endfield","bindingList":[{"uid":"game-1","roles":[{"roleId":"123456789","serverId":"3"}]}]}]}}""");
        var gameMap = Encoding.UTF8.GetBytes(
            """{"code":0,"data":{"gameMap":{"endfield":{"bindingList":[{"isOfficial":true,"roles":[{"roleId":"987654321","serverId":"prod-eu"}],"defaultRole":{"roleId":"987654321","serverId":"prod-eu"}}]}}}}""");

        Assert.True(PublisherEndfieldAccountIdentityParser.TryParseBindingResponse(list, out var fromList));
        Assert.Equal(new PublisherEndfieldAccountIdentity("123456789", "3"), fromList);
        Assert.True(PublisherEndfieldAccountIdentityParser.TryParseBindingResponse(gameMap, out var fromMap));
        Assert.Equal(new PublisherEndfieldAccountIdentity("987654321", "prod-eu"), fromMap);
    }

    [Theory]
    [InlineData("Asia", true)]
    [InlineData("Americas / Europe", true)]
    [InlineData("Europe", false)]
    [InlineData(" Americas / Europe ", false)]
    [InlineData("", false)]
    public void Endfield_region_only_fallback_accepts_only_the_two_official_regions(
        string region,
        bool expected)
    {
        Assert.Equal(
            expected,
            PublisherEndfieldAccountIdentityParser.TryCreateRegionOnly(region, out var identity));
        if (expected)
        {
            Assert.NotNull(identity);
            Assert.Empty(identity.Uid);
            Assert.Equal(region, identity.DisplayText);
        }
        else
        {
            Assert.Null(identity);
        }
    }

    [Theory]
    [InlineData("{\"code\":0,\"data\":{\"list\":[]}}")]
    [InlineData("{\"code\":0,\"data\":{\"list\":[{\"appCode\":\"endfield\",\"bindingList\":[{\"isDefault\":true,\"roles\":[{\"roleId\":\"123\",\"serverId\":\"3\"}],\"defaultRole\":{\"roleId\":\"123\",\"serverId\":\"3\"}}]}],\"list\":[]}}")]
    [InlineData("{\"code\":0,\"data\":{\"list\":[{\"appCode\":\"endfield\",\"bindingList\":[{\"isDefault\":true,\"roles\":[{\"roleId\":\"123\",\"serverId\":\"3\"}],\"defaultRole\":{\"roleId\":\"999\",\"serverId\":\"3\"}}]}]}}")]
    [InlineData("{\"code\":0,\"data\":{\"list\":[{\"appCode\":\"endfield\",\"bindingList\":[{\"isDefault\":true,\"roles\":[{\"roleId\":\"123\",\"serverId\":\"3\"}],\"defaultRole\":{\"roleId\":\"123\",\"serverId\":\"3\"}}]}],\"gameMap\":{\"endfield\":{\"bindingList\":[{\"isDefault\":true,\"roles\":[{\"roleId\":\"456\",\"serverId\":\"3\"}],\"defaultRole\":{\"roleId\":\"456\",\"serverId\":\"3\"}}]}}}}")]
    public void Endfield_identity_parser_rejects_incomplete_or_ambiguous_binding_data(string raw)
    {
        Assert.False(PublisherEndfieldAccountIdentityParser.TryParseBindingResponse(
            Encoding.UTF8.GetBytes(raw),
            out var identity));
        Assert.Null(identity);
    }

    [Theory]
    [InlineData("https://zonai.skport.com/api/v1/game/player/binding?uid=123456789", "GET", true)]
    [InlineData("https://zonai.skport.com/api/v1/game/player/binding", "GET", false)]
    [InlineData("https://zonai.skport.com/api/v1/game/player/binding?uid=123&extra=1", "GET", false)]
    [InlineData("https://zonai.skport.com/api/v1/game/player/binding?uid=123456789", "POST", false)]
    [InlineData("https://zonai.skport.com/api/v1/game/player/other?uid=123456789", "GET", false)]
    public void Endfield_identity_capture_accepts_only_the_exact_official_request(
        string rawUri,
        string method,
        bool expected)
    {
        Assert.Equal(
            expected,
            PublisherAccountCatalog.IsExactEndfieldAccountIdentityRequest(new Uri(rawUri), method));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"state\":\"done\",\"identity\":null}")]
    [InlineData("{\"state\":\"done\",\"identity\":{\"uid\":\"abc\",\"region\":\"prod-eu\"}}")]
    [InlineData("{\"state\":\"done\",\"identity\":{\"uid\":\"123\",\"region\":\"prod/eu\"}}")]
    [InlineData("{\"state\":\"done\",\"identity\":{\"uid\":\"123\",\"region\":\"prod-eu\",\"nickname\":\"unreviewed\"}}")]
    [InlineData("{\"state\":\"done\",\"state\":\"login\",\"identity\":{\"uid\":\"123\",\"region\":\"prod-eu\"}}")]
    [InlineData("{\"state\":\"login\",\"identity\":{\"uid\":\"123\",\"region\":\"prod-eu\"}}")]
    [InlineData("{\"state\":\"future\",\"identity\":null}")]
    public void Endfield_identity_parser_rejects_ambiguous_unreviewed_or_unsafe_data(string raw)
    {
        Assert.False(PublisherEndfieldAccountIdentityParser.TryParseBindingResponse(
            Encoding.UTF8.GetBytes(raw),
            out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void Endfield_identity_parser_rejects_oversized_data_before_parsing()
    {
        var raw = Encoding.UTF8.GetBytes(new string(
            'x',
            PublisherAccountCatalog.MaximumResourceResponseBytes + 1));

        Assert.False(PublisherEndfieldAccountIdentityParser.TryParseBindingResponse(raw, out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void Endfield_review_is_visible_bounded_and_never_deletes_protected_state()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var contracts = ReadCoreAccountFile("PublisherAccountContracts.cs");
        var connect = Slice(
            service,
            "public async Task<PublisherConnectionState> ConnectAsync",
            "public Task<PublisherResourceSnapshot?> RefreshResourceAsync");
        var review = Slice(
            service,
            "public async Task<PublisherEndfieldAccountReviewResult> ReviewEndfieldAccountAsync",
            "private async Task<bool> ClearAllHoyoSavedPasswordsAsync");
        var identityReview = Slice(
            browser,
            "private async Task<PublisherEndfieldAccountIdentity?> ReviewEndfieldAccountIdentityAsync",
            "private async Task<PublisherEndfieldAccountIdentity?> TryReadEndfieldRegionAsync");
        var regionReview = Slice(
            browser,
            "private async Task<PublisherEndfieldAccountIdentity?> TryReadEndfieldRegionAsync",
            "public async Task<PublisherSessionProof> GetSessionProofAsync");
        var doneHandler = Slice(
            browser,
            "private async void DoneButton_Click",
            "private async void RetryButton_Click");
        var responseHandler = Slice(
            browser,
            "private void Core_WebResourceResponseReceived",
            "private static async Task CompleteSessionProbeAsync");
        var identityCapture = Slice(
            browser,
            "private async Task CompleteEndfieldIdentityCaptureAsync",
            "private static async Task CompleteCheckInCaptureAsync");

        Assert.True(
            connect.IndexOf("ReviewEndfieldAccountAsync(cancellationToken)", StringComparison.Ordinal)
            < connect.IndexOf("TryDeleteProtectedProviderState", StringComparison.Ordinal));
        Assert.Contains("BeginRotatedOperation(entry.Provider, cancellationToken)", review, StringComparison.Ordinal);
        Assert.Contains("await gate.WaitAsync(cancellationToken)", review, StringComparison.Ordinal);
        Assert.Contains("ProfileAccessAllowedAfterGate", review, StringComparison.Ordinal);
        Assert.Contains("ProfileMutationsFor(entry.Provider)", review, StringComparison.Ordinal);
        Assert.Contains("TrySetCanceledConnectState", review, StringComparison.Ordinal);
        Assert.Contains("QuarantineProvider", review, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDeleteProtected", review, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearProviderState", review, StringComparison.Ordinal);
        Assert.DoesNotContain("roleBindings", review, StringComparison.Ordinal);
        Assert.DoesNotContain("resourceSnapshots", review, StringComparison.Ordinal);
        Assert.Contains(
            "completion == PublisherVisibleConnectCompletion.Done",
            review,
            StringComparison.Ordinal);
        Assert.Contains("identity is not null", review, StringComparison.Ordinal);
        Assert.Contains("TryPublishEndfieldReview(identity, operation)", review, StringComparison.Ordinal);
        Assert.DoesNotContain("PublisherVisibleConnectFlow.CompleteAsync", review, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeConnectionCoreAsync", review, StringComparison.Ordinal);

        Assert.Contains("var observedIdentity = ReviewedEndfieldIdentity", identityReview, StringComparison.Ordinal);
        Assert.True(
            identityReview.IndexOf("var observedIdentity = ReviewedEndfieldIdentity", StringComparison.Ordinal)
            < identityReview.IndexOf("Browser.CoreWebView2!.Reload()", StringComparison.Ordinal));
        Assert.Contains("pendingEndfieldIdentityCapture", identityReview, StringComparison.Ordinal);
        Assert.Contains("Browser.CoreWebView2!.Reload()", identityReview, StringComparison.Ordinal);
        Assert.Contains("ResourceCaptureTimeoutSeconds", identityReview, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteScriptAsync", identityReview, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", identityReview, StringComparison.Ordinal);
        Assert.DoesNotContain("credentials", identityReview, StringComparison.Ordinal);
        Assert.Contains("PublisherAccountCatalog.IsExactCheckInUri(\"ae\", currentPage)", regionReview, StringComparison.Ordinal);
        Assert.Contains("'Asia', 'Americas / Europe'", regionReview, StringComparison.Ordinal);
        Assert.Contains("document.querySelectorAll('body *')", regionReview, StringComparison.Ordinal);
        Assert.Contains("TryCreateRegionOnly", regionReview, StringComparison.Ordinal);
        Assert.DoesNotContain("email", regionReview, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Log Out", regionReview, StringComparison.Ordinal);
        Assert.True(
            doneHandler.IndexOf("TryReadEndfieldRegionAsync", StringComparison.Ordinal)
            < doneHandler.IndexOf("GetSessionProofAsync", StringComparison.Ordinal));
        Assert.Contains("PublisherEndfieldAccountIdentity? endfieldIdentity", doneHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("did not prove an Endfield UID", doneHandler, StringComparison.Ordinal);
        Assert.Contains("purpose == PublisherSessionPurpose.Connect", responseHandler, StringComparison.Ordinal);
        Assert.Contains("provider == \"SKPORT\"", responseHandler, StringComparison.Ordinal);
        Assert.Contains("authorizedGameId == \"ae\"", responseHandler, StringComparison.Ordinal);
        Assert.Contains("IsExactEndfieldAccountIdentityRequest", responseHandler, StringComparison.Ordinal);
        Assert.Contains("CompleteEndfieldIdentityCaptureAsync(args, null)", responseHandler, StringComparison.Ordinal);
        Assert.Contains("TryParseBindingResponse", identityCapture, StringComparison.Ordinal);
        Assert.Contains("Volatile.Write(ref reviewedEndfieldIdentity, identity)", identityCapture, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(body)", identityCapture, StringComparison.Ordinal);

        Assert.Contains(
            "new Uri(\"https://game.skport.com/endfield/game-data?header=0\")",
            contracts,
            StringComparison.Ordinal);
        Assert.Equal("Sanity", PublisherAccountCatalog.Get("ae").ResourceName);
    }

    [Theory]
    [InlineData("gi", """{"retcode":0,"message":"OK","data":{"current_resin":124,"max_resin":200,"resin_recovery_time":"36480"}}""", 124, 200)]
    [InlineData("hsr", """{"retcode":0,"message":"OK","data":{"current_stamina":221,"max_stamina":300,"stamina_recover_time":23700,"current_reserve_stamina":840}}""", 221, 300)]
    [InlineData("zzz", """{"retcode":0,"message":"OK","data":{"energy":{"progress":{"current":87,"max":240},"restore":44100}}}""", 87, 240)]
    public void Per_game_resource_parsers_accept_complete_bounded_official_page_responses(
        string gameId,
        string json,
        int expectedCurrent,
        int expectedMaximum)
    {
        var observedAt = DateTimeOffset.Parse("2026-07-21T12:00:00Z");

        Assert.True(PublisherAccountCatalog.TryParseResourceResponse(
            gameId,
            Encoding.UTF8.GetBytes(json),
            observedAt,
            out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(expectedCurrent, snapshot.Current);
        Assert.Equal(expectedMaximum, snapshot.Maximum);
        Assert.Equal(observedAt, snapshot.ObservedAt);
    }

    [Theory]
    [InlineData(330, 300, 0, 0)]
    [InlineData(300, 300, -1, 0)]
    [InlineData(299, 300, 0, 0)]
    [InlineData(301, 300, 1, 1)]
    [InlineData(300, 300, 1, 1)]
    public void Hsr_resource_parser_accepts_independent_bounded_over_cap_and_recovery_semantics(
        int current,
        int maximum,
        int recoverySeconds,
        int expectedRecoverySeconds)
    {
        var json = JsonSerializer.Serialize(new
        {
            retcode = 0,
            data = new
            {
                current_stamina = current,
                max_stamina = maximum,
                stamina_recover_time = recoverySeconds,
                current_reserve_stamina = 840,
            },
        });

        Assert.True(PublisherAccountCatalog.TryParseResourceResponse(
            "hsr",
            Encoding.UTF8.GetBytes(json),
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(current, snapshot.Current);
        Assert.Equal(maximum, snapshot.Maximum);
        Assert.Equal(expectedRecoverySeconds, snapshot.RecoverySeconds);
        Assert.Equal(840, snapshot.Reserve);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Zzz_resource_parser_uses_complete_current_energy_time_fields_without_restore(
        int dayType)
    {
        var json = JsonSerializer.Serialize(new
        {
            retcode = 0,
            data = new
            {
                energy = new
                {
                    progress = new { current = 87, max = 240 },
                    day_type = dayType,
                    hour = 2,
                    minute = 3,
                },
            },
        });

        Assert.True(PublisherAccountCatalog.TryParseResourceResponse(
            "zzz",
            Encoding.UTF8.GetBytes(json),
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(87, snapshot.Current);
        Assert.Equal(240, snapshot.Maximum);
        Assert.Equal(0, snapshot.RecoverySeconds);
    }

    [Fact]
    public void Zzz_resource_parser_accepts_live_energy_shape_and_prefers_bounded_restore()
    {
        const string json =
            """{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1,"minute":2,"restore":3720}}}""";

        Assert.True(PublisherAccountCatalog.TryParseResourceResponse(
            "zzz",
            Encoding.UTF8.GetBytes(json),
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(87, snapshot.Current);
        Assert.Equal(240, snapshot.Maximum);
        Assert.Equal(3720, snapshot.RecoverySeconds);
    }

    [Theory]
    [InlineData("""{"retcode":0,"data":{"current_stamina":10001,"max_stamina":300,"stamina_recover_time":0,"current_reserve_stamina":840}}""")]
    [InlineData("""{"retcode":0,"data":{"current_stamina":301,"max_stamina":300,"stamina_recover_time":-604801,"current_reserve_stamina":840}}""")]
    [InlineData("""{"retcode":0,"data":{"current_stamina":301,"max_stamina":300,"stamina_recover_time":604801,"current_reserve_stamina":840}}""")]
    public void Hsr_resource_parser_rejects_unbounded_values(string json)
    {
        Assert.False(PublisherAccountCatalog.TryParseResourceResponse(
            "hsr",
            Encoding.UTF8.GetBytes(json),
            DateTimeOffset.UtcNow,
            out var snapshot));
        Assert.Null(snapshot);
    }

    [Theory]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":0,"hour":1,"restore":3600}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1,"restore":3600}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":24,"minute":0}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1,"minute":60}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":0,"hour":0,"minute":0}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":3,"hour":0,"minute":0}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":2147483647,"hour":23,"minute":59}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"day_type":1,"hour":1,"minute":0}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1,"minute":0,"restore":3600,"restore":3600}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":{},"hour":1,"minute":0,"restore":3600}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":true,"minute":0,"restore":3600}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1,"minute":[],"restore":3600}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1,"minute":0,"restore":{}}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1,"minute":0,"restore":-1}}}""")]
    [InlineData("""{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":1,"minute":0,"restore":604801}}}""")]
    public void Zzz_resource_parser_rejects_partial_duplicate_or_unbounded_current_time_fields(string json)
    {
        Assert.False(PublisherAccountCatalog.TryParseResourceResponse(
            "zzz",
            Encoding.UTF8.GetBytes(json),
            DateTimeOffset.UtcNow,
            out var snapshot));
        Assert.Null(snapshot);
    }

    [Theory]
    [InlineData("hsr", "not-json", PublisherResourceCaptureDiagnostic.EnvelopeRejected)]
    [InlineData("hsr", """{"retcode":42,"message":"private publisher text","data":{}}""", PublisherResourceCaptureDiagnostic.PublisherResultRejected)]
    [InlineData("hsr", """{"retcode":0,"data":[]}""", PublisherResourceCaptureDiagnostic.DataRejected)]
    [InlineData("hsr", """{"retcode":0,"data":{"current_stamina":{},"max_stamina":300,"stamina_recover_time":1,"current_reserve_stamina":840}}""", PublisherResourceCaptureDiagnostic.CoreFieldsRejected)]
    [InlineData("hsr", """{"retcode":0,"data":{"current_stamina":221,"max_stamina":300,"current_reserve_stamina":840}}""", PublisherResourceCaptureDiagnostic.TimeFieldsRejected)]
    [InlineData("hsr", """{"retcode":0,"data":{"current_stamina":221,"max_stamina":300,"stamina_recover_time":1}}""", PublisherResourceCaptureDiagnostic.ReserveRejected)]
    [InlineData("hsr", """{"retcode":0,"data":{"current_stamina":10001,"max_stamina":300,"stamina_recover_time":1,"current_reserve_stamina":840}}""", PublisherResourceCaptureDiagnostic.BoundsRejected)]
    [InlineData("zzz", """{"retcode":0,"data":{"energy":{"progress":{"current":87,"max":240},"day_type":1,"hour":2}}}""", PublisherResourceCaptureDiagnostic.TimeFieldsRejected)]
    public void Resource_parser_returns_only_fixed_non_sensitive_failure_categories(
        string gameId,
        string json,
        PublisherResourceCaptureDiagnostic expectedDiagnostic)
    {
        var proof = PublisherAccountCatalog.ParseResourceResponse(
            gameId,
            Encoding.UTF8.GetBytes(json),
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            out var snapshot,
            out var diagnostic);

        Assert.Equal(PublisherResourceProof.Invalid, proof);
        Assert.Null(snapshot);
        Assert.Equal(expectedDiagnostic, diagnostic);
        var label = PublisherAccountPresentation.ResourceCaptureGuidance(diagnostic);
        Assert.NotNull(label);
        Assert.DoesNotContain("42", label, StringComparison.Ordinal);
        Assert.DoesNotContain("private", label, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publisher text", label, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("gi", """{"retcode":0,"data":{"current_resin":201,"max_resin":200,"resin_recovery_time":0}}""")]
    [InlineData("gi", """{"retcode":0,"data":{"current_resin":100,"max_resin":200}}""")]
    [InlineData("hsr", """{"retcode":0,"data":{"current_stamina":100,"max_stamina":300,"stamina_recover_time":1}}""")]
    [InlineData("zzz", """{"retcode":0,"data":{"energy":{"progress":{"current":1,"max":240}}}}""")]
    [InlineData("zzz", """{"retcode":-100,"data":{"energy":{"progress":{"current":1,"max":240},"restore":1}}}""")]
    [InlineData("zzz", """{"retcode":0,"data":{"energy":{"progress":{"current":240,"max":240},"restore":1}}}""")]
    [InlineData("gi", """{"retcode":0,"data":{"current_resin":124,"current_resin":125,"max_resin":200,"resin_recovery_time":36480}}""")]
    [InlineData("gi", """{"retcode":0,"retcode":0,"data":{"current_resin":124,"max_resin":200,"resin_recovery_time":36480}}""")]
    [InlineData("zzz", "not-json")]
    public void Per_game_resource_parsers_fail_closed_on_partial_impossible_or_failed_responses(
        string gameId,
        string json)
    {
        Assert.False(PublisherAccountCatalog.TryParseResourceResponse(
            gameId,
            Encoding.UTF8.GetBytes(json),
            DateTimeOffset.UtcNow,
            out var snapshot));
        Assert.Null(snapshot);
    }

    [Fact]
    public void Resource_parser_rejects_oversized_content_before_parsing()
    {
        var oversized = new byte[PublisherAccountCatalog.MaximumResourceResponseBytes + 1];

        Assert.False(PublisherAccountCatalog.TryParseResourceResponse(
            "gi",
            oversized,
            DateTimeOffset.UtcNow,
            out _));
    }

    [Theory]
    [InlineData("gi", "GET", "https://sg-act-public-api.hoyolab.com/event/sol/info?uid=123456789&region=os_euro&lang=en-us&act_id=e202102251931481")]
    [InlineData("gi", "GET", "https://sg-act-public-api.hoyolab.com/event/sol/info?act_id=e202102251931481&lang=en-us")]
    [InlineData("gi", "GET", "https://sg-act-public-api.hoyolab.com/event/sol/info?act_id=e202102251931481&publisher_version=10")]
    [InlineData("hsr", "GET", "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311&lang=en-us&region=prod_official_eur&uid=123456789")]
    [InlineData("hsr", "GET", "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311")]
    [InlineData("zzz", "GET", "https://sg-act-public-api.hoyolab.com/event/luna/zzz/os/info?lang=en-us&uid=123456789&region=prod_gf_eu&act_id=e202406031448091")]
    [InlineData("zzz", "GET", "https://sg-act-public-api.hoyolab.com/event/luna/zzz/os/info?act_id=e202406031448091&lang=en-us")]
    [InlineData("gi", "POST", "https://sg-act-public-api.hoyolab.com/event/sol/sign?lang=en-us")]
    [InlineData("hsr", "POST", "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/sign")]
    [InlineData("zzz", "POST", "https://sg-act-public-api.hoyolab.com/event/luna/zzz/os/sign")]
    [InlineData("ae", "GET", "https://zonai.skport.com/web/v1/game/endfield/attendance")]
    [InlineData("ae", "POST", "https://zonai.skport.com/web/v1/game/endfield/attendance")]
    public void Check_in_response_filter_accepts_the_reviewed_endpoint_and_method_policy(
        string gameId,
        string method,
        string value)
    {
        var uri = new Uri(value);
        Assert.True(PublisherAccountCatalog.IsExactCheckInResponseUri(gameId, uri, method));
        Assert.Equal(
            gameId == "ae",
            PublisherAccountCatalog.IsExactCheckInResponseUri(gameId, uri, method == "GET" ? "POST" : "GET"));
        Assert.Equal(
            method == "GET" && gameId != "ae",
            PublisherAccountCatalog.IsExactCheckInResponseUri(
                gameId,
                new Uri(value + (value.Contains('?') ? "&extra=1" : "?extra=1")),
                method));
    }

    [Fact]
    public void Genshin_check_in_runtime_filters_cover_current_claim_and_retired_endpoints()
    {
        Assert.Equal(
            new[]
            {
                "https://sg-act-public-api.hoyolab.com/event/sol/sign",
                "https://sg-act-public-api.hoyolab.com/event/sol/sign?*",
                "https://sg-hk4e-api.hoyolab.com/event/sol/info",
                "https://sg-hk4e-api.hoyolab.com/event/sol/info?*",
                "https://sg-hk4e-api.hoyolab.com/event/sol/sign",
                "https://sg-hk4e-api.hoyolab.com/event/sol/sign?*",
            },
            PublisherAccountCatalog.GetCheckInWebResourceFilterPatterns("gi"));

        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        Assert.Contains(
            "PublisherAccountCatalog.GetCheckInWebResourceFilterPatterns(gameId)",
            browser,
            StringComparison.Ordinal);

        Assert.Equal(
            new[]
            {
                "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken",
                "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken?*",
                "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken",
                "https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByCookieToken?*",
                "https://sg-act-public-api.hoyolab.com/common/badge/v1/login/account",
                "https://sg-act-public-api.hoyolab.com/common/badge/v1/login/account?*",
                "https://sg-public-api.hoyolab.com/common/badge/v1/login/info",
                "https://sg-public-api.hoyolab.com/common/badge/v1/login/info?*",
                "https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list",
                "https://sg-act-public-api.hoyolab.com/event/rpgcultivate/achievement/list?*",
                "https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list",
                "https://sg-public-api.hoyolab.com/event/rpgcultivate/achievement/list?*",
            },
            PublisherAccountCatalog.GetAchievementWebResourceFilterPatterns("hsr"));
        Assert.Contains(
            "PublisherAccountCatalog.GetAchievementWebResourceFilterPatterns(gameId)",
            browser,
            StringComparison.Ordinal);

        var fallbackLogin = browser.IndexOf(
            "fallbackLogin = await request(fallbackLoginUrl, 16384, 'login')",
            StringComparison.Ordinal);
        var fallbackList = browser.IndexOf(
            "const fallbackUrl = new URL(FALLBACK_LIST)",
            StringComparison.Ordinal);
        Assert.True(fallbackLogin >= 0);
        Assert.True(fallbackList > fallbackLogin);
        Assert.Contains("fallbackRegion !== region", browser, StringComparison.Ordinal);
        Assert.Contains("String(fallbackUid || '') !== uid", browser, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/sol/sign")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/sol/sign?lang=en-us&lang=en-us")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/sol/sign?lang=en-us&extra=1")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/sol/sign?act_id=e202102251931481&lang=en-us")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/sol/sign?lang=EN-us")]
    public void Genshin_claim_filter_accepts_only_the_current_single_bounded_language_query(
        string value)
    {
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "gi",
            new Uri(value),
            "POST"));
    }

    [Theory]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=wrong&lang=en-us")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311&lang=en-us&uid=123456789")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311&lang=en-us&region=prod_official_eur")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311&act_id=e202303301540311&lang=en-us")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311&lang=en-us&lang=en-us")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311&lang=")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311&region=&uid=123456789")]
    [InlineData("https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info?act_id=e202303301540311&region=prod_official_eur&uid=")]
    public void Hoyo_status_filter_rejects_wrong_partial_empty_or_duplicate_identity(
        string value)
    {
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr",
            new Uri(value),
            "GET"));
    }

    [Fact]
    public void Hoyo_status_filter_rejects_oversized_malformed_and_control_query_data()
    {
        const string endpoint =
            "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info";
        const string actId = "?act_id=e202303301540311";
        var oversizedQuery = endpoint + actId + "&metadata=" + new string('a', 256);
        var oversizedKey = endpoint + actId + "&" + new string('k', 65) + "=1";
        var oversizedValue = endpoint + actId + "&metadata=" + new string('a', 65);

        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", new Uri(oversizedQuery), "GET"));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", new Uri(oversizedKey), "GET"));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", new Uri(oversizedValue), "GET"));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", new Uri(endpoint + actId + "&metadata"), "GET"));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", new Uri(endpoint + actId + "&metadata=%0A"), "GET"));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", new Uri(endpoint + actId + "&metadata="), "GET"));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", new Uri(endpoint + actId + "&metadata=1&metadata=2"), "GET"));
    }

    [Fact]
    public void Endfield_session_probe_requires_bounded_authenticated_JSON_not_status_alone()
    {
        var exact = new Uri("https://web-api.skport.com/cookie_store/account_token");
        Assert.True(PublisherAccountCatalog.IsExactSkportSessionProbeUri(exact, "GET"));
        Assert.False(PublisherAccountCatalog.IsExactSkportSessionProbeUri(exact, "POST"));
        Assert.False(PublisherAccountCatalog.IsExactSkportSessionProbeUri(
            new Uri("https://web-api.skport.com/cookie_store/account_token?extra=1"),
            "GET"));
        var authenticated = Encoding.UTF8.GetBytes(
            """{"code":0,"data":{"content":"test-only-nonempty-proof"}}""");
        Assert.True(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            200,
            "application/json; charset=utf-8",
            authenticated));
        Assert.False(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            401,
            "application/json",
            authenticated));
        Assert.False(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            200,
            "text/plain",
            authenticated));
        Assert.False(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            200,
            "application/json",
            Encoding.UTF8.GetBytes("""{"code":0,"data":{}}""")));
        Assert.False(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            200,
            "application/json",
            Encoding.UTF8.GetBytes("""{"code":0,"data":{"calendar":[]}}""")));
        Assert.False(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            200,
            "application/json",
            Encoding.UTF8.GetBytes("not-json")));
        Assert.False(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            200,
            "application/json",
            Encoding.UTF8.GetBytes("""{"code":"0","data":{"content":"test-only-nonempty-proof"}}""")));
        Assert.False(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            200,
            "application/json",
            Encoding.UTF8.GetBytes("""{"code":0,"code":0,"data":{"content":"test-only-nonempty-proof"}}""")));
        Assert.False(PublisherAccountCatalog.IsAuthenticatedSkportSessionResponse(
            200,
            "application/json",
            new byte[PublisherAccountCatalog.MaximumResourceResponseBytes + 1]));
        Assert.Equal(
            PublisherSessionProof.Authenticated,
            PublisherAccountCatalog.ClassifySkportSessionResponse(
                200,
                "application/json",
                authenticated));
        Assert.Equal(
            PublisherSessionProof.LoginRequired,
            PublisherAccountCatalog.ClassifySkportSessionResponse(
                401,
                "application/json",
                ReadOnlyMemory<byte>.Empty));
        Assert.Equal(
            PublisherSessionProof.NeedsReview,
            PublisherAccountCatalog.ClassifySkportSessionResponse(
                200,
                "application/json",
                Encoding.UTF8.GetBytes("""{"code":0,"data":{}}""")));
        Assert.Equal(
            PublisherConnectionState.NeedsReview,
            PublisherAccountStatePolicy.ForSessionProof(PublisherSessionProof.NeedsReview));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "ae",
            new Uri("https://game.skport.com/web/v1/game/endfield/attendance"),
            "GET"));
    }

    [Fact]
    public void Hoyo_info_uses_positive_current_day_state_and_never_prior_day_DOM_inference()
    {
        Assert.Equal(
            PublisherCheckInProof.Ready,
            ParseProof("hsr", "GET", """{"retcode":0,"data":{"is_sign":false,"total_sign_day":20,"history":[{"claimed":true}]}}"""));
        Assert.Equal(
            PublisherCheckInProof.Claimed,
            ParseProof("zzz", "GET", """{"retcode":0,"data":{"is_sign":true,"total_sign_day":21,"today":"2026-07-21"}}"""));
        Assert.Equal(
            PublisherCheckInProof.LoginNeeded,
            ParseProof("gi", "GET", """{"retcode":-100,"data":null,"message":"Not logged in"}"""));
    }

    [Theory]
    [InlineData("""{"retcode":0,"data":{"total_sign_day":21,"today":"2026-07-21"}}""")]
    [InlineData("""{"retcode":0,"data":{"is_sign":"true","total_sign_day":21,"today":"2026-07-21"}}""")]
    [InlineData("""{"retcode":0,"data":{"is_sign":true,"is_sign":false,"total_sign_day":21,"today":"2026-07-21"}}""")]
    [InlineData("""{"retcode":0,"data":{"is_sign":true,"total_sign_day":21,"today":"2026-07-21","today":"2026-07-20"}}""")]
    [InlineData("""{"retcode":0,"data":null}""")]
    [InlineData("not-json")]
    public void Hoyo_missing_role_or_layout_change_fails_closed(string json)
    {
        Assert.Equal(PublisherCheckInProof.Invalid, ParseProof("hsr", "GET", json));
    }

    [Fact]
    public void Malformed_check_in_requires_review_while_explicit_expiry_requires_login()
    {
        Assert.Equal(
            PublisherCheckInProof.LoginNeeded,
            PublisherAccountCatalog.ClassifyCheckInResponse(
                401,
                "text/html",
                "gi",
                "GET",
                ReadOnlyMemory<byte>.Empty,
                new DateOnly(2026, 7, 21),
                DateTimeOffset.Parse("2026-07-21T12:00:00Z")));
        Assert.Equal(
            PublisherCheckInProof.Invalid,
            PublisherAccountCatalog.ClassifyCheckInResponse(
                500,
                "application/json",
                "gi",
                "GET",
                Encoding.UTF8.GetBytes("""{"retcode":-100}"""),
                new DateOnly(2026, 7, 21),
                DateTimeOffset.Parse("2026-07-21T12:00:00Z")));
        Assert.Null(PublisherAccountStatePolicy.ForCheckIn(DailyCheckInState.CouldNotCheck));
        Assert.Equal(
            PublisherConnectionState.LoginRequired,
            PublisherAccountStatePolicy.ForCheckIn(DailyCheckInState.LoginNeeded));
    }

    [Theory]
    [InlineData("2026-07-20")]
    [InlineData("2026-02-30")]
    [InlineData("2026-7-21")]
    [InlineData("garbage")]
    public void Hoyo_optional_today_rejects_stale_or_malformed_values(string today)
    {
        var json = """{"retcode":0,"data":{"is_sign":false,"total_sign_day":20,"today":"DATE"}}"""
            .Replace("DATE", today, StringComparison.Ordinal);

        Assert.Equal(PublisherCheckInProof.Invalid, ParseProof("hsr", "GET", json));
    }

    [Fact]
    public void Hoyo_optional_today_accepts_the_utc_plus_eight_reset_date()
    {
        var proof = PublisherAccountCatalog.ParseCheckInResponse(
            "hsr",
            "GET",
            Encoding.UTF8.GetBytes(
                """{"retcode":0,"data":{"is_sign":false,"total_sign_day":20,"today":"2026-07-22"}}"""),
            new DateOnly(2026, 7, 21),
            DateTimeOffset.Parse("2026-07-21T18:30:00Z"));

        Assert.Equal(PublisherCheckInProof.Ready, proof);
    }

    [Fact]
    public void Endfield_attendance_GET_proves_current_ready_or_current_claimed()
    {
        const string ready = """
            {"code":0,"data":{"currentTs":"1784635200","hasToday":false,
            "calendar":[{"awardId":"item-1","available":true,"done":false},{"awardId":"item-2","available":false,"done":false}],
            "first":[],"resourceInfoMap":{}}}
            """;
        const string claimed = """
            {"code":0,"data":{"currentTs":"1784635200","hasToday":true,
            "calendar":[{"awardId":"item-1","available":false,"done":true},{"awardId":"item-2","available":false,"done":false}],
            "first":[],"resourceInfoMap":{}}}
            """;

        Assert.Equal(PublisherCheckInProof.Ready, ParseProof("ae", "GET", ready));
        Assert.Equal(PublisherCheckInProof.Claimed, ParseProof("ae", "GET", claimed));
    }

    [Theory]
    [InlineData("""{"code":0,"data":{"currentTs":"1","hasToday":false,"calendar":[],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"currentTs":"1","hasToday":false,"calendar":[{"awardId":"a","available":true,"done":false},{"awardId":"b","available":true,"done":false}],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"currentTs":"1","hasToday":false,"calendar":[{"awardId":"a","available":true,"done":true}],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"hasToday":false,"calendar":[{"awardId":"a","available":true,"done":false}],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"currentTs":"1","hasToday":true,"calendar":[{"awardId":"a","available":true,"done":false}],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"currentTs":"1","hasToday":true,"calendar":[{"awardId":"a","available":false,"done":false}],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"currentTs":"1784635200","hasToday":false,"hasToday":true,"calendar":[{"awardId":"a","available":true,"done":false}],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"currentTs":"1784635200","hasToday":false,"calendar":[{"awardId":"a","available":true,"available":false,"done":false}],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"currentTs":"1784635200","hasToday":false,"calendar":[{"awardId":"a","available":false,"done":false}],"first":[],"resourceInfoMap":{}}}""")]
    [InlineData("""{"code":0,"data":{"currentTs":"1784635200","hasToday":false,"calendar":[{"awardId":"a","available":true,"done":false},{"awardId":"a","available":false,"done":false}],"first":[],"resourceInfoMap":{}}}""")]
    public void Endfield_malformed_or_ambiguous_attendance_fails_closed(string json)
    {
        Assert.Equal(PublisherCheckInProof.Invalid, ParseProof("ae", "GET", json));
    }

    [Fact]
    public void Endfield_POST_requires_a_complete_bounded_claim_response()
    {
        const string accepted = """
            {"code":0,"data":{"ts":"1784635200","awardIds":[{"id":"item-1","type":1}],
            "tomorrowAwardIds":[],"resourceInfoMap":{"item-1":{"name":"Reward","icon":"https://example.invalid/i.png","count":1}}}}
            """;
        const string malformed = """{"code":0,"data":{"ts":"1784635200","awardIds":[],"tomorrowAwardIds":[],"resourceInfoMap":{}}}""";

        Assert.Equal(PublisherCheckInProof.ClaimAccepted, ParseProof("ae", "POST", accepted));
        Assert.Equal(PublisherCheckInProof.Invalid, ParseProof("ae", "POST", malformed));
    }

    [Theory]
    [InlineData("GET", "1784635079")]
    [InlineData("GET", "1784635231")]
    [InlineData("POST", "1784635079")]
    [InlineData("POST", "1784635231")]
    public void Endfield_GET_and_POST_reject_stale_or_future_server_timestamps(
        string method,
        string timestamp)
    {
        var json = method == "GET"
            ? """
                {"code":0,"data":{"currentTs":"TIMESTAMP","hasToday":false,
                "calendar":[{"awardId":"item-1","available":true,"done":false}],
                "first":[],"resourceInfoMap":{}}}
                """
            : """
                {"code":0,"data":{"ts":"TIMESTAMP","awardIds":[{"id":"item-1","type":1}],
                "tomorrowAwardIds":[],"resourceInfoMap":{}}}
                """;

        Assert.Equal(
            PublisherCheckInProof.Invalid,
            ParseProof("ae", method, json.Replace("TIMESTAMP", timestamp, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("2026-07-21T20:00:30Z")]
    [InlineData("2026-07-21T09:00:30Z")]
    public void Endfield_timestamp_must_not_cross_either_possible_server_reset_day(
        string expectedText)
    {
        var expectedInstant = DateTimeOffset.Parse(expectedText);
        var responseTimestamp = expectedInstant.AddMinutes(-1).ToUnixTimeSeconds().ToString();
        var json = """
            {"code":0,"data":{"currentTs":"TIMESTAMP","hasToday":false,
            "calendar":[{"awardId":"item-1","available":true,"done":false}],
            "first":[],"resourceInfoMap":{}}}
            """.Replace("TIMESTAMP", responseTimestamp, StringComparison.Ordinal);

        Assert.Equal(
            PublisherCheckInProof.Invalid,
            PublisherAccountCatalog.ParseCheckInResponse(
                "ae",
                "GET",
                Encoding.UTF8.GetBytes(json),
                DateOnly.FromDateTime(expectedInstant.DateTime),
                expectedInstant));
    }

    [Fact]
    public void Endfield_available_field_remains_the_primary_claim_state()
    {
        const string noAvailableReward = """
            {"code":0,"data":{"currentTs":"1784635200","hasToday":false,
            "calendar":[{"awardId":"item-1","available":false,"done":true},{"awardId":"item-2","available":false,"done":false}],
            "first":[],"resourceInfoMap":{}}}
            """;

        Assert.Equal(PublisherCheckInProof.Claimed, ParseProof("ae", "GET", noAvailableReward));
    }

    [Fact]
    public void Authenticated_resource_expiry_is_a_distinct_login_needed_proof()
    {
        var proof = PublisherAccountCatalog.ParseResourceResponse(
            "gi",
            Encoding.UTF8.GetBytes("""{"retcode":-100,"message":"expired","data":null}"""),
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            out var snapshot);

        Assert.Equal(PublisherResourceProof.LoginNeeded, proof);
        Assert.Null(snapshot);
    }

    [Fact]
    public void Resource_capture_accepts_one_binding_and_rejects_mixed_roles_or_servers_in_any_order()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-21T12:00:00Z");
        var older = new PublisherResourceSnapshot("gi", "Original Resin", 124, 200, observedAt, RecoverySeconds: 36480);
        var newer = older with { ObservedAt = observedAt.AddSeconds(1) };
        var firstBinding = new PublisherRoleBinding("123456789", "os_euro");
        var otherRole = new PublisherRoleBinding("987654321", "os_euro");
        var otherServer = new PublisherRoleBinding("123456789", "os_usa");
        PublisherResourceCandidate[] sameBinding =
        [
            new(firstBinding, older),
            new(firstBinding, newer),
        ];

        Assert.Equal(newer, PublisherAccountCatalog.SelectUnambiguousResource(sameBinding));

        PublisherResourceCandidate[] mixedRole =
        [
            new(firstBinding, older),
            new(otherRole, newer),
        ];
        Assert.Null(PublisherAccountCatalog.SelectUnambiguousResource(mixedRole));
        Assert.Null(PublisherAccountCatalog.SelectUnambiguousResource(mixedRole.Reverse().ToArray()));

        PublisherResourceCandidate[] mixedServer =
        [
            new(firstBinding, older),
            new(otherServer, newer),
        ];
        Assert.Null(PublisherAccountCatalog.SelectUnambiguousResource(mixedServer));
        Assert.Null(PublisherAccountCatalog.SelectUnambiguousResource(mixedServer.Reverse().ToArray()));
        Assert.Null(PublisherAccountCatalog.SelectUnambiguousResource(
            Enumerable.Repeat(new PublisherResourceCandidate(firstBinding, older), 9).ToArray()));
    }

    [Fact]
    public void Resource_response_is_ignored_until_its_request_was_reserved()
    {
        const long generation = 17;
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            RecoverySeconds: 36480);
        var capture = new PublisherResourceCaptureAuthority("gi", generation);

        Assert.True(capture.Open(generation));
        Assert.False(capture.AllResponsesCompleted);
        Assert.False(capture.TryBeginResponse(generation, binding));
        Assert.True(capture.TryReserve(generation, "gi", binding));
        Assert.False(capture.AllResponsesCompleted);
        Assert.True(capture.TryBeginResponse(generation, binding));
        Assert.False(capture.AllResponsesCompleted);
        Assert.True(capture.CompleteResponse(generation, binding, PublisherResourceProof.Valid, snapshot));
        Assert.True(capture.AllResponsesCompleted);

        var result = capture.Seal(generation);
        Assert.Equal(PublisherResourceReadOutcome.Valid, result.Outcome);
        Assert.Equal(snapshot, result.Snapshot);
    }

    [Fact]
    public void Resource_capture_diagnostic_classifies_only_bounded_non_sensitive_stages()
    {
        const long generation = 117;
        var firstBinding = new PublisherRoleBinding("123456789", "os_euro");
        var secondBinding = new PublisherRoleBinding("987654321", "os_usa");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            RecoverySeconds: 36480);

        var noRequest = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(noRequest.Open(generation));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.NoAcceptedRequest,
            noRequest.Seal(generation).Diagnostic);

        var invalid = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(invalid.Open(generation));
        Assert.True(invalid.TryReserve(generation, "gi", firstBinding));
        Assert.True(invalid.TryBeginResponse(generation, firstBinding));
        Assert.True(invalid.CompleteResponse(
            generation,
            firstBinding,
            PublisherResourceProof.Invalid,
            null));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.ResponseRejected,
            invalid.Seal(generation).Diagnostic);

        var incomplete = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(incomplete.Open(generation));
        Assert.True(incomplete.TryReserve(generation, "gi", firstBinding));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.ResponseIncomplete,
            incomplete.Seal(generation).Diagnostic);

        var login = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(login.Open(generation));
        Assert.True(login.TryReserve(generation, "gi", firstBinding));
        Assert.True(login.TryBeginResponse(generation, firstBinding));
        Assert.True(login.CompleteResponse(
            generation,
            firstBinding,
            PublisherResourceProof.LoginNeeded,
            null));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.LoginRequired,
            login.Seal(generation).Diagnostic);

        var valid = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(valid.Open(generation));
        Assert.True(valid.TryReserve(generation, "gi", firstBinding));
        Assert.True(valid.TryBeginResponse(generation, firstBinding));
        Assert.True(valid.CompleteResponse(
            generation,
            firstBinding,
            PublisherResourceProof.Valid,
            snapshot));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.Valid,
            valid.Seal(generation).Diagnostic);

        var selection = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(selection.Open(generation));
        foreach (var binding in new[] { firstBinding, secondBinding })
        {
            Assert.True(selection.TryReserve(generation, "gi", binding));
            Assert.True(selection.TryBeginResponse(generation, binding));
            Assert.True(selection.CompleteResponse(
                generation,
                binding,
                PublisherResourceProof.Valid,
                snapshot));
        }
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.SelectionRequired,
            selection.Seal(generation).Diagnostic);
    }

    [Fact]
    public void Resource_capture_authority_preserves_only_one_fixed_failure_category()
    {
        const long generation = 1170;
        var firstBinding = new PublisherRoleBinding("123456789", "os_euro");
        var secondBinding = new PublisherRoleBinding("987654321", "os_usa");
        var fixedFailures = new[]
        {
            PublisherResourceCaptureDiagnostic.RequestRejected,
            PublisherResourceCaptureDiagnostic.PublisherResultRejected,
            PublisherResourceCaptureDiagnostic.EnvelopeRejected,
            PublisherResourceCaptureDiagnostic.DataRejected,
            PublisherResourceCaptureDiagnostic.CoreFieldsRejected,
            PublisherResourceCaptureDiagnostic.TimeFieldsRejected,
            PublisherResourceCaptureDiagnostic.ReserveRejected,
            PublisherResourceCaptureDiagnostic.BoundsRejected,
        };

        foreach (var fixedFailure in fixedFailures)
        {
            var capture = new PublisherResourceCaptureAuthority("hsr", generation);
            Assert.True(capture.Open(generation));
            Assert.True(capture.TryReserve(generation, "hsr", firstBinding));
            Assert.True(capture.TryBeginResponse(generation, firstBinding));
            Assert.True(capture.CompleteResponse(
                generation,
                firstBinding,
                PublisherResourceProof.Invalid,
                null,
                fixedFailure));
            Assert.Equal(fixedFailure, capture.Seal(generation).Diagnostic);
        }

        var conflicting = new PublisherResourceCaptureAuthority("hsr", generation + 1);
        Assert.True(conflicting.Open(generation + 1));
        foreach (var (binding, diagnostic) in new[]
        {
            (firstBinding, PublisherResourceCaptureDiagnostic.EnvelopeRejected),
            (secondBinding, PublisherResourceCaptureDiagnostic.BoundsRejected),
        })
        {
            Assert.True(conflicting.TryReserve(generation + 1, "hsr", binding));
            Assert.True(conflicting.TryBeginResponse(generation + 1, binding));
            Assert.True(conflicting.CompleteResponse(
                generation + 1,
                binding,
                PublisherResourceProof.Invalid,
                null,
                diagnostic));
        }
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.ResponseRejected,
            conflicting.Seal(generation + 1).Diagnostic);

        var invalidEnum = new PublisherResourceCaptureAuthority("hsr", generation + 2);
        Assert.True(invalidEnum.Open(generation + 2));
        Assert.True(invalidEnum.TryReserve(generation + 2, "hsr", firstBinding));
        Assert.True(invalidEnum.TryBeginResponse(generation + 2, firstBinding));
        Assert.True(invalidEnum.CompleteResponse(
            generation + 2,
            firstBinding,
            PublisherResourceProof.Invalid,
            null,
            (PublisherResourceCaptureDiagnostic)int.MaxValue));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.ResponseRejected,
            invalidEnum.Seal(generation + 2).Diagnostic);

        var spoofedTriggerStage = new PublisherResourceCaptureAuthority("hsr", generation + 3);
        Assert.True(spoofedTriggerStage.Open(generation + 3));
        Assert.True(spoofedTriggerStage.TryReserve(generation + 3, "hsr", firstBinding));
        Assert.True(spoofedTriggerStage.TryBeginResponse(generation + 3, firstBinding));
        Assert.True(spoofedTriggerStage.CompleteResponse(
            generation + 3,
            firstBinding,
            PublisherResourceProof.Invalid,
            null,
            PublisherResourceCaptureDiagnostic.SignatureRejected));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.ResponseRejected,
            spoofedTriggerStage.Seal(generation + 3).Diagnostic);
    }

    [Fact]
    public void Exact_role_discovery_login_signal_is_generation_bound_and_cannot_override_note_evidence()
    {
        const long generation = 118;
        var binding = new PublisherRoleBinding("123456789", "os_euro");

        var login = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(login.Open(generation));
        Assert.False(login.MarkRoleDiscoveryLoginRequired(generation - 1));
        Assert.True(login.MarkRoleDiscoveryLoginRequired(generation));
        var loginResult = login.Seal(generation);
        Assert.Equal(PublisherResourceReadOutcome.LoginRequired, loginResult.Outcome);
        Assert.Equal(PublisherResourceCaptureDiagnostic.LoginRequired, loginResult.Diagnostic);
        Assert.False(login.MarkRoleDiscoveryLoginRequired(generation));

        var conflicting = new PublisherResourceCaptureAuthority("gi", generation + 1);
        Assert.True(conflicting.Open(generation + 1));
        Assert.True(conflicting.TryReserve(generation + 1, "gi", binding));
        Assert.True(conflicting.MarkRoleDiscoveryLoginRequired(generation + 1));
        var conflictingResult = conflicting.Seal(generation + 1);
        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, conflictingResult.Outcome);
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.ResponseRejected,
            conflictingResult.Diagnostic);
    }

    public static TheoryData<string?> NonDoneResourceTriggerStates => new()
    {
        "running",
        "login",
        "invalid",
        "no-roles",
        "canceled",
        "missing",
        "unexpected",
        null,
    };

    [Theory]
    [MemberData(nameof(NonDoneResourceTriggerStates))]
    public void Completed_resource_evidence_is_rejected_unless_the_active_trigger_finished_done(
        string? triggerState)
    {
        const long generation = 119;
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            RecoverySeconds: 36480);
        var capture = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(capture.Open(generation));
        Assert.True(capture.TryReserve(generation, "gi", binding));
        Assert.True(capture.TryBeginResponse(generation, binding));
        Assert.True(capture.CompleteResponse(
            generation,
            binding,
            PublisherResourceProof.Valid,
            snapshot));

        var result = PublisherResourceTriggerPolicy.Seal(
            capture,
            generation,
            triggerState);

        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, result.Outcome);
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.ResponseRejected,
            result.Diagnostic);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Completed_resource_evidence_is_accepted_when_the_active_trigger_finished_done()
    {
        const long generation = 120;
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            RecoverySeconds: 36480);
        var capture = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(capture.Open(generation));
        Assert.True(capture.TryReserve(generation, "gi", binding));
        Assert.True(capture.TryBeginResponse(generation, binding));
        Assert.True(capture.CompleteResponse(
            generation,
            binding,
            PublisherResourceProof.Valid,
            snapshot));

        var result = PublisherResourceTriggerPolicy.Seal(
            capture,
            generation,
            "done");

        Assert.Equal(PublisherResourceReadOutcome.Valid, result.Outcome);
        Assert.Equal(PublisherResourceCaptureDiagnostic.Valid, result.Diagnostic);
        Assert.Equal(snapshot, result.Snapshot);
    }

    [Fact]
    public void Trigger_login_without_note_evidence_still_requires_login()
    {
        const long generation = 121;
        var capture = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(capture.Open(generation));

        var result = PublisherResourceTriggerPolicy.Seal(
            capture,
            generation,
            "login");

        Assert.Equal(PublisherResourceReadOutcome.LoginRequired, result.Outcome);
        Assert.Equal(PublisherResourceCaptureDiagnostic.LoginRequired, result.Diagnostic);
    }

    [Theory]
    [InlineData("hsr", "signature-rejected", PublisherResourceCaptureDiagnostic.SignatureRejected)]
    [InlineData("hsr", "request-blocked", PublisherResourceCaptureDiagnostic.BrowserRequestBlocked)]
    [InlineData("zzz", "request-blocked", PublisherResourceCaptureDiagnostic.BrowserRequestBlocked)]
    [InlineData("hsr", "timed-out", PublisherResourceCaptureDiagnostic.OperationTimedOut)]
    [InlineData("zzz", "timed-out", PublisherResourceCaptureDiagnostic.OperationTimedOut)]
    public void Fixed_trigger_failures_surface_only_for_active_hsr_and_zzz_pre_response_captures(
        string gameId,
        string triggerState,
        PublisherResourceCaptureDiagnostic expectedDiagnostic)
    {
        const long generation = 1210;
        var capture = new PublisherResourceCaptureAuthority(gameId, generation);
        Assert.True(capture.Open(generation));

        var result = PublisherResourceTriggerPolicy.Seal(
            capture,
            generation,
            triggerState);

        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, result.Outcome);
        Assert.Equal(expectedDiagnostic, result.Diagnostic);
        Assert.Null(result.Snapshot);
        Assert.Null(result.Candidates);
    }

    [Fact]
    public void Fixed_trigger_failures_cannot_override_cancellation_generation_identity_or_response_evidence()
    {
        const long generation = 1211;
        var binding = new PublisherRoleBinding("123456789", "prod_official_eur");

        var canceled = new PublisherResourceCaptureAuthority("hsr", generation);
        Assert.True(canceled.Open(generation));
        canceled.Cancel();

        var stale = new PublisherResourceCaptureAuthority("hsr", generation + 1);
        Assert.True(stale.Open(generation + 1));

        var processing = new PublisherResourceCaptureAuthority("hsr", generation + 2);
        Assert.True(processing.Open(generation + 2));
        Assert.True(processing.TryReserve(generation + 2, "hsr", binding));
        Assert.True(processing.TryBeginResponse(generation + 2, binding));

        var completed = new PublisherResourceCaptureAuthority("hsr", generation + 3);
        Assert.True(completed.Open(generation + 3));
        Assert.True(completed.TryReserve(generation + 3, "hsr", binding));
        Assert.True(completed.TryBeginResponse(generation + 3, binding));
        Assert.True(completed.CompleteResponse(
            generation + 3,
            binding,
            PublisherResourceProof.Invalid,
            null,
            PublisherResourceCaptureDiagnostic.EnvelopeRejected));

        var wrongGame = new PublisherResourceCaptureAuthority("gi", generation + 4);
        Assert.True(wrongGame.Open(generation + 4));

        var wrongSignatureGame = new PublisherResourceCaptureAuthority("zzz", generation + 5);
        Assert.True(wrongSignatureGame.Open(generation + 5));

        var directWrongGame = new PublisherResourceCaptureAuthority("gi", generation + 6);
        Assert.True(directWrongGame.Open(generation + 6));

        var identityBearing = new PublisherResourceCaptureAuthority("hsr", generation + 7);
        Assert.True(identityBearing.Open(generation + 7));

        foreach (var result in new[]
        {
            PublisherResourceTriggerPolicy.Seal(canceled, generation, "request-blocked"),
            PublisherResourceTriggerPolicy.Seal(stale, generation, "timed-out"),
            PublisherResourceTriggerPolicy.Seal(processing, generation + 2, "request-blocked"),
            PublisherResourceTriggerPolicy.Seal(completed, generation + 3, "request-blocked"),
            PublisherResourceTriggerPolicy.Seal(wrongGame, generation + 4, "request-blocked"),
            PublisherResourceTriggerPolicy.Seal(
                wrongSignatureGame,
                generation + 5,
                "signature-rejected"),
            directWrongGame.SealTriggerFailure(
                generation + 6,
                PublisherResourceCaptureDiagnostic.BrowserRequestBlocked),
            PublisherResourceTriggerPolicy.Seal(
                identityBearing,
                generation + 7,
                new PublisherResourceTriggerResult(
                    "request-blocked",
                    [new(binding, "private")])),
        })
        {
            Assert.Equal(PublisherResourceReadOutcome.NeedsReview, result.Outcome);
            Assert.Equal(PublisherResourceCaptureDiagnostic.ResponseRejected, result.Diagnostic);
            Assert.Null(result.Snapshot);
        }
    }

    [Fact]
    public void Cancel_permanently_invalidates_already_completed_resource_evidence()
    {
        const long generation = 122;
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            RecoverySeconds: 36480);
        var capture = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(capture.Open(generation));
        Assert.True(capture.TryReserve(generation, "gi", binding));
        Assert.True(capture.TryBeginResponse(generation, binding));
        Assert.True(capture.CompleteResponse(
            generation,
            binding,
            PublisherResourceProof.Valid,
            snapshot));

        capture.Cancel();
        var result = capture.Seal(generation);

        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, result.Outcome);
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.ResponseRejected,
            result.Diagnostic);
        Assert.Null(result.Snapshot);
        Assert.False(capture.Open(generation));
        Assert.False(capture.TryReserve(generation, "gi", binding));
    }

    [Fact]
    public void Replacement_cancellation_blocks_old_completed_generation_but_not_the_new_one()
    {
        const long oldGeneration = 123;
        const long newGeneration = 124;
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            RecoverySeconds: 36480);

        static void Complete(
            PublisherResourceCaptureAuthority capture,
            long generation,
            PublisherRoleBinding binding,
            PublisherResourceSnapshot snapshot)
        {
            Assert.True(capture.Open(generation));
            Assert.True(capture.TryReserve(generation, "gi", binding));
            Assert.True(capture.TryBeginResponse(generation, binding));
            Assert.True(capture.CompleteResponse(
                generation,
                binding,
                PublisherResourceProof.Valid,
                snapshot));
        }

        var oldCapture = new PublisherResourceCaptureAuthority("gi", oldGeneration);
        Complete(oldCapture, oldGeneration, binding, snapshot);
        oldCapture.Cancel();

        var newCapture = new PublisherResourceCaptureAuthority("gi", newGeneration);
        Complete(newCapture, newGeneration, binding, snapshot);

        Assert.Equal(
            PublisherResourceReadOutcome.NeedsReview,
            PublisherResourceTriggerPolicy.Seal(oldCapture, oldGeneration, "done").Outcome);
        Assert.Equal(
            PublisherResourceReadOutcome.NeedsReview,
            PublisherResourceTriggerPolicy.Seal(newCapture, oldGeneration, "done").Outcome);
        Assert.Equal(
            PublisherResourceReadOutcome.Valid,
            PublisherResourceTriggerPolicy.Seal(newCapture, newGeneration, "done").Outcome);
    }

    [Fact]
    public void Resource_capture_guidance_is_short_and_cannot_render_publisher_values()
    {
        Assert.Equal(
            "OFFICIAL REQUEST NOT SEEN · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.NoAcceptedRequest));
        Assert.Equal(
            "RESPONSE NOT ACCEPTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.ResponseRejected));
        Assert.Equal(
            "RESPONSE INCOMPLETE · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.ResponseIncomplete));
        Assert.Equal(
            "REQUEST REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.RequestRejected));
        Assert.Equal(
            "PUBLISHER RESULT REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.PublisherResultRejected));
        Assert.Equal(
            "RESPONSE ENVELOPE REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.EnvelopeRejected));
        Assert.Equal(
            "RESPONSE DATA REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.DataRejected));
        Assert.Equal(
            "RESOURCE FIELDS REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.CoreFieldsRejected));
        Assert.Equal(
            "RECOVERY FIELDS REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.TimeFieldsRejected));
        Assert.Equal(
            "RESERVE FIELD REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.ReserveRejected));
        Assert.Equal(
            "VALUE BOUNDS REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.BoundsRejected));
        Assert.Equal(
            "SIGNATURE REJECTED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.SignatureRejected));
        Assert.Equal(
            "BROWSER REQUEST BLOCKED · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.BrowserRequestBlocked));
        Assert.Equal(
            "OPERATION TIMED OUT · TRY AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.OperationTimedOut));
        Assert.Equal(
            "BROWSER CLOSED · RESTART NYX",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.BrowserSessionUnavailable));
        Assert.Equal(
            "SIGN IN AGAIN",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.LoginRequired));
        Assert.Equal(
            "CHOOSE REGION",
            PublisherAccountPresentation.ResourceCaptureGuidance(
                PublisherResourceCaptureDiagnostic.SelectionRequired));
        Assert.Null(PublisherAccountPresentation.ResourceCaptureGuidance(
            PublisherResourceCaptureDiagnostic.NotAvailable));
        Assert.Null(PublisherAccountPresentation.ResourceCaptureGuidance(
            PublisherResourceCaptureDiagnostic.Valid));

        var labels = Enum.GetValues<PublisherResourceCaptureDiagnostic>()
            .Select(PublisherAccountPresentation.ResourceCaptureGuidance)
            .Where(static label => label is not null)
            .Cast<string>()
            .ToArray();
        Assert.All(labels, label =>
        {
            Assert.InRange(label.Length, 1, 40);
            Assert.DoesNotMatch("[0-9]", label);
            foreach (var forbidden in new[]
            {
                "http", "url", "query", "body", "header", "cookie",
                "uid", "email", "token", "exception", "role_id", "server",
                "retcode", "message", "status code",
            })
            {
                Assert.DoesNotContain(forbidden, label, StringComparison.OrdinalIgnoreCase);
            }
        });

        var render = Slice(
            ReadAppFile("MainPage.xaml.cs"),
            "private void RenderPublisherAccountStatus",
            "public static string FormatPublisherResource");
        var service = ReadAppFile("PublisherAccountService.cs");
        Assert.Contains("summary.ResourceDiagnostics", render, StringComparison.Ordinal);
        Assert.Contains(
            "PublisherAccountPresentation.ResourceCaptureGuidance",
            render,
            StringComparison.Ordinal);
        Assert.True(
            render.LastIndexOf(
                "WuWaAccountMetricsText.Text);",
                StringComparison.Ordinal)
            > render.IndexOf("if (gameId != \"ae\" && resource is null)", StringComparison.Ordinal));
        Assert.Contains("resourceRead.Diagnostic", service, StringComparison.Ordinal);
        Assert.Contains("SetResourceDiagnosticIfCurrent", service, StringComparison.Ordinal);
        Assert.Contains(
            "PublisherResourceCaptureDiagnostic.Valid",
            Slice(
                service,
                "if (resourceRead.Outcome == PublisherResourceReadOutcome.SelectionRequired)",
                "if (entry.Provider == \"HoYoLAB\""),
            StringComparison.Ordinal);
        Assert.DoesNotContain(".RoleId", render, StringComparison.Ordinal);
        Assert.DoesNotContain(".Server", render, StringComparison.Ordinal);
        Assert.DoesNotContain("AbsoluteUri", render, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", render, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hsr", PublisherResourceCaptureDiagnostic.EnvelopeRejected)]
    [InlineData("zzz", PublisherResourceCaptureDiagnostic.BrowserRequestBlocked)]
    [InlineData("zzz", PublisherResourceCaptureDiagnostic.OperationTimedOut)]
    [InlineData("hsr", PublisherResourceCaptureDiagnostic.LoginRequired)]
    public void Browser_teardown_preserves_prior_fixed_hsr_and_zzz_evidence(
        string gameId,
        PublisherResourceCaptureDiagnostic prior)
    {
        Assert.Equal(
            prior,
            PublisherResourceTeardownDiagnosticPolicy.ForQuarantine(
                gameId,
                prior,
                preservePriorEvidence: true));
    }

    [Theory]
    [InlineData(PublisherResourceCaptureDiagnostic.NotAvailable)]
    [InlineData(PublisherResourceCaptureDiagnostic.Valid)]
    [InlineData(PublisherResourceCaptureDiagnostic.SelectionRequired)]
    [InlineData((PublisherResourceCaptureDiagnostic)int.MaxValue)]
    public void Browser_teardown_uses_one_fixed_sanitized_fallback_without_trusting_unknown_values(
        PublisherResourceCaptureDiagnostic prior)
    {
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.BrowserSessionUnavailable,
            PublisherResourceTeardownDiagnosticPolicy.ForQuarantine(
                "zzz",
                prior,
                preservePriorEvidence: true));
    }

    [Theory]
    [InlineData("gi")]
    [InlineData("ae")]
    [InlineData("unknown")]
    public void Browser_teardown_fixed_diagnostic_does_not_expand_beyond_hsr_and_zzz(
        string gameId)
    {
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.NotAvailable,
            PublisherResourceTeardownDiagnosticPolicy.ForQuarantine(
                gameId,
                PublisherResourceCaptureDiagnostic.BrowserRequestBlocked,
                preservePriorEvidence: true));
    }

    [Theory]
    [InlineData("hsr", PublisherResourceCaptureDiagnostic.EnvelopeRejected)]
    [InlineData("zzz", PublisherResourceCaptureDiagnostic.BrowserRequestBlocked)]
    [InlineData("zzz", PublisherResourceCaptureDiagnostic.LoginRequired)]
    public void Stale_or_canceled_teardown_never_republishes_prior_evidence(
        string gameId,
        PublisherResourceCaptureDiagnostic prior)
    {
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.BrowserSessionUnavailable,
            PublisherResourceTeardownDiagnosticPolicy.ForQuarantine(
                gameId,
                prior,
                preservePriorEvidence: false));
    }

    [Fact]
    public void Multiple_roles_require_explicit_selection_and_never_auto_pick()
    {
        const long generation = 18;
        var firstBinding = new PublisherRoleBinding("123456789", "os_euro");
        var delayedBinding = new PublisherRoleBinding("987654321", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            RecoverySeconds: 36480);
        var capture = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(capture.Open(generation));
        Assert.True(capture.TryReserve(generation, "gi", firstBinding));
        Assert.True(capture.TryBeginResponse(generation, firstBinding));
        Assert.True(capture.CompleteResponse(generation, firstBinding, PublisherResourceProof.Valid, snapshot));

        // This request arrives after the first response, but before the
        // bounded observation is sealed. It must still make the result fail.
        Assert.True(capture.TryReserve(generation, "gi", delayedBinding));
        Assert.True(capture.TryBeginResponse(generation, delayedBinding));
        Assert.True(capture.CompleteResponse(
            generation,
            delayedBinding,
            PublisherResourceProof.Valid,
            snapshot));
        var result = capture.Seal(generation);

        Assert.Equal(PublisherResourceReadOutcome.SelectionRequired, result.Outcome);
        Assert.Null(result.Snapshot);
        Assert.Equal(2, result.Candidates!.Count);
        Assert.Equal(PublisherConnectionState.Connected, PublisherAccountStatePolicy.ForResourceRead(result));
    }

    [Fact]
    public void Stored_role_blocks_other_role_requests_and_chooser_explicitly_shows_full_uid_and_nickname()
    {
        const long generation = 19;
        var selected = new PublisherRoleBinding("123456789", "os_euro");
        var other = new PublisherRoleBinding("987654321", "os_usa");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            RecoverySeconds: 36480);
        var capture = new PublisherResourceCaptureAuthority("gi", generation, selected);

        Assert.True(capture.Open(generation));
        Assert.False(capture.TryReserve(generation, "gi", other));
        Assert.True(capture.TryReserve(generation, "gi", selected));
        Assert.True(capture.TryBeginResponse(generation, selected));
        Assert.True(capture.CompleteResponse(generation, selected, PublisherResourceProof.Valid, snapshot));
        Assert.Equal(PublisherResourceReadOutcome.Valid, capture.Seal(generation).Outcome);

        var choices = PublisherAccountCatalog.CreateRoleChoices(
            "gi",
            [
                new(selected, snapshot, "Lumine"),
                new(other, snapshot, "Aether"),
            ]);
        Assert.Equal(2, choices.Count);
        Assert.All(choices, choice => Assert.Contains(choice.Binding.RoleId, choice.DisplayText, StringComparison.Ordinal));
        Assert.Contains(
            choices,
            choice => choice.DisplayText == "Lumine · UID 123456789 · Europe");
        Assert.Contains(
            choices,
            choice => choice.DisplayText == "Aether · UID 987654321 · Americas");
        Assert.All(
            choices,
            choice => Assert.Equal(nameof(PublisherRoleChoice), choice.ToString()));
        Assert.DoesNotContain(selected.RoleId, selected.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Role_chooser_falls_back_to_full_uid_and_region_when_nickname_is_absent()
    {
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            RecoverySeconds: 36480);
        var choices = PublisherAccountCatalog.CreateRoleChoices(
            "gi",
            [
                new(new("123456789", "os_euro"), snapshot),
                new(new("987654321", "os_usa"), snapshot, "Aether"),
            ]);

        Assert.Contains(
            choices,
            choice => choice.DisplayText == "UID 123456789 · Europe");
        Assert.Contains(
            choices,
            choice => choice.DisplayText == "Aether · UID 987654321 · Americas");
    }

    [Fact]
    public void Sanitized_role_handoff_accepts_bounded_unicode_nickname_and_exact_fields()
    {
        const string raw =
            """{"state":"done","roles":[{"region":"os_euro","uid":"123456789","nickname":"旅人✨"}]}""";

        Assert.True(PublisherResourceTriggerResultParser.TryParse(
            "gi",
            raw,
            out var result));
        var role = Assert.Single(result!.Roles);
        Assert.Equal(new PublisherRoleBinding("123456789", "os_euro"), role.Binding);
        Assert.Equal("旅人✨", role.Nickname);
        Assert.Equal(nameof(PublisherResourceRoleIdentity), role.ToString());
        Assert.Equal(nameof(PublisherResourceTriggerResult), result.ToString());
    }

    [Fact]
    public void Sanitized_role_handoff_accepts_absent_nickname_as_optional()
    {
        const string raw =
            """{"state":"done","roles":[{"region":"os_euro","uid":"123456789","nickname":null}]}""";

        Assert.True(PublisherResourceTriggerResultParser.TryParse(
            "gi",
            raw,
            out var result));
        Assert.Null(Assert.Single(result!.Roles).Nickname);
    }

    [Theory]
    [InlineData("hsr", "signature-rejected")]
    [InlineData("hsr", "request-blocked")]
    [InlineData("zzz", "request-blocked")]
    [InlineData("hsr", "timed-out")]
    [InlineData("zzz", "timed-out")]
    public void Sanitized_trigger_handoff_accepts_only_fixed_scoped_failure_states(
        string gameId,
        string state)
    {
        var raw = JsonSerializer.Serialize(new
        {
            state,
            roles = Array.Empty<object>(),
        });

        Assert.True(PublisherResourceTriggerResultParser.TryParse(
            gameId,
            raw,
            out var result));
        Assert.Equal(state, result!.State);
        Assert.Empty(result.Roles);
        Assert.Equal(nameof(PublisherResourceTriggerResult), result.ToString());
    }

    [Theory]
    [InlineData("gi", """{"state":"request-blocked","roles":[]}""")]
    [InlineData("gi", """{"state":"timed-out","roles":[]}""")]
    [InlineData("zzz", """{"state":"signature-rejected","roles":[]}""")]
    [InlineData("hsr", """{"state":"request-blocked:private-message","roles":[]}""")]
    [InlineData("hsr", """{"state":"timed-out","roles":[],"error":"private-message"}""")]
    [InlineData("hsr", """{"state":"signature-rejected","roles":[{"region":"prod_official_eur","uid":"123456789","nickname":"private"}]}""")]
    public void Sanitized_trigger_handoff_rejects_unscoped_untrusted_or_identity_bearing_failures(
        string gameId,
        string raw)
    {
        Assert.False(PublisherResourceTriggerResultParser.TryParse(
            gameId,
            raw,
            out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData("""{"state":"done","roles":[{"region":"os_euro","uid":"123456789","nickname":"bad\u000a"]}""")]
    [InlineData("""{"state":"done","roles":[{"region":"os_euro","uid":"123456789","nickname":{"text":"Aether"}}]}""")]
    [InlineData("""{"state":"done","roles":[{"region":"os_euro","uid":"123456789","nickname":"Aether","cookie":"secret"}]}""")]
    [InlineData("""{"state":"done","roles":[{"region":"os_euro","uid":"123456789","nickname":"Aether"},{"region":"os_euro","uid":"123456789","nickname":"Other"}]}""")]
    [InlineData("""{"state":"done","roles":[{"region":"attacker","uid":"123456789","nickname":"Aether"}]}""")]
    [InlineData("""{"state":"done","roles":[{"region":"os_euro","uid":"not-a-uid","nickname":"Aether"}]}""")]
    [InlineData("""{"state":"invalid","roles":[{"region":"os_euro","uid":"123456789","nickname":"Aether"}]}""")]
    [InlineData("""{"state":"done","roles":[],"error":"raw-script-error"}""")]
    public void Sanitized_role_handoff_rejects_controls_duplicates_untrusted_fields_and_scope_changes(
        string raw)
    {
        Assert.False(PublisherResourceTriggerResultParser.TryParse(
            "gi",
            raw,
            out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Sanitized_role_handoff_rejects_oversized_utf8_nickname_and_payload()
    {
        var nickname = new string('é', 33);
        var raw =
            $$"""{"state":"done","roles":[{"region":"os_euro","uid":"123456789","nickname":{{JsonSerializer.Serialize(nickname)}}}]}""";

        Assert.False(PublisherResourceTriggerResultParser.TryParse(
            "gi",
            raw,
            out _));
        Assert.False(PublisherResourceTriggerResultParser.TryParse(
            "gi",
            new string('x', PublisherResourceTriggerResultParser.MaximumPayloadCharacters + 1),
            out _));
    }

    [Fact]
    public void Sanitized_role_handoff_is_required_to_match_every_unknown_capture_binding()
    {
        const long generation = 125;
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            RecoverySeconds: 36480);
        var first = new PublisherRoleBinding("123456789", "os_euro");
        var second = new PublisherRoleBinding("987654321", "os_usa");
        var capture = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(capture.Open(generation));
        foreach (var binding in new[] { first, second })
        {
            Assert.True(capture.TryReserve(generation, "gi", binding));
            Assert.True(capture.TryBeginResponse(generation, binding));
            Assert.True(capture.CompleteResponse(
                generation,
                binding,
                PublisherResourceProof.Valid,
                snapshot));
        }
        Assert.True(PublisherResourceTriggerResultParser.TryParse(
            "gi",
            """{"state":"done","roles":[{"region":"os_euro","uid":"123456789","nickname":"Lumine"},{"region":"os_usa","uid":"987654321","nickname":"Aether"}]}""",
            out var trigger));

        var result = PublisherResourceTriggerPolicy.Seal(
            capture,
            generation,
            trigger);

        Assert.Equal(PublisherResourceReadOutcome.SelectionRequired, result.Outcome);
        var resultCandidates = Assert.IsAssignableFrom<IReadOnlyList<PublisherResourceCandidate>>(
            result.Candidates);
        Assert.Equal("Lumine", resultCandidates.Single(candidate => candidate.Binding == first).Nickname);
        Assert.Equal("Aether", resultCandidates.Single(candidate => candidate.Binding == second).Nickname);
    }

    [Fact]
    public void Sanitized_role_handoff_mismatch_fails_closed_after_valid_note_evidence()
    {
        const long generation = 126;
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            RecoverySeconds: 36480);
        var capture = new PublisherResourceCaptureAuthority("gi", generation);
        Assert.True(capture.Open(generation));
        Assert.True(capture.TryReserve(generation, "gi", binding));
        Assert.True(capture.TryBeginResponse(generation, binding));
        Assert.True(capture.CompleteResponse(
            generation,
            binding,
            PublisherResourceProof.Valid,
            snapshot));
        Assert.True(PublisherResourceTriggerResultParser.TryParse(
            "gi",
            """{"state":"done","roles":[{"region":"os_usa","uid":"987654321","nickname":"Aether"}]}""",
            out var trigger));

        var result = PublisherResourceTriggerPolicy.Seal(
            capture,
            generation,
            trigger);

        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, result.Outcome);
        Assert.Equal(PublisherResourceCaptureDiagnostic.ResponseRejected, result.Diagnostic);
        Assert.Null(result.Candidates);
    }

    [Fact]
    public void Trigger_policy_defensively_rejects_direct_duplicate_or_untrusted_identity_objects()
    {
        static PublisherResourceCaptureAuthority Completed(
            long generation,
            PublisherRoleBinding binding,
            PublisherResourceSnapshot snapshot)
        {
            var capture = new PublisherResourceCaptureAuthority("gi", generation);
            Assert.True(capture.Open(generation));
            Assert.True(capture.TryReserve(generation, "gi", binding));
            Assert.True(capture.TryBeginResponse(generation, binding));
            Assert.True(capture.CompleteResponse(
                generation,
                binding,
                PublisherResourceProof.Valid,
                snapshot));
            return capture;
        }

        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-29T12:00:00Z"),
            RecoverySeconds: 36480);
        var duplicate = new PublisherResourceTriggerResult(
            "done",
            [
                new(binding, "Lumine"),
                new(binding, "Lumine"),
            ]);
        var untrusted = new PublisherResourceTriggerResult(
            "done",
            [new(binding, "bad\nname")]);

        var duplicateResult = PublisherResourceTriggerPolicy.Seal(
            Completed(127, binding, snapshot),
            127,
            duplicate);
        var untrustedResult = PublisherResourceTriggerPolicy.Seal(
            Completed(128, binding, snapshot),
            128,
            untrusted);

        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, duplicateResult.Outcome);
        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, untrustedResult.Outcome);
        Assert.Null(duplicateResult.Candidates);
        Assert.Null(untrustedResult.Candidates);
    }

    [Fact]
    public void Daily_one_role_is_resolved_and_may_use_account_wide_status()
    {
        var role = new PublisherRoleBinding("123456789", "os_euro");
        var result = PublisherDailyRolePolicy.Resolve(
            "gi",
            DailyRoleRead("gi", PublisherResourceReadOutcome.Valid, role),
            storedBinding: null);

        Assert.Equal(PublisherDailyRoleResolutionState.Resolved, result.State);
        Assert.Equal(role, result.Binding);
        Assert.True(result.AccountWideStatusAllowed);
        Assert.False(result.StoredBindingStillMatches);
    }

    [Fact]
    public void Daily_saved_valid_role_is_reused_only_when_fresh_discovery_still_contains_it()
    {
        var saved = new PublisherRoleBinding("123456789", "prod_official_eur");
        var other = new PublisherRoleBinding("987654321", "prod_official_usa");
        var result = PublisherDailyRolePolicy.Resolve(
            "hsr",
            DailyRoleRead(
                "hsr",
                PublisherResourceReadOutcome.SelectionRequired,
                saved,
                other),
            saved);

        Assert.Equal(PublisherDailyRoleResolutionState.Resolved, result.State);
        Assert.Equal(saved, result.Binding);
        Assert.False(result.AccountWideStatusAllowed);
        Assert.True(result.StoredBindingStillMatches);
    }

    [Fact]
    public void Daily_multiple_roles_without_a_valid_choice_require_selection()
    {
        var first = new PublisherRoleBinding("123456789", "prod_gf_eu");
        var second = new PublisherRoleBinding("987654321", "prod_gf_us");
        var result = PublisherDailyRolePolicy.Resolve(
            "zzz",
            DailyRoleRead(
                "zzz",
                PublisherResourceReadOutcome.SelectionRequired,
                first,
                second),
            storedBinding: null);

        Assert.Equal(PublisherDailyRoleResolutionState.SelectionRequired, result.State);
        Assert.Null(result.Binding);
        Assert.False(result.AccountWideStatusAllowed);
        Assert.Equal(2, result.Choices.Count);
        Assert.All(
            result.Choices,
            choice => Assert.Contains(
                choice.Binding.RoleId,
                choice.DisplayText,
                StringComparison.Ordinal));
    }

    [Fact]
    public void Daily_explicit_picker_choice_must_be_one_of_the_fresh_transient_choices()
    {
        var first = new PublisherRoleBinding("123456789", "os_euro");
        var selected = new PublisherRoleBinding("987654321", "os_usa");
        var read = DailyRoleRead(
            "gi",
            PublisherResourceReadOutcome.SelectionRequired,
            first,
            selected);

        var accepted = PublisherDailyRolePolicy.Resolve(
            "gi",
            read,
            storedBinding: null,
            explicitSelection: selected);
        var injected = PublisherDailyRolePolicy.Resolve(
            "gi",
            read,
            storedBinding: null,
            explicitSelection: new PublisherRoleBinding("111111111", "os_asia"));

        Assert.Equal(PublisherDailyRoleResolutionState.Resolved, accepted.State);
        Assert.Equal(selected, accepted.Binding);
        Assert.False(accepted.AccountWideStatusAllowed);
        Assert.Equal(PublisherDailyRoleResolutionState.SelectionRequired, injected.State);
        Assert.Null(injected.Binding);
    }

    [Fact]
    public void Daily_multi_role_stored_and_picker_resolution_end_with_valid_diagnostic()
    {
        var stored = new PublisherRoleBinding("123456789", "os_euro");
        var picked = new PublisherRoleBinding("987654321", "os_usa");
        var read = DailyRoleRead(
            "gi",
            PublisherResourceReadOutcome.SelectionRequired,
            stored,
            picked) with
        {
            Diagnostic = PublisherResourceCaptureDiagnostic.SelectionRequired,
        };

        var storedResolution = PublisherDailyRolePolicy.Resolve(
            "gi",
            read,
            storedBinding: stored);
        var pickerResolution = PublisherDailyRolePolicy.Resolve(
            "gi",
            read,
            storedBinding: null,
            explicitSelection: picked);
        var unresolved = PublisherDailyRolePolicy.Resolve(
            "gi",
            read,
            storedBinding: null);

        Assert.True(storedResolution.StoredBindingStillMatches);
        Assert.Equal(PublisherDailyRoleResolutionState.Resolved, pickerResolution.State);
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.Valid,
            PublisherDailyRolePolicy.FinalDiagnostic(read, storedResolution));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.Valid,
            PublisherDailyRolePolicy.FinalDiagnostic(read, pickerResolution));
        Assert.Equal(
            PublisherResourceCaptureDiagnostic.SelectionRequired,
            PublisherDailyRolePolicy.FinalDiagnostic(read, unresolved));
    }

    [Fact]
    public void Daily_stale_binding_or_account_change_never_reuses_the_missing_role()
    {
        var stale = new PublisherRoleBinding("123456789", "os_euro");
        var current = new PublisherRoleBinding("987654321", "os_usa");
        var oneRole = PublisherDailyRolePolicy.Resolve(
            "gi",
            DailyRoleRead("gi", PublisherResourceReadOutcome.Valid, current),
            stale);
        var multipleRoles = PublisherDailyRolePolicy.Resolve(
            "gi",
            DailyRoleRead(
                "gi",
                PublisherResourceReadOutcome.SelectionRequired,
                current,
                new PublisherRoleBinding("111111111", "os_asia")),
            stale);

        Assert.Equal(PublisherDailyRoleResolutionState.Resolved, oneRole.State);
        Assert.Equal(current, oneRole.Binding);
        Assert.True(oneRole.AccountWideStatusAllowed);
        Assert.False(oneRole.StoredBindingStillMatches);
        Assert.Equal(PublisherDailyRoleResolutionState.SelectionRequired, multipleRoles.State);
        Assert.Null(multipleRoles.Binding);
        Assert.False(multipleRoles.StoredBindingStillMatches);
    }

    [Fact]
    public void Daily_role_policy_reports_login_and_review_without_a_binding()
    {
        var login = PublisherDailyRolePolicy.Resolve(
            "gi",
            new(null, PublisherResourceReadOutcome.LoginRequired),
            storedBinding: null);
        var review = PublisherDailyRolePolicy.Resolve(
            "gi",
            new(null, PublisherResourceReadOutcome.NeedsReview),
            storedBinding: null);

        Assert.Equal(PublisherDailyRoleResolutionState.LoginRequired, login.State);
        Assert.Null(login.Binding);
        Assert.Equal(PublisherDailyRoleResolutionState.NeedsReview, review.State);
        Assert.Null(review.Binding);
    }

    [Fact]
    public void Daily_status_request_matches_any_supplied_role_and_allows_account_scope_after_role_proof()
    {
        var expected = new PublisherRoleBinding("123456789", "prod_official_eur");
        var exact = new Uri(
            "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info"
            + "?act_id=e202303301540311&lang=en-us&region=prod_official_eur&uid=123456789"
            + "&publisher_version=10");
        var wrongRole = new Uri(
            "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info"
            + "?act_id=e202303301540311&lang=en-us&region=prod_official_eur&uid=987654321");
        var wrongRegion = new Uri(
            "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info"
            + "?act_id=e202303301540311&lang=en-us&region=prod_official_usa&uid=123456789");
        var accountWide = new Uri(
            "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info"
            + "?act_id=e202303301540311&lang=en-us");
        var emptyRole = new Uri(
            "https://sg-act-public-api.hoyolab.com/event/luna/hkrpg/os/info"
            + "?act_id=e202303301540311&region=&uid=&publisher_version=10");

        Assert.True(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", exact, "GET", expected, allowAccountWideStatus: false));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", wrongRole, "GET", expected, allowAccountWideStatus: false));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", wrongRegion, "GET", expected, allowAccountWideStatus: false));
        Assert.True(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", accountWide, "GET", expected, allowAccountWideStatus: false));
        Assert.True(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", accountWide, "GET", expected, allowAccountWideStatus: true));
        Assert.True(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", emptyRole, "GET", expected, allowAccountWideStatus: false));
        Assert.True(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", emptyRole, "GET", expected, allowAccountWideStatus: true));
        Assert.False(PublisherAccountCatalog.IsExactCheckInResponseUri(
            "hsr", accountWide, "GET", expectedBinding: null, allowAccountWideStatus: false));
    }

    [Fact]
    public void Daily_capture_clears_candidate_noise_but_preserves_selected_response_failures()
    {
        var gate = new PublisherCheckInCaptureDiagnosticGate();

        gate.MarkCandidate(PublisherCheckInCaptureDiagnostic.EndpointQueryRejected);
        Assert.Equal(PublisherCheckInCaptureDiagnostic.EndpointQueryRejected, gate.Current);

        Assert.True(gate.TryBeginSelectedResponse());
        Assert.Equal(PublisherCheckInCaptureDiagnostic.None, gate.Current);

        gate.MarkCandidate(PublisherCheckInCaptureDiagnostic.EndpointQueryRejected);
        Assert.Equal(PublisherCheckInCaptureDiagnostic.None, gate.Current);

        gate.MarkSelectedResponse(PublisherCheckInCaptureDiagnostic.InvalidBody);
        Assert.Equal(PublisherCheckInCaptureDiagnostic.InvalidBody, gate.Current);
        Assert.False(gate.TryBeginSelectedResponse());
    }

    [Fact]
    public void Stored_role_capture_bounds_rejected_role_attempts()
    {
        const long generation = 20;
        var selected = new PublisherRoleBinding("123456789", "os_euro");
        var capture = new PublisherResourceCaptureAuthority("gi", generation, selected);
        Assert.True(capture.Open(generation));

        for (var index = 0; index < 9; index++)
        {
            var other = new PublisherRoleBinding((900000000 + index).ToString(), "os_usa");
            Assert.False(capture.TryReserve(generation, "gi", other));
        }

        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, capture.Seal(generation).Outcome);
    }

    [Fact]
    public void Previous_generation_requests_are_ignored_and_pending_timeout_fails_closed()
    {
        const long generation = 22;
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var capture = new PublisherResourceCaptureAuthority("gi", generation);

        Assert.True(capture.Open(generation));
        Assert.False(capture.TryReserve(generation - 1, "gi", binding));
        Assert.True(capture.TryReserve(generation, "gi", binding));
        Assert.False(capture.TryBeginResponse(generation - 1, binding));

        var result = capture.Seal(generation);
        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, result.Outcome);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Resource_schema_drift_demotes_to_review_but_explicit_auth_rejection_demotes_to_login()
    {
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var malformed = new PublisherResourceCaptureAuthority("gi", 30);
        Assert.True(malformed.Open(30));
        Assert.True(malformed.TryReserve(30, "gi", binding));
        Assert.True(malformed.TryBeginResponse(30, binding));
        Assert.True(malformed.CompleteResponse(30, binding, PublisherResourceProof.Invalid, null));
        var malformedResult = malformed.Seal(30);
        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, malformedResult.Outcome);
        Assert.Equal(
            PublisherConnectionState.NeedsReview,
            PublisherAccountStatePolicy.ForResourceRead(malformedResult));

        var rejected = new PublisherResourceCaptureAuthority("gi", 31);
        Assert.True(rejected.Open(31));
        Assert.True(rejected.TryReserve(31, "gi", binding));
        Assert.True(rejected.TryBeginResponse(31, binding));
        Assert.True(rejected.CompleteResponse(31, binding, PublisherResourceProof.LoginNeeded, null));
        var rejectedResult = rejected.Seal(31);
        Assert.Equal(PublisherResourceReadOutcome.LoginRequired, rejectedResult.Outcome);
        Assert.Equal(
            PublisherConnectionState.LoginRequired,
            PublisherAccountStatePolicy.ForResourceRead(rejectedResult));

        Assert.Equal(
            PublisherConnectionState.NeedsReview,
            PublisherAccountStatePolicy.ForResourceRead(
                new PublisherResourceReadResult(null, PublisherResourceReadOutcome.Valid)));
    }

    [Fact]
    public void Mixed_valid_and_login_resource_proofs_are_ambiguous_and_need_review()
    {
        const long generation = 32;
        var binding = new PublisherRoleBinding("123456789", "os_euro");
        var snapshot = new PublisherResourceSnapshot(
            "gi",
            "Original Resin",
            124,
            200,
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            RecoverySeconds: 36480);
        var capture = new PublisherResourceCaptureAuthority("gi", generation);

        Assert.True(capture.Open(generation));
        Assert.True(capture.TryReserve(generation, "gi", binding));
        Assert.True(capture.TryReserve(generation, "gi", binding));
        Assert.True(capture.TryBeginResponse(generation, binding));
        Assert.True(capture.CompleteResponse(generation, binding, PublisherResourceProof.Valid, snapshot));
        Assert.True(capture.TryBeginResponse(generation, binding));
        Assert.True(capture.CompleteResponse(generation, binding, PublisherResourceProof.LoginNeeded, null));

        var result = capture.Seal(generation);
        Assert.Equal(PublisherResourceReadOutcome.NeedsReview, result.Outcome);
        Assert.Equal(PublisherConnectionState.NeedsReview, PublisherAccountStatePolicy.ForResourceRead(result));
    }

    [Fact]
    public void Resource_binding_is_taken_from_the_exact_authenticated_endpoint_query()
    {
        Assert.True(PublisherAccountCatalog.TryGetResourceBinding(
            "gi",
            new Uri("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro"),
            out var binding));
        Assert.Equal(new PublisherRoleBinding("123456789", "os_euro"), binding);
        Assert.False(PublisherAccountCatalog.TryGetResourceBinding(
            "gi",
            new Uri("https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro&role_id=987654321"),
            out _));
    }

    [Theory]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://act.hoyolab.com/ys/event/signin-sea-v3/index.html?act_id=e202102251931481", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Resource, "gi", "https://act.hoyolab.com/app/community-game-records-sea/index.html", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "gi", "https://account.hoyoverse.com/passport/index.html?origin=account", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "gi", "https://account.hoyoverse.com/single-page?origin=account", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "gi", "https://account.hoyoverse.com/passport/assets/main.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/login-platform/index.html?app_id=c9oqaq3s3gu8", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/login-platform/chunk-common.8caf3da0.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/login-platform/web.8caf3da0.css", "GET", PublisherWebResourceContext.Stylesheet)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/login-platform/password-login-web.8caf3da0.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/login-platform/password-login-web.8caf3da0.css", "GET", PublisherWebResourceContext.Stylesheet)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/m02091416191721-en-us.json", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/bh3_global/20190812_5d51512fdef47/20190812_5d51512fdef47-en-us.json", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/bbs_oversea/m07281525151831/m07281525151831-en-us.json", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://sg-public-api-static.hoyolab.com/account/ma-passport/api/getSwitchStatus?app_id=c9oqaq3s3gu8&platform=4", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Achievements, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/m02091416191721-en-us.json", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Achievements, "hsr", "https://sg-public-api-static.hoyolab.com/account/ma-passport/api/getSwitchStatus?app_id=c9oqaq3s3gu8&platform=4", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://bbs-api-os.hoyolab.com/community/misc/wapi/langs?lang2022=true", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://sdk-os-static.hoyoverse.com/combo/box/api/config/porte-fe-os/config?type=common", "GET", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://sg-public-data-api.hoyoverse.com/device-fp/api/getExtList?platform=4&app_name=hkrpg_global", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://webstatic.hoyoverse.com/dora/biz/mihoyo-account-sdk/main.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "hsr", "https://upload-static.hoyoverse.com/event/2023/04/21/reward.png", "GET", PublisherWebResourceContext.Image)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "zzz", "https://act-webstatic.hoyoverse.com/event-static/2024/06/17/reward.png", "GET", PublisherWebResourceContext.Image)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Resource, "gi", "https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro", "GET", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://sg-act-public-api.hoyolab.com/event/sol/sign?lang=en-us", "OPTIONS", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "gi", "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/getSwitchStatus?app_id=c9oqaq3s3gu8&platform=4", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("SKPORT", PublisherSessionPurpose.CheckIn, "ae", "https://game.skport.com/endfield/sign-in", "GET", PublisherWebResourceContext.Document)]
    [InlineData("SKPORT", PublisherSessionPurpose.CheckIn, "ae", "https://static.skport.com/skport-fe-static/skport-game-tools/1412.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("SKPORT", PublisherSessionPurpose.CheckIn, "ae", "https://web-api.skport.com/cookie_store/account_token", "GET", PublisherWebResourceContext.Fetch)]
    [InlineData("SKPORT", PublisherSessionPurpose.CheckIn, "ae", "https://zonai.skport.com/web/v1/game/endfield/attendance", "OPTIONS", PublisherWebResourceContext.Other)]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "ae", "https://as.gryphline.com/user/info/v1/basic?token=opaque-token", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "ae", "https://binding-api-account-prod.gryphline.com/account/binding/v1/binding_list?token=opaque-token&appCode=endfield", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    public void Publisher_request_policy_preserves_only_reviewed_required_requests(
        string provider,
        PublisherSessionPurpose purpose,
        string gameId,
        string value,
        string method,
        PublisherWebResourceContext context)
    {
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            provider,
            purpose,
            gameId,
            new Uri(value),
            method,
            context));
    }

    [Theory]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://account.hoyoverse.com/passport/index.html", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://account.hoyoverse.com/passport/assets/main.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "hsr", "https://account.hoyolab.com/login-platform/index.html", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/passport/index.html", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/single-page/index.html", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/ue/login-platform", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/login-platform/private.8caf3da1.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/login-platform/web.8caf3da0.js?extra=1", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://account.hoyolab.com/login-platform/password-login-web.8caf3da1.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/other-en-us.json", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/m02091416191721-fr-fr.json", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/m02091416191721-en-us.json?extra=1", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://evil.webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/m02091416191721-en-us.json", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/m02091416191721-en-us.json", "POST", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/m02091416191721-en-us.json", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://sg-public-api-static.hoyolab.com/account/ma-passport/api/getSwitchStatus?app_id=ciebhwzprpq8&platform=4", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "hsr", "https://sg-public-api-static.hoyolab.com/account/ma-passport/api/getSwitchStatus?app_id=c9oqaq3s3gu8&platform=4&extra=1", "GET", PublisherWebResourceContext.XmlHttpRequest)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://act.hoyolab.com.evil.example/ys/event/signin-sea-v3/index.html", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "http://act.hoyolab.com/ys/event/signin-sea-v3/index.html?act_id=e202102251931481", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://act.hoyolab.com:444/ys/event/signin-sea-v3/index.html?act_id=e202102251931481", "GET", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://act.hoyolab.com/unreviewed/script.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "hsr", "https://upload-static.hoyoverse.com/unreviewed/script.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "zzz", "https://act-webstatic.hoyoverse.com/event-static/2024/06/17/script.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://google-analytics.com/g/collect", "POST", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Resource, "gi", "https://sg-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro", "GET", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Resource, "gi", "https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro&lang=en-us", "GET", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Resource, "gi", "https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/dailyNote?role_id=123456789&server=os_euro", "GET", PublisherWebResourceContext.Script)]
    [InlineData("SKPORT", PublisherSessionPurpose.CheckIn, "ae", "https://web-api.skport.com/cookie_store/other", "GET", PublisherWebResourceContext.Fetch)]
    [InlineData("SKPORT", PublisherSessionPurpose.CheckIn, "ae", "https://static.skport.com/unreviewed/main.js", "GET", PublisherWebResourceContext.Script)]
    [InlineData("SKPORT", PublisherSessionPurpose.CheckIn, "ae", "https://game.skport.com/endfield/sign-in", "POST", PublisherWebResourceContext.Document)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Resource, "gi", "https://sg-hk4e-api.hoyolab.com/event/sol/sign", "OPTIONS", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://sg-hk4e-api.hoyolab.com/event/sol/info?act_id=e202102251931481&lang=en-us", "GET", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.CheckIn, "gi", "https://sg-hk4e-api.hoyolab.com/event/sol/sign", "OPTIONS", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "gi", "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/deleteAccount", "OPTIONS", PublisherWebResourceContext.Fetch)]
    [InlineData("HoYoLAB", PublisherSessionPurpose.Connect, "gi", "https://api-account-os.hoyoverse.com/account/auth/api/webLoginByPassword", "OPTIONS", PublisherWebResourceContext.Fetch)]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "ae", "https://as.gryphline.com/user/auth/v1/register", "OPTIONS", PublisherWebResourceContext.Fetch)]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "ae", "https://binding-api-account-prod.gryphline.com/account/binding/v1/set_default_role", "OPTIONS", PublisherWebResourceContext.Fetch)]
    [InlineData("SKPORT", PublisherSessionPurpose.Connect, "ae", "https://web-api.skport.com/cookie_store/other", "OPTIONS", PublisherWebResourceContext.Fetch)]
    public void Publisher_request_policy_blocks_unreviewed_hosts_paths_ports_methods_and_contexts(
        string provider,
        PublisherSessionPurpose purpose,
        string gameId,
        string value,
        string method,
        PublisherWebResourceContext context)
    {
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            provider,
            purpose,
            gameId,
            new Uri(value),
            method,
            context));
    }

    [Fact]
    public void Current_hoyolab_connect_requests_reject_bodies_and_wrong_purposes()
    {
        var loginPage = new Uri(
            "https://account.hoyolab.com/login-platform/index.html?app_id=c9oqaq3s3gu8");
        var localization = new Uri(
            "https://webstatic.hoyoverse.com/admin/mi18n/hkrpg_global/m02091416191721/m02091416191721-en-us.json");
        var passwordLogin = new Uri(
            "https://passport-api-sg.hoyolab.com/account/ma-passport/api/webLoginByPassword");
        var passwordBody = Encoding.UTF8.GetBytes(
            """{"account":"encrypted-account","password":"encrypted-password","token_type":6}""");

        Assert.True(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "hsr",
            loginPage));
        Assert.False(PublisherAccountCatalog.IsAllowedTopLevelNavigation(
            "HoYoLAB",
            PublisherSessionPurpose.CheckIn,
            "hsr",
            loginPage));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "hsr",
            localization,
            "GET",
            PublisherWebResourceContext.XmlHttpRequest,
            requestBody: Encoding.UTF8.GetBytes("{}"),
            contentType: "application/json"));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Achievements,
            "hsr",
            passwordLogin,
            "POST",
            PublisherWebResourceContext.XmlHttpRequest,
            requestBody: passwordBody,
            contentType: "application/json"));
    }

    [Fact]
    public void Exact_claim_write_is_wrong_purpose_until_armed_then_is_consumed_once()
    {
        var claim = new Uri("https://sg-act-public-api.hoyolab.com/event/sol/sign?lang=en-us");
        var retiredClaim = new Uri("https://sg-hk4e-api.hoyolab.com/event/sol/sign");
        var authority = new PublisherClaimWriteAuthority();
        using var scope = authority.Arm("gi");

        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "gi",
            claim,
            "POST",
            PublisherWebResourceContext.Fetch,
            authority));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.CheckIn,
            "gi",
            retiredClaim,
            "POST",
            PublisherWebResourceContext.Fetch,
            authority));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.CheckIn,
            "gi",
            claim,
            "OPTIONS",
            PublisherWebResourceContext.Fetch,
            authority));
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.CheckIn,
            "gi",
            claim,
            "POST",
            PublisherWebResourceContext.Fetch,
            authority));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.CheckIn,
            "gi",
            claim,
            "POST",
            PublisherWebResourceContext.Fetch,
            authority));
        Assert.Throws<InvalidOperationException>(() => authority.Arm("gi"));
    }

    [Fact]
    public void Claim_scope_is_game_bound_and_revoked_even_when_unused()
    {
        var giClaim = new Uri("https://sg-act-public-api.hoyolab.com/event/sol/sign?lang=en-us");
        var authority = new PublisherClaimWriteAuthority();
        using (authority.Arm("hsr"))
        {
            Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
                "HoYoLAB",
                PublisherSessionPurpose.CheckIn,
                "hsr",
                giClaim,
                "POST",
                PublisherWebResourceContext.Fetch,
                authority));
        }

        Assert.False(authority.TryConsume("hsr"));
    }

    [Fact]
    public void Connect_auth_writes_do_not_authorize_claim_or_resource_mutations()
    {
        var accountLogin = new Uri("https://passport-api-eu.hoyoverse.com/account/ma-passport/api/webLoginByPassword");
        var accountLoginBody = Encoding.UTF8.GetBytes(
            """{"account":"encrypted-account","password":"encrypted-password","token_type":2}""");
        var claim = new Uri("https://sg-act-public-api.hoyolab.com/event/sol/sign?lang=en-us");
        var communityMutation = new Uri("https://bbs-api-os.hoyolab.com/community/painter/wapi/post");

        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "gi",
            accountLogin,
            "POST",
            PublisherWebResourceContext.XmlHttpRequest,
            requestBody: accountLoginBody,
            contentType: "application/json; charset=utf-8"));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Resource,
            "gi",
            accountLogin,
            "POST",
            PublisherWebResourceContext.XmlHttpRequest));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "gi",
            claim,
            "POST",
            PublisherWebResourceContext.Fetch));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.Connect,
            "gi",
            communityMutation,
            "POST",
            PublisherWebResourceContext.Fetch));

        var skportClaim = new Uri("https://zonai.skport.com/web/v1/game/endfield/attendance");
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "SKPORT",
            PublisherSessionPurpose.Connect,
            "ae",
            skportClaim,
            "POST",
            PublisherWebResourceContext.Fetch));

        var skportAuthority = new PublisherClaimWriteAuthority();
        using var skportScope = skportAuthority.Arm("ae");
        Assert.True(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "SKPORT",
            PublisherSessionPurpose.CheckIn,
            "ae",
            skportClaim,
            "POST",
            PublisherWebResourceContext.Fetch,
            skportAuthority));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "SKPORT",
            PublisherSessionPurpose.CheckIn,
            "ae",
            skportClaim,
            "POST",
            PublisherWebResourceContext.Fetch,
            skportAuthority));
    }

    [Fact]
    public void Exact_reviewed_connect_inventory_accepts_required_routes_and_json_shapes()
    {
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/getSwitchStatus?app_id=c9oqaq3s3gu8&platform=4",
            "GET",
            gameId: "gi"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/getSwitchStatus?app_id=ciebhwzprpq8&platform=4",
            "GET",
            gameId: "hsr"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/getSwitchStatus?app_id=cieaz4epd5vk&platform=4",
            "GET",
            gameId: "zzz"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/getConfig",
            "POST",
            "{}"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/webLoginByPassword",
            "POST",
            """{"account":"encrypted-account","password":"encrypted-password","token_type":2}"""));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-sg.hoyolab.com/account/ma-passport/api/webLoginByPassword",
            "POST",
            """{"account":"encrypted-account","password":"encrypted-password","token_type":6}""",
            gameId: "hsr"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://sg-public-data-api.hoyoverse.com/device-fp/api/getFp",
            "POST",
            """
            {
              "app_name":"hkrpg_global",
              "device_fp":"test-fingerprint",
              "device_id":"test-device",
              "ext_fields":"{}",
              "platform":"4",
              "seed_id":"test-seed",
              "seed_time":"test-time"
            }
            """,
            gameId: "hsr",
            context: PublisherWebResourceContext.XmlHttpRequest));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/webLoginByPassword",
            "OPTIONS"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-sg.hoyolab.com/account/ma-passport/api/webLoginByPassword",
            "OPTIONS",
            gameId: "hsr"));

        Assert.True(AllowsConnect(
            "SKPORT",
            "https://as.gryphline.com/user/auth/v1/token_by_email_password",
            "POST",
            """{"email":"person@example.com","password":"secret"}"""));
        Assert.True(AllowsConnect(
            "SKPORT",
            "https://as.gryphline.com/user/oauth2/v2/grant",
            "POST",
            """{"token":"opaque-token","appCode":"endfield","type":1}"""));
        Assert.True(AllowsConnect(
            "SKPORT",
            "https://web-api.skport.com/cookie_store/account_token",
            "POST",
            """{"content":"opaque-token"}"""));
        Assert.True(AllowsConnect(
            "SKPORT",
            "https://zonai.skport.com/web/v1/user/auth/generate_cred_by_code",
            "POST",
            """{"kind":1,"code":"opaque-code"}"""));
        Assert.True(AllowsConnect(
            "SKPORT",
            "https://as.gryphline.com/user/oauth2/v2/grant",
            "OPTIONS"));
    }

    [Fact]
    public void Connect_rejects_unknown_posts_and_preflights_under_reviewed_prefixes()
    {
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/getSwitchStatus?app_id=ciebhwzprpq8&platform=4",
            "GET",
            gameId: "gi"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://account.hoyoverse.com/account/ma-passport/api/getConfig",
            "POST",
            "{}"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://bbs-api-os.hoyolab.com/community/private/future-account-data",
            "GET"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://account.hoyoverse.com/login-platform/private/future-account-data",
            "GET"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://webstatic.hoyoverse.com/dora/private/future-account-data",
            "GET"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/deleteAccount",
            "POST",
            "{}"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/deleteAccount",
            "OPTIONS"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-sg.hoyolab.com/account/ma-passport/api/getConfig",
            "POST",
            "{}",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-sg.hoyolab.com/account/ma-passport/api/getConfig",
            "OPTIONS",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://as.gryphline.com/user/auth/v1/register",
            "POST",
            """{"email":"person@example.com","password":"secret","code":"123456"}"""));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://as.gryphline.com/user/auth/v1/register",
            "OPTIONS"));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://binding-api-account-prod.gryphline.com/account/binding/v1/set_default_role",
            "POST",
            """{"token":"opaque-token","appCode":"endfield","uid":"123"}"""));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://binding-api-account-prod.gryphline.com/account/binding/v1/set_default_role",
            "OPTIONS"));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://static.skport.com/skport-fe-static/skport-game-tools/private-account-data",
            "GET"));
    }

    [Fact]
    public void Connect_allows_only_the_exact_read_only_hoyolab_profile_refresh()
    {
        const string profile =
            "https://bbs-api-os.hoyolab.com/community/user/wapi/getUserFullInfo?t=123456789";

        Assert.True(AllowsConnect(
            "HoYoLAB",
            profile,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.XmlHttpRequest));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            profile,
            "POST",
            "{}",
            gameId: "hsr",
            context: PublisherWebResourceContext.XmlHttpRequest));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            profile,
            "GET",
            "{}",
            gameId: "hsr",
            context: PublisherWebResourceContext.XmlHttpRequest));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://bbs-api-os.hoyolab.com/community/user/wapi/getUserFullInfoExtra?t=123456789",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.XmlHttpRequest));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://sg-public-api.hoyolab.com/community/user/wapi/getUserFullInfo?t=123456789",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.XmlHttpRequest));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            profile,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Other));
    }

    [Fact]
    public void Current_hsr_connect_support_requests_reject_neighboring_routes_and_shapes()
    {
        const string language =
            "https://bbs-api-os.hoyolab.com/community/misc/wapi/langs?lang2022=true";
        const string config =
            "https://sdk-os-static.hoyoverse.com/combo/box/api/config/porte-fe-os/config?type=common";
        const string extList =
            "https://sg-public-data-api.hoyoverse.com/device-fp/api/getExtList?platform=4&app_name=hkrpg_global";
        const string fingerprint =
            "https://sg-public-data-api.hoyoverse.com/device-fp/api/getFp";
        const string exactBody =
            """
            {
              "app_name":"hkrpg_global",
              "device_fp":"test-fingerprint",
              "device_id":"test-device",
              "ext_fields":"{}",
              "platform":"4",
              "seed_id":"test-seed",
              "seed_time":"test-time"
            }
            """;

        Assert.False(AllowsConnect("HoYoLAB", language + "&extra=1", "GET", gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            language.Replace("/langs?", "/other?", StringComparison.Ordinal),
            "GET",
            gameId: "hsr"));
        Assert.False(AllowsConnect("HoYoLAB", language, "POST", "{}", gameId: "hsr"));
        Assert.False(AllowsConnect("HoYoLAB", language, "GET", gameId: "gi"));
        Assert.False(PublisherAccountCatalog.IsAllowedWebResourceRequest(
            "HoYoLAB",
            PublisherSessionPurpose.CheckIn,
            "hsr",
            new Uri(config),
            "GET",
            PublisherWebResourceContext.Fetch));

        Assert.False(AllowsConnect(
            "HoYoLAB",
            config.Replace("type=common", "type=private", StringComparison.Ordinal),
            "GET",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            config,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            extList.Replace("hkrpg_global", "hk4e_global", StringComparison.Ordinal),
            "GET",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            extList + "&platform=4",
            "GET",
            gameId: "hsr"));

        Assert.True(AllowsConnect("HoYoLAB", fingerprint, "OPTIONS", gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            fingerprint,
            "OPTIONS",
            "{}",
            gameId: "hsr"));
        Assert.False(AllowsConnect("HoYoLAB", fingerprint, "OPTIONS", gameId: "gi"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            fingerprint + "?extra=1",
            "POST",
            exactBody,
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            fingerprint,
            "POST",
            exactBody,
            contentType: "text/plain",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            fingerprint,
            "POST",
            exactBody.Replace(
                "\"seed_time\":\"test-time\"",
                "\"seed_time\":123",
                StringComparison.Ordinal),
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            fingerprint,
            "POST",
            exactBody.Replace(
                "\"seed_time\":\"test-time\"",
                "\"seed_time\":\"test-time\",\"action\":\"delete\"",
                StringComparison.Ordinal),
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            fingerprint,
            "POST",
            exactBody.Replace(
                "\"seed_time\":\"test-time\"",
                "\"seed_id\":\"duplicate\",\"seed_time\":\"test-time\"",
                StringComparison.Ordinal),
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            fingerprint,
            "POST",
            exactBody,
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
    }

    [Fact]
    public void Current_hsr_aigis_requests_are_destination_bounded_with_safe_json_bodies()
    {
        const string create =
            "https://passport-api-sg.hoyolab.com/account/ma-aigis/api/createBySmartCaptchaTicket";
        const string check =
            "https://passport-api-sg.hoyolab.com/account/ma-aigis/api/checkSmartCaptcha";

        Assert.True(AllowsConnect("HoYoLAB", create, "OPTIONS", gameId: "hsr"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            create,
            "POST",
            """{"ticket":"opaque-ticket"}""",
            gameId: "hsr"));
        Assert.True(AllowsConnect("HoYoLAB", check, "OPTIONS", gameId: "hsr"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            check,
            "POST",
            """{"ticket":"opaque-ticket","check_data":"opaque-check"}""",
            gameId: "hsr"));

        Assert.False(AllowsConnect("HoYoLAB", create, "OPTIONS", "{}", gameId: "hsr"));
        Assert.False(AllowsConnect("HoYoLAB", create + "?extra=1", "OPTIONS", gameId: "hsr"));
        Assert.False(AllowsConnect("HoYoLAB", create, "GET", gameId: "hsr"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            create,
            "POST",
            """{"ticket":""}""",
            gameId: "hsr"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            create,
            "POST",
            """{"ticket":"opaque-ticket","action":"delete"}""",
            gameId: "hsr"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            check,
            "POST",
            """{"ticket":"opaque-ticket"}""",
            gameId: "hsr"));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            check,
            "POST",
            """{"ticket":"opaque-ticket","check_data":1}""",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            check,
            "POST",
            """{"ticket":"opaque-ticket","check_data":"a","check_data":"b"}""",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            create.Replace("passport-api-sg.hoyolab.com", "passport-api-eu.hoyolab.com"),
            "POST",
            """{"ticket":"opaque-ticket"}""",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            create,
            "POST",
            """{"ticket":"opaque-ticket"}""",
            contentType: "text/plain",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            create,
            "POST",
            """{"ticket":"opaque-ticket"}""",
            gameId: "gi"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            create,
            "POST",
            """{"ticket":"opaque-ticket"}""",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
    }

    [Fact]
    public void Current_hsr_geetest_v4_requests_accept_only_reviewed_bootstrap_and_static_assets()
    {
        const string load =
            "https://gcaptcha4.geetest.com/load"
            + "?captcha_id=captcha"
            + "&challenge=challenge"
            + "&client_type=web"
            + "&risk_type=slide"
            + "&user_info=user"
            + "&call_type=reload"
            + "&lang=en"
            + "&callback=geetest_callback";
        const string staticScript =
            "https://static.geetest.com/v4/gcaptcha4.js";
        const string verify =
            "https://gcaptcha4.geetest.com/verify"
            + "?captcha_id=captcha"
            + "&client_type=web"
            + "&lot_number=lot"
            + "&risk_type=slide"
            + "&payload=payload"
            + "&process_token=token"
            + "&payload_protocol=1"
            + "&pt=1"
            + "&w=encrypted"
            + "&callback=geetest_callback";

        Assert.True(AllowsConnect(
            "HoYoLAB",
            load,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            staticScript,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://static.geetest.com/v4/assets/captcha.webp",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Image));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://static.geetest.com/v4/assets/captcha.woff2",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Font));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("w=encrypted", $"w={new string('a', 4096)}", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load.Replace("gcaptcha4.geetest.com", "gcaptcha4.gsensebot.com"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load.Replace("&callback=geetest_callback", "&callback=geetest_callback&pt=1"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        foreach (var (present, empty) in new[]
                 {
                     ("captcha_id=captcha", "captcha_id="),
                     ("challenge=challenge", "challenge="),
                     ("client_type=web", "client_type="),
                     ("risk_type=slide", "risk_type="),
                     ("user_info=user", "user_info="),
                     ("call_type=reload", "call_type="),
                     ("lang=en", "lang="),
                     ("callback=geetest_callback", "callback="),
                 })
        {
            Assert.True(AllowsConnect(
                "HoYoLAB",
                load.Replace(present, empty, StringComparison.Ordinal),
                "GET",
                gameId: "hsr",
                context: PublisherWebResourceContext.Script));
        }
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load
                .Replace("&risk_type=slide", "", StringComparison.Ordinal)
                .Replace("&user_info=user", "", StringComparison.Ordinal)
                .Replace("&call_type=reload", "", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load.Replace("gcaptcha4.geetest.com", "gcaptcha4.geevisit.com"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("gcaptcha4.geetest.com", "gcaptcha4.gsensebot.com"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify
                .Replace("gcaptcha4.geetest.com", "gcaptcha4.gsensebot.com")
                .Replace("payload=payload", "payload=", StringComparison.Ordinal)
                .Replace("process_token=token", "process_token=", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("gcaptcha4.geetest.com", "gcaptcha4.geevisit.com"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://static.geevisit.com/v4/gcaptcha4.js",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://static.geevisit.com/captcha_v4/demo/slide/bg/image.webp",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Image));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://static.geetest.com/captcha_v4/demo/slide/slice/image.png",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Image));

        Assert.True(AllowsConnect(
            "HoYoLAB",
            load.Replace("&callback=geetest_callback", ""),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load.Replace("&lang=en", "", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load + "&extra=1",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load + "&callback=duplicate",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load.Replace("&callback=geetest_callback", "&callback=geetest_callback&pt=2"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            load.Replace("gcaptcha4.geetest.com", "gcaptcha4.attacker.example"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            load.Replace("/load?", "/verify?"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("&pt=1", "", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("captcha_id=captcha", "captcha_id=", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("risk_type=slide", "risk_type=", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("payload=payload", "payload=", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("w=encrypted", "w=", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("callback=geetest_callback", "callback=", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify + "&extra=1",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify + "&w=duplicate",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("client_type=web", "client_type=mobile", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            verify.Replace("payload_protocol=1", "payload_protocol=2", StringComparison.Ordinal),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://gcaptcha4.geetest.com/load",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://gcaptcha4.geetest.com/load?q=" + new string('a', 4093),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://gcaptcha4.geetest.com/load?q=" + new string('a', 8189),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://gcaptcha4.geetest.com/load?q=" + new string('a', 8192),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.True(AllowsConnect(
            "HoYoLAB",
            "https://gcaptcha4.geetest.com/verify?q=" + new string('a', 65533),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://gcaptcha4.geetest.com/verify?q=" + new string('a', 65534),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            verify.Replace("gcaptcha4.geetest.com", "gcaptcha4.attacker.example"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            verify,
            "HEAD",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            verify,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Fetch));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            verify,
            "GET",
            gameId: "gi",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            load,
            "HEAD",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            load,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Fetch));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            load,
            "POST",
            "{}",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            load,
            "GET",
            gameId: "gi",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            staticScript.Replace("/v4/", "/v3/"),
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://static.attacker.example/v4/gcaptcha4.js",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://static.geevisit.com/captcha_v4/demo/slide/bg/image.webp",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://static.geevisit.com/captcha_v4-private/image.webp",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Image));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://static.attacker.example/captcha_v4/demo/image.webp",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Image));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            staticScript,
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Document));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            staticScript,
            "GET",
            gameId: "gi",
            context: PublisherWebResourceContext.Script));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://monitor.geetest.com/monitor/send",
            "GET",
            gameId: "hsr",
            context: PublisherWebResourceContext.Fetch));
    }

    [Fact]
    public void Connect_rejects_extra_duplicate_missing_and_wrong_typed_body_fields()
    {
        const string hoyoLogin =
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/webLoginByPassword";
        Assert.False(AllowsConnect(
            "HoYoLAB",
            hoyoLogin,
            "POST",
            """{"account":"a","password":"p","token_type":2,"action":"delete"}"""));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            hoyoLogin,
            "POST",
            """{"account":"a","account":"b","password":"p","token_type":2}"""));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            hoyoLogin,
            "POST",
            """{"account":"a","password":"p","token_type":"2"}"""));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            hoyoLogin,
            "POST",
            """{"account":"a","password":"p","token_type":2}""",
            "application/x-www-form-urlencoded"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-sg.hoyolab.com/account/ma-passport/api/webLoginByPassword",
            "POST",
            """{"account":"a","password":"p","token_type":2}""",
            gameId: "hsr"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            hoyoLogin,
            "POST",
            """{"account":"a","password":"p","token_type":6}"""));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-sg.hoyolab.com/account/ma-passport/api/webLoginByPassword",
            "POST",
            """{"account":"a","password":"p","token_type":6,"remember":true}""",
            gameId: "hsr"));

        Assert.False(AllowsConnect(
            "SKPORT",
            "https://as.gryphline.com/user/auth/v1/token_by_email_password",
            "POST",
            """{"email":"person@example.com","password":"secret","emailSubscription":true}"""));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://as.gryphline.com/user/oauth2/v2/grant",
            "POST",
            """{"token":"opaque-token","appCode":"arbitrary-app","type":1}"""));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://web-api.skport.com/cookie_store/account_token",
            "POST",
            """{"content":{"token":"opaque-token"}}"""));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://zonai.skport.com/web/v1/user/auth/generate_cred_by_code",
            "POST",
            """{"kind":0,"code":"opaque-code"}"""));
        Assert.False(AllowsConnect(
            "SKPORT",
            "https://as.gryphline.com/user/auth/v1/token_by_email_password",
            "POST",
            """{"email":"","password":"secret"}"""));
    }

    [Fact]
    public void Connect_rejects_missing_malformed_oversized_and_preflight_bodies()
    {
        const string login =
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/webLoginByPassword";
        Assert.False(AllowsConnect("HoYoLAB", login, "POST"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            login,
            "POST",
            """{"account":"a","password":"p","token_type":2,}"""));

        var oversized = "{\"account\":\""
            + new string('a', PublisherAccountCatalog.MaximumConnectRequestBodyBytes)
            + "\",\"password\":\"p\",\"token_type\":2}";
        Assert.False(AllowsConnect("HoYoLAB", login, "POST", oversized));
        Assert.False(AllowsConnect("HoYoLAB", login, "OPTIONS", "{}"));
        Assert.False(AllowsConnect(
            "HoYoLAB",
            "https://passport-api-eu.hoyoverse.com/account/ma-passport/api/getSwitchStatus?app_id=c9oqaq3s3gu8&platform=4",
            "GET",
            "{}"));
    }

    [Fact]
    public void Hsr_snapshot_retains_reserve_and_recovery_information()
    {
        Assert.True(PublisherAccountCatalog.TryParseResourceResponse(
            "hsr",
            Encoding.UTF8.GetBytes("""{"retcode":0,"data":{"current_stamina":221,"max_stamina":300,"stamina_recover_time":23700,"current_reserve_stamina":840}}"""),
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            out var snapshot));

        Assert.Equal(840, snapshot!.Reserve);
        Assert.Equal(23700, snapshot.RecoverySeconds);
    }

    [Fact]
    public void Claimed_today_projection_expires_on_the_next_calendar_day_without_a_timer()
    {
        var result = new DailyCheckInResult(
            "hsr",
            DailyCheckInState.Claimed,
            "Daily reward claimed.",
            DateTimeOffset.Parse("2026-07-21T23:55:00+02:00"));

        Assert.True(PublisherAccountPresentation.IsCurrentDayCheckIn(
            result,
            DateTimeOffset.Parse("2026-07-21T23:59:00+02:00")));
        Assert.False(PublisherAccountPresentation.IsCurrentDayCheckIn(
            result,
            DateTimeOffset.Parse("2026-07-22T00:01:00+02:00")));
    }

    [Fact]
    public async Task Repeated_clicks_join_one_in_flight_operation()
    {
        var singleFlight = new PublisherSingleFlight<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<int> Work(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(cancellationToken);
            return 42;
        }

        var first = singleFlight.RunAsync(Work, CancellationToken.None);
        var second = singleFlight.RunAsync(Work, CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
    }

    [Fact]
    public async Task Late_coalesced_resource_observer_cannot_reset_the_owner_publication()
    {
        var singleFlight = new PublisherSingleFlight<int>();
        var ownerPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOwnerReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var state = PublisherResourceState.NotStarted;
        var diagnostic = PublisherResourceCaptureDiagnostic.NotAvailable;

        async Task<int> Work(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            diagnostic = PublisherResourceCaptureDiagnostic.NotAvailable;
            state = PublisherResourceState.Checking;
            diagnostic = PublisherResourceCaptureDiagnostic.Valid;
            state = PublisherResourceState.Fresh;
            ownerPublished.SetResult();
            await allowOwnerReturn.Task.WaitAsync(cancellationToken);
            return 42;
        }

        var owner = singleFlight.RunAsync(Work, CancellationToken.None);
        await ownerPublished.Task;
        var observer = singleFlight.RunAsync(Work, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(PublisherResourceCaptureDiagnostic.Valid, diagnostic);
        Assert.Equal(PublisherResourceState.Fresh, state);

        allowOwnerReturn.SetResult();
        Assert.Equal(42, await owner);
        Assert.Equal(42, await observer);
        Assert.Equal(PublisherResourceCaptureDiagnostic.Valid, diagnostic);
        Assert.Equal(PublisherResourceState.Fresh, state);

        var refresh = Slice(
            ReadAppFile("PublisherAccountService.cs"),
            "public Task<PublisherResourceSnapshot?> RefreshResourceAsync",
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync");
        Assert.DoesNotContain("SetResourceDiagnostic(", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("SetResourceState(", refresh, StringComparison.Ordinal);

        var core = Slice(
            ReadAppFile("PublisherAccountService.cs"),
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        var accessProof = core.IndexOf(
            "if (!ProfileAccessAllowedAfterGate(entry.Provider, consentRequired: true, operation))",
            StringComparison.Ordinal);
        var resetDiagnostic = core.IndexOf(
            "PublisherResourceCaptureDiagnostic.NotAvailable",
            accessProof,
            StringComparison.Ordinal);
        var resetState = core.IndexOf(
            "PublisherResourceState.Checking",
            accessProof,
            StringComparison.Ordinal);
        var createWindow = core.IndexOf("CreateWindow(entry.Provider, operation)", StringComparison.Ordinal);
        Assert.True(accessProof >= 0 && accessProof < resetDiagnostic);
        Assert.True(resetDiagnostic < resetState && resetState < createWindow);
        Assert.Contains("SetResourceDiagnosticIfCurrent(", core[accessProof..createWindow], StringComparison.Ordinal);
        Assert.Contains("SetResourceStateIfCurrent(", core[accessProof..createWindow], StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_refresh_service_cleanup_race_cannot_cross_the_gate_recheck()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        var wait = refresh.IndexOf("await gate.WaitAsync(cancellationToken);", StringComparison.Ordinal);
        var recheck = refresh.IndexOf(
            "if (!ProfileAccessAllowedAfterGate(entry.Provider, consentRequired: true, operation))",
            wait,
            StringComparison.Ordinal);
        var rejectedReturn = refresh.IndexOf("return null;", recheck, StringComparison.Ordinal);
        var resetDiagnostic = refresh.IndexOf(
            "PublisherResourceCaptureDiagnostic.NotAvailable",
            rejectedReturn,
            StringComparison.Ordinal);
        var resetState = refresh.IndexOf(
            "PublisherResourceState.Checking",
            rejectedReturn,
            StringComparison.Ordinal);

        Assert.True(wait >= 0 && wait < recheck && recheck < rejectedReturn);
        Assert.True(rejectedReturn < resetDiagnostic && resetDiagnostic < resetState);
        Assert.DoesNotContain(
            "PublisherResourceCaptureDiagnostic.NotAvailable",
            refresh[wait..rejectedReturn],
            StringComparison.Ordinal);
        Assert.Contains(
            "SetResourceDiagnosticIfCurrent(",
            refresh[rejectedReturn..resetState],
            StringComparison.Ordinal);
        Assert.Contains(
            "SetResourceStateIfCurrent(",
            refresh[resetDiagnostic..],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canceling_one_observer_does_not_cancel_shared_publisher_work()
    {
        var singleFlight = new PublisherSingleFlight<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationWasCanceled = false;
        async Task<int> Work(CancellationToken cancellationToken)
        {
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return 7;
            }
            catch (OperationCanceledException)
            {
                operationWasCanceled = true;
                throw;
            }
        }

        var owner = singleFlight.RunAsync(Work, CancellationToken.None);
        using var observerCancellation = new CancellationTokenSource();
        var observer = singleFlight.RunAsync(Work, CancellationToken.None, observerCancellation.Token);
        observerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
        Assert.False(operationWasCanceled);
        release.SetResult();
        Assert.Equal(7, await owner);
    }

    [Fact]
    public async Task Completed_results_are_not_reused_by_a_later_click()
    {
        var singleFlight = new PublisherSingleFlight<int>();
        var calls = 0;
        Task<int> Work(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref calls));

        Assert.Equal(1, await singleFlight.RunAsync(Work, CancellationToken.None));
        Assert.Equal(2, await singleFlight.RunAsync(Work, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Advanced_or_canceled_generation_cannot_publish()
    {
        var generation = new PublisherGeneration();
        var first = generation.Current;
        Assert.True(generation.CanPublish(first));

        generation.Advance();
        Assert.False(generation.CanPublish(first));
        Assert.True(generation.CanPublish(generation.Current));

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.False(generation.CanPublish(generation.Current, canceled.Token));
    }

    [Theory]
    [InlineData(PublisherConnectionState.NotConnected, PublisherConnectionState.NotConnected)]
    [InlineData(PublisherConnectionState.Connecting, PublisherConnectionState.NotConnected)]
    [InlineData(PublisherConnectionState.Connected, PublisherConnectionState.Connected)]
    [InlineData(PublisherConnectionState.LoginRequired, PublisherConnectionState.LoginRequired)]
    [InlineData(PublisherConnectionState.NeedsReview, PublisherConnectionState.NeedsReview)]
    public void Canceled_connect_has_one_deterministic_non_connecting_terminal_write(
        PublisherConnectionState previous,
        PublisherConnectionState expected)
    {
        var generation = new PublisherGeneration();
        var profile = new PublisherProfileMutationJournal();
        var authority = new PublisherConnectCancellationAuthority(
            generation.Current,
            previous,
            profile.Capture());

        Assert.True(authority.TryConsume(generation.Current, profile.Capture(), out var terminal));
        Assert.Equal(expected, terminal);
        Assert.NotEqual(PublisherConnectionState.Connecting, terminal);
        Assert.False(authority.TryConsume(generation.Current, profile.Capture(), out _));
    }

    [Fact]
    public void Stale_connect_cancellation_cannot_overwrite_a_newer_generation()
    {
        var generation = new PublisherGeneration();
        var profile = new PublisherProfileMutationJournal();
        var stale = new PublisherConnectCancellationAuthority(
            generation.Current,
            PublisherConnectionState.NotConnected,
            profile.Capture());
        generation.Advance();
        var current = new PublisherConnectCancellationAuthority(
            generation.Current,
            PublisherConnectionState.LoginRequired,
            profile.Capture());

        Assert.False(stale.TryConsume(generation.Current, profile.Capture(), out _));
        Assert.True(current.TryConsume(generation.Current, profile.Capture(), out var terminal));
        Assert.Equal(PublisherConnectionState.LoginRequired, terminal);
    }

    [Fact]
    public void Canceled_connect_after_persistent_profile_use_cannot_restore_old_connected_state()
    {
        var generation = new PublisherGeneration();
        var profile = new PublisherProfileMutationJournal();
        var authority = new PublisherConnectCancellationAuthority(
            generation.Current,
            PublisherConnectionState.Connected,
            profile.Capture());

        profile.MarkMayHaveChanged();

        Assert.True(authority.TryConsume(generation.Current, profile.Capture(), out var terminal));
        Assert.Equal(PublisherConnectionState.NeedsReview, terminal);
    }

    [Fact]
    public void Profile_deletion_is_an_irreversible_disconnect_commit_even_after_cancellation()
    {
        var profile = new PublisherProfileMutationJournal();
        var beforeDeletion = profile.Capture();

        Assert.False(PublisherProfileCommitPolicy.MustCommitDeletedProfile(
            beforeDeletion,
            profile.Capture()));
        Assert.False(PublisherProfileCommitPolicy.TryGetInterruptedDisconnectState(
            beforeDeletion,
            profile.Capture(),
            out _));

        profile.MarkDeleted();
        var afterDeletion = profile.Capture();

        Assert.True(PublisherProfileCommitPolicy.MustCommitDeletedProfile(
            beforeDeletion,
            afterDeletion));
        Assert.True(PublisherProfileCommitPolicy.TryGetInterruptedDisconnectState(
            beforeDeletion,
            afterDeletion,
            out var disconnectTerminal));
        Assert.Equal(PublisherConnectionState.NotConnected, disconnectTerminal);
        Assert.Equal(
            PublisherConnectionState.NotConnected,
            PublisherProfileCommitPolicy.ForCanceledConnect(
                PublisherConnectionState.Connected,
                beforeDeletion,
                afterDeletion));
    }

    [Fact]
    public void Partially_changed_profile_cannot_keep_connected_state_after_interrupted_disconnect()
    {
        var profile = new PublisherProfileMutationJournal();
        var beforeChange = profile.Capture();
        profile.MarkMayHaveChanged();
        var afterChange = profile.Capture();

        Assert.True(PublisherProfileCommitPolicy.TryGetInterruptedDisconnectState(
            beforeChange,
            afterChange,
            out var terminal));
        Assert.Equal(PublisherConnectionState.NeedsReview, terminal);
        Assert.False(PublisherProfileCommitPolicy.MustCommitDeletedProfile(beforeChange, afterChange));
    }

    [Fact]
    public void Browser_uses_exact_response_capture_and_never_a_generic_claim_search()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var service = ReadAppFile("PublisherAccountService.cs");

        Assert.Contains("WebResourceResponseReceived", browser, StringComparison.Ordinal);
        Assert.Contains("TryGetResourceBinding", browser, StringComparison.Ordinal);
        Assert.Contains("ParseResourceResponse", browser, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(body)", browser, StringComparison.Ordinal);
        Assert.Contains("Array.Clear(buffer)", browser, StringComparison.Ordinal);
        var requestAuthorization = Slice(
            browser,
            "private bool TryAuthorizeWebResourceRequest",
            "private void TryBlockWebResourceRequest");
        Assert.Contains(
            "string.Equals(args.Request.Method, \"OPTIONS\", StringComparison.Ordinal)",
            requestAuthorization,
            StringComparison.Ordinal);
        Assert.Contains(
            "args.Request.Content is not null",
            requestAuthorization,
            StringComparison.Ordinal);
        var responseCapture = Slice(
            browser,
            "private void Core_WebResourceResponseReceived",
            "private static async Task CompleteSessionProbeAsync");
        var getOnlyResponse = responseCapture.IndexOf(
            "!string.Equals(args.Request.Method, \"GET\", StringComparison.Ordinal)",
            StringComparison.Ordinal);
        var responseBinding = responseCapture.IndexOf(
            "PublisherAccountCatalog.TryGetResourceBinding",
            StringComparison.Ordinal);
        Assert.True(getOnlyResponse >= 0 && getOnlyResponse < responseBinding);
        var resourceCapture = Slice(
            browser,
            "private static async Task CompleteResourceCaptureAsync",
            "private static bool HasJsonContentType");
        Assert.Contains(
            "PublisherResourceCaptureDiagnostic.RequestRejected",
            resourceCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublisherResourceCaptureDiagnostic.BoundsRejected",
            resourceCapture,
            StringComparison.Ordinal);
        Assert.Contains("out var diagnostic", resourceCapture, StringComparison.Ordinal);
        Assert.Contains(
            "gameId is \"hsr\" or \"zzz\"",
            resourceCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            ": PublisherResourceCaptureDiagnostic.ResponseRejected",
            resourceCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            "SafeResourceFailureDiagnostic(authority.GameId, diagnostic)",
            resourceCapture,
            StringComparison.Ordinal);
        Assert.True(
            resourceCapture.IndexOf("out var diagnostic", StringComparison.Ordinal)
            < resourceCapture.IndexOf("Array.Clear(body)", StringComparison.Ordinal));
        Assert.DoesNotContain("response.StatusCode.ToString", resourceCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHeader(\"", resourceCapture.Replace(
            "GetHeader(\"Content-Type\")",
            string.Empty,
            StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("Console", resourceCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("Trace", resourceCapture, StringComparison.Ordinal);
        Assert.Contains("IsExactCheckInResponseUri", browser, StringComparison.Ordinal);
        Assert.Contains("ClassifyCheckInResponse", browser, StringComparison.Ordinal);
        Assert.Contains("ExpectedDate", browser, StringComparison.Ordinal);
        Assert.Contains("IsExactSkportSessionProbeUri", browser, StringComparison.Ordinal);
        Assert.Contains("ClassifySkportSessionResponse", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSuccessfulSkportSessionProbe", browser, StringComparison.Ordinal);
        Assert.Contains("core.Reload()", browser, StringComparison.Ordinal);
        Assert.Contains("AddWebResourceRequestedFilter", browser, StringComparison.Ordinal);
        Assert.Contains("Core_WebResourceRequested", browser, StringComparison.Ordinal);
        Assert.Contains("purpose != PublisherSessionPurpose.Connect", browser, StringComparison.Ordinal);
        Assert.Contains("purpose == PublisherSessionPurpose.CheckIn", browser, StringComparison.Ordinal);
        Assert.Contains("GetCheckInWebResourceFilterPatterns(gameId)", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("SensitiveRequestBodyStream", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("var requestContent = args.Request.Content", browser, StringComparison.Ordinal);
        var requestFilter = Slice(
            browser,
            "private void Core_WebResourceRequested",
            "private bool TryAuthorizeWebResourceRequest");
        Assert.DoesNotContain("PublisherSessionPurpose.Connect", requestFilter, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDeferral", requestFilter, StringComparison.Ordinal);
        Assert.DoesNotContain("IsConnectProfileMutationRequest", browser, StringComparison.Ordinal);
        var connectProfileBoundary = browser.IndexOf(
            "profileMutationJournal!.MarkMayHaveChanged();",
            StringComparison.Ordinal);
        var profileNavigation = browser.IndexOf("await NavigateAsync(initialUri", StringComparison.Ordinal);
        Assert.True(connectProfileBoundary >= 0 && connectProfileBoundary < profileNavigation);
        Assert.DoesNotContain(
            "TryGetBoundedString",
            ReadCoreAccountFile("PublisherAccountContracts.cs"),
            StringComparison.Ordinal);
        Assert.Contains("claimWriteAuthority.Arm", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("PublisherLoginTriggerOutcome", browser, StringComparison.Ordinal);
        Assert.Contains("items.find(item => !item.querySelector(receivedSelector))", browser, StringComparison.Ordinal);
        var achievementScript = Slice(
            browser,
            "private static string BuildHsrAchievementExportScript",
            "private async Task NavigateAsync");
        Assert.Contains("typeof window.Vue === 'function'", browser, StringComparison.Ordinal);
        Assert.Contains("webpackRequire.n(vueModule)", browser, StringComparison.Ordinal);
        Assert.Contains("Vue.prototype.$session", achievementScript, StringComparison.Ordinal);
        Assert.Contains("typeof publisherSession.init === 'function'", achievementScript, StringComparison.Ordinal);
        Assert.Contains("typeof publisherSession.initGameRole === 'function'", achievementScript, StringComparison.Ordinal);
        Assert.Contains("await publisherSession.init();", achievementScript, StringComparison.Ordinal);
        Assert.Contains("const publisherState = publisherSession.state", achievementScript, StringComparison.Ordinal);
        Assert.Contains("await publisherSession.initGameRole();", achievementScript, StringComparison.Ordinal);
        var primaryRoleInit = Slice(
            achievementScript,
            "await publisherSession.initGameRole();",
            "publisherRole = publisherSession.state");
        Assert.DoesNotContain("throw new Error('session-role');", primaryRoleInit, StringComparison.Ordinal);
        Assert.Contains("publisherSession.state.role", achievementScript, StringComparison.Ordinal);
        Assert.Contains("Vue.prototype.$accountRoleUtil", achievementScript, StringComparison.Ordinal);
        Assert.Contains("await roleUtil.initGameRole({", achievementScript, StringComparison.Ordinal);
        Assert.Contains("chooseRoleExplicitly: list =>", achievementScript, StringComparison.Ordinal);
        Assert.Contains("const provenRole = Object.freeze({", achievementScript, StringComparison.Ordinal);
        Assert.Contains("publisherRole = publisherRole || explicitlySelectedRole || provenRole", achievementScript, StringComparison.Ordinal);
        Assert.Contains("matches.length !== 1", achievementScript, StringComparison.Ordinal);
        Assert.Contains("explicitlySelectedRole = matches[0]", achievementScript, StringComparison.Ordinal);
        Assert.Contains("publisherRole = publisherRole || explicitlySelectedRole", achievementScript, StringComparison.Ordinal);
        Assert.Contains("publisherRole = selectedRole", achievementScript, StringComparison.Ordinal);
        Assert.Contains("cookie.Name, \"account_id_v2\"", browser, StringComparison.Ordinal);
        Assert.Contains("cookie.Name, \"ltuid_v2\"", browser, StringComparison.Ordinal);
        Assert.Contains(".Select(static cookie => cookie.Value)", browser, StringComparison.Ordinal);
        Assert.Contains(".Distinct(StringComparer.Ordinal)", browser, StringComparison.Ordinal);
        Assert.Contains("accountIds.Length != 1", browser, StringComparison.Ordinal);
        Assert.Contains("accountIds[0].Length is < 1 or > 32", browser, StringComparison.Ordinal);
        Assert.Contains("accountIds[0][0] == '0'", browser, StringComparison.Ordinal);
        Assert.Contains("!accountIds[0].All(char.IsAsciiDigit)", browser, StringComparison.Ordinal);
        Assert.Contains("hoyolab-api-account-mismatch", browser, StringComparison.Ordinal);
        Assert.Contains("BuildHsrAchievementExportScript(resultKey, role)", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("publisherState.account", achievementScript, StringComparison.Ordinal);
        Assert.DoesNotContain("PUBLISHER_ACCOUNT_ID", achievementScript, StringComparison.Ordinal);
        Assert.DoesNotContain("getRoleInfoByAccount", achievementScript, StringComparison.Ordinal);
        Assert.DoesNotContain("getInfoByAccount", achievementScript, StringComparison.Ordinal);
        Assert.DoesNotContain("EVENT_LOGIN_ACCOUNT", achievementScript, StringComparison.Ordinal);
        Assert.DoesNotContain("bindRoleDirect", achievementScript, StringComparison.Ordinal);
        Assert.DoesNotContain("method: 'POST'", achievementScript, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelectorAll", achievementScript, StringComparison.Ordinal);
        Assert.Contains(
            "throw new ExportProviderException(\"hoyolab-api-account-mismatch\");",
            browser,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$\"hoyolab-api-account-mismatch", browser, StringComparison.Ordinal);
        Assert.Contains("/^login-retcode:-?[0-9]{1,7}$/.test(message)", browser, StringComparison.Ordinal);
        Assert.Contains("components-pc-assets-__prize-list_---received---tOZ4Gy", ReadCoreAccountFile("PublisherAccountContracts.cs"), StringComparison.Ordinal);
        Assert.Contains("components-home-assets-__sign-content-test_---sign-wrapper---22GpLY", ReadCoreAccountFile("PublisherAccountContracts.cs"), StringComparison.Ordinal);
        Assert.Contains("components-m-assets-__index_---sign-wrapper---3WcYRI", ReadCoreAccountFile("PublisherAccountContracts.cs"), StringComparison.Ordinal);
        Assert.Contains("PublisherSessionPurpose.Resource", browser, StringComparison.Ordinal);
        Assert.Contains("The publisher session purpose is already fixed.", browser, StringComparison.Ordinal);
        Assert.Contains("capture.Authority.TryReserve", browser, StringComparison.Ordinal);
        Assert.Contains("403", browser, StringComparison.Ordinal);
        Assert.Contains("PCCalendarTodayBg.510de0.png", browser, StringComparison.Ordinal);
        Assert.Contains("MobileCalendarTodayBg.5f4677.png", browser, StringComparison.Ordinal);
        Assert.Contains("if (!windowClosed) Close()", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("document.body?.innerText", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("querySelectorAll('button", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("[role=button]", browser, StringComparison.Ordinal);
        Assert.Contains("PublisherSingleFlight", service, StringComparison.Ordinal);
        Assert.Contains("resourceSingleFlights", service, StringComparison.Ordinal);
        Assert.Contains("CanPublish", service, StringComparison.Ordinal);
        Assert.Contains("if (!allProviderWorkStopped) return;", service, StringComparison.Ordinal);
        Assert.Contains("ownsHoyoProfile && !hoyoQuarantined", service, StringComparison.Ordinal);
        Assert.DoesNotContain("checkInGate.WaitAsync(0", service, StringComparison.Ordinal);
        Assert.Contains("checkInSingleFlights.TryGetValue(gameId", service, StringComparison.Ordinal);
        Assert.Contains("RunProviderCheckInsAsync(", service, StringComparison.Ordinal);
        Assert.Contains("[entry.GameId],", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RunProviderCheckInsAsync(\"SKPORT\", [\"ae\"]", service, StringComparison.Ordinal);
        Assert.Contains("AcquireProfileOwnership(\"SKPORT\")", service, StringComparison.Ordinal);
        Assert.Contains("resourceRead.Outcome", service, StringComparison.Ordinal);
        Assert.Contains(
            "PublisherAccountStatePolicy.ForAuthenticatedResourceRead",
            service,
            StringComparison.Ordinal);
        Assert.Contains("TrySetCanceledConnectState", service, StringComparison.Ordinal);
        Assert.Contains("BeginRotatedOperation", service, StringComparison.Ordinal);
        Assert.Contains("ProfileAccessAllowedAfterGate", service, StringComparison.Ordinal);
        Assert.True(CountOccurrences(service, "if (!ProfileAccessAllowedAfterGate(") >= 4);
        Assert.Contains("profileMutations.MarkDeleted", service, StringComparison.Ordinal);
        Assert.Contains("profileMutations.MarkMayHaveChanged", service, StringComparison.Ordinal);
        Assert.Contains("CommitDeletedProfile", service, StringComparison.Ordinal);
        Assert.Contains("CommitInterruptedDisconnectIfNeeded", service, StringComparison.Ordinal);
        Assert.DoesNotContain("operation.Cancellation.Token.ThrowIfCancellationRequested();", service, StringComparison.Ordinal);
        Assert.Contains("DailyCheckInState.LoginNeeded", service, StringComparison.Ordinal);
        Assert.Contains("PublisherConnectionState.LoginRequired", service, StringComparison.Ordinal);
        Assert.Contains("if (provider != \"SKPORT\")", browser, StringComparison.Ordinal);
        Assert.Contains("ResolveProfilePath(provider)", service, StringComparison.Ordinal);
        Assert.True(
            service.IndexOf("var sessionProof = await window.GetSessionProofAsync", StringComparison.Ordinal)
            < service.IndexOf("result = await window.RunCheckInAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Daily_resolves_and_saves_exact_role_before_any_claim_authority_is_armed()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var page = ReadAppFile("MainPage.xaml.cs");
        var resolver = Slice(
            service,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        var checkIn = Slice(
            browser,
            "public async Task<DailyCheckInResult> RunCheckInAsync",
            "public async Task<PublisherResourceReadResult> ReadResourceAsync");

        Assert.Contains("purpose: PublisherSessionPurpose.Resource", resolver, StringComparison.Ordinal);
        Assert.Contains("expectedBinding: null", resolver, StringComparison.Ordinal);
        Assert.Contains("TryLoadRoleRecord(entry.GameId, operation)", resolver, StringComparison.Ordinal);
        Assert.Contains("PublisherDailyRolePolicy.Resolve(", resolver, StringComparison.Ordinal);
        Assert.Contains(
            "TryDeleteProtectedGameState(entry.GameId, entry.Provider, operation)",
            resolver,
            StringComparison.Ordinal);
        Assert.Contains("await rolePicker(resolution.Choices", resolver, StringComparison.Ordinal);
        Assert.Contains("SaveRoleRecord(", resolver, StringComparison.Ordinal);
        Assert.True(CountOccurrences(resolver, "if (!CanPublish(entry.Provider, operation))") >= 3);
        Assert.Contains("ChoosePublisherRoleAsync,", page, StringComparison.Ordinal);
        Assert.Contains("entry.GameId == \"ae\" && entry.Provider == \"SKPORT\"", resolver, StringComparison.Ordinal);
        Assert.Contains("AccountWideStatusAllowed: true", resolver, StringComparison.Ordinal);

        var preClaimProof = checkIn.IndexOf("var before = await CaptureCheckInProofAsync", StringComparison.Ordinal);
        var exactPage = checkIn.IndexOf("PublisherAccountCatalog.IsExactCheckInUri", StringComparison.Ordinal);
        var arm = checkIn.IndexOf("claimWriteAuthority.Arm(entry.GameId)", StringComparison.Ordinal);
        var click = checkIn.IndexOf("BuildExactClaimScript(entry.GameId)", StringComparison.Ordinal);
        Assert.True(preClaimProof >= 0 && preClaimProof < exactPage && exactPage < arm && arm < click);
        Assert.Contains("expectedBinding", checkIn, StringComparison.Ordinal);
        Assert.Contains("allowAccountWideStatus", checkIn, StringComparison.Ordinal);
        Assert.Contains("expectedBinding is not null || !allowAccountWideStatus", checkIn, StringComparison.Ordinal);
        Assert.Contains("checkInCapture.ExpectedBinding", browser, StringComparison.Ordinal);
        Assert.Contains("checkInCapture.AllowAccountWideStatus", browser, StringComparison.Ordinal);
        Assert.Contains("return proof;", browser, StringComparison.Ordinal);
        Assert.Contains("diagnostics.TryBeginSelectedResponse()", browser, StringComparison.Ordinal);
    }

    [Fact]
    public void Daily_selection_login_unavailable_and_late_cancellation_fail_honestly()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var checkInBoundary = Slice(
            service,
            "public Task<DailyCheckInResult> CheckInAsync",
            "private async Task<DailyCheckInResult> CheckInCoreAsync");
        var providerOperation = Slice(
            service,
            "private async Task RunProviderCheckInsAsync",
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync");
        var resolver = Slice(
            service,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");

        Assert.Contains("DailyCheckInState.Unavailable", checkInBoundary, StringComparison.Ordinal);
        Assert.Contains("DailyCheckInState.LoginNeeded", checkInBoundary, StringComparison.Ordinal);
        Assert.Contains("DailyCheckInState.SelectionRequired", providerOperation, StringComparison.Ordinal);
        Assert.Contains("DailyCheckInState.CouldNotCheck", providerOperation, StringComparison.Ordinal);
        Assert.Contains("SetCheckInIfCurrent(provider, operation, result)", providerOperation, StringComparison.Ordinal);
        Assert.Contains("singleFlight.RunAsync(", checkInBoundary, StringComparison.Ordinal);
        Assert.Contains("shutdown.Token", checkInBoundary, StringComparison.Ordinal);
        Assert.Contains("await rolePicker(resolution.Choices, cancellationToken)", resolver, StringComparison.Ordinal);
        Assert.True(
            resolver.IndexOf("await rolePicker(resolution.Choices", StringComparison.Ordinal)
            < resolver.LastIndexOf("if (!CanPublish(entry.Provider, operation))", StringComparison.Ordinal));
        Assert.Contains("Interlocked.Read(ref checkInGeneration)", browser, StringComparison.Ordinal);
        Assert.Contains("capture.Cancel()", browser, StringComparison.Ordinal);
        Assert.Contains("Completion.TrySetCanceled(CancellationToken)", browser, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_browser_password_storage_defaults_on_and_opt_out_removes_only_saved_passwords()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var browserMarkup = ReadAppFile("PublisherSessionWindow.xaml");
        var service = ReadAppFile("PublisherAccountService.cs");
        var app = ReadAppFile("App.xaml.cs");
        var settings = ReadAppFile("MainPage.xaml.cs");
        var state = ReadCoreStateFile("LauncherStateContracts.cs");
        var migrations = ReadCoreStateFile("LauncherStateMigrations.cs");
        var privacyOrchestrator = ReadCoreAccountFile("PublisherProfilePrivacyOrchestrator.cs");

        Assert.Contains("bool passwordSavingEnabled = false", browser, StringComparison.Ordinal);
        Assert.Contains(
            "bool publisherPasswordSavingEnabled = false",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "LauncherState.Snapshot.Preferences.PublisherPasswordSavingEnabled",
            app,
            StringComparison.Ordinal);
        Assert.Contains("AppWindow.Resize(new SizeInt32(1280, 720))", browser, StringComparison.Ordinal);
        Assert.Contains("core.Settings.IsGeneralAutofillEnabled = false;", browser, StringComparison.Ordinal);
        Assert.Contains(
            "core.Settings.IsPasswordAutosaveEnabled = passwordSavingEnabled;",
            browser,
            StringComparison.Ordinal);
        var initialize = browser.IndexOf(
            "var core = await InitializeBrowserProfileAsync(visible, cancellationToken);",
            StringComparison.Ordinal);
        var navigation = browser.IndexOf("await NavigateAsync(initialUri", StringComparison.Ordinal);
        Assert.True(initialize >= 0 && initialize < navigation);
        var passwordCleanup = Slice(
            browser,
            "private async Task ClearPublisherBrowsingDataAsync",
            "private async Task AttemptVisibleConnectPageAsync");
        Assert.Contains(
            "CoreWebView2BrowsingDataKinds.PasswordAutosave",
            passwordCleanup,
            StringComparison.Ordinal);
        Assert.Contains("passwordNavigationGate.NavigateAsync(", browser, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CoreWebView2BrowsingDataKinds.AllProfile",
            browser,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CoreWebView2BrowsingDataKinds.Cookies",
            browser,
            StringComparison.Ordinal);
        Assert.Contains("ApplyPasswordSavingPreference(enabled: false);", service, StringComparison.Ordinal);
        Assert.True(
            service.IndexOf("ApplyPasswordSavingPreference(enabled: false);", StringComparison.Ordinal)
            < service.IndexOf("ClearSavedPasswordsAsync(\"HoYoLAB\"", StringComparison.Ordinal));
        Assert.Contains(
            "Future publisher windows retry the exact",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "passwordStorage.RequireFullProfileCleanup();",
            privacyOrchestrator,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublisherProfileCleanupScope.FullProfile",
            privacyOrchestrator,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublisherProfilePrivacyOrchestrator.DeleteFullProfileAsync(",
            service,
            StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(profile, recursive);", service, StringComparison.Ordinal);
        Assert.Contains(
            "Header = \"Locally save browser login?\"",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn = before.Preferences.PublisherPasswordSavingEnabled",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (before.Preferences.PublisherPasswordSavingEnabled",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "&& !publisherPasswordSaving.IsOn",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "&& !await app.PublisherAccounts.ClearSavedPasswordsAsync()",
            settings,
            StringComparison.Ordinal);
        Assert.Contains(
            "Disconnecting the publisher account also deletes its private profile.",
            settings,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "The official page and WebView2 handle sign-in directly.",
            browserMarkup,
            StringComparison.Ordinal);
        Assert.Contains(
            "Keeps your publisher login saved on this PC. Turning it off removes saved passwords.",
            settings,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WebView2 may save and autofill passwords in this private profile.",
            browser,
            StringComparison.Ordinal);
        var privacyWording = string.Concat(browserMarkup, browser, settings);
        Assert.DoesNotContain("never sees", privacyWording, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("never reads them", privacyWording, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "public bool PublisherPasswordSavingEnabled { get; init; } = true;",
            state,
            StringComparison.Ordinal);
        Assert.Contains(
            "dto.Preferences?.PublisherPasswordSavingEnabled ?? true",
            migrations,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_connect_completion_uses_the_same_strict_proof_for_auto_and_manual_finish()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var page = ReadAppFile("MainPage.xaml.cs");
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var connect = Slice(
            service,
            "public async Task<PublisherConnectionState> ConnectAsync",
            "public Task<PublisherResourceSnapshot?> RefreshResourceAsync");
        var done = Slice(
            browser,
            "private async void DoneButton_Click",
            "private async void RetryButton_Click");
        var monitor = Slice(
            browser,
            "private async Task MonitorVisibleConnectAsync",
            "public Task<PublisherVisibleConnectCompletion> WaitForConnectCompletionAsync");

        var completion = connect.IndexOf(
            "completion = await window.WaitForConnectCompletionAsync",
            StringComparison.Ordinal);
        var decision = connect.IndexOf(
            "PublisherVisibleConnectFlow.CompleteAsync(",
            StringComparison.Ordinal);
        var probe = connect.IndexOf(
            "operationCancellation => ProbeConnectionCoreAsync(",
            StringComparison.Ordinal);
        Assert.True(completion >= 0 && completion < decision && decision < probe);
        Assert.DoesNotContain(
            "var sessionProof = await ProbeConnectionCoreAsync",
            connect,
            StringComparison.Ordinal);
        Assert.Contains(
            "connection != PublisherConnectionState.Connecting",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "var proof = await GetSessionProofAsync",
            done,
            StringComparison.Ordinal);
        Assert.Contains(
            "proof == PublisherSessionProof.Authenticated",
            done,
            StringComparison.Ordinal);
        Assert.Contains(
            "Login was not detected.",
            done,
            StringComparison.Ordinal);
        Assert.Contains(
            "Interlocked.CompareExchange(ref visibleConnectOperationInFlight, 1, 0)",
            done,
            StringComparison.Ordinal);
        Assert.True(
            done.IndexOf("proof == PublisherSessionProof.Authenticated", StringComparison.Ordinal)
            < done.IndexOf(
                "connectCompletion.TrySetResult(PublisherVisibleConnectCompletion.Done)",
                StringComparison.Ordinal));
        Assert.DoesNotContain("visibleConnectAttemptInFlight", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("visibleConnectVerificationInFlight", browser, StringComparison.Ordinal);
        Assert.Contains("GetHoyoSessionProofOnceAsync(lifetime.Token)", monitor, StringComparison.Ordinal);
        Assert.Contains("TryReadEndfieldRegionAsync(lifetime.Token)", monitor, StringComparison.Ordinal);
        Assert.Contains("TryCompleteVisibleConnectAsync(", monitor, StringComparison.Ordinal);
        Assert.Contains("reportFailure: false", monitor, StringComparison.Ordinal);
        Assert.Contains("Done remains available", monitor, StringComparison.Ordinal);
        Assert.Contains("DoneButton.IsEnabled = false;", browser, StringComparison.Ordinal);
    }

    [Fact]
    public void Teardown_cancellation_policy_returns_when_the_operation_is_not_canceled()
    {
        var teardown = new InvalidOperationException("teardown");

        PublisherTeardownCancellationPolicy.ThrowIfCanceled(CancellationToken.None, teardown);
    }

    [Fact]
    public void Teardown_cancellation_policy_rejects_a_missing_teardown_failure()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PublisherTeardownCancellationPolicy.ThrowIfCanceled(
                CancellationToken.None,
                null!));
    }

    [Fact]
    public void Teardown_cancellation_policy_preserves_the_token_and_teardown_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var teardown = new InvalidOperationException("teardown");

        var exception = Assert.Throws<OperationCanceledException>(() =>
            PublisherTeardownCancellationPolicy.ThrowIfCanceled(cancellation.Token, teardown));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Same(teardown, exception.InnerException);
    }

    [Fact]
    public void Connect_teardown_handles_cancellation_before_quarantine_and_projection()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var connect = Slice(
            service,
            "public async Task<PublisherConnectionState> ConnectAsync",
            "public Task<PublisherResourceSnapshot?> RefreshResourceAsync");
        var teardownCatch = Slice(
            connect,
            "catch (PublisherSessionTeardownException exception)",
            "catch (Exception)");

        var canceled = teardownCatch.IndexOf(
            "if (cancellationToken.IsCancellationRequested)",
            StringComparison.Ordinal);
        var consumeCancellation = teardownCatch.IndexOf(
            "TrySetCanceledConnectState(entry.Provider, cancellationWrite);",
            StringComparison.Ordinal);
        var quarantine = teardownCatch.IndexOf(
            "QuarantineProvider(entry.Provider, operation);",
            StringComparison.Ordinal);
        var projectCancellation = teardownCatch.IndexOf(
            "PublisherTeardownCancellationPolicy.ThrowIfCanceled(cancellationToken, exception);",
            StringComparison.Ordinal);
        var needsReview = teardownCatch.IndexOf(
            "return PublisherConnectionState.NeedsReview;",
            StringComparison.Ordinal);

        Assert.True(
            canceled >= 0
            && canceled < consumeCancellation
            && consumeCancellation < quarantine
            && quarantine < projectCancellation
            && projectCancellation < needsReview);
    }

    [Fact]
    public async Task Session_proof_retry_reaches_authenticated_after_one_bounded_delay()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var proofs = new Queue<PublisherSessionProof>([
            PublisherSessionProof.LoginRequired,
            PublisherSessionProof.Authenticated]);

        var result = await PublisherSessionProofRetryPolicy.RunAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(proofs.Dequeue());
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(PublisherSessionProof.Authenticated, result);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], delays);
    }

    [Fact]
    public async Task Session_proof_retry_stops_after_exactly_eight_login_required_attempts()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await PublisherSessionProofRetryPolicy.RunAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(PublisherSessionProof.LoginRequired);
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(PublisherSessionProof.LoginRequired, result);
        Assert.Equal(8, attempts);
        Assert.Equal(7, delays.Count);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromMilliseconds(250), delay));
    }

    [Fact]
    public async Task Session_proof_retry_does_not_retry_needs_review()
    {
        var attempts = 0;
        var delays = 0;

        var result = await PublisherSessionProofRetryPolicy.RunAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(PublisherSessionProof.NeedsReview);
            },
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(PublisherSessionProof.NeedsReview, result);
        Assert.Equal(1, attempts);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task Session_proof_retry_propagates_cancellation_during_delay()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var delays = 0;

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PublisherSessionProofRetryPolicy.RunAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromResult(PublisherSessionProof.LoginRequired);
                },
                (_, operationCancellation) =>
                {
                    delays++;
                    cancellation.Cancel();
                    return Task.FromCanceled(operationCancellation);
                },
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, attempts);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task Session_proof_retry_propagates_proof_failures_without_delay()
    {
        var failure = new InvalidOperationException("proof-failed");
        var attempts = 0;
        var delays = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PublisherSessionProofRetryPolicy.RunAsync(
                _ =>
                {
                    attempts++;
                    return Task.FromException<PublisherSessionProof>(failure);
                },
                (_, _) =>
                {
                    delays++;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.Same(failure, exception);
        Assert.Equal(1, attempts);
        Assert.Equal(0, delays);
    }

    [Fact]
    public void Hoyo_session_proof_retries_name_only_cookies_on_the_proven_api_host()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var proof = Slice(
            browser,
            "public async Task<PublisherSessionProof> GetSessionProofAsync",
            "public async Task<HoyoLabHsrAchievementResult> ReadHsrAchievementsAsync");

        Assert.Contains("PublisherSessionProofRetryPolicy.RunAsync(", proof, StringComparison.Ordinal);
        Assert.Contains(
            "GetCookiesAsync(\"https://sg-public-api.hoyolab.com/\")",
            proof,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(proof, "GetCookiesAsync("));
        Assert.Contains("cookie.Name", proof, StringComparison.Ordinal);
        Assert.Contains("names.Contains(\"ltoken_v2\")", proof, StringComparison.Ordinal);
        Assert.Contains("names.Contains(\"ltuid_v2\")", proof, StringComparison.Ordinal);
        Assert.Contains("names.Contains(\"account_id_v2\")", proof, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.hoyolab.com", proof, StringComparison.Ordinal);
        Assert.DoesNotContain("cookie.Value", proof, StringComparison.Ordinal);
        Assert.DoesNotContain("Console", proof, StringComparison.Ordinal);
        Assert.DoesNotContain("Trace", proof, StringComparison.Ordinal);
    }

    [Fact]
    public void Publisher_browser_uses_the_installed_evergreen_runtime()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var initialization = Slice(
            browser,
            "private async Task<CoreWebView2> InitializeBrowserProfileAsync",
            "private async Task ClearPublisherBrowsingDataAsync");

        Assert.Contains(
            "CoreWebView2Environment.GetAvailableBrowserVersionString(null)",
            browser,
            StringComparison.Ordinal);
        Assert.Contains("CoreWebView2Environment.CreateWithOptionsAsync(", initialization, StringComparison.Ordinal);
        Assert.Contains("null,", initialization, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView2Runtime", initialization, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists", initialization, StringComparison.Ordinal);
    }

    [Fact]
    public void Publisher_browser_exit_is_an_early_signal_safe_bounded_handoff_barrier()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var initialization = Slice(
            browser,
            "private async Task<CoreWebView2> InitializeBrowserProfileAsync",
            "private async Task ClearPublisherBrowsingDataAsync");
        var attachment = Slice(
            browser,
            "private void AttachBrowserProcessExitHandler",
            "private void DetachBrowserProcessExitHandler");
        var detachment = Slice(
            browser,
            "private void DetachBrowserProcessExitHandler",
            "private async Task ClearPublisherBrowsingDataAsync");
        var exitSignal = Slice(
            browser,
            "private readonly TaskCompletionSource browserProcessExited",
            "private readonly TaskCompletionSource<PublisherVisibleConnectCompletion>");
        var disposal = Slice(
            browser,
            "public async ValueTask DisposeAsync()",
            "private void CloseBrowserOnce()");

        var subscribe = initialization.IndexOf(
            "AttachBrowserProcessExitHandler(environment);",
            StringComparison.Ordinal);
        var createController = initialization.IndexOf(
            "await Browser.EnsureCoreWebView2Async(environment);",
            StringComparison.Ordinal);
        var requireController = initialization.IndexOf(
            "var core = Browser.CoreWebView2",
            StringComparison.Ordinal);
        var arm = initialization.IndexOf(
            "Volatile.Write(ref browserProcessExitBarrierArmed, 1);",
            StringComparison.Ordinal);
        Assert.True(
            subscribe >= 0
            && subscribe < createController
            && createController < requireController
            && requireController < arm);

        Assert.Contains(
            "new(TaskCreationOptions.RunContinuationsAsynchronously)",
            exitSignal,
            StringComparison.Ordinal);
        Assert.Contains(
            "(_, _) => browserProcessExited.TrySetResult()",
            attachment,
            StringComparison.Ordinal);
        Assert.Contains(
            "environment.BrowserProcessExited += handler;",
            attachment,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (Volatile.Read(ref browserProcessExitBarrierArmed) == 0)",
            initialization,
            StringComparison.Ordinal);
        Assert.Contains("DetachBrowserProcessExitHandler();", initialization, StringComparison.Ordinal);
        Assert.Contains("environment.BrowserProcessExited -= handler;", detachment, StringComparison.Ordinal);

        var closeBrowser = disposal.IndexOf("CloseBrowserOnce();", StringComparison.Ordinal);
        var armedWait = disposal.IndexOf(
            "if (Volatile.Read(ref browserProcessExitBarrierArmed) != 0)",
            StringComparison.Ordinal);
        var waitForExit = disposal.IndexOf(
            "await browserProcessExited.Task.WaitAsync(BrowserProcessExitTimeout);",
            StringComparison.Ordinal);
        Assert.True(closeBrowser >= 0 && closeBrowser < armedWait && armedWait < waitForExit);
        Assert.Contains(
            "BrowserProcessExitTimeout = TimeSpan.FromSeconds(5)",
            browser,
            StringComparison.Ordinal);
        Assert.Contains("finally", disposal, StringComparison.Ordinal);
        Assert.Contains("DetachBrowserProcessExitHandler();", disposal, StringComparison.Ordinal);
        var gate = disposal.IndexOf("await passwordNavigationGate.DisposeAsync();", StringComparison.Ordinal);
        var lifetime = disposal.IndexOf("lifetime.Dispose();", StringComparison.Ordinal);
        Assert.True(waitForExit < gate && gate < lifetime);
        Assert.Equal(
            1,
            disposal.Split("passwordNavigationGate.DisposeAsync", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "throw new PublisherSessionTeardownException(teardownFailure);",
            disposal,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay", disposal, StringComparison.Ordinal);
    }

    [Fact]
    public void Visible_connect_leaves_sign_in_to_the_user_and_only_auto_finishes_from_strict_proof()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var flow = ReadCoreAccountFile("PublisherVisibleConnectFlow.cs");
        var markup = ReadAppFile("PublisherSessionWindow.xaml");
        var initialization = Slice(
            browser,
            "public async Task InitializeAsync",
            "private async Task AttemptVisibleConnectPageAsync");
        var visibleConnect = Slice(
            browser,
            "private async Task AttemptVisibleConnectPageAsync",
            "private async Task MonitorVisibleConnectAsync");
        var monitor = Slice(
            browser,
            "private async Task MonitorVisibleConnectAsync",
            "public Task<PublisherVisibleConnectCompletion> WaitForConnectCompletionAsync");

        Assert.Contains("if (visible", initialization, StringComparison.Ordinal);
        Assert.Contains(
            "purpose == PublisherSessionPurpose.Connect",
            initialization,
            StringComparison.Ordinal);
        Assert.Contains("await AttemptVisibleConnectPageAsync(", initialization, StringComparison.Ordinal);
        Assert.Contains("PublisherVisibleConnectFlow.AttemptPageAsync(", visibleConnect, StringComparison.Ordinal);
        Assert.Contains(
            "NavigateWithOutcomeAsync(uri, operationCancellation)",
            visibleConnect,
            StringComparison.Ordinal);
        Assert.Contains(
            "? presentation.Guidance ?? string.Empty",
            visibleConnect,
            StringComparison.Ordinal);
        Assert.DoesNotContain("TryOpenHoyoLabLoginDialogAsync", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("PublisherLoginTriggerOutcome", string.Concat(browser, flow), StringComparison.Ordinal);
        Assert.DoesNotContain("ForLoginTrigger", flow, StringComparison.Ordinal);
        Assert.Contains(
            "Sign in on the official page. Nyx will finish automatically; choose Done if needed.",
            flow,
            StringComparison.Ordinal);
        var navigate = flow.IndexOf(
            "var navigationOutcome = await navigate(cancellationToken);",
            StringComparison.Ordinal);
        var rejectNavigation = flow.IndexOf(
            "if (navigationOutcome is not PublisherVisibleConnectNavigationOutcome.Succeeded)",
            navigate,
            StringComparison.Ordinal);
        var ready = flow.IndexOf(
            "return PublisherVisibleConnectPresentation.ReadyToSignIn;",
            rejectNavigation,
            StringComparison.Ordinal);
        Assert.True(navigate >= 0 && navigate < rejectNavigation && rejectNavigation < ready);
        Assert.DoesNotContain("The official page and WebView2 handle sign-in directly.", browser, StringComparison.Ordinal);

        var lowered = visibleConnect.ToLowerInvariant();
        Assert.DoesNotContain("getsessionproofasync", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("executescriptasync", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("queryselector", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("getelementbyid", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("document.", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("input[type", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain(".click(", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain(".value", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain(".name", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("innertext", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("textcontent", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("innerhtml", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("outerhtml", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder", lowered, StringComparison.Ordinal);
        Assert.DoesNotContain("getattribute", lowered, StringComparison.Ordinal);
        Assert.Contains("GetHoyoSessionProofOnceAsync", monitor, StringComparison.Ordinal);
        Assert.Contains("TryReadEndfieldRegionAsync", monitor, StringComparison.Ordinal);
        Assert.Contains("TryCompleteVisibleConnectAsync", monitor, StringComparison.Ordinal);
        Assert.DoesNotContain("executescriptasync", monitor.ToLowerInvariant(), StringComparison.Ordinal);

        var closeHandler = Slice(
            browser,
            "Closed += (_, _) =>",
            "public async Task InitializeAsync");
        var markClosed = closeHandler.IndexOf("windowClosed = true;", StringComparison.Ordinal);
        var cancelLifetime = closeHandler.IndexOf("lifetime.Cancel();", StringComparison.Ordinal);
        var closeBrowser = closeHandler.IndexOf("CloseBrowserOnce();", StringComparison.Ordinal);
        Assert.True(markClosed >= 0 && markClosed < cancelLifetime && cancelLifetime < closeBrowser);
        Assert.Contains("Browser.Close();", Slice(
            browser,
            "private void CloseBrowserOnce()",
            "private sealed class PendingResourceCapture"), StringComparison.Ordinal);

        Assert.Contains("Click=\"CloseButton_Click\"", markup, StringComparison.Ordinal);
        Assert.Contains("Click=\"DoneButton_Click\"", markup, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RetryButton\"", markup, StringComparison.Ordinal);
        Assert.Contains("Click=\"RetryButton_Click\"", markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://www.hoyolab.com/home")]
    [InlineData("https://act.hoyolab.com/bbs/event/signin/hkrpg/e202303301540311.html?act_id=e202303301540311&lang=en-us")]
    [InlineData("https://account.hoyolab.com/login-platform/index.html")]
    [InlineData("https://account.hoyoverse.com/passport/index.html#/login")]
    [InlineData("https://sdk-os-static.hoyoverse.com/combo/box/api/config/porte-fe-os/config?type=common")]
    public void Visible_HoYoLAB_connect_allows_official_top_level_pages(string value)
    {
        Assert.True(Nyx_Desktop_App.PublisherVisibleConnectNavigationPolicy.IsAllowed(
            "HoYoLAB",
            "hsr",
            new Uri(value)));
    }

    [Theory]
    [InlineData("http://www.hoyolab.com/home")]
    [InlineData("https://user:pass@www.hoyolab.com/home")]
    [InlineData("https://www.hoyolab.com:444/home")]
    [InlineData("https://hoyolab.com.attacker.example/home")]
    [InlineData("https://hoyoverse.com.attacker.example/home")]
    [InlineData("https://example.com/home")]
    public void Visible_HoYoLAB_connect_rejects_nonofficial_top_level_pages(string value)
    {
        Assert.False(Nyx_Desktop_App.PublisherVisibleConnectNavigationPolicy.IsAllowed(
            "HoYoLAB",
            "hsr",
            new Uri(value)));
    }

    [Fact]
    public void App_shutdown_awaits_secret_bearing_providers_without_blocking_UI_continuations()
    {
        var app = ReadAppFile("App.xaml.cs");

        Assert.Contains("_window.AppWindow.Closing += AppWindow_Closing", app, StringComparison.Ordinal);
        Assert.Contains("private void AppWindow_Closing", app, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true", app, StringComparison.Ordinal);
        Assert.Contains("DisposeWuWaAccountStatusAsync(_wuwaAccountStatus)", app, StringComparison.Ordinal);
        Assert.Contains("DisposePublisherAccountsAsync(_publisherAccounts)", app, StringComparison.Ordinal);
        var shutdown = Slice(app, "private async Task ShutDownAccountsAndCloseAsync", "private void Window_Closed");
        var page = shutdown.IndexOf("await DisposeMainPageAsync(mainWindow)", StringComparison.Ordinal);
        var wuwaStart = shutdown.IndexOf("DisposeWuWaAccountStatusAsync(_wuwaAccountStatus)", StringComparison.Ordinal);
        var publisherStart = shutdown.IndexOf("DisposePublisherAccountsAsync(_publisherAccounts)", StringComparison.Ordinal);
        var providers = shutdown.IndexOf("await Task.WhenAll(", StringComparison.Ordinal);
        Assert.True(page >= 0 && page < wuwaStart && page < publisherStart && publisherStart < providers);
        Assert.Contains("wuwaAccountShutdown", shutdown, StringComparison.Ordinal);
        Assert.Contains("publisherAccountShutdown", shutdown, StringComparison.Ordinal);
        Assert.Contains("_stableUpdateTask", shutdown, StringComparison.Ordinal);
        Assert.Contains("_accountShutdownComplete = true", app, StringComparison.Ordinal);
        Assert.DoesNotContain("publisherAccounts.DisposeAsync().AsTask().GetAwaiter().GetResult()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("accountStatus.DisposeAsync().AsTask().GetAwaiter().GetResult()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_read_actively_starts_one_bounded_fixed_endpoint_trigger_and_aborts_it_on_exit()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var read = Slice(
            browser,
            "public async Task<PublisherResourceReadResult> ReadResourceAsync",
            "private async Task<PublisherCheckInProof> CaptureCheckInProofAsync");

        Assert.Contains("BuildResourceFetchScript(", read, StringComparison.Ordinal);
        Assert.Contains(".ExecuteScriptAsync(", read, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Serialize(\"started\")", read, StringComparison.Ordinal);
        Assert.Contains("AbortResourceFetchAsync(", read, StringComparison.Ordinal);
        Assert.Contains("previous.Cancel();", read, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Read(ref resourceGeneration)", read, StringComparison.Ordinal);
        Assert.Contains("PublisherResourceTriggerPolicy.Seal(", read, StringComparison.Ordinal);
        Assert.DoesNotContain("return authority.Seal(", read, StringComparison.Ordinal);
        Assert.DoesNotContain("CookieManager", read, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_capture_cancellation_stops_inflight_bounded_response_reads()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var capture = Slice(
            browser,
            "private sealed class PendingResourceCapture",
            "private sealed class SessionProbeCapture(");

        Assert.Contains(
            "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)",
            capture,
            StringComparison.Ordinal);
        Assert.Contains("cancellation.Cancel();", capture, StringComparison.Ordinal);
        Assert.Contains("cancellation.Dispose();", capture, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref canceled, 1)", capture, StringComparison.Ordinal);
        Assert.Contains("this.cancellationToken = cancellation.Token", capture, StringComparison.Ordinal);
        Assert.Contains("public CancellationToken CancellationToken => cancellationToken", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_trigger_keeps_credentials_and_raw_role_data_inside_WebView2()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var script = Slice(
            browser,
            "private static string BuildResourceFetchScript",
            "private static string BuildHsrAchievementExportScript");

        Assert.Contains("method: 'GET'", script, StringComparison.Ordinal);
        Assert.Contains("credentials: 'include'", script, StringComparison.Ordinal);
        Assert.Contains("redirect: 'error'", script, StringComparison.Ordinal);
        Assert.Contains("cache: 'no-store'", script, StringComparison.Ordinal);
        Assert.Contains("referrerPolicy: 'no-referrer'", script, StringComparison.Ordinal);
        Assert.Contains("response.body.getReader()", script, StringComparison.Ordinal);
        Assert.Contains("MAX_ROLE_RESPONSE_BYTES", script, StringComparison.Ordinal);
        Assert.Contains("MAX_ROLE_COUNT", script, StringComparison.Ordinal);
        Assert.Contains("const REQUEST_TIMEOUT = 6000;", script, StringComparison.Ordinal);
        Assert.Contains("const OPERATION_TIMEOUT = 10000;", script, StringComparison.Ordinal);
        Assert.Contains("new AbortController()", script, StringComparison.Ordinal);
        Assert.Contains("operationController.signal.addEventListener", script, StringComparison.Ordinal);
        Assert.Contains("part.value.fill(0)", script, StringComparison.Ordinal);
        Assert.Contains("text = ''", script, StringComparison.Ordinal);
        Assert.Contains("roleResult = null", script, StringComparison.Ordinal);
        Assert.Contains("operationState.roles = roles.map(", script, StringComparison.Ordinal);
        Assert.Contains("operationState.roles.length = 0", script, StringComparison.Ordinal);
        Assert.Contains("discovered.length = 0", script, StringComparison.Ordinal);
        Assert.Contains("roles.length = 0", script, StringComparison.Ordinal);
        Assert.Contains("seen.clear()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("response.text()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("response.json()", script, StringComparison.Ordinal);
        Assert.DoesNotContain("response.arrayBuffer()", script, StringComparison.Ordinal);
        Assert.Contains(
            "{ headers: { 'x-rpc-language': 'en' } }",
            script,
            StringComparison.Ordinal);
        Assert.Contains("const response = await request(roleUrl, true);", script, StringComparison.Ordinal);
        Assert.Contains("await request(noteUrl);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("body:", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CookieManager", script, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.webview.postMessage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("return roleResult", script, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON.stringify(roleResult", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Hsr_resource_note_uses_the_exact_bounded_official_ds_formula_and_headers()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var script = Slice(
            browser,
            "private static string BuildResourceFetchScript",
            "private static string BuildHsrAchievementExportScript");
        var signer = Slice(script, "// HSR_DS_SIGNER_START", "// HSR_DS_SIGNER_END");

        Assert.Contains(
            "const HSR_DS_SALT = '6s25p5ox5y14umn1p61aqyyvbvvl3lrt';",
            signer,
            StringComparison.Ordinal);
        Assert.Contains("const HSR_DS_RANDOM_LENGTH = 6;", signer, StringComparison.Ordinal);
        Assert.Contains(
            "const HSR_DS_ALPHABET = 'abcdefghijklmnopqrstuvwxyz';",
            signer,
            StringComparison.Ordinal);
        Assert.Contains("globalThis.crypto.getRandomValues(sample)", signer, StringComparison.Ordinal);
        Assert.Contains("sample[0] >= 234", signer, StringComparison.Ordinal);
        Assert.Contains("random.length !== HSR_DS_RANDOM_LENGTH", signer, StringComparison.Ordinal);
        Assert.Contains("!Number.isSafeInteger(timestamp)", signer, StringComparison.Ordinal);
        Assert.Contains("timestamp < 1600000000", signer, StringComparison.Ordinal);
        Assert.Contains("timestamp > 4102444800", signer, StringComparison.Ordinal);
        Assert.Contains(
            "const material = 'salt=' + HSR_DS_SALT + '&t=' + timestamp + '&r=' + random;",
            signer,
            StringComparison.Ordinal);
        Assert.Contains("const signature = hsrMd5Ascii(material);", signer, StringComparison.Ordinal);
        Assert.Contains(
            "salt=6s25p5ox5y14umn1p61aqyyvbvvl3lrt&t=1700000000&r=abcdef",
            signer,
            StringComparison.Ordinal);
        Assert.Contains("52ac4768378434146675f980be7d092a", signer, StringComparison.Ordinal);
        Assert.Contains("'x-rpc-client_type': '5'", signer, StringComparison.Ordinal);
        Assert.Contains("'x-rpc-app_version': '1.5.0'", signer, StringComparison.Ordinal);
        Assert.Contains("'x-rpc-language': 'en-us'", signer, StringComparison.Ordinal);
        Assert.Contains(
            "DS: timestamp + ',' + random + ',' + signature",
            signer,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(signer, "'x-rpc-client_type'"));
        Assert.Equal(1, CountOccurrences(signer, "'x-rpc-app_version'"));
        Assert.Equal(1, CountOccurrences(signer, "'x-rpc-language'"));
        Assert.Equal(1, CountOccurrences(signer, "DS: timestamp"));
        Assert.Contains("noteHeaders = hsrNoteHeaders();", script, StringComparison.Ordinal);
        Assert.Contains(
            "await request(noteUrl, false, noteHeaders);",
            script,
            StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(script, "hsrNoteHeaders()"));
    }

    [Fact]
    public void Hsr_resource_signer_cannot_spill_to_other_games_or_inspect_identity_or_device_state()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var script = Slice(
            browser,
            "private static string BuildResourceFetchScript",
            "private static string BuildHsrAchievementExportScript");
        var policy = Slice(script, "var hsrSignerScript", "return $$");
        var signer = Slice(script, "// HSR_DS_SIGNER_START", "// HSR_DS_SIGNER_END");
        var discovery = Slice(script, "async function discover", "async function requestNote");

        Assert.Equal(2, CountOccurrences(policy, "gameId == \"hsr\""));
        Assert.DoesNotContain("gameId == \"gi\"", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("gameId == \"zzz\"", policy, StringComparison.Ordinal);
        Assert.Contains(": string.Empty;", policy, StringComparison.Ordinal);
        Assert.Contains(": \"await request(noteUrl);\";", policy, StringComparison.Ordinal);
        Assert.Contains("{{hsrSignerScript}}", script, StringComparison.Ordinal);
        Assert.Contains("{{noteRequestScript}}", script, StringComparison.Ordinal);
        Assert.Contains("const response = await request(roleUrl, true);", discovery, StringComparison.Ordinal);
        Assert.DoesNotContain("hsrNoteHeaders", discovery, StringComparison.Ordinal);
        foreach (var forbidden in new[]
        {
            "CookieManager", "document.cookie", "cookieStore", "device_id",
            "deviceId", "fingerprint", "geetest", "localStorage", "sessionStorage",
            "navigator", "screen.", "canvas", "role_id", "game_uid", "nickname",
            "SAVED_ROLE_UID", "SAVED_ROLE_REGION", "Origin", "Referer",
        })
        {
            Assert.DoesNotContain(forbidden, signer, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Resource_request_aborts_an_already_aborted_parent_before_fetch_and_always_cleans_up()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var script = Slice(
            browser,
            "private static string BuildResourceFetchScript",
            "private static string BuildHsrAchievementExportScript");
        var request = Slice(
            script,
            "async function request(url, roleDiscovery = false, noteHeaders = null)",
            "async function discover");

        var addListener = request.IndexOf(
            "operationController.signal.addEventListener('abort', abort, { once: true });",
            StringComparison.Ordinal);
        var alreadyAborted = request.IndexOf(
            "if (operationController.signal.aborted)",
            StringComparison.Ordinal);
        var abortChild = alreadyAborted < 0
            ? -1
            : request.IndexOf("requestController.abort();", alreadyAborted, StringComparison.Ordinal);
        var failClosed = abortChild < 0
            ? -1
            : request.IndexOf("throw INVALID;", abortChild, StringComparison.Ordinal);
        var fetch = request.IndexOf("await fetch(expected", StringComparison.Ordinal);
        var outerFinally = request.LastIndexOf("finally", StringComparison.Ordinal);
        var removeListener = request.IndexOf(
            "operationController.signal.removeEventListener('abort', abort);",
            StringComparison.Ordinal);

        Assert.True(addListener >= 0);
        Assert.True(addListener < alreadyAborted);
        Assert.True(alreadyAborted < abortChild);
        Assert.True(abortChild < failClosed);
        Assert.True(failClosed < fetch);
        Assert.True(fetch < outerFinally);
        Assert.True(outerFinally < removeListener);
        Assert.Equal(1, CountOccurrences(request, "addEventListener('abort', abort"));
        Assert.Equal(1, CountOccurrences(request, "removeEventListener('abort', abort"));
        Assert.Contains("clearTimeout(timeout);", request, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_trigger_classifies_only_fixed_pre_response_failure_stages()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var script = Slice(
            browser,
            "private static string BuildResourceFetchScript",
            "private static string BuildHsrAchievementExportScript");
        var request = Slice(
            script,
            "async function request(url, roleDiscovery = false, noteHeaders = null)",
            "async function discover");
        var operation = Slice(
            script,
            "let operationTimedOut",
            "return 'started';");

        Assert.Contains(
            "const SIGNATURE_REJECTED = Symbol('signature-rejected');",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const BROWSER_REQUEST_BLOCKED = Symbol('request-blocked');",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const OPERATION_TIMED_OUT = Symbol('timed-out');",
            script,
            StringComparison.Ordinal);
        Assert.Contains("noteHeaders = hsrNoteHeaders();", script, StringComparison.Ordinal);
        Assert.Contains("throw SIGNATURE_REJECTED;", script, StringComparison.Ordinal);

        Assert.Contains("let response;", request, StringComparison.Ordinal);
        Assert.Contains("let requestTimedOut = false;", request, StringComparison.Ordinal);
        Assert.Contains("requestTimedOut = true;", request, StringComparison.Ordinal);
        Assert.True(
            request.IndexOf("requestTimedOut = true;", StringComparison.Ordinal)
            < request.IndexOf("requestController.abort();", request.IndexOf(
                "requestTimedOut = true;",
                StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.Contains("response = await fetch(expected", request, StringComparison.Ordinal);
        Assert.Contains("if (operationController.signal.aborted)", request, StringComparison.Ordinal);
        Assert.Contains("if (requestTimedOut) throw OPERATION_TIMED_OUT;", request, StringComparison.Ordinal);
        Assert.Contains("if (requestController.signal.aborted)", request, StringComparison.Ordinal);
        Assert.Contains("throw BROWSER_REQUEST_BLOCKED;", request, StringComparison.Ordinal);
        Assert.True(
            request.IndexOf("response = await fetch(expected", StringComparison.Ordinal)
            < request.IndexOf("throw BROWSER_REQUEST_BLOCKED;", StringComparison.Ordinal));
        Assert.True(
            request.IndexOf("if (operationController.signal.aborted)", StringComparison.Ordinal)
            < request.IndexOf("if (requestTimedOut)", StringComparison.Ordinal));
        Assert.True(
            request.IndexOf("if (requestTimedOut)", StringComparison.Ordinal)
            < request.IndexOf("if (requestController.signal.aborted)", StringComparison.Ordinal));
        Assert.True(
            request.IndexOf("throw BROWSER_REQUEST_BLOCKED;", StringComparison.Ordinal)
            < request.IndexOf("if (!response || response.url !== expected)", StringComparison.Ordinal));

        Assert.Contains("let operationTimedOut = false;", operation, StringComparison.Ordinal);
        Assert.Contains("operationTimedOut = true;", operation, StringComparison.Ordinal);
        Assert.True(
            operation.IndexOf("operationTimedOut = true;", StringComparison.Ordinal)
            < operation.IndexOf("operationController.abort();", StringComparison.Ordinal));
        Assert.Contains("reason === SIGNATURE_REJECTED", operation, StringComparison.Ordinal);
        Assert.Contains("? 'signature-rejected'", operation, StringComparison.Ordinal);
        Assert.Contains("reason === BROWSER_REQUEST_BLOCKED", operation, StringComparison.Ordinal);
        Assert.Contains("? 'request-blocked'", operation, StringComparison.Ordinal);
        Assert.Contains("reason === OPERATION_TIMED_OUT", operation, StringComparison.Ordinal);
        Assert.Contains(": operationTimedOut", operation, StringComparison.Ordinal);
        Assert.Contains("? 'timed-out'", operation, StringComparison.Ordinal);
        Assert.Contains("clearTimeout(operationTimeout);", operation, StringComparison.Ordinal);
        Assert.Contains("operationState.roles.length = 0;", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("reason.message", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("String(reason", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("error.message", operation, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitized_role_handoff_is_consumed_once_and_raw_script_data_is_not_retained()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var read = Slice(
            browser,
            "private async Task<PublisherResourceTriggerResult?> ReadResourceFetchStateAsync",
            "private async Task AbortResourceFetchAsync");

        Assert.Contains("state.roles.splice(", read, StringComparison.Ordinal);
        Assert.Contains("state.roles.length = 0", read, StringComparison.Ordinal);
        Assert.Contains("state.status === 'signature-rejected'", read, StringComparison.Ordinal);
        Assert.Contains("state.status === 'request-blocked'", read, StringComparison.Ordinal);
        Assert.Contains("state.status === 'timed-out'", read, StringComparison.Ordinal);
        Assert.Contains(
            "PublisherResourceTriggerResultParser.TryParse(",
            read,
            StringComparison.Ordinal);
        Assert.DoesNotContain("roleResult", read, StringComparison.Ordinal);
        Assert.DoesNotContain("error.message", read, StringComparison.Ordinal);
        Assert.DoesNotContain("Console", read, StringComparison.Ordinal);
        Assert.DoesNotContain("Trace", read, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_trigger_handles_known_single_multiple_and_no_role_paths_without_returning_identifiers()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var script = Slice(
            browser,
            "private static string BuildResourceFetchScript",
            "private static string BuildHsrAchievementExportScript");

        Assert.Contains(
            "const HAS_SAVED_ROLE = SAVED_ROLE_REGION !== '' && SAVED_ROLE_UID !== '';",
            script,
            StringComparison.Ordinal);
        Assert.Contains("if (HAS_SAVED_ROLE)", script, StringComparison.Ordinal);
        Assert.Contains("await requestNote(SAVED_ROLE_REGION, SAVED_ROLE_UID)", script, StringComparison.Ordinal);
        Assert.Contains(
            "REGIONS.map(requestedRegion => discover(requestedRegion))",
            script,
            StringComparison.Ordinal);
        Assert.Contains("if (roles.length === 0) return;", script, StringComparison.Ordinal);
        Assert.Contains("if (roles.length > MAX_ROLE_COUNT)", script, StringComparison.Ordinal);
        Assert.Contains("for (const role of roles)", script, StringComparison.Ordinal);
        Assert.Contains("await requestNote(role.region, role.uid)", script, StringComparison.Ordinal);
        Assert.Contains("return 'started';", script, StringComparison.Ordinal);
        Assert.DoesNotContain("return roles", script, StringComparison.Ordinal);
        Assert.DoesNotContain("return roleResult", script, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON.stringify(roles", script, StringComparison.Ordinal);
        Assert.DoesNotContain("error.message", script, StringComparison.Ordinal);
        Assert.DoesNotContain("String(error", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_service_preserves_a_fixed_result_across_browser_teardown_quarantine()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        var read = refresh.IndexOf(
            "var resourceRead = await window.ReadResourceAsync",
            StringComparison.Ordinal);
        var diagnostic = refresh.IndexOf(
            "resourceRead.Diagnostic",
            read,
            StringComparison.Ordinal);
        var cleanup = refresh.IndexOf(
            "TryDeleteProtectedGameState(entry.GameId, entry.Provider, operation)",
            diagnostic,
            StringComparison.Ordinal);
        var catchBlock = refresh.IndexOf(
            "catch (Exception exception) when (exception is not OperationCanceledException)",
            StringComparison.Ordinal);
        var teardownCatch = refresh.IndexOf(
            "catch (PublisherSessionTeardownException)",
            StringComparison.Ordinal);
        var quarantine = refresh.IndexOf(
            "QuarantineProvider(entry.Provider, operation);",
            teardownCatch,
            StringComparison.Ordinal);
        var fixedFailure = refresh.IndexOf(
            "SetQuarantinedResourceFailure(",
            quarantine,
            StringComparison.Ordinal);

        Assert.True(read >= 0 && read < diagnostic && diagnostic < cleanup && cleanup < catchBlock);
        Assert.True(
            teardownCatch >= 0
            && teardownCatch < quarantine
            && quarantine < fixedFailure
            && fixedFailure < catchBlock);
        Assert.DoesNotContain("exception.Message", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.ToString", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void Quarantined_resource_publication_checks_generation_and_cancellation_inside_one_lock()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var publish = Slice(
            service,
            "private void SetQuarantinedResourceFailure(",
            "private PublisherGeneration GenerationFor");
        var lockIndex = publish.IndexOf("lock (sync)", StringComparison.Ordinal);
        var current = publish.IndexOf(
            "CanPublish(provider, operation)",
            lockIndex,
            StringComparison.Ordinal);
        var policy = publish.IndexOf(
            "PublisherResourceTeardownDiagnosticPolicy.ForQuarantine(",
            lockIndex,
            StringComparison.Ordinal);
        var write = publish.IndexOf(
            "resourceDiagnostics[gameId] = diagnostic;",
            policy,
            StringComparison.Ordinal);

        Assert.True(lockIndex >= 0 && lockIndex < policy && policy < current && current < write);
        Assert.DoesNotContain("consent.IsEnabled(provider)", publish, StringComparison.Ordinal);
        Assert.Contains("resources.Remove(gameId)", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("priorDiagnostic.ToString", publish, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("Console", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("Trace", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void Quarantine_cache_cleanup_is_write_ahead_durable_and_all_results_are_checked()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var quarantine = Slice(
            service,
            "private void QuarantineProvider(",
            "private void SetQuarantinedResourceFailure(");
        var pending = quarantine.IndexOf(
            "PublisherQuarantineCleanupStore.TryClean(",
            StringComparison.Ordinal);
        var volatilePending = quarantine.IndexOf(
            "SetCleanupPending(",
            pending,
            StringComparison.Ordinal);

        Assert.True(pending >= 0 && pending < volatilePending);
        Assert.DoesNotContain(
            "roleBindings.DeleteProvider(provider);",
            quarantine,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "resourceSnapshots.DeleteProvider(provider);",
            quarantine,
            StringComparison.Ordinal);

        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        var teardown = refresh.IndexOf(
            "catch (PublisherSessionTeardownException)",
            StringComparison.Ordinal);
        var nextCatch = refresh.IndexOf(
            "catch (OperationCanceledException)",
            teardown,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "resourceSnapshots.Delete(",
            refresh[teardown..nextCatch],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "roleBindings.Delete(",
            refresh[teardown..nextCatch],
            StringComparison.Ordinal);

        var constructor = Slice(
            service,
            "public PublisherAccountService(",
            "public event EventHandler? Updated;");
        var pendingOnRestart = constructor.IndexOf(
            "revocations.IsPending(\"HoYoLAB\")",
            StringComparison.Ordinal);
        var consent = constructor.IndexOf(
            "hoyoLabAccountAccess && !this.hoyoCleanupPending",
            pendingOnRestart,
            StringComparison.Ordinal);
        var restore = constructor.IndexOf(
            "RestoreCachedResources();",
            consent,
            StringComparison.Ordinal);
        var hoyoOwnership = constructor.IndexOf(
            "AcquireProfileOwnership(\"HoYoLAB\")",
            consent,
            StringComparison.Ordinal);
        var skportOwnership = constructor.IndexOf(
            "AcquireProfileOwnership(\"SKPORT\")",
            restore,
            StringComparison.Ordinal);
        Assert.True(
            pendingOnRestart >= 0
            && pendingOnRestart < consent
            && consent < hoyoOwnership
            && hoyoOwnership < restore
            && restore < skportOwnership);

        var app = ReadAppFile("App.xaml.cs");
        var recovery = Slice(
            app,
            "private async Task RecoverPendingPublisherRevocationsAsync()",
            "private bool TryPersistPublisherCleanupPending(");
        var discoverPending = recovery.IndexOf(
            "accounts.HasPendingConsentRevocation(provider)",
            StringComparison.Ordinal);
        var persistPending = recovery.IndexOf(
            "cleanupPending: true",
            discoverPending,
            StringComparison.Ordinal);
        var optOutIntent = recovery.IndexOf(
            "accounts.PendingConsentRevocationDisablesAccess(",
            discoverPending,
            StringComparison.Ordinal);
        var preserveOptOut = recovery.IndexOf(
            "accountAccess: disableAccess ? false : null",
            optOutIntent,
            StringComparison.Ordinal);
        var retry = recovery.IndexOf(
            "accounts.RetryPendingConsentRevocationAsync(provider)",
            persistPending,
            StringComparison.Ordinal);
        var clearMarker = recovery.IndexOf(
            "accounts.CompleteConsentRevocation(",
            retry,
            StringComparison.Ordinal);
        var preserveConcurrentOptOut = recovery.IndexOf(
            "clearOptOutIntent: disableAccess",
            clearMarker,
            StringComparison.Ordinal);
        var persistComplete = recovery.IndexOf(
            "cleanupPending: false",
            preserveConcurrentOptOut,
            StringComparison.Ordinal);
        Assert.True(
            discoverPending >= 0
            && discoverPending < optOutIntent
            && optOutIntent < persistPending
            && persistPending < preserveOptOut
            && preserveOptOut < retry
            && retry < clearMarker
            && clearMarker < preserveConcurrentOptOut
            && preserveConcurrentOptOut < persistComplete);
        Assert.Contains(
            "stateCleanupPending",
            recovery[discoverPending..optOutIntent],
            StringComparison.Ordinal);

        var persistence = Slice(
            app,
            "private bool TryPersistPublisherCleanupPending(",
            "private void AppWindow_Closing");
        Assert.Contains(
            "LauncherState.TryUpdatePublisherCleanupPending(",
            persistence,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AccountAccess =", persistence, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_revocation_requires_a_checked_durable_boundary_before_cleanup()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var revoke = Slice(
            service,
            "public async Task<PublisherConnectionState> RevokeConsentAsync(",
            "private async Task<PublisherConnectionState> DisconnectCoreAsync");
        var closeGate = revoke.IndexOf(
            "SetConsentSynchronized(entry.Provider, enabled: false)",
            StringComparison.Ordinal);
        var rotate = revoke.IndexOf(
            "RotateSession(entry.Provider)",
            closeGate,
            StringComparison.Ordinal);
        var cancel = revoke.IndexOf(
            "previousSession.Cancel()",
            rotate,
            StringComparison.Ordinal);
        var clearValues = revoke.IndexOf(
            "ClearProviderState(entry.Provider)",
            cancel,
            StringComparison.Ordinal);
        var persistState = revoke.IndexOf(
            "persistCleanupPending?.Invoke(",
            clearValues,
            StringComparison.Ordinal);
        var persistMarker = revoke.IndexOf(
            "revocations.MarkOptOutPending(entry.Provider)",
            persistState,
            StringComparison.Ordinal);
        var requireChannel = revoke.IndexOf(
            "if (!stateRecorded && !markerRecorded)",
            persistMarker,
            StringComparison.Ordinal);
        var mutate = revoke.IndexOf(
            "DisconnectCoreAsync(entry, consentRequired: false",
            requireChannel,
            StringComparison.Ordinal);

        Assert.True(
            closeGate >= 0
            && closeGate < rotate
            && rotate < cancel
            && cancel < clearValues
            && clearValues < persistState
            && persistState < persistMarker
            && persistMarker < requireChannel
            && requireChannel < mutate);
        Assert.Contains("SetCleanupPending(entry.Provider, pending: true)", revoke, StringComparison.Ordinal);
        Assert.Contains("PublisherConnectionState.NeedsReview", revoke, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(revoke, "ClearProviderState(entry.Provider)"));
    }

    [Fact]
    public void Explicit_destructive_authorities_still_clear_protected_resource_state()
    {
        foreach (var authority in new[]
        {
            PublisherProtectedStateAuthority.ExplicitConsentOff,
            PublisherProtectedStateAuthority.DisconnectOrProfileDeletion,
            PublisherProtectedStateAuthority.ProvenAccountOrRoleReplacement,
            PublisherProtectedStateAuthority.Quarantine,
        })
        {
            Assert.True(PublisherProtectedStateRetentionPolicy.ClearsVerifiedState(authority));
            Assert.False(PublisherProtectedStateRetentionPolicy.RetainsVerifiedState(authority));
        }

        var service = ReadAppFile("PublisherAccountService.cs");
        var connect = Slice(
            service,
            "public async Task<PublisherConnectionState> ConnectAsync",
            "public Task<PublisherResourceSnapshot?> RefreshResourceAsync");
        Assert.Contains("TryDeleteProtectedProviderState(entry.Provider, operation)", connect, StringComparison.Ordinal);

        var revoke = Slice(
            service,
            "public async Task<PublisherConnectionState> RevokeConsentAsync(",
            "private async Task<PublisherConnectionState> DisconnectCoreAsync");
        Assert.Contains("SetConsentSynchronized(entry.Provider, enabled: false)", revoke, StringComparison.Ordinal);
        Assert.Contains("ClearProviderState(entry.Provider)", revoke, StringComparison.Ordinal);

        var disconnect = Slice(
            service,
            "private async Task<PublisherConnectionState> DisconnectCoreAsync",
            "private void CommitDeletedProfile");
        Assert.Contains("roleBindings.DeleteProvider(entry.Provider)", disconnect, StringComparison.Ordinal);
        Assert.Contains("resourceSnapshots.DeleteProvider(entry.Provider)", disconnect, StringComparison.Ordinal);
        Assert.Contains("DeleteProfileDirectoryAsync(", disconnect, StringComparison.Ordinal);

        var quarantine = Slice(
            service,
            "private void QuarantineProvider(",
            "private void SetQuarantinedResourceFailure(");
        Assert.Contains("PublisherQuarantineCleanupStore.TryClean(", quarantine, StringComparison.Ordinal);
        Assert.Contains("ClearProviderState(provider)", quarantine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Protected_state_deletion_policy_checks_snapshot_and_role_failures_at_both_scopes(
        bool providerScope,
        bool snapshotDeleteSucceeded,
        bool roleDeleteSucceeded)
    {
        var snapshotDeleteCalls = 0;
        var roleDeleteCalls = 0;
        bool DeleteSnapshot()
        {
            snapshotDeleteCalls++;
            return snapshotDeleteSucceeded;
        }
        bool DeleteRole()
        {
            roleDeleteCalls++;
            return roleDeleteSucceeded;
        }

        var deleted = providerScope
            ? PublisherProtectedStateDeletionPolicy.TryDeleteProviderState(
                DeleteSnapshot,
                DeleteRole)
            : PublisherProtectedStateDeletionPolicy.TryDeleteGameState(
                DeleteSnapshot,
                DeleteRole);

        Assert.False(deleted);
        Assert.Equal(1, snapshotDeleteCalls);
        Assert.Equal(1, roleDeleteCalls);
    }

    [Fact]
    public void Account_replacement_paths_quarantine_and_stop_when_protected_deletion_fails()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var connect = Slice(
            service,
            "public async Task<PublisherConnectionState> ConnectAsync",
            "public Task<PublisherResourceSnapshot?> RefreshResourceAsync");
        Assert.Contains("if (!TryDeleteProtectedProviderState(entry.Provider, operation))", connect, StringComparison.Ordinal);
        Assert.Contains("return PublisherConnectionState.NeedsReview", connect, StringComparison.Ordinal);

        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");
        Assert.Equal(2, CountOccurrences(refresh, "if (!TryDeleteProtectedGameState(entry.GameId, entry.Provider, operation))"));

        var daily = Slice(
            service,
            "private async Task<PublisherDailyRoleResolution> ResolveDailyRoleAsync",
            "private async Task<PublisherSessionProof> ProbeConnectionCoreAsync");
        Assert.Contains("if (!TryDeleteProtectedGameState(entry.GameId, entry.Provider, operation))", daily, StringComparison.Ordinal);
        Assert.Contains("PublisherDailyRoleResolutionState.NeedsReview", daily, StringComparison.Ordinal);

        var interrupted = Slice(
            service,
            "private void CommitInterruptedProfileChange(",
            "private Task DeleteProfileDirectoryAsync(");
        Assert.Contains("if (!TryDeleteProtectedProviderState(provider)) return;", interrupted, StringComparison.Ordinal);

        var helpers = Slice(
            service,
            "private bool TryDeleteProtectedGameState(",
            "private void SetQuarantinedResourceFailure(");
        Assert.Contains("PublisherProtectedStateDeletionPolicy.TryDeleteGameState(", helpers, StringComparison.Ordinal);
        Assert.Contains("PublisherProtectedStateDeletionPolicy.TryDeleteProviderState(", helpers, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(helpers, "if (!deleted) QuarantineProvider(provider, operation);"));
    }

    [Fact]
    public void Role_binding_save_failures_publish_an_honest_terminal_resource_status()
    {
        var service = ReadAppFile("PublisherAccountService.cs");
        var refresh = Slice(
            service,
            "private async Task<PublisherResourceSnapshot?> RefreshResourceCoreAsync",
            "public Task<DailyCheckInResult> CheckInAsync");

        var officialSave = refresh.IndexOf(
            "&& !SaveRoleRecord(",
            StringComparison.Ordinal);
        Assert.True(officialSave >= 0);
        var officialReturn = refresh.IndexOf("return null;", officialSave, StringComparison.Ordinal);
        var officialFailure = refresh[officialSave..officialReturn];
        Assert.Contains(
            "PublisherResourceCaptureDiagnostic.NotAvailable",
            officialFailure,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublisherResourceState.NeedsReview",
            officialFailure,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_trigger_compiles_only_the_three_reviewed_game_contracts()
    {
        var browser = ReadAppFile("PublisherSessionWindow.xaml.cs");
        var script = Slice(
            browser,
            "private static string BuildResourceFetchScript",
            "private static string BuildHsrAchievementExportScript");

        Assert.Contains(
            "PublisherAccountCatalog.GetResourceFetchContract(gameId)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "contract.RoleDiscoveryEndpoint.AbsoluteUri",
            script,
            StringComparison.Ordinal);
        Assert.Contains("contract.NoteEndpoint.AbsoluteUri", script, StringComparison.Ordinal);
        Assert.Contains("contract.GameBusiness", script, StringComparison.Ordinal);
        Assert.Contains("contract.Regions", script, StringComparison.Ordinal);
        Assert.DoesNotContain("endsWith(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("includes(", script, StringComparison.Ordinal);
    }

    private static PublisherResourceReadResult DailyRoleRead(
        string gameId,
        PublisherResourceReadOutcome outcome,
        params PublisherRoleBinding[] bindings)
    {
        var entry = PublisherAccountCatalog.Get(gameId);
        var snapshot = new PublisherResourceSnapshot(
            gameId,
            entry.ResourceName,
            1,
            2,
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            RecoverySeconds: 300);
        return new(
            outcome == PublisherResourceReadOutcome.Valid ? snapshot : null,
            outcome,
            bindings.Select(binding => new PublisherResourceCandidate(binding, snapshot)).ToArray());
    }

    private static string ReadAppFile(string fileName) =>
        File.ReadAllText(Path.Combine(FindWorkspaceRoot(), "Desktop", "src", "Nyx.Desktop.App", fileName));

    private static string ReadCoreAccountFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindWorkspaceRoot(),
            "Desktop",
            "src",
            "Nyx.Desktop.Core",
            "AccountStatus",
            fileName));

    private static string ReadCoreStateFile(string fileName) =>
        File.ReadAllText(Path.Combine(
            FindWorkspaceRoot(),
            "Desktop",
            "src",
            "Nyx.Desktop.Core",
            "State",
            fileName));

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(fragment, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += fragment.Length;
        }
        return count;
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {startMarker}.");
        Assert.True(end > start, $"Could not find {endMarker} after {startMarker}.");
        return source[start..end];
    }

    private static bool AllowsConnect(
        string provider,
        string value,
        string method,
        string? json = null,
        string contentType = "application/json",
        string? gameId = null,
        PublisherWebResourceContext context = PublisherWebResourceContext.Fetch) =>
        PublisherAccountCatalog.IsAllowedWebResourceRequest(
            provider,
            PublisherSessionPurpose.Connect,
            gameId ?? (provider == "HoYoLAB" ? "gi" : "ae"),
            new Uri(value),
            method,
            context,
            requestBody: json is null ? null : Encoding.UTF8.GetBytes(json),
            contentType: json is null ? null : contentType);

    private static string FindWorkspaceRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(current.FullName, "Desktop")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Nyx workspace root.");
    }

    private static PublisherCheckInProof ParseProof(string gameId, string method, string json) =>
        PublisherAccountCatalog.ParseCheckInResponse(
            gameId,
            method,
            Encoding.UTF8.GetBytes(json),
            new DateOnly(2026, 7, 21),
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"));
}
