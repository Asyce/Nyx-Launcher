using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nyx.Desktop.Core.Content;
using Nyx.Desktop.Core.State;
using Nyx.Desktop.Infrastructure.Content;
using Nyx.Desktop.Infrastructure.State;

namespace Nyx.Desktop.Tests.Content;

public sealed class LauncherBannersContentTests
{
    [Fact]
    public void Production_https_endpoint_uses_its_implicit_default_port()
    {
        LauncherBannersTransport.ValidateEndpoint(
            new Uri(LauncherBannersTransport.ProductionEndpoint),
            allowConfigured: true,
            requireJson: true);
    }

    [Theory]
    [InlineData("https://evil.workers.dev/dist/launcher-banners-v1.json")]
    [InlineData("https://evil.hoyoverse.com/dist/launcher-banners-v1.json")]
    [InlineData("https://evil.kurogames.com/dist/launcher-banners-v1.json")]
    [InlineData("https://evil.gryphline.com/dist/launcher-banners-v1.json")]
    public void Transport_rejects_arbitrary_publisher_and_preview_subdomains(string url)
    {
        Assert.Throws<InvalidOperationException>(() => LauncherBannersTransport.ValidateEndpoint(
            new Uri(url), allowConfigured: true, requireJson: true));
    }

    [Theory]
    [InlineData("https://assets.pengo.gg/legacy/Database/GameData/hsr/icon.webp", true)]
    [InlineData("https://assets.pengo.gg/legacy/Database/GameData/hsr/icon.webp?x=1", false)]
    [InlineData("https://assets.pengo.gg:444/legacy/Database/GameData/hsr/icon.webp", false)]
    [InlineData("https://assets.pengo.gg/legacy/database/GameData/hsr/icon.webp", false)]
    [InlineData("https://assets.pengo.gg/legacy/Other/icon.webp", false)]
    [InlineData("https://assets.pengo.gg/legacy/Database/%2e%2e/icon.webp", false)]
    [InlineData("https://assets.pengo.gg/legacy/Database/GameData%2f..%2fOther/icon.webp", false)]
    [InlineData("https://evil.assets.pengo.gg/legacy/Database/GameData/hsr/icon.webp", false)]
    public void Asset_transport_allows_only_the_exact_legacy_database_origin(string url, bool allowed)
    {
        var action = () => LauncherBannersTransport.ValidateEndpoint(new Uri(url), allowConfigured: true, requireJson: false);
        if (allowed) action(); else Assert.Throws<InvalidOperationException>(action);
        Assert.Throws<InvalidOperationException>(() => LauncherBannersTransport.ValidateEndpoint(new Uri(url), allowConfigured: true, requireJson: true));
    }

    [Theory]
    [InlineData("https://pengo.gg/dist/launcher-art/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.webp", true)]
    [InlineData("https://pengo.gg/dist/launcher-art/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.webp?x=1", false)]
    [InlineData("https://pengo.gg:444/dist/launcher-art/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.webp", false)]
    [InlineData("https://user@pengo.gg/dist/launcher-art/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.webp", false)]
    [InlineData("https://pengo.gg/dist/launcher-art/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.webp#x", false)]
    [InlineData("https://pengo.gg/dist/launcher-art/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.webp", false)]
    [InlineData("https://pengo.gg/dist/launcher-art/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png", false)]
    [InlineData("https://pengo.gg/dist/launcher-art/%2e%2e/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.webp", false)]
    [InlineData("https://pengo.gg/other/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.webp", false)]
    public void Asset_transport_allows_only_exact_content_addressed_art(string url, bool allowed)
    {
        var action = () => LauncherBannersTransport.ValidateEndpoint(new Uri(url), allowConfigured: true, requireJson: false);
        if (allowed) action(); else Assert.Throws<InvalidOperationException>(action);
    }

    [Theory]
    [InlineData("https://pengo.gg/dist/launcher-banners-v1.json", true)]
    [InlineData("https://pengo.gg/dist/launcher-codes-v1.json", true)]
    [InlineData("https://pengo.gg/dist/launcher-tools-v1.json", true)]
    [InlineData("https://pengo.gg/dist/other.json", false)]
    [InlineData("https://pengo.gg/dist/launcher-banners-v1.json?x=1", false)]
    [InlineData("https://pengo.gg:444/dist/launcher-banners-v1.json", false)]
    [InlineData("https://user@pengo.gg/dist/launcher-banners-v1.json", false)]
    [InlineData("https://pengo.gg/dist/launcher-banners-v1.json#x", false)]
    public void Json_transport_allows_only_the_three_fixed_production_feeds(string url, bool allowed)
    {
        var action = () => LauncherBannersTransport.ValidateEndpoint(new Uri(url), allowConfigured: true, requireJson: true);
        if (allowed) action(); else Assert.Throws<InvalidOperationException>(action);
    }

    [Fact]
    public void Tools_parser_accepts_full_subset_and_empty_feeds_in_canonical_order()
    {
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var full = LauncherBannersManifestParser.ParseTools(
            ToolsJson(now.AddMinutes(-10), OfficialToolRows.Reverse()),
            observedAt: now);

        Assert.Equal(
            OfficialToolRows,
            full.Tools.Select(static tool => (tool.Game, tool.Id, tool.Label, tool.Url.OriginalString)));
        Assert.Equal(13, full.Tools.Count);
        Assert.DoesNotContain(full.Tools, static tool => tool.Game == "wuwa");
        Assert.All(full.Tools, tool => Assert.True(LauncherBannersManifestParser.IsApprovedOfficialTool(
            tool.Game,
            tool.Id,
            tool.Label,
            tool.Url)));

        var subsetRows = new[] { OfficialToolRows[^1], OfficialToolRows[0] };
        var subset = LauncherBannersManifestParser.ParseTools(
            ToolsJson(now.AddMinutes(-9), subsetRows),
            observedAt: now);
        Assert.Equal(
            new[] { OfficialToolRows[0], OfficialToolRows[^1] },
            subset.Tools.Select(static tool => (tool.Game, tool.Id, tool.Label, tool.Url.OriginalString)));

        Assert.Empty(LauncherBannersManifestParser.ParseTools(
            ToolsJson(now.AddMinutes(-8), []),
            observedAt: now).Tools);
    }

    [Fact]
    public void Tools_parser_rejects_unknown_duplicate_extra_missing_and_malformed_content()
    {
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var valid = JsonNode.Parse(ToolsJson(now.AddMinutes(-10), [OfficialToolRows[0]]))!.AsObject();

        var unknown = JsonNode.Parse(ToolsJson(
            now.AddMinutes(-10),
            [("wuwa", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/genshin/home")]))!.AsObject();
        AssertInvalid(unknown);

        var duplicate = valid.DeepClone().AsObject();
        duplicate["tools"]!.AsArray().Add(duplicate["tools"]![0]!.DeepClone());
        AssertInvalid(duplicate);

        var extraRowField = valid.DeepClone().AsObject();
        extraRowField["tools"]![0]!["revision"] = "not-allowed";
        AssertInvalid(extraRowField);

        var missingRowField = valid.DeepClone().AsObject();
        missingRowField["tools"]![0]!.AsObject().Remove("label");
        AssertInvalid(missingRowField);

        var extraRootField = valid.DeepClone().AsObject();
        extraRootField["revision"] = new string('a', 64);
        AssertInvalid(extraRootField);

        var missingRootField = valid.DeepClone().AsObject();
        missingRootField.Remove("tools");
        AssertInvalid(missingRootField);

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.ParseTools(
            Encoding.UTF8.GetBytes("{"),
            observedAt: now));

        void AssertInvalid(JsonObject root) => Assert.Throws<InvalidDataException>(() =>
            LauncherBannersManifestParser.ParseTools(JsonSerializer.SerializeToUtf8Bytes(root), observedAt: now));
    }

    [Theory]
    [InlineData("gi", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/genshin/home", true)]
    [InlineData("gi", "wiki", "Wiki", "http://wiki.hoyolab.com/pc/genshin/home", false)]
    [InlineData("gi", "wiki", "Wiki", "https://wiki.hoyolab.com:443/pc/genshin/home", false)]
    [InlineData("gi", "wiki", "Wiki", "https://user@wiki.hoyolab.com/pc/genshin/home", false)]
    [InlineData("gi", "wiki", "Wiki", "https://evil.hoyolab.com/pc/genshin/home", false)]
    [InlineData("gi", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/genshin/other", false)]
    [InlineData("gi", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/genshin/home?x=1", false)]
    [InlineData("gi", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/genshin/home#x", false)]
    [InlineData("gi", "wiki", "Official Wiki", "https://wiki.hoyolab.com/pc/genshin/home", false)]
    [InlineData("hsr", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/genshin/home", false)]
    [InlineData("gi", "other", "Wiki", "https://wiki.hoyolab.com/pc/genshin/home", false)]
    public void Official_tool_authority_and_parser_require_the_exact_canonical_tuple(
        string game,
        string id,
        string label,
        string url,
        bool approved)
    {
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        Assert.Equal(
            approved,
            LauncherBannersManifestParser.IsApprovedOfficialTool(game, id, label, new Uri(url)));
        var parse = () => LauncherBannersManifestParser.ParseTools(
            ToolsJson(now.AddMinutes(-10), [(game, id, label, url)]),
            observedAt: now);
        if (approved) parse(); else Assert.Throws<InvalidDataException>(parse);
    }

    [Fact]
    public void Tools_parser_rejects_stale_or_future_remote_data_but_allows_an_older_cached_fallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-tools-fallback-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var stale = ToolsJson(now - LauncherBannersManifestParser.MaximumRemoteAge - TimeSpan.FromSeconds(1));
        var future = ToolsJson(now + LauncherBannersManifestParser.MaximumFutureSkew + TimeSpan.FromSeconds(1));

        try
        {
            Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.ParseTools(stale, observedAt: now));
            Assert.Equal(13, LauncherBannersManifestParser.ParseTools(stale, fallback: true, observedAt: now).Tools.Count);
            Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.ParseTools(future, observedAt: now));
            Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.ParseTools(future, fallback: true, observedAt: now));

            var cache = new LauncherBannersCache(root);
            Directory.CreateDirectory(cache.LastKnownGoodDirectory);
            File.WriteAllBytes(cache.LastKnownGoodToolsPath, stale);
            Assert.Equal(13, cache.TryLoadLastKnownGoodTools(now)!.Tools.Count);
            File.WriteAllBytes(cache.LastKnownGoodToolsPath, future);
            Assert.Null(cache.TryLoadLastKnownGoodTools(now));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Tools_cache_reparses_atomic_promotions_and_preserves_the_last_good_on_replay_or_tamper()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-tools-cache-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var firstPayload = ToolsJson(now.AddMinutes(-30), [OfficialToolRows[0], OfficialToolRows[4]]);
        var first = LauncherBannersManifestParser.ParseTools(firstPayload, observedAt: now);
        var newerPayload = ToolsJson(now.AddMinutes(-20), [OfficialToolRows[1]]);
        var newer = LauncherBannersManifestParser.ParseTools(newerPayload, observedAt: now);
        try
        {
            var cache = new LauncherBannersCache(root);
            await cache.PromoteToolsAsync(first, firstPayload);
            var saved = File.ReadAllBytes(cache.LastKnownGoodToolsPath);
            Assert.Equal(first.Tools, cache.TryLoadLastKnownGoodTools(now)!.Tools);

            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PromoteToolsAsync(first, newerPayload));
            Assert.Equal(saved, File.ReadAllBytes(cache.LastKnownGoodToolsPath));

            var tampered = JsonNode.Parse(newerPayload)!.AsObject();
            tampered["tools"]![0]!["label"] = "Tampered";
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PromoteToolsAsync(
                newer,
                JsonSerializer.SerializeToUtf8Bytes(tampered)));
            Assert.Equal(saved, File.ReadAllBytes(cache.LastKnownGoodToolsPath));

            var replayPayload = ToolsJson(now.AddMinutes(-40), [OfficialToolRows[0]]);
            var replay = LauncherBannersManifestParser.ParseTools(replayPayload, observedAt: now);
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PromoteToolsAsync(replay, replayPayload));
            Assert.Equal(saved, File.ReadAllBytes(cache.LastKnownGoodToolsPath));

            var replacementPayload = ToolsJson(first.GeneratedAt, [OfficialToolRows[1]]);
            var replacement = LauncherBannersManifestParser.ParseTools(replacementPayload, observedAt: now);
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PromoteToolsAsync(replacement, replacementPayload));
            Assert.Equal(saved, File.ReadAllBytes(cache.LastKnownGoodToolsPath));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.PromoteToolsAsync(
                newer,
                newerPayload,
                cancellation.Token));
            Assert.Equal(saved, File.ReadAllBytes(cache.LastKnownGoodToolsPath));

            await cache.PromoteToolsAsync(newer, newerPayload);
            Assert.Equal(newerPayload, File.ReadAllBytes(cache.LastKnownGoodToolsPath));
            Assert.Equal(newer.Tools, cache.TryLoadLastKnownGoodTools(now)!.Tools);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_fetches_restores_revalidates_and_immediately_applies_tool_removal()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-tools-service-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var bannerEndpoint = new Uri("http://127.0.0.1:32123/launcher-banners-v1.json");
        var codesEndpoint = new Uri("http://127.0.0.1:32123/launcher-codes-v1.json");
        var toolsEndpoint = new Uri("http://127.0.0.1:32123/launcher-tools-v1.json");
        var firstPayload = ToolsJson(now.AddMinutes(-10), [OfficialToolRows[0]]);
        var olderPayload = ToolsJson(now.AddMinutes(-20), []);
        var replacementPayload = ToolsJson(now.AddMinutes(-10), [OfficialToolRows[1]]);
        try
        {
            var transport = new RoutedToolsTransport(
                tools: [firstPayload, firstPayload, olderPayload, replacementPayload]);
            await using (var service = new LauncherBannersContentService(
                ManifestJson(null),
                root,
                bannerEndpoint,
                transport,
                () => now,
                TimeSpan.FromMinutes(15),
                codesEndpoint: codesEndpoint,
                toolsEndpoint: toolsEndpoint))
            {
                var updates = 0;
                service.Updated += (_, _) => updates++;
                Assert.Empty(service.OfficialToolsFor("gi"));

                await service.RefreshAsync();
                var firstRead = service.OfficialToolsFor("gi");
                Assert.Equal("wiki", Assert.Single(firstRead).Id);
                Assert.NotSame(firstRead, service.OfficialToolsFor("gi"));
                Assert.Empty(service.OfficialToolsFor("hsr"));
                Assert.Empty(service.OfficialToolsFor("wuwa"));

                await service.RefreshAsync();
                await service.RefreshAsync();
                await service.RefreshAsync();

                Assert.Equal("wiki", Assert.Single(service.OfficialToolsFor("gi")).Id);
                Assert.Equal(1, updates);
                Assert.Equal(4, transport.ToolsRequests);
                Assert.Equal(toolsEndpoint, transport.ToolsEndpoint);
                Assert.Equal(firstPayload, File.ReadAllBytes(new LauncherBannersCache(root).LastKnownGoodToolsPath));
            }

            await using (var restarted = new LauncherBannersContentService(
                ManifestJson(null),
                root,
                bannerEndpoint,
                new RoutedToolsTransport(),
                () => now,
                TimeSpan.FromMinutes(15),
                codesEndpoint: codesEndpoint,
                toolsEndpoint: toolsEndpoint))
            {
                Assert.Equal("wiki", Assert.Single(restarted.OfficialToolsFor("gi")).Id);
            }

            var removedPayload = ToolsJson(now.AddMinutes(-5), []);
            await using var removal = new LauncherBannersContentService(
                ManifestJson(null),
                root,
                bannerEndpoint,
                new RoutedToolsTransport(tools: [removedPayload]),
                () => now,
                TimeSpan.FromMinutes(15),
                codesEndpoint: codesEndpoint,
                toolsEndpoint: toolsEndpoint);
            var removalUpdates = 0;
            removal.Updated += (_, _) => removalUpdates++;
            Assert.Single(removal.OfficialToolsFor("gi"));

            await removal.RefreshAsync();

            Assert.Empty(removal.OfficialToolsFor("gi"));
            Assert.Equal(1, removalUpdates);
            Assert.Empty(new LauncherBannersCache(root).TryLoadLastKnownGoodTools(now)!.Tools);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Tool_failure_cannot_suppress_independent_banner_or_code_refreshes()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-tools-independent-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var bannerPayload = ManifestIdentityJson(now.AddMinutes(-20), 'b');
        var codesPayload = CodesJson(now.AddMinutes(-10), "INDEPENDENT", 'c');
        var transport = new RoutedToolsTransport(
            banners: [bannerPayload],
            codes: [codesPayload],
            tools: [Encoding.UTF8.GetBytes("{")]);
        try
        {
            await using var service = new LauncherBannersContentService(
                ManifestJson(null),
                root,
                new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                transport,
                () => now,
                TimeSpan.FromMinutes(15),
                codesEndpoint: new Uri("http://127.0.0.1:32123/launcher-codes-v1.json"),
                toolsEndpoint: new Uri("http://127.0.0.1:32123/launcher-tools-v1.json"));
            var updates = 0;
            service.Updated += (_, _) => updates++;

            await service.RefreshAsync();

            Assert.Equal(now.AddMinutes(-20), service.Current.GeneratedAt);
            Assert.Equal("INDEPENDENT", Assert.Single(service.Current.Games["gi"].Codes).Code);
            Assert.Empty(service.OfficialToolsFor("gi"));
            Assert.Equal(1, updates);
            var cache = new LauncherBannersCache(root);
            Assert.True(File.Exists(cache.LastKnownGoodManifestPath));
            Assert.True(File.Exists(cache.LastKnownGoodCodesPath));
            Assert.False(File.Exists(cache.LastKnownGoodToolsPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Parser_accepts_only_the_exact_legacy_database_asset_origin()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        AssertAssetUrl("https://assets.pengo.gg/legacy/Database/GameData/hsr/icon.webp", accepted: true);
        AssertAssetUrl("https://assets.pengo.gg/legacy/Database/GameData/hsr/icon.webp?x=1", accepted: false);
        AssertAssetUrl("https://assets.pengo.gg/legacy/Other/icon.webp", accepted: false);

        void AssertAssetUrl(string url, bool accepted)
        {
            var root = JsonNode.Parse(ManifestWithAssetJson(generatedAt))!.AsObject();
            var asset = root["games"]!["gi"]!["current"]!["variants"]![0]!;
            asset["path"] = "/Database/GameData/hsr/icon.webp";
            asset["url"] = url;
            var payload = JsonSerializer.SerializeToUtf8Bytes(root);
            if (accepted) LauncherBannersManifestParser.Parse(payload, true, generatedAt);
            else Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(payload, true, generatedAt));
        }
    }

    [Fact]
    public void Parser_preserves_an_exact_supported_banner_region()
    {
        var payload = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ManifestJson(null))
            .Replace("\"region\":\"global\"", "\"region\":\"europe\"", StringComparison.Ordinal));
        var manifest = LauncherBannersManifestParser.Parse(payload, fallback: true, DateTimeOffset.UtcNow);
        Assert.All(manifest.Games.Values, game => Assert.Equal("europe", game.Region));

        var unsupported = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ManifestJson(null))
            .Replace("\"region\":\"global\"", "\"region\":\"moon\"", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(unsupported, fallback: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Parser_requires_all_five_games_and_keeps_unsafe_news_non_clickable()
    {
        var payload = ManifestJson("https://evil.example/news");
        var manifest = LauncherBannersManifestParser.Parse(payload, fallback: true, DateTimeOffset.UtcNow);
        Assert.Equal(5, manifest.Games.Count);
        Assert.Single(manifest.Games["gi"].News);
        Assert.False(manifest.Games["gi"].News[0].IsLinkSafe);
        Assert.Null(manifest.Games["gi"].News[0].ApprovedUrl);
        Assert.Equal("https://evil.example/news", manifest.Games["gi"].News[0].RawUrl);
    }

    [Fact]
    public void Parser_accepts_at_most_five_safe_dated_redemption_codes()
    {
        var text = Encoding.UTF8.GetString(ManifestJson(null))
            .Replace("\"news\":", "\"codes\":[{\"code\":\"NYX_2026\",\"added\":\"2026-07-17\"}],\"news\":", StringComparison.Ordinal);
        var manifest = LauncherBannersManifestParser.Parse(Encoding.UTF8.GetBytes(text), fallback: true, DateTimeOffset.UtcNow);

        var code = Assert.Single(manifest.Games["gi"].Codes);
        Assert.Equal("NYX_2026", code.Code);
        Assert.Equal(new DateOnly(2026, 7, 17), code.Added);

        var unsafeCode = text.Replace("NYX_2026", "NYX CODE", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(Encoding.UTF8.GetBytes(unsafeCode), true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Dedicated_code_feed_requires_all_five_games_and_exact_safe_rows()
    {
        var games = string.Join(',', new[] { "gi", "hsr", "zzz", "wuwa", "ae" }
            .Select(game => $"\"{game}\":[{{\"code\":\"{game.ToUpperInvariant()}2026\",\"added\":\"2026-07-17\",\"amount\":60,\"currency\":\"Premium\"}}]"));
        var payload = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"revision\":\"{new string('b', 64)}\",\"generatedAt\":\"2026-07-17T00:00:00.000Z\",\"games\":{{{games}}}}}");
        var manifest = LauncherBannersManifestParser.ParseCodes(payload, fallback: true, DateTimeOffset.Parse("2026-07-17T01:00:00Z"));

        Assert.Equal(5, manifest.Games.Count);
        var code = Assert.Single(manifest.Games["gi"]);
        Assert.Equal("GI2026", code.Code);
        Assert.Equal(60, code.CurrencyAmount);
        Assert.Equal("Premium", code.CurrencyName);

        var missing = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("\"ae\":[{\"code\":\"AE2026\",\"added\":\"2026-07-17\",\"amount\":60,\"currency\":\"Premium\"}]", "", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.ParseCodes(missing, true, DateTimeOffset.Parse("2026-07-17T01:00:00Z")));

        var incomplete = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("\"currency\":\"Premium\"", "\"currency\":\"\"", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.ParseCodes(incomplete, true, DateTimeOffset.Parse("2026-07-17T01:00:00Z")));
    }

    [Fact]
    public void Parser_accepts_explicit_default_https_port_for_official_news()
    {
        var manifest = LauncherBannersManifestParser.Parse(ManifestJson("https://genshin.hoyoverse.com:443/news"), fallback: true, DateTimeOffset.UtcNow);
        Assert.True(manifest.Games["gi"].News[0].IsLinkSafe);
        Assert.Equal(443, manifest.Games["gi"].News[0].ApprovedUrl!.Port);
    }

    [Fact]
    public void Parser_rejects_a_different_games_publisher_host()
    {
        var manifest = LauncherBannersManifestParser.Parse(
            ManifestJson("https://sg-hkrpg-api.hoyoverse.com/news"),
            fallback: true,
            DateTimeOffset.UtcNow);

        Assert.False(manifest.Games["gi"].News[0].IsLinkSafe);
        Assert.Null(manifest.Games["gi"].News[0].ApprovedUrl);
    }

    [Theory]
    [InlineData("https://evil.sg-hk4e-api.hoyoverse.com/news")]
    [InlineData("https://pengo.gg/news")]
    public void Parser_requires_an_exact_game_news_host(string url)
    {
        var manifest = LauncherBannersManifestParser.Parse(ManifestJson(url), true, DateTimeOffset.UtcNow);
        Assert.False(manifest.Games["gi"].News[0].IsLinkSafe);
        Assert.Null(manifest.Games["gi"].News[0].ApprovedUrl);
    }

    [Fact]
    public void Parser_keeps_current_only_inside_the_start_inclusive_end_exclusive_window()
    {
        var start = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var end = DateTimeOffset.Parse("2026-07-18T00:00:00Z");
        var payload = ManifestWithWindowJson(start, end);

        Assert.NotNull(LauncherBannersManifestParser.Parse(payload, true, start).Games["gi"].Current);
        Assert.NotNull(LauncherBannersManifestParser.Parse(payload, true, end.AddTicks(-1)).Games["gi"].Current);
        Assert.Null(LauncherBannersManifestParser.Parse(payload, true, start.AddTicks(-1)).Games["gi"].Current);
        Assert.Null(LauncherBannersManifestParser.Parse(payload, true, end).Games["gi"].Current);
    }

    [Fact]
    public void Parser_requires_each_schema_v1_game_to_have_exactly_empty_collections()
    {
        var accepted = LauncherBannersManifestParser.Parse(ManifestJson(null), true, DateTimeOffset.UtcNow);
        Assert.All(accepted.Games.Values, game => Assert.Empty(game.Upcoming));

        var missing = JsonNode.Parse(Encoding.UTF8.GetString(ManifestJson(null)))!.AsObject();
        missing["games"]!["gi"]!.AsObject().Remove("collections");
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            JsonSerializer.SerializeToUtf8Bytes(missing), true, DateTimeOffset.UtcNow));

        var nonEmpty = JsonNode.Parse(Encoding.UTF8.GetString(ManifestJson(null)))!.AsObject();
        nonEmpty["games"]!["gi"]!["collections"] = new JsonArray(new JsonObject());
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            JsonSerializer.SerializeToUtf8Bytes(nonEmpty), true, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("degraded")]
    [InlineData("unavailable")]
    public void Remote_parser_rejects_an_unhealthy_manifest(string status)
    {
        var text = ReplaceFirst(
            Encoding.UTF8.GetString(ManifestJson(null)),
            "\"status\":\"ok\"",
            $"\"status\":\"{status}\"");

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            Encoding.UTF8.GetBytes(text),
            fallback: false,
            DateTimeOffset.Parse("2026-07-17T00:01:00Z")));
    }

    [Theory]
    [InlineData("unknown", "ok")]
    [InlineData("ok", "unknown")]
    public void Parser_rejects_health_values_outside_the_contract(string overall, string game)
    {
        var text = ReplaceFirst(
            Encoding.UTF8.GetString(ManifestJson(null)),
            "\"status\":\"ok\"",
            $"\"status\":\"{overall}\"");
        if (game != "ok") text = ReplaceFirst(text, "\"status\":\"ok\"", $"\"status\":\"{game}\"");

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            Encoding.UTF8.GetBytes(text),
            fallback: true,
            DateTimeOffset.Parse("2026-07-17T00:01:00Z")));
    }

    [Fact]
    public void Parser_requires_the_same_five_canonical_games_in_health_and_content()
    {
        var missingContent = JsonNode.Parse(Encoding.UTF8.GetString(ManifestJson(null)))!.AsObject();
        missingContent["games"]!.AsObject().Remove("ae");
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            JsonSerializer.SerializeToUtf8Bytes(missingContent),
            true,
            DateTimeOffset.Parse("2026-07-17T00:01:00Z")));

        var missingHealth = JsonNode.Parse(Encoding.UTF8.GetString(ManifestJson(null)))!.AsObject();
        missingHealth["health"]!["games"]!.AsObject().Remove("ae");
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            JsonSerializer.SerializeToUtf8Bytes(missingHealth),
            true,
            DateTimeOffset.Parse("2026-07-17T00:01:00Z")));
    }

    [Fact]
    public void Parser_rejects_health_news_counts_that_disagree_with_content()
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(ManifestJson(null)))!.AsObject();
        root["health"]!["games"]!["gi"]!["newsCount"] = 2;
        var payload = JsonSerializer.SerializeToUtf8Bytes(root);

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            payload,
            fallback: true,
            DateTimeOffset.Parse("2026-07-17T00:01:00Z")));
    }

    [Fact]
    public void Unhealthy_game_phases_are_rejected_remotely_and_hidden_in_fallbacks()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var root = JsonNode.Parse(Encoding.UTF8.GetString(ManifestWithGiPhasesJson(
            generatedAt,
            generatedAt.AddHours(-1),
            generatedAt.AddHours(1),
            [(generatedAt.AddHours(1), generatedAt.AddHours(2))])))!.AsObject();
        root["health"]!["games"]!["gi"]!["status"] = "degraded";
        var payload = JsonSerializer.SerializeToUtf8Bytes(root);

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(payload, false, generatedAt));
        var fallback = LauncherBannersManifestParser.Parse(payload, true, generatedAt);
        Assert.Null(fallback.Games["gi"].Current);
        Assert.Empty(fallback.Games["gi"].Upcoming);
    }

    [Fact]
    public void Remote_parser_rejects_expired_or_future_current_phases_while_fallback_hides_them()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var start = generatedAt.AddHours(-1);
        var end = generatedAt.AddHours(1);
        var payload = ManifestWithGiPhasesJson(generatedAt, start, end);

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(payload, false, end));
        Assert.Null(LauncherBannersManifestParser.Parse(payload, true, end).Games["gi"].Current);
        var futurePayload = ManifestWithGiPhasesJson(generatedAt, generatedAt.AddHours(1), generatedAt.AddHours(2));
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(futurePayload, false, generatedAt));
        Assert.Null(LauncherBannersManifestParser.Parse(futurePayload, true, generatedAt).Games["gi"].Current);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Parser_rejects_a_forged_current_countdown(int adjustment)
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var payload = ManifestWithGiPhasesJson(
            generatedAt,
            generatedAt.AddHours(-1),
            generatedAt.AddHours(1),
            countdownAdjustment: adjustment);

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(payload, true, generatedAt));
    }

    [Fact]
    public void Parser_rejects_non_positive_current_and_upcoming_windows()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            ManifestWithGiPhasesJson(generatedAt, generatedAt, generatedAt),
            true,
            generatedAt));
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            ManifestWithGiPhasesJson(generatedAt, null, null, [(generatedAt.AddHours(1), generatedAt.AddHours(1))]),
            true,
            generatedAt));
    }

    [Fact]
    public void Parser_rejects_overlapping_current_and_upcoming_windows()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var payload = ManifestWithGiPhasesJson(
            generatedAt,
            generatedAt.AddHours(-1),
            generatedAt.AddHours(1),
            [(generatedAt.AddMinutes(30), generatedAt.AddHours(2))]);

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(payload, true, generatedAt));
    }

    [Fact]
    public void Parser_rejects_duplicate_or_overlapping_upcoming_windows()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var first = (generatedAt.AddHours(2), generatedAt.AddHours(3));

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            ManifestWithGiPhasesJson(generatedAt, null, null, [first, first]),
            true,
            generatedAt));
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
            ManifestWithGiPhasesJson(generatedAt, null, null, [first, (generatedAt.AddMinutes(150), generatedAt.AddHours(4))]),
            true,
            generatedAt));
    }

    [Fact]
    public void Remote_parser_rejects_an_upcoming_phase_that_has_already_started()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var observedAt = generatedAt.AddHours(1);
        var payload = ManifestWithGiPhasesJson(
            generatedAt,
            null,
            null,
            [(generatedAt.AddMinutes(30), generatedAt.AddHours(2))]);

        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(payload, false, observedAt));
        Assert.Empty(LauncherBannersManifestParser.Parse(payload, true, observedAt).Games["gi"].Upcoming);
    }

    [Fact]
    public void Announced_upcoming_requires_icons_and_survives_date_filtering()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var root = JsonNode.Parse(ManifestJson(null))!.AsObject();
        var announced = new JsonObject
        {
            ["phase"] = null,
            ["announced"] = true,
            ["start"] = null,
            ["end"] = null,
            ["characters"] = new JsonArray(
                TestCharacterJson("si"),
                TestCharacterJson("hongshan"),
                TestCharacterJson("sarkaz")),
        };
        root["games"]!["ae"]!["upcoming"] = new JsonArray(announced);
        var payload = WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(root));

        var parsed = LauncherBannersManifestParser.Parse(payload, fallback: false, generatedAt);
        var phase = Assert.Single(parsed.Games["ae"].Upcoming);
        Assert.True(phase.Announced);
        Assert.Null(phase.Start);
        Assert.Null(phase.End);
        Assert.Equal(["si", "hongshan", "sarkaz"], phase.Characters.Select(character => character.Name));
        Assert.Single(parsed.ForDisplayAt(generatedAt.AddYears(1)).Games["ae"].Upcoming);

        announced["characters"]![0]!["icon"] = null;
        var iconless = WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(root));
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(iconless, fallback: true, generatedAt));
    }

    [Fact]
    public void Upcoming_projection_keeps_announced_rows_after_scheduled_rows()
    {
        var now = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var icon = RemoteAsset("projection", WebpFixture(50));
        LauncherBannersUpcomingPhase Phase(string name, DateTimeOffset? start, DateTimeOffset? end, bool announced = false) =>
            new(name, start, end, [new(name, name, 6, true, null, [], icon)], announced);
        var game = new LauncherBannersGame("ae", "global", null, [],
        [
            Phase("ANNOUNCED", null, null, announced: true),
            Phase("LATER", now.AddHours(2), now.AddHours(3)),
            Phase("EXPIRED", now.AddHours(-2), now.AddHours(-1)),
            Phase("NEXT", now.AddHours(1), now.AddHours(2)),
        ]);

        Assert.Equal(
            ["NEXT", "LATER", "ANNOUNCED"],
            game.UpcomingForDisplayAt(now, 3).Select(phase => phase.Phase));
        Assert.Equal(
            ["NEXT", "LATER"],
            game.UpcomingForDisplayAt(now, 2).Select(phase => phase.Phase));
    }

    [Fact]
    public void Bundled_generated_snapshot_round_trips_through_the_desktop_parser()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Site", "src", "data", "generated", "launcher-banners-v1.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var payload = File.ReadAllBytes(Path.Combine(directory!.FullName, "Site", "src", "data", "generated", "launcher-banners-v1.json"));
        using var document = JsonDocument.Parse(payload);
        var generatedAt = document.RootElement.GetProperty("generatedAt").GetDateTimeOffset();
        var remote = LauncherBannersManifestParser.Parse(payload, fallback: false, generatedAt);
        var fallback = LauncherBannersManifestParser.Parse(payload, fallback: true, DateTimeOffset.UtcNow);
        Assert.Equal(remote.Revision, fallback.Revision);
        Assert.Equal(remote.Revision, LauncherBannersCache.ComputeSemanticRevision(payload));
        Assert.Equal(new[] { "gi", "hsr", "zzz", "wuwa", "ae" }, remote.Games.Keys);
        var endfield = remote.Games["ae"];
        Assert.Equal(["Liino", "Arcane", "Camille"], endfield.Current!.Characters.Select(character => character.Name));
        Assert.Equal("Liino", endfield.Current.Characters.Single(character => character.Id == endfield.Current.SelectedCharacterId).Name);
        var announced = Assert.Single(endfield.Upcoming);
        Assert.True(announced.Announced);
        Assert.Equal(["Si", "Hongshan Imperial Guard"], announced.Characters.Select(character => character.Name));
    }

    [Fact]
    public async Task Every_available_selected_current_character_has_resolvable_bundled_art()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Site", "src", "data", "generated", "launcher-banners-v1.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var generated = Path.Combine(directory!.FullName, "Site", "src", "data", "generated");
        var cache = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var service = new LauncherBannersContentService(
                File.ReadAllBytes(Path.Combine(generated, "launcher-banners-v1.json")),
                cache,
                bundledAssetsDirectory: Path.Combine(generated, "launcher-art"));
            var currentGames = service.Current.Games.Where(pair => pair.Value.Current is not null).ToArray();
            Assert.NotEmpty(currentGames);
            foreach (var pair in currentGames)
            {
                var game = pair.Value;
                if (game.Current is not LauncherBannersCurrentPhase current) continue;
                var selected = Assert.Single(current.Characters, character => character.Id == current.SelectedCharacterId);
                var usableArt = selected.Variants.Count > 0 ? selected.Variants : current.Variants;
                Assert.NotEmpty(usableArt);
                Assert.All(usableArt, asset =>
                {
                    Assert.True(Math.Max(asset.Dimensions.Width, asset.Dimensions.Height) >= 800, $"{pair.Key} selected a thumbnail instead of launcher artwork.");
                    Assert.True(asset.Placement.X > 0.5, $"{pair.Key} artwork must stay on the right side of the launcher copy.");
                    Assert.NotNull(service.TryResolveManagedAsset(asset));
                });
            }
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task Every_banner_character_icon_resolves_from_the_bundle_or_its_validated_pengo_source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Site", "src", "data", "generated", "launcher-banners-v1.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var generated = Path.Combine(directory!.FullName, "Site", "src", "data", "generated");
        var cache = Path.Combine(Path.GetTempPath(), "nyx-launcher-icon-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var payload = File.ReadAllBytes(Path.Combine(generated, "launcher-banners-v1.json"));
            using var document = JsonDocument.Parse(payload);
            var manifest = LauncherBannersManifestParser.Parse(payload, fallback: false, document.RootElement.GetProperty("generatedAt").GetDateTimeOffset());
            var launcherCache = new LauncherBannersCache(cache);
            _ = await launcherCache.HydrateAssetsAsync(
                manifest,
                new LocalDatabaseAssetTransport(directory.FullName),
                Path.Combine(generated, "launcher-art"));
            var characters = manifest.Games.Values
                .SelectMany(game => (game.Current?.Characters ?? []).Concat(game.Upcoming.SelectMany(phase => phase.Characters)))
                .ToArray();
            Assert.NotEmpty(characters);
            Assert.All(characters, character =>
            {
                var icon = Assert.IsType<LauncherBannersAsset>(character.Icon);
                Assert.Equal("character-icon", icon.Source);
                Assert.DoesNotContain(character.Variants, variant => variant.Sha256 == icon.Sha256);
                Assert.True(
                    icon.Url!.AbsoluteUri.StartsWith("https://assets.pengo.gg/legacy/Database/", StringComparison.Ordinal)
                    || icon.Url.AbsoluteUri.StartsWith("https://pengo.gg/dist/launcher-art/", StringComparison.Ordinal));
                Assert.True(
                    launcherCache.TryResolveBundledAsset(icon, Path.Combine(generated, "launcher-art")) is not null
                    || launcherCache.TryResolveManagedAsset(icon) is not null);
            });
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Fact]
    public void Parser_rejects_bad_asset_path_hash_dimensions_and_mime()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");

        AssertRejected(asset => asset["path"] = "/launcher-art/../escape.webp");
        AssertRejected(asset => asset["sha256"] = new string('g', 64));
        AssertRejected(asset => asset["dimensions"]!["width"] = 0);
        AssertRejected(asset => asset["mime"] = "image/gif");

        void AssertRejected(Action<JsonObject> mutate)
        {
            var root = JsonNode.Parse(Encoding.UTF8.GetString(ManifestWithAssetJson(generatedAt)))!.AsObject();
            var asset = root["games"]!["gi"]!["current"]!["variants"]!.AsArray()[0]!.AsObject();
            mutate(asset);
            Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
                JsonSerializer.SerializeToUtf8Bytes(root),
                fallback: true,
                generatedAt));
        }
    }

    [Fact]
    public async Task Cache_promotes_validated_remote_art_with_hash_and_preserves_user_art()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var art = WebpFixture(1);
            var hash = Convert.ToHexString(SHA256.HashData(art)).ToLowerInvariant();
            var manifest = ManifestModel(new LauncherBannersAsset("asset", "test", "/assets/test.webp", new Uri("https://pengo.gg/assets/test.webp"), "image/webp", art.Length, new(1, 1), hash, new(0, 0, 1, 1), new("center", "contain", .5, .5)));
            var cache = new LauncherBannersCache(root);
            var transport = new FakeTransport(art);
            var payload = ManifestJson(null);
            await cache.PromoteAsync(manifest, payload, transport);
            Assert.True(File.Exists(Path.Combine(cache.ManagedAssetsDirectory, hash + ".webp")));
            Assert.NotNull(cache.TryLoadLastKnownGood(DateTimeOffset.UtcNow));
            Directory.CreateDirectory(cache.UserArtDirectory);
            var user = Path.Combine(cache.UserArtDirectory, "keep.webp");
            await File.WriteAllTextAsync(user, "user");
            cache.PruneManagedCache(1);
            Assert.True(File.Exists(user));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cache_rejects_corrupt_asset_bytes_before_promotion()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var expected = WebpFixture(1);
            var bad = WebpFixture(2);
            var hash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
            var manifest = ManifestModel(new LauncherBannersAsset("asset", "test", "/assets/test.webp", new Uri("https://pengo.gg/assets/test.webp"), "image/webp", expected.Length, new(1, 1), hash, new(0, 0, 1, 1), new("center", "contain", .5, .5)));
            var cache = new LauncherBannersCache(root);
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PromoteAsync(manifest, ManifestJson(null), new FakeTransport(bad)));
            Assert.False(File.Exists(cache.LastKnownGoodManifestPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Generated_codes_snapshot_revision_matches_its_exact_content()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Site", "src", "data", "generated", "launcher-codes-v1.json"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var payload = File.ReadAllBytes(Path.Combine(directory!.FullName, "Site", "src", "data", "generated", "launcher-codes-v1.json"));
        var manifest = LauncherBannersManifestParser.ParseCodes(payload, fallback: true, DateTimeOffset.UtcNow);
        Assert.Equal(manifest.Revision, LauncherBannersCache.ComputeCodesRevision(payload));
    }

    [Fact]
    public void Managed_cache_guard_rejects_escape_and_reparse_entries_without_touching_external_files()
    {
        var parent = Path.Combine(Path.GetTempPath(), "nyx-launcher-reparse-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "cache");
        var external = Path.Combine(parent, "external");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var cache = new LauncherBannersCache(root);
        try
        {
            Assert.False(cache.IsSafeOwnedCachePath(Path.Combine(parent, "outside.txt"), mustExist: false));
            Assert.False(cache.IsSafeOwnedCachePath(Path.Combine(root, "..", "outside.txt"), mustExist: false));

            Directory.Delete(root);
            try { Directory.CreateSymbolicLink(root, external); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
            Assert.False(cache.IsSafeOwnedCachePath(root, mustExist: true));
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Directory.Delete(root);
            Directory.CreateDirectory(root);

            try { Directory.CreateSymbolicLink(cache.ManagedDirectory, external); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
            Assert.False(cache.IsSafeOwnedCachePath(cache.ManagedDirectory, mustExist: true));
            Assert.Throws<InvalidDataException>(() => cache.PruneManagedCache());
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Directory.Delete(cache.ManagedDirectory);

            Directory.CreateDirectory(cache.ManagedAssetsDirectory);
            var staging = Path.Combine(cache.ManagedDirectory, ".test.staging");
            try { Directory.CreateSymbolicLink(staging, external); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
            Assert.Throws<InvalidDataException>(() => cache.PruneManagedCache());
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Directory.Delete(staging);

            var bytes = WebpFixture(50);
            var asset = RemoteAsset("linked", bytes);
            var externalAsset = Path.Combine(external, "asset.webp");
            File.WriteAllBytes(externalAsset, bytes);
            var link = Path.Combine(cache.ManagedAssetsDirectory, asset.Sha256 + ".webp");
            try { File.CreateSymbolicLink(link, externalAsset); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
            Assert.Null(cache.TryResolveManagedAsset(asset));
            Assert.Throws<InvalidDataException>(() => cache.PruneManagedCache(activeManifest: ManifestModel(asset)));
            Assert.Equal(bytes, File.ReadAllBytes(externalAsset));
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public void Parser_rejects_every_incomplete_current_phase_shape()
    {
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var valid = JsonNode.Parse(ManifestWithGiPhasesJson(
            generatedAt,
            generatedAt.AddHours(-1),
            generatedAt.AddHours(1)))!.AsObject();

        AssertInvalid(current => current["characters"] = new JsonArray());
        AssertInvalid(current => current["variants"] = new JsonArray());
        AssertInvalid(current =>
        {
            current["selectedCharacterId"] = "not-in-roster";
            current["selectedCharacter"]!["id"] = "not-in-roster";
        });
        AssertInvalid(current =>
        {
            current["characters"]![0]!["icon"] = null;
            current["selectedCharacter"]!["icon"] = null;
        });
        AssertInvalid(current => current["variants"]![0]!["url"] = $"https://pengo.gg/dist/launcher-art/{new string('a', 64)}.webp?x=1");
        AssertInvalid(current => current["selectedCharacter"]!["name"] = "Different identity");

        void AssertInvalid(Action<JsonObject> mutate)
        {
            var root = valid.DeepClone().AsObject();
            mutate(root["games"]!["gi"]!["current"]!.AsObject());
            Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(
                JsonSerializer.SerializeToUtf8Bytes(root), true, generatedAt));
        }
    }

    [Fact]
    public async Task Incomplete_remote_current_never_promotes_or_replaces_the_bundled_snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-current-trust-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var bundled = ManifestIdentityJson(now.AddHours(-2), 'a');
        var remote = JsonNode.Parse(ManifestWithGiPhasesJson(now.AddMinutes(-10), now.AddHours(-1), now.AddHours(1)))!.AsObject();
        remote["games"]!["gi"]!["current"]!["variants"] = new JsonArray();
        var incomplete = WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(remote));
        try
        {
            await using var service = new LauncherBannersContentService(
                bundled, root, new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                new FakeTransport(incomplete), () => now, TimeSpan.FromMinutes(15));
            await service.RefreshAsync();
            Assert.Equal(LauncherBannersManifestParser.Parse(bundled, true, now).Revision, service.Current.Revision);
            Assert.False(File.Exists(new LauncherBannersCache(root).LastKnownGoodManifestPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Hydration_is_atomic_and_prunes_only_after_every_asset_is_valid()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-hydrate-" + Guid.NewGuid().ToString("N"));
        var firstBytes = WebpFixture(41);
        var secondBytes = WebpFixture(42);
        var first = RemoteAsset("first", firstBytes);
        var second = RemoteAsset("second", secondBytes);
        try
        {
            var cache = new LauncherBannersCache(root);
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.HydrateAssetsAsync(
                ManifestModel(first, second),
                new QueueAssetTransport(firstBytes, WebpFixture(99))));
            Assert.Null(cache.TryResolveManagedAsset(first));
            Assert.Null(cache.TryResolveManagedAsset(second));

            Directory.CreateDirectory(cache.ManagedAssetsDirectory);
            var stale = Path.Combine(cache.ManagedAssetsDirectory, new string('f', 64) + ".webp");
            File.WriteAllBytes(stale, WebpFixture(40));
            Assert.True(await cache.HydrateAssetsAsync(ManifestModel(first, second), new QueueAssetTransport(firstBytes, secondBytes)));
            Assert.NotNull(cache.TryResolveManagedAsset(first));
            Assert.NotNull(cache.TryResolveManagedAsset(second));
            Assert.False(File.Exists(stale));
            Assert.False(await cache.HydrateAssetsAsync(ManifestModel(first, second), new QueueAssetTransport()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Hydration_honors_cancellation_without_installing_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-hydrate-" + Guid.NewGuid().ToString("N"));
        var bytes = WebpFixture(43);
        var asset = RemoteAsset("cancel", bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var cache = new LauncherBannersCache(root);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.HydrateAssetsAsync(
                ManifestModel(asset), new QueueAssetTransport(bytes), cancellationToken: cancellation.Token));
            Assert.Null(cache.TryResolveManagedAsset(asset));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cache_keeps_old_art_until_verified_replacement_commits()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        var generatedAt = DateTimeOffset.UtcNow;
        var oldArt = WebpFixture(31);
        var replacementArt = WebpFixture(32);
        var oldPayload = ManifestWithAssetJson(generatedAt);
        var replacementRoot = JsonNode.Parse(oldPayload)!.AsObject();
        replacementRoot["revision"] = new string('b', 64);
        var replacementNode = replacementRoot["games"]!["gi"]!["current"]!["variants"]!.AsArray()[0]!.AsObject();
        var replacementHash = Convert.ToHexString(SHA256.HashData(replacementArt)).ToLowerInvariant();
        replacementNode["path"] = $"/launcher-art/{replacementHash}.webp";
        replacementNode["url"] = $"https://pengo.gg/dist/launcher-art/{replacementHash}.webp";
        replacementNode["sha256"] = replacementHash;
        replacementRoot["games"]!["gi"]!["current"]!["characters"]![0]!["icon"] = replacementNode.DeepClone();
        replacementRoot["games"]!["gi"]!["current"]!["selectedCharacter"]!["icon"] = replacementNode.DeepClone();
        var replacementPayload = WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(replacementRoot));
        var oldManifest = LauncherBannersManifestParser.Parse(oldPayload, fallback: false, generatedAt);
        var replacementManifest = LauncherBannersManifestParser.Parse(replacementPayload, fallback: false, generatedAt);
        var oldAsset = Assert.Single(oldManifest.Games["gi"].Current!.Variants);
        var oldPath = Path.Combine(root, "managed", "assets", oldAsset.Sha256 + ".webp");
        var replacementPath = Path.Combine(root, "managed", "assets", replacementHash + ".webp");

        try
        {
            var cache = new LauncherBannersCache(root);
            await cache.PromoteAsync(oldManifest, oldPayload, new FakeTransport(oldArt));

            await Assert.ThrowsAsync<InvalidDataException>(() => cache.PromoteAsync(
                replacementManifest,
                replacementPayload,
                new FakeTransport(WebpFixture(33))));

            Assert.True(File.Exists(oldPath));
            Assert.False(File.Exists(replacementPath));
            Assert.Equal(oldPayload, File.ReadAllBytes(cache.LastKnownGoodManifestPath));
            Assert.Equal(oldManifest.Revision, cache.TryLoadLastKnownGood(generatedAt)!.Revision);

            await cache.PromoteAsync(replacementManifest, replacementPayload, new FakeTransport(replacementArt));

            Assert.False(File.Exists(oldPath));
            Assert.Equal(replacementArt, File.ReadAllBytes(replacementPath));
            Assert.Equal(replacementPayload, File.ReadAllBytes(cache.LastKnownGoodManifestPath));
            Assert.Equal(replacementManifest.Revision, cache.TryLoadLastKnownGood(generatedAt)!.Revision);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_multi_asset_promotion_removes_all_staged_downloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstBytes = WebpFixture(21);
            var secondBytes = WebpFixture(22);
            var first = RemoteAsset("first", firstBytes);
            var second = RemoteAsset("second", secondBytes);
            var cache = new LauncherBannersCache(root);
            var transport = new QueueAssetTransport(firstBytes, WebpFixture(23));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.PromoteAsync(ManifestModel(first, second), ManifestJson(null), transport));

            Assert.False(File.Exists(cache.LastKnownGoodManifestPath));
            Assert.Empty(Directory.EnumerateFiles(cache.ManagedAssetsDirectory, "*", SearchOption.AllDirectories));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(cache.ManagedDirectory, "*", SearchOption.TopDirectoryOnly),
                directory => directory.EndsWith(".staging", StringComparison.Ordinal));
            Assert.Equal(2, transport.AssetRequests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Promotion_rejects_an_asset_set_above_the_cache_cap_before_downloading()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = Enumerable.Range(1, 19)
                .Select(index => new LauncherBannersAsset(
                    $"asset-{index}",
                    "test",
                    $"/assets/{index}.webp",
                    new Uri($"https://pengo.gg/assets/{index}.webp"),
                    "image/webp",
                    LauncherBannersTransport.MaximumAssetBytes,
                    new(1, 1),
                    index.ToString("x64"),
                    new(0, 0, 1, 1),
                    new("center", "contain", .5, .5)))
                .ToArray();
            var cache = new LauncherBannersCache(root);
            var transport = new QueueAssetTransport();

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                cache.PromoteAsync(ManifestModel(assets), ManifestJson(null), transport));

            Assert.Equal(0, transport.AssetRequests);
            Assert.True(Directory.EnumerateFiles(cache.ManagedDirectory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length) <= LauncherBannersCache.MaximumManagedBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Cache_prune_is_deterministic_and_removes_interrupted_temp_files_only_from_managed_area()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        var cache = new LauncherBannersCache(root);
        Directory.CreateDirectory(cache.ManagedAssetsDirectory);
        Directory.CreateDirectory(cache.UserArtDirectory);
        File.WriteAllBytes(Path.Combine(cache.ManagedDirectory, ".interrupted.tmp"), new byte[40]);
        var staging = Path.Combine(cache.ManagedDirectory, ".interrupted.staging");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "partial.webp"), new byte[40]);
        File.WriteAllBytes(Path.Combine(cache.ManagedAssetsDirectory, "a.webp"), new byte[80]);
        File.WriteAllBytes(Path.Combine(cache.ManagedAssetsDirectory, "b.webp"), new byte[80]);
        var user = Path.Combine(cache.UserArtDirectory, "owned.webp");
        File.WriteAllBytes(user, new byte[120]);
        var removed = cache.PruneManagedCache(80);
        Assert.True(removed >= 1);
        Assert.False(File.Exists(Path.Combine(cache.ManagedDirectory, ".interrupted.tmp")));
        Assert.False(Directory.Exists(staging));
        Assert.True(File.Exists(user));
        Directory.Delete(root, true);
    }

    [Fact]
    public void Bundled_asset_is_resolved_and_validated_before_managed_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "launcher-art");
        Directory.CreateDirectory(bundled);
        try
        {
            var bytes = WebpFixture(3);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var asset = new LauncherBannersAsset("asset", "test", $"/launcher-art/{hash}.webp", null, "image/webp", bytes.Length, new(1, 1), hash, new(0, 0, 1, 1), new("center", "contain", .5, .5));
            var file = Path.Combine(bundled, hash + ".webp");
            File.WriteAllBytes(file, bytes);
            var cache = new LauncherBannersCache(root);
            Assert.Equal(Path.GetFullPath(file), cache.TryResolveBundledAsset(asset, bundled));
            File.WriteAllBytes(file, WebpFixture(4));
            Assert.Null(cache.TryResolveBundledAsset(asset, bundled));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_keeps_bundled_snapshot_when_transport_is_offline()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundled = LauncherBannersManifestParser.Parse(ManifestJson(null), true, DateTimeOffset.UtcNow);
            await using var service = new LauncherBannersContentService(
                ManifestJson(null),
                root,
                new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                new FakeTransport(new HttpRequestException("offline")),
                () => DateTimeOffset.Parse("2026-07-17T00:00:00Z"),
                TimeSpan.FromMinutes(15));
            Assert.Null(service.LastRefreshDuration);
            await service.RefreshAsync();
            Assert.NotNull(service.LastRefreshDuration);
            Assert.Equal(bundled.Revision, service.Current.Revision);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_prefers_a_newer_bundled_snapshot_over_an_older_valid_cache()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var olderPayload = ManifestIdentityJson(now.AddHours(-2), 'a');
        var newerPayload = ManifestIdentityJson(now.AddHours(-1), 'b');
        try
        {
            var cache = new LauncherBannersCache(root);
            var older = LauncherBannersManifestParser.Parse(olderPayload, fallback: false, now);
            await cache.PromoteAsync(older, olderPayload, new FakeTransport([]));

            await using var service = new LauncherBannersContentService(
                newerPayload,
                root,
                new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                new FakeTransport(new HttpRequestException("offline")),
                () => now,
                TimeSpan.FromMinutes(15));

            Assert.Equal(LauncherBannersManifestParser.Parse(newerPayload, true, now).Revision, service.Current.Revision);
            Assert.Equal(now.AddHours(-1), service.Current.GeneratedAt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_keeps_last_known_good_when_remote_health_is_degraded()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundled = ManifestJson(null);
            var remote = Encoding.UTF8.GetBytes(ReplaceFirst(
                Encoding.UTF8.GetString(bundled)
                    .Replace(new string('a', 64), new string('b', 64), StringComparison.Ordinal),
                "\"status\":\"ok\"",
                "\"status\":\"degraded\""));
            await using var service = new LauncherBannersContentService(
                bundled,
                root,
                new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                new FakeTransport(remote),
                () => DateTimeOffset.Parse("2026-07-17T00:01:00Z"),
                TimeSpan.FromMinutes(15));

            await service.RefreshAsync();

            Assert.Equal(LauncherBannersManifestParser.Parse(bundled, true, DateTimeOffset.Parse("2026-07-17T00:01:00Z")).Revision, service.Current.Revision);
            Assert.False(File.Exists(Path.Combine(root, "last-known-good", "launcher-banners-v1.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_persists_newer_codes_rejects_replay_and_restores_them_without_changing_banner_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var bannerPayload = ManifestJson(null);
        var newerCodes = CodesJson(now.AddMinutes(-10), "NEWCODE", 'c');
        var olderCodes = CodesJson(now.AddMinutes(-20), "OLDCODE", 'b');
        var bannerEndpoint = new Uri("http://127.0.0.1:32123/launcher-banners-v1.json");
        var codesEndpoint = new Uri("http://127.0.0.1:32123/launcher-codes-v1.json");
        try
        {
            await using (var service = new LauncherBannersContentService(
                bannerPayload,
                root,
                bannerEndpoint,
                new RoutedManifestTransport(bannerPayload, newerCodes, newerCodes, olderCodes),
                () => now,
                TimeSpan.FromMinutes(15),
                codesEndpoint: codesEndpoint))
            {
                var bannerRevision = service.Current.Revision;
                var bannerGeneratedAt = service.Current.GeneratedAt;

                await service.RefreshAsync();
                Assert.Equal("NEWCODE", Assert.Single(service.Current.Games["gi"].Codes).Code);
                Assert.Equal(bannerRevision, service.Current.Revision);
                Assert.Equal(bannerGeneratedAt, service.Current.GeneratedAt);
                var cache = new LauncherBannersCache(root);
                Assert.Equal(newerCodes, File.ReadAllBytes(cache.LastKnownGoodCodesPath));

                await service.RefreshAsync();
                Assert.Equal("NEWCODE", Assert.Single(service.Current.Games["gi"].Codes).Code);
                Assert.Equal(newerCodes, File.ReadAllBytes(cache.LastKnownGoodCodesPath));
                Assert.Equal(bannerRevision, service.Current.Revision);
                Assert.Equal(bannerGeneratedAt, service.Current.GeneratedAt);

                await service.RefreshAsync();
                Assert.Equal("NEWCODE", Assert.Single(service.Current.Games["gi"].Codes).Code);
                Assert.Equal(newerCodes, File.ReadAllBytes(cache.LastKnownGoodCodesPath));
                Assert.Equal(bannerRevision, service.Current.Revision);
                Assert.Equal(bannerGeneratedAt, service.Current.GeneratedAt);
            }

            await using var restarted = new LauncherBannersContentService(
                bannerPayload,
                root,
                bannerEndpoint,
                new FakeTransport(new HttpRequestException("offline")),
                () => now,
                TimeSpan.FromMinutes(15),
                codesEndpoint: codesEndpoint);
            Assert.Equal("NEWCODE", Assert.Single(restarted.Current.Games["gi"].Codes).Code);
            Assert.Equal(LauncherBannersManifestParser.Parse(bannerPayload, true, now).Revision, restarted.Current.Revision);
            Assert.Equal(DateTimeOffset.Parse("2026-07-17T00:00:00Z"), restarted.Current.GeneratedAt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Future_or_tampered_codes_cache_is_ignored_and_a_healthy_feed_recovers(bool tamper)
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-codes-integrity-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var cached = CodesJson(tamper ? now.AddHours(-1) : now.AddHours(1), "CACHED", 'a');
        if (tamper)
        {
            var node = JsonNode.Parse(cached)!.AsObject();
            node["games"]!["gi"]![0]!["code"] = "EDITED";
            cached = JsonSerializer.SerializeToUtf8Bytes(node);
        }
        var healthy = CodesJson(now.AddMinutes(-10), "HEALTHY", 'b');
        try
        {
            var cache = new LauncherBannersCache(root);
            Directory.CreateDirectory(cache.LastKnownGoodDirectory);
            File.WriteAllBytes(cache.LastKnownGoodCodesPath, cached);
            Assert.Null(cache.TryLoadLastKnownGoodCodes(now));

            await using var service = CodesService(root, now, new CodesOnlyTransport(healthy));
            Assert.Empty(service.Current.Games["gi"].Codes);
            Assert.True(await service.RefreshCodesManualAsync());
            Assert.Equal("HEALTHY", Assert.Single(service.Current.Games["gi"].Codes).Code);
            Assert.Equal("HEALTHY", Assert.Single(cache.TryLoadLastKnownGoodCodes(now)!.Games["gi"]).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Authenticated_old_banner_and_codes_caches_remain_available_offline()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-old-cache-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-08-17T02:00:00Z");
        try
        {
            var cache = new LauncherBannersCache(root);
            Directory.CreateDirectory(cache.LastKnownGoodDirectory);
            File.WriteAllBytes(cache.LastKnownGoodManifestPath, ManifestIdentityJson(now.AddDays(-30), 'a'));
            File.WriteAllBytes(cache.LastKnownGoodCodesPath, CodesJson(now.AddDays(-30), "OFFLINE", 'a'));
            Assert.NotNull(cache.TryLoadLastKnownGood(now));
            Assert.NotNull(cache.TryLoadLastKnownGoodCodes(now));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Future_or_semantically_tampered_banner_cache_cannot_pin_startup_or_recovery(bool tamper)
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-integrity-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var bundled = ManifestIdentityJson(now.AddHours(-2), 'a');
        var cached = ManifestIdentityJson(tamper ? now.AddHours(-1) : now.AddHours(1), 'b');
        if (tamper)
        {
            var node = JsonNode.Parse(cached)!.AsObject();
            node["games"]!["gi"]!["region"] = "europe";
            cached = JsonSerializer.SerializeToUtf8Bytes(node);
        }
        var healthy = ManifestIdentityJson(now.AddMinutes(-10), 'c');
        try
        {
            var cache = new LauncherBannersCache(root);
            Directory.CreateDirectory(cache.LastKnownGoodDirectory);
            File.WriteAllBytes(cache.LastKnownGoodManifestPath, cached);
            Assert.Null(cache.TryLoadLastKnownGood(now));

            await using var service = new LauncherBannersContentService(
                bundled,
                root,
                new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                new FakeTransport(healthy),
                () => now,
                TimeSpan.FromMinutes(15));
            Assert.Equal(LauncherBannersManifestParser.Parse(bundled, true, now).Revision, service.Current.Revision);
            Assert.Null(service.LastRefreshDuration);
            await service.RefreshAsync();
            Assert.NotNull(service.LastRefreshDuration);
            Assert.Equal(LauncherBannersManifestParser.Parse(healthy, true, now).Revision, service.Current.Revision);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Cached_manifest_requires_every_referenced_asset_before_startup_accepts_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-complete-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var bundled = ManifestIdentityJson(now.AddHours(-2), 'a');
        var firstBytes = WebpFixture(31);
        var secondBytes = WebpFixture(32);
        var node = JsonNode.Parse(ManifestWithAssetJson(now.AddMinutes(-30)))!.AsObject();
        var secondHash = Convert.ToHexString(SHA256.HashData(secondBytes)).ToLowerInvariant();
        var second = node["games"]!["gi"]!["current"]!["variants"]![0]!.DeepClone();
        second["id"] = "second";
        second["path"] = $"/launcher-art/{secondHash}.webp";
        second["url"] = $"https://pengo.gg/dist/launcher-art/{secondHash}.webp";
        second["size"] = secondBytes.Length;
        second["sha256"] = secondHash;
        node["games"]!["gi"]!["current"]!["variants"]!.AsArray().Add(second);
        var cached = WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(node));
        var parsed = LauncherBannersManifestParser.Parse(cached, true, now);
        var assets = parsed.Games["gi"].Current!.Variants;
        try
        {
            var cache = new LauncherBannersCache(root);
            Directory.CreateDirectory(cache.LastKnownGoodDirectory);
            Directory.CreateDirectory(cache.ManagedAssetsDirectory);
            File.WriteAllBytes(cache.LastKnownGoodManifestPath, cached);
            File.WriteAllBytes(Path.Combine(cache.ManagedAssetsDirectory, assets[0].Sha256 + ".webp"), firstBytes);
            Assert.Null(cache.TryLoadLastKnownGood(now));
            File.WriteAllBytes(Path.Combine(cache.ManagedAssetsDirectory, assets[1].Sha256 + ".webp"), WebpFixture(33));
            Assert.Null(cache.TryLoadLastKnownGood(now));

            await using (var fallback = new LauncherBannersContentService(
                bundled, root, new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                new FakeTransport(new HttpRequestException("offline")), () => now, TimeSpan.FromMinutes(15)))
            {
                Assert.Equal(LauncherBannersManifestParser.Parse(bundled, true, now).Revision, fallback.Current.Revision);
            }

            File.WriteAllBytes(Path.Combine(cache.ManagedAssetsDirectory, assets[1].Sha256 + ".webp"), secondBytes);
            Assert.NotNull(cache.TryLoadLastKnownGood(now));
            await using var healthy = new LauncherBannersContentService(
                bundled, root, new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                new FakeTransport(new HttpRequestException("offline")), () => now, TimeSpan.FromMinutes(15));
            Assert.Equal(parsed.Revision, healthy.Current.Revision);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_hydrates_selected_assets_before_an_older_or_failed_remote(bool remoteFails)
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-hydrate-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var bundled = ManifestWithAssetJson(now.AddMinutes(-30));
        var olderRemote = ManifestIdentityJson(now.AddHours(-2), 'b');
        var transport = new BannerAssetTransport(remoteFails ? new HttpRequestException("offline") : olderRemote, WebpFixture(31));
        try
        {
            await using var service = new LauncherBannersContentService(
                bundled,
                root,
                new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                transport,
                () => now,
                TimeSpan.FromMinutes(15));
            var updates = 0;
            service.Updated += (_, _) => updates++;
            var asset = Assert.Single(service.Current.Games["gi"].Current!.Variants);

            await service.RefreshAsync();
            Assert.NotNull(service.TryResolveManagedAsset(asset));
            Assert.Equal(LauncherBannersManifestParser.Parse(bundled, true, now).Revision, service.Current.Revision);
            Assert.Equal(1, updates);

            await service.RefreshAsync();
            Assert.Equal(1, updates);
            Assert.Equal(1, transport.AssetRequests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_stale_art_hydration_does_not_block_a_newer_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-stale-art-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var bundled = ManifestWithAssetJson(now.AddMinutes(-30));
        var remote = ManifestIdentityJson(now.AddMinutes(-10), 'b');
        var transport = new BannerAssetTransport(remote, WebpFixture(99));
        var bundledDirectory = Path.Combine(root, "bundled");
        try
        {
            Directory.CreateDirectory(bundledDirectory);
            await using var service = new LauncherBannersContentService(
                bundled,
                root,
                new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                transport,
                () => now,
                TimeSpan.FromMinutes(15),
                bundledAssetsDirectory: bundledDirectory);

            await service.RefreshAsync();

            Assert.Equal(LauncherBannersManifestParser.Parse(remote, true, now).Revision, service.Current.Revision);
            Assert.Equal(1, transport.AssetRequests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Bundled_upcoming_fills_older_empty_games_but_newer_valid_content_is_authoritative()
    {
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var bundledNames = new[] { "gi", "hsr", "zzz", "wuwa", "ae" }
            .ToDictionary(game => game, game => $"bundled-{game}", StringComparer.Ordinal);
        var bundled = UpcomingManifest(now.AddHours(-2), 'a', bundledNames, now.AddHours(1), now.AddHours(2));
        var emptyRemote = UpcomingManifest(now.AddHours(-3), 'b', new Dictionary<string, string>(), now.AddHours(1), now.AddHours(2));

        var merged = LauncherBannersContentService.ApplyBundledUpcomingFallback(emptyRemote, bundled);
        Assert.All(merged.Games, pair => Assert.Equal($"bundled-{pair.Key}", Assert.Single(Assert.Single(pair.Value.Upcoming).Characters).Name));
        Assert.Equal(
            merged.Games.Select(pair => Assert.Single(Assert.Single(pair.Value.Upcoming).Characters).Name),
            LauncherBannersContentService.ApplyBundledUpcomingFallback(emptyRemote, bundled).Games.Select(pair => Assert.Single(Assert.Single(pair.Value.Upcoming).Characters).Name));
        Assert.All(merged.ForDisplayAt(now.AddHours(2)).Games.Values, game => Assert.Empty(game.Upcoming));

        var remoteNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["hsr"] = "remote-hsr" };
        var authoritative = LauncherBannersContentService.ApplyBundledUpcomingFallback(
            UpcomingManifest(now.AddHours(-1), 'c', remoteNames, now.AddHours(1), now.AddHours(2)),
            bundled);
        Assert.Equal("remote-hsr", Assert.Single(Assert.Single(authoritative.Games["hsr"].Upcoming).Characters).Name);
        Assert.Empty(authoritative.Games["gi"].Upcoming);
    }

    [Fact]
    public void Newer_valid_empty_feed_retires_announced_rows_and_unpins_their_managed_art()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-retired-announced-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var bundled = UpcomingManifest(
            now.AddHours(-2),
            'a',
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ae"] = "announced-ae" },
            now.AddHours(1),
            now.AddHours(2),
            announced: true);
        var newer = UpcomingManifest(
            now.AddHours(-1),
            'b',
            new Dictionary<string, string>(),
            now.AddHours(1),
            now.AddHours(2));
        var retiredAsset = Assert.IsType<LauncherBannersAsset>(Assert.Single(Assert.Single(bundled.Games["ae"].Upcoming).Characters).Icon);
        var cache = new LauncherBannersCache(root);
        try
        {
            var merged = LauncherBannersContentService.ApplyBundledUpcomingFallback(newer, bundled);
            Assert.Empty(merged.Games["ae"].Upcoming);
            Assert.Empty(merged.ForDisplayAt(now).Games["ae"].Upcoming);

            Directory.CreateDirectory(cache.ManagedAssetsDirectory);
            File.WriteAllBytes(
                Path.Combine(cache.ManagedAssetsDirectory, retiredAsset.Sha256 + ".webp"),
                WebpFixture(50));
            Assert.NotNull(cache.TryResolveManagedAsset(retiredAsset));
            cache.PruneManagedCache(activeManifest: merged);
            Assert.Null(cache.TryResolveManagedAsset(retiredAsset));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("gi", "empty")]
    [InlineData("hsr", "empty")]
    [InlineData("zzz", "empty")]
    [InlineData("wuwa", "empty")]
    [InlineData("gi", "iconless")]
    [InlineData("hsr", "iconless")]
    [InlineData("zzz", "iconless")]
    [InlineData("wuwa", "iconless")]
    [InlineData("gi", "mixed")]
    [InlineData("hsr", "mixed")]
    [InlineData("zzz", "mixed")]
    [InlineData("wuwa", "mixed")]
    public async Task Incomplete_remote_upcoming_cannot_suppress_the_complete_bundled_fallback(string game, string defect)
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-incomplete-upcoming-" + Guid.NewGuid().ToString("N"));
        var bundledAssets = Path.Combine(root, "bundled");
        var now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
        var bundled = ManifestWithCompleteUpcomingJson(now.AddHours(-2), 'a', "bundled", now.AddHours(1), now.AddHours(2));
        var remoteRoot = JsonNode.Parse(ManifestWithCompleteUpcomingJson(now.AddHours(-1), 'b', "remote", now.AddHours(1), now.AddHours(2)))!.AsObject();
        var characters = remoteRoot["games"]![game]!["upcoming"]![0]!["characters"]!.AsArray();
        if (defect == "empty") characters.Clear();
        else if (defect == "iconless") characters[0]!["icon"] = null;
        else characters.Add(TestCharacterJson($"{game}-invalid", includeIcon: false));
        var remote = JsonSerializer.SerializeToUtf8Bytes(remoteRoot);
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(remote, false, now));

        try
        {
            Directory.CreateDirectory(bundledAssets);
            var iconBytes = WebpFixture(50);
            var iconHash = Convert.ToHexString(SHA256.HashData(iconBytes)).ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(bundledAssets, iconHash + ".webp"), iconBytes);
            await using var service = new LauncherBannersContentService(
                bundled,
                root,
                new Uri("http://127.0.0.1:32123/launcher-banners-v1.json"),
                new FakeTransport(remote),
                () => now,
                TimeSpan.FromMinutes(15),
                bundledAssets);
            var updates = 0;
            service.Updated += (_, _) => updates++;

            await service.RefreshAsync();

            foreach (var supported in new[] { "gi", "hsr", "zzz", "wuwa" })
                Assert.StartsWith("bundled-", Assert.Single(Assert.Single(service.Current.Games[supported].Upcoming).Characters).Name, StringComparison.Ordinal);
            Assert.Empty(service.Current.Games["ae"].Upcoming);
            Assert.Equal(0, updates);
            Assert.False(File.Exists(new LauncherBannersCache(root).LastKnownGoodManifestPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Upcoming_model_requires_at_least_one_character_with_a_downloadable_icon()
    {
        var start = DateTimeOffset.Parse("2026-07-17T03:00:00Z");
        Assert.Throws<InvalidDataException>(() => new LauncherBannersUpcomingPhase("next", start, start.AddHours(1), []));
        Assert.Throws<InvalidDataException>(() => new LauncherBannersUpcomingPhase("next", start, start.AddHours(1), [new("a", "A", 5, true, null, [])]));
        var icon = RemoteAsset("icon", WebpFixture(50));
        _ = new LauncherBannersUpcomingPhase("next", start, start.AddHours(1), [new("a", "A", 5, true, null, [], icon)]);
    }

    [Fact]
    public async Task Service_manual_codes_refresh_promotes_codes_only_and_coalesces_requests()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-codes-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var payload = CodesJson(now.AddMinutes(-10), "MANUALCODE", 'b');
        var transport = new CodesOnlyTransport(payload);
        try
        {
            await using var service = CodesService(root, now, transport);
            var updates = 0;
            service.Updated += (_, _) => updates++;

            var first = service.RefreshCodesManualAsync();
            var second = service.RefreshCodesManualAsync();

            Assert.True(await first);
            Assert.True(await second);
            Assert.Equal(1, transport.CodesRequests);
            Assert.Equal(LauncherBannersTransport.ProductionCodesEndpoint, transport.CodesEndpoint?.AbsoluteUri);
            Assert.Equal(0, transport.BannerRequests);
            Assert.Equal("MANUALCODE", Assert.Single(service.Current.Games["gi"].Codes).Code);
            Assert.Equal(LauncherBannersManifestParser.Parse(ManifestJson(null), true, now).Revision, service.Current.Revision);
            Assert.Equal(1, updates);
            Assert.False(File.Exists(new LauncherBannersCache(root).LastKnownGoodManifestPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_manual_codes_refresh_keeps_last_good_on_malformed_or_network_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-codes-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var payload = CodesJson(now.AddMinutes(-10), "GOODCODE", 'b');
        var transport = new CodesOnlyTransport(payload, Encoding.UTF8.GetBytes("{"), new HttpRequestException("offline"));
        try
        {
            await using var service = CodesService(root, now, transport);
            var cache = new LauncherBannersCache(root);

            Assert.True(await service.RefreshCodesManualAsync());
            var lastGood = File.ReadAllBytes(cache.LastKnownGoodCodesPath);
            Assert.False(await service.RefreshCodesManualAsync());
            Assert.False(await service.RefreshCodesManualAsync());

            Assert.Equal(lastGood, File.ReadAllBytes(cache.LastKnownGoodCodesPath));
            Assert.Equal("GOODCODE", Assert.Single(service.Current.Games["gi"].Codes).Code);
            Assert.Equal(0, transport.BannerRequests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_manual_codes_refresh_reports_an_unchanged_current_feed_as_success_without_event()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-codes-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var payload = CodesJson(now.AddMinutes(-10), "CURRENTCODE", 'b');
        var transport = new CodesOnlyTransport(payload, payload);
        try
        {
            await using var service = CodesService(root, now, transport);
            var updates = 0;
            service.Updated += (_, _) => updates++;

            Assert.True(await service.RefreshCodesManualAsync());
            var cache = new LauncherBannersCache(root);
            var lastGood = File.ReadAllBytes(cache.LastKnownGoodCodesPath);
            Assert.True(await service.RefreshCodesManualAsync());

            Assert.Equal(lastGood, File.ReadAllBytes(cache.LastKnownGoodCodesPath));
            Assert.Equal(1, updates);
            Assert.Equal(2, transport.CodesRequests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_manual_codes_refresh_rejects_equal_time_with_a_different_revision()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-codes-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var firstPayload = CodesJson(now.AddMinutes(-10), "FIRSTCODE", 'b');
        var replayPayload = CodesJson(now.AddMinutes(-10), "REPLAYCODE", 'c');
        var transport = new CodesOnlyTransport(firstPayload, replayPayload);
        try
        {
            await using var service = CodesService(root, now, transport);
            var cache = new LauncherBannersCache(root);

            Assert.True(await service.RefreshCodesManualAsync());
            var lastGood = File.ReadAllBytes(cache.LastKnownGoodCodesPath);
            Assert.False(await service.RefreshCodesManualAsync());

            Assert.Equal(lastGood, File.ReadAllBytes(cache.LastKnownGoodCodesPath));
            Assert.Equal("FIRSTCODE", Assert.Single(service.Current.Games["gi"].Codes).Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Service_manual_codes_refresh_propagates_caller_cancellation_without_leaking_details()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-codes-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.Parse("2026-07-17T01:00:00Z");
        var transport = new BlockingCodesTransport();
        try
        {
            await using var service = CodesService(root, now, transport);
            using var cancellation = new CancellationTokenSource();
            var request = service.RefreshCodesManualAsync(cancellation.Token);
            await transport.Started.Task;
            cancellation.Cancel();

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.DoesNotContain(LauncherBannersTransport.ProductionCodesEndpoint, exception.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, transport.BannerRequests);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Service_records_whole_refresh_duration_when_shutdown_cancels_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-duration-" + Guid.NewGuid().ToString("N"));
        var transport = new BlockingCodesTransport();
        try
        {
            await using var service = CodesService(
                root,
                DateTimeOffset.Parse("2026-07-17T01:00:00Z"),
                transport);
            var refresh = service.RefreshAsync();
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await service.DisposeAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            Assert.NotNull(service.LastRefreshDuration);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Service_never_returns_a_current_phase_after_it_expires()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-launcher-cache-" + Guid.NewGuid().ToString("N"));
        var generatedAt = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var start = generatedAt.AddHours(-1);
        var end = generatedAt.AddHours(1);
        var now = generatedAt;
        try
        {
            await using var service = new LauncherBannersContentService(
                ManifestWithGiPhasesJson(generatedAt, start, end),
                root,
                clock: () => now);

            Assert.NotNull(service.Current.Games["gi"].Current);
            now = end;
            Assert.Null(service.Current.Games["gi"].Current);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Service_refreshes_shortly_after_the_next_current_banner_expires()
    {
        var now = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var manifest = ManifestModel(new LauncherBannersAsset(
            "asset", "test", $"/launcher-art/{new string('a', 64)}.webp", new Uri($"https://pengo.gg/dist/launcher-art/{new string('a', 64)}.webp"), "image/webp", 30,
            new(1, 1), new string('a', 64), new(0, 0, 1, 1), new("center", "contain", .5, .5)));

        Assert.Equal(
            TimeSpan.FromHours(24) + TimeSpan.FromSeconds(30),
            LauncherBannersContentService.CalculateNextRefreshDelay(manifest, now, TimeSpan.FromDays(2)));
        Assert.Equal(
            TimeSpan.FromHours(6),
            LauncherBannersContentService.CalculateNextRefreshDelay(manifest, now, TimeSpan.FromHours(6)));
    }

    private static LauncherBannersManifest ManifestModel(params LauncherBannersAsset[] assets)
    {
        var games = new Dictionary<string, LauncherBannersGame>(StringComparer.Ordinal);
        foreach (var game in new[] { "gi", "hsr", "zzz", "wuwa", "ae" })
        {
            var current = game == "gi" ? new LauncherBannersCurrentPhase("1.0", DateTimeOffset.Parse("2026-07-16T00:00:00Z"), DateTimeOffset.Parse("2026-07-18T00:00:00Z"), 1, [new LauncherBannersCharacter("a", "Alpha", 5, true, null, [], assets[0])], "a", "highest-rarity", assets) : null;
            games[game] = new LauncherBannersGame(game, "global", current, []);
        }
        var health = new LauncherBannersHealth("ok", games.ToDictionary(pair => pair.Key, _ => new LauncherBannersGameHealth("ok", null, 0), StringComparer.Ordinal));
        return new LauncherBannersManifest(1, new string('a', 64), DateTimeOffset.Parse("2026-07-17T00:00:00Z"), health, games);
    }

    [Fact]
    public void Effective_current_boundary_and_upcoming_start_drive_visibility_and_refresh()
    {
        var now = DateTimeOffset.Parse("2026-07-17T00:00:00Z");
        var root = JsonNode.Parse(ManifestWithGiPhasesJson(now, now.AddHours(-1), now.AddHours(4)))!.AsObject();
        var current = root["games"]!["gi"]!["current"]!;
        current["nextChangeAt"] = now.AddHours(1).ToString("O");
        current["timingMode"] = "next-change";
        current["remaining"]!["endsAt"] = now.AddHours(1).ToString("O");
        current["remaining"]!["durationSeconds"] = 3600;
        var payload = WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(root));
        var manifest = LauncherBannersManifestParser.Parse(payload, true, now);

        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(30),
            LauncherBannersContentService.CalculateNextRefreshDelay(manifest, now, TimeSpan.FromHours(6)));
        Assert.Null(manifest.ForDisplayAt(now.AddHours(1)).Games["gi"].Current);
        Assert.Throws<InvalidDataException>(() => LauncherBannersManifestParser.Parse(payload, false, now.AddHours(1)));

        var upcomingPayload = ManifestWithGiPhasesJson(now, null, null, [(now.AddMinutes(20), now.AddHours(1))]);
        var upcomingManifest = LauncherBannersManifestParser.Parse(upcomingPayload, true, now);
        Assert.Equal(TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(30),
            LauncherBannersContentService.CalculateNextRefreshDelay(upcomingManifest, now, TimeSpan.FromHours(6)));
    }

    private static LauncherBannersManifest UpcomingManifest(
        DateTimeOffset generatedAt,
        char revision,
        IReadOnlyDictionary<string, string> names,
        DateTimeOffset start,
        DateTimeOffset end,
        bool announced = false)
    {
        var games = new Dictionary<string, LauncherBannersGame>(StringComparer.Ordinal);
        foreach (var game in new[] { "gi", "hsr", "zzz", "wuwa", "ae" })
        {
            var icon = RemoteAsset($"{game}-icon", WebpFixture(50));
            IReadOnlyList<LauncherBannersUpcomingPhase> upcoming = names.TryGetValue(game, out var name)
                ? [new("next", announced ? null : start, announced ? null : end, [new($"{game}-character", name, 5, true, null, [], icon)], announced)]
                : [];
            games[game] = new LauncherBannersGame(game, "global", null, [], upcoming);
        }
        var health = new LauncherBannersHealth("ok", games.ToDictionary(pair => pair.Key, _ => new LauncherBannersGameHealth("ok", null, 0), StringComparer.Ordinal));
        return new LauncherBannersManifest(1, new string(revision, 64), generatedAt, health, games);
    }

    private static readonly (string Game, string Id, string Label, string Url)[] OfficialToolRows =
    [
        ("gi", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/genshin/home"),
        ("gi", "material-calculator", "Material Calculator", "https://act.hoyolab.com/ys/event/calculator-sea/index.html"),
        ("gi", "battle-records", "Battle Records", "https://act.hoyolab.com/app/community-game-records-sea/index.html?gid=2#/ys"),
        ("gi", "upgrade-guide", "Upgrade Guide", "https://act.hoyolab.com/ys/event/bbs-lineup-ys-sea/index.html"),
        ("hsr", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/hsr/home"),
        ("hsr", "material-calculator", "Material Calculator", "https://act.hoyolab.com/sr/event/calculator/index.html"),
        ("hsr", "battle-records", "Battle Records", "https://act.hoyolab.com/app/community-game-records-sea/index.html?gid=6#/hsr"),
        ("hsr", "upgrade-guide", "Upgrade Guide", "https://act.hoyolab.com/sr/event/cultivation-tool/#/tools/suggestion"),
        ("zzz", "wiki", "Wiki", "https://wiki.hoyolab.com/pc/zzz/home"),
        ("zzz", "battle-records", "Battle Records", "https://act.hoyolab.com/app/zzz-game-record/index.html"),
        ("ae", "wiki", "Wiki", "https://wiki.skport.com/endfield"),
        ("ae", "material-calculator", "Material Calculator", "https://game.skport.com/tools/endfield/cost-calculator?header=0"),
        ("ae", "team-recommendations", "Team Recommendations", "https://game.skport.com/tools/endfield/rec-team"),
    ];

    private static byte[] ToolsJson(
        DateTimeOffset generatedAt,
        IEnumerable<(string Game, string Id, string Label, string Url)>? rows = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["schemaVersion"] = 1,
            ["generatedAt"] = generatedAt.ToString("O"),
            ["tools"] = new JsonArray((rows ?? OfficialToolRows)
                .Select(static row => (JsonNode)new JsonObject
                {
                    ["game"] = row.Game,
                    ["id"] = row.Id,
                    ["label"] = row.Label,
                    ["url"] = row.Url,
                })
                .ToArray()),
        });

    private static byte[] ManifestJson(string? url)
    {
        var newsUrl = url is null ? "null" : $"\"{url}\"";
        var games = string.Join(',', new[] { "gi", "hsr", "zzz", "wuwa", "ae" }.Select(game => $"\"{game}\":{{\"game\":\"{game}\",\"region\":\"global\",\"current\":null,\"collections\":[],\"news\":[{{\"id\":\"{game}-news\",\"title\":\"Official\",\"type\":\"event\",\"start\":null,\"end\":null,\"url\":{newsUrl}}}]}}"));
        var health = string.Join(',', new[] { "gi", "hsr", "zzz", "wuwa", "ae" }.Select(game => $"\"{game}\":{{\"status\":\"ok\",\"reason\":null,\"newsCount\":1}}"));
        return WithSemanticRevision(Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"revision\":\"{new string('a', 64)}\",\"generatedAt\":\"2026-07-17T00:00:00.000Z\",\"health\":{{\"status\":\"ok\",\"games\":{{{health}}}}},\"games\":{{{games}}}}}"));
    }

    private static byte[] WithSemanticRevision(byte[] payload)
    {
        var root = JsonNode.Parse(payload)!.AsObject();
        root["revision"] = LauncherBannersCache.ComputeSemanticRevision(payload);
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private static byte[] ManifestIdentityJson(DateTimeOffset generatedAt, char revision)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(ManifestJson(null)))!.AsObject();
        root["generatedAt"] = generatedAt.ToString("O");
        root["revision"] = new string(revision, 64);
        return WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static byte[] CodesJson(DateTimeOffset generatedAt, string code, char revision)
    {
        var games = string.Join(',', new[] { "gi", "hsr", "zzz", "wuwa", "ae" }
            .Select(game => $"\"{game}\":[{{\"code\":\"{code}\",\"added\":\"2026-07-17\",\"amount\":60,\"currency\":\"Premium\"}}]"));
        var payload = Encoding.UTF8.GetBytes($"{{\"schemaVersion\":1,\"revision\":\"{new string(revision, 64)}\",\"generatedAt\":\"{generatedAt:O}\",\"games\":{{{games}}}}}");
        var root = JsonNode.Parse(payload)!.AsObject();
        root["revision"] = LauncherBannersCache.ComputeCodesRevision(payload);
        return JsonSerializer.SerializeToUtf8Bytes(root);
    }

    private static LauncherBannersContentService CodesService(
        string root,
        DateTimeOffset now,
        ILauncherBannersTransport transport) =>
        new(
            ManifestJson(null),
            root,
            transport: transport,
            clock: () => now,
            interval: TimeSpan.FromMinutes(15));

    private static byte[] ManifestWithAssetJson(DateTimeOffset generatedAt)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(ManifestWithGiPhasesJson(
            generatedAt,
            generatedAt.AddHours(-1),
            generatedAt.AddHours(1))))!.AsObject();
        var bytes = WebpFixture(31);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var asset = new JsonObject
        {
            ["id"] = "asset",
            ["source"] = "test",
            ["path"] = $"/launcher-art/{hash}.webp",
            ["url"] = $"https://pengo.gg/dist/launcher-art/{hash}.webp",
            ["mime"] = "image/webp",
            ["size"] = bytes.Length,
            ["dimensions"] = new JsonObject { ["width"] = 1, ["height"] = 1 },
            ["sha256"] = hash,
            ["transparentBounds"] = new JsonObject { ["left"] = 0, ["top"] = 0, ["right"] = 1, ["bottom"] = 1 },
            ["placement"] = new JsonObject { ["anchor"] = "center", ["fit"] = "contain", ["x"] = .5, ["y"] = .5 },
        };
        root["games"]!["gi"]!["current"]!["variants"] = new JsonArray(asset.DeepClone());
        root["games"]!["gi"]!["current"]!["characters"]![0]!["icon"] = asset.DeepClone();
        root["games"]!["gi"]!["current"]!["selectedCharacter"]!["icon"] = asset.DeepClone();
        return WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static byte[] ManifestWithWindowJson(DateTimeOffset start, DateTimeOffset end)
    {
        return ManifestWithGiPhasesJson(start, start, end);
    }

    private static byte[] ManifestWithGiPhasesJson(
        DateTimeOffset generatedAt,
        DateTimeOffset? currentStart,
        DateTimeOffset? currentEnd,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? upcoming = null,
        int countdownAdjustment = 0)
    {
        var root = JsonNode.Parse(Encoding.UTF8.GetString(ManifestJson(null)))!.AsObject();
        root["generatedAt"] = generatedAt.ToString("O");
        var game = root["games"]!["gi"]!.AsObject();
        if (currentStart.HasValue != currentEnd.HasValue) throw new ArgumentException("Current phase bounds must be paired.");
        if (currentStart is { } start && currentEnd is { } end)
        {
            var duration = Math.Max(0, (long)Math.Floor((end - generatedAt).TotalSeconds)) + countdownAdjustment;
            var character = TestCharacterJson("current");
            game["current"] = new JsonObject
            {
                ["phase"] = "1.0",
                ["start"] = start.ToString("O"),
                ["end"] = end.ToString("O"),
                ["remaining"] = new JsonObject
                {
                    ["startsAt"] = start.ToString("O"),
                    ["endsAt"] = end.ToString("O"),
                    ["durationSeconds"] = duration,
                },
                ["characters"] = new JsonArray(character.DeepClone()),
                ["selectedCharacter"] = character.DeepClone(),
                ["selectedCharacterId"] = "current",
                ["selectionReason"] = "highest-rarity",
                ["variants"] = new JsonArray(character["icon"]!.DeepClone()),
            };
        }
        var upcomingIndex = 0;
        game["upcoming"] = new JsonArray((upcoming ?? [])
            .Select(window => (JsonNode)new JsonObject
            {
                ["phase"] = "next",
                ["start"] = window.Start.ToString("O"),
                ["end"] = window.End.ToString("O"),
                ["characters"] = new JsonArray(TestCharacterJson($"upcoming-{upcomingIndex++}")),
            })
            .ToArray());
        return WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static byte[] ManifestWithCompleteUpcomingJson(
        DateTimeOffset generatedAt,
        char revision,
        string prefix,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var root = JsonNode.Parse(ManifestJson(null))!.AsObject();
        root["generatedAt"] = generatedAt.ToString("O");
        root["revision"] = new string(revision, 64);
        foreach (var game in new[] { "gi", "hsr", "zzz", "wuwa" })
        {
            root["games"]![game]!["upcoming"] = new JsonArray(new JsonObject
            {
                ["phase"] = "next",
                ["start"] = start.ToString("O"),
                ["end"] = end.ToString("O"),
                ["characters"] = new JsonArray(TestCharacterJson($"{prefix}-{game}")),
            });
        }
        root["games"]!["ae"]!["upcoming"] = new JsonArray();
        return WithSemanticRevision(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static JsonObject TestCharacterJson(string id, bool includeIcon = true)
    {
        var bytes = WebpFixture(50);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = id,
            ["rarity"] = 5,
            ["limited"] = true,
            ["debut"] = null,
            ["characterUrl"] = null,
            ["icon"] = includeIcon ? new JsonObject
            {
                ["id"] = $"{id}-icon",
                ["source"] = "test",
                ["path"] = $"/launcher-art/{hash}.webp",
                ["url"] = $"https://pengo.gg/dist/launcher-art/{hash}.webp",
                ["mime"] = "image/webp",
                ["size"] = bytes.Length,
                ["dimensions"] = new JsonObject { ["width"] = 1, ["height"] = 1 },
                ["sha256"] = hash,
                ["transparentBounds"] = new JsonObject { ["left"] = 0, ["top"] = 0, ["right"] = 1, ["bottom"] = 1 },
                ["placement"] = new JsonObject { ["anchor"] = "center", ["fit"] = "contain", ["x"] = .5, ["y"] = .5 },
            } : null,
            ["variants"] = new JsonArray(),
        };
    }

    private static byte[] WebpFixture(byte marker)
    {
        var bytes = new byte[30];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("VP8X").CopyTo(bytes, 12);
        bytes[16] = 10;
        bytes[20] = marker;
        return bytes;
    }

    private static LauncherBannersAsset RemoteAsset(string id, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new LauncherBannersAsset(
            id,
            "test",
            $"/assets/{id}.webp",
            new Uri($"https://pengo.gg/assets/{id}.webp"),
            "image/webp",
            bytes.Length,
            new(1, 1),
            hash,
            new(0, 0, 1, 1),
            new("center", "contain", .5, .5));
    }

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? value : string.Concat(value.AsSpan(0, index), newValue, value.AsSpan(index + oldValue.Length));
    }

    private sealed class CodesOnlyTransport(params object[] responses) : ILauncherBannersTransport
    {
        private readonly Queue<object> responseQueue = new(responses);

        public int BannerRequests { get; private set; }
        public int CodesRequests { get; private set; }
        public Uri? CodesEndpoint { get; private set; }

        public Task<byte[]> GetManifestAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken)
        {
            if (endpoint.AbsoluteUri == LauncherBannersTransport.ProductionCodesEndpoint)
            {
                CodesRequests++;
                CodesEndpoint = endpoint;
                var response = responseQueue.Dequeue();
                return response switch
                {
                    byte[] bytes => Task.FromResult(bytes),
                    Exception exception => Task.FromException<byte[]>(exception),
                    _ => Task.FromException<byte[]>(new InvalidDataException("Unexpected test response.")),
                };
            }

            BannerRequests++;
            return Task.FromException<byte[]>(new InvalidOperationException("Banner endpoint was not requested."));
        }

        public Task<byte[]> GetAssetAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(new InvalidOperationException("Launcher art was not requested."));
    }

    private sealed class RoutedToolsTransport(
        IEnumerable<object>? banners = null,
        IEnumerable<object>? codes = null,
        IEnumerable<object>? tools = null) : ILauncherBannersTransport
    {
        private readonly Queue<object> bannerResponses = new(banners ?? []);
        private readonly Queue<object> codeResponses = new(codes ?? []);
        private readonly Queue<object> toolResponses = new(tools ?? []);

        public int BannerRequests { get; private set; }
        public int CodesRequests { get; private set; }
        public int ToolsRequests { get; private set; }
        public Uri? ToolsEndpoint { get; private set; }

        public Task<byte[]> GetManifestAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (endpoint.AbsolutePath.EndsWith("/launcher-tools-v1.json", StringComparison.Ordinal))
            {
                ToolsRequests++;
                ToolsEndpoint = endpoint;
                return Next(toolResponses);
            }
            if (endpoint.AbsolutePath.EndsWith("/launcher-codes-v1.json", StringComparison.Ordinal))
            {
                CodesRequests++;
                return Next(codeResponses);
            }
            BannerRequests++;
            return Next(bannerResponses);
        }

        public Task<byte[]> GetAssetAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(new InvalidOperationException("Launcher art was not requested."));

        private static async Task<byte[]> Next(Queue<object> responses)
        {
            await Task.Yield();
            if (responses.Count == 0) throw new HttpRequestException("offline");
            return responses.Dequeue() switch
            {
                byte[] payload => payload,
                Exception exception => throw exception,
                _ => throw new InvalidDataException("Unexpected test response."),
            };
        }
    }

    private sealed class BlockingCodesTransport : ILauncherBannersTransport
    {
        private readonly TaskCompletionSource<byte[]> response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BannerRequests { get; private set; }

        public Task<byte[]> GetManifestAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken)
        {
            if (endpoint.AbsoluteUri == LauncherBannersTransport.ProductionCodesEndpoint)
            {
                Started.TrySetResult(true);
                return response.Task.WaitAsync(cancellationToken);
            }

            BannerRequests++;
            return Task.FromException<byte[]>(new InvalidOperationException("Banner endpoint was not requested."));
        }

        public Task<byte[]> GetAssetAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken) =>
            Task.FromException<byte[]>(new InvalidOperationException("Launcher art was not requested."));
    }

    private sealed class FakeTransport : ILauncherBannersTransport
    {
        private readonly byte[]? bytes;
        private readonly Exception? exception;
        public FakeTransport(byte[] bytes) => this.bytes = bytes;
        public FakeTransport(Exception exception) => this.exception = exception;
        public Task<byte[]> GetManifestAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken) => exception is null ? Task.FromResult(bytes ?? throw new InvalidOperationException()) : Task.FromException<byte[]>(exception);
        public Task<byte[]> GetAssetAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken) => Task.FromResult(bytes ?? throw exception!);
    }

    private sealed class RoutedManifestTransport(byte[] banner, params byte[][] codes) : ILauncherBannersTransport
    {
        private readonly Queue<byte[]> codePayloads = new(codes);

        public Task<byte[]> GetManifestAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken)
        {
            if (endpoint.AbsolutePath.Contains("launcher-codes", StringComparison.Ordinal))
                return Task.FromResult(codePayloads.Dequeue());
            return Task.FromResult(banner);
        }

        public Task<byte[]> GetAssetAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No remote art was expected.");
    }

    private sealed class BannerAssetTransport(object banner, byte[] asset) : ILauncherBannersTransport
    {
        public int AssetRequests { get; private set; }

        public Task<byte[]> GetManifestAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken)
        {
            if (endpoint.AbsolutePath.Contains("launcher-codes", StringComparison.Ordinal))
                return Task.FromException<byte[]>(new HttpRequestException("offline"));
            return banner is byte[] payload
                ? Task.FromResult(payload)
                : Task.FromException<byte[]>((Exception)banner);
        }

        public Task<byte[]> GetAssetAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken)
        {
            AssetRequests++;
            return Task.FromResult(asset);
        }
    }

    private sealed class LocalDatabaseAssetTransport(string root) : ILauncherBannersTransport
    {
        public Task<byte[]> GetManifestAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No manifest request was expected.");

        public Task<byte[]> GetAssetAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken)
        {
            const string prefix = "/legacy/";
            Assert.StartsWith(prefix, endpoint.AbsolutePath, StringComparison.Ordinal);
            var relative = Uri.UnescapeDataString(endpoint.AbsolutePath[prefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
            var file = Path.GetFullPath(Path.Combine(root, relative));
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, file, StringComparison.OrdinalIgnoreCase);
            return File.ReadAllBytesAsync(file, cancellationToken);
        }
    }

    private sealed class QueueAssetTransport(params byte[][] assets) : ILauncherBannersTransport
    {
        private readonly Queue<byte[]> payloads = new(assets);
        public int AssetRequests { get; private set; }

        public Task<byte[]> GetManifestAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No manifest request was expected.");

        public Task<byte[]> GetAssetAsync(Uri endpoint, int maximumBytes, CancellationToken cancellationToken)
        {
            AssetRequests++;
            return Task.FromResult(payloads.Dequeue());
        }
    }
}
