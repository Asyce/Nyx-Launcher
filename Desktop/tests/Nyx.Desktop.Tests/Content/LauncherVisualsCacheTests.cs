using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Infrastructure.Content;

namespace Nyx.Desktop.Tests.Content;

public sealed class LauncherVisualsCacheTests
{
    private const string HoyoEndpoint = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=VYTpXlbWo8&language=en-us";
    private const string HoyoGenshinVideo = "https://launcher-webstatic.hoyoverse.com/launcher-public/2026/08/11/8e1c78aaa6e33ed60b88e12a461f8ee5_960034339638640640.webm";
    private const string HoyoGenshinImage = "https://launcher-webstatic.hoyoverse.com/launcher-public/2026/08/11/7add3d312f1a796796e6b7b939a1f3a4_3849034172684705523.webp";
    private const string HoyoHsrVideo = "https://fastcdn.hoyoverse.com/static-resource-v2/2026/08/21/e03cdbee1cd9f23890a912539cdbd24b_6037866158777122843.webm";
    private const string HoyoHsrImage = "https://fastcdn.hoyoverse.com/static-resource-v2/2026/08/21/0ad3be08f9842b2acd4713c08346aa0d_7479681194112238612.webp";
    private const string HoyoZzzVideo = "https://fastcdn.hoyoverse.com/static-resource-v2/2026/08/17/f1ca2e4158a18b4a770bb956a07f8ea8_823680119809509683.webm";
    private const string HoyoZzzImage = "https://fastcdn.hoyoverse.com/static-resource-v2/2026/08/17/467d79a09078b7e5a96a46c092f3d972_6212424333152206963.webp";
    private const string WuwaIndexEndpoint = "https://prod-alicdn-gamestarter.kurogame.com/launcher/launcher/50004_obOHXFrFanqsaIEOmuKroCcbZkQRBC7c/G153/index.json";
    private const string WuwaBackgroundId = "nmJutnA7saYMz2eJ46CL8mB3VUEZvyCs";
    private const string WuwaBackgroundEndpoint = "https://prod-alicdn-gamestarter.kurogame.com/launcher/50004_obOHXFrFanqsaIEOmuKroCcbZkQRBC7c/G153/background/nmJutnA7saYMz2eJ46CL8mB3VUEZvyCs/en.json";
    private const string WuwaVideo = "https://hw-pcdownload-qcloud.aki-game.net/launcher/clientUpload/0nr2n8wkbta7l7flfl.mp4";
    private const string WuwaImage = "https://hw-pcdownload-qcloud.aki-game.net/launcher/clientUpload/kp82e0hn4uq2qqwz80.webp";
    private const string OfficialEndpoint = "https://launcher.gryphline.com/api/proxy/web/batch_proxy";
    private const string OfficialVideo = "https://gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/background/main.mp4";
    private const string OfficialImage = "https://gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/background/main.png";
    private const string OfficialRequest = "{\"proxy_reqs\":[{\"kind\":\"get_main_bg_image\",\"get_main_bg_image_req\":{\"appcode\":\"YDUTE5gscDZ229CW\",\"language\":\"en-us\",\"channel\":\"6\",\"sub_channel\":\"6\",\"platform\":\"Windows\",\"source\":\"launcher\"}}]}";

    [Theory]
    [InlineData("hsr", HoyoHsrVideo, HoyoHsrImage)]
    [InlineData("zzz", HoyoZzzVideo, HoyoZzzImage)]
    public async Task Hoyo_refresh_uses_the_current_official_launcher_video_before_Pengo_fallback(
        string gameId,
        string officialVideo,
        string? officialImage)
    {
        await WithRoot(async root =>
        {
            var video = Media("video/webm", "official Genshin background");
            var image = Media("image/webp", "official Genshin fallback");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                HoyoEndpoint => JsonResponse(HoyoPayload()),
                var url when url == officialVideo => MediaResponse(video, "video/webm"),
                var url when url == officialImage => MediaResponse(image, "image/webp"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync(gameId);

            Assert.NotNull(selection);
            var hash = Convert.ToHexString(SHA256.HashData(video)).ToLowerInvariant();
            Assert.Equal(hash, selection.Revision);
            Assert.Equal(hash + ".webm", Path.GetFileName(selection.Files[0]));
            if (officialImage is null)
            {
                Assert.Single(selection.Files);
                Assert.Equal([HoyoEndpoint, officialVideo], handler.Requests.Select(static request => request.Uri));
            }
            else
            {
                Assert.Equal(2, selection.Files.Count);
                Assert.EndsWith(".webp", selection.Files[1], StringComparison.Ordinal);
                Assert.Equal([HoyoEndpoint, officialVideo, officialImage], handler.Requests.Select(static request => request.Uri));
                Assert.Equal(2, Assert.IsType<LauncherVisualSelection>(
                    new LauncherVisualsCache(root, http).TryLoadLastGood(gameId)).Files.Count);
            }
            Assert.DoesNotContain(handler.Requests, static request => request.Uri == LauncherVisualsCache.DefaultManifestUri.AbsoluteUri);
        });
    }

    [Theory]
    [InlineData("gi", HoyoGenshinImage)]
    [InlineData("hsr", HoyoHsrImage)]
    [InlineData("zzz", HoyoZzzImage)]
    public async Task Hoyo_refresh_accepts_image_only_backgrounds(string gameId, string officialImage)
    {
        await WithRoot(async root =>
        {
            var image = Media("image/webp", "official static background");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                HoyoEndpoint => JsonResponse(HoyoPayload(
                    genshinVideo: null,
                    hsrVideo: null,
                    zzzVideo: null)),
                var url when url == officialImage => MediaResponse(image, "image/webp"),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            }));
            using var http = new HttpClient(handler);
            var cache = new LauncherVisualsCache(root, http);
            Assert.Null(cache.LastRefreshDuration);

            var selection = await cache.RefreshAsync(gameId);

            Assert.NotNull(selection);
            Assert.NotNull(cache.LastRefreshDuration);
            Assert.Equal("image", selection.Kind);
            Assert.EndsWith(".webp", Assert.Single(selection.Files), StringComparison.Ordinal);
            Assert.Contains(handler.Requests, request => request.Uri == officialImage);
        });
    }

    [Theory]
    [InlineData(true, false, "video")]
    [InlineData(false, true, "image")]
    [InlineData(false, false, null)]
    public async Task Hoyo_combined_backgrounds_degrade_each_asset_independently(
        bool videoSucceeds,
        bool imageSucceeds,
        string? expectedKind)
    {
        await WithRoot(async root =>
        {
            var video = Media("video/webm", "official combined animation");
            var image = Media("image/webp", "official combined fallback");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                HoyoEndpoint => JsonResponse(HoyoPayload()),
                HoyoHsrVideo when videoSucceeds => MediaResponse(video, "video/webm"),
                HoyoHsrImage when imageSucceeds => MediaResponse(image, "image/webp"),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            }));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("hsr");

            if (expectedKind is null)
            {
                Assert.Null(selection);
                Assert.Contains(handler.Requests, request => request.Uri == LauncherVisualsCache.DefaultManifestUri.AbsoluteUri);
            }
            else
            {
                Assert.NotNull(selection);
                Assert.Equal(expectedKind, selection.Kind);
                Assert.Single(selection.Files);
            }
            Assert.DoesNotContain(Directory.Exists(CacheRoot(root, "hsr"))
                    ? Directory.EnumerateFiles(CacheRoot(root, "hsr"))
                    : [],
                path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Genshin_prefers_verified_Pengo_MP4_and_keeps_the_current_official_image_as_fallback()
    {
        await WithRoot(async root =>
        {
            var video = Media("video/mp4", "compatible Genshin animation");
            var image = Media("image/webp", "current official Genshin image");
            var hash = Convert.ToHexString(SHA256.HashData(video)).ToLowerInvariant();
            var url = $"https://assets.pengo.gg/launcher-visuals/{hash}.mp4";
            var manifest = SingleAssetManifest(url, video, "video/mp4");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                HoyoEndpoint => JsonResponse(HoyoPayload()),
                var value when value == LauncherVisualsCache.DefaultManifestUri.AbsoluteUri => JsonResponse(manifest),
                var value when value == url => MediaResponse(video, "video/mp4"),
                HoyoGenshinImage => MediaResponse(image, "image/webp"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("gi");

            Assert.NotNull(selection);
            Assert.Equal(2, selection.Files.Count);
            Assert.EndsWith(".mp4", selection.Files[0], StringComparison.Ordinal);
            Assert.EndsWith(".webp", selection.Files[1], StringComparison.Ordinal);
            Assert.Contains(handler.Requests, request => request.Uri == url);
            Assert.Contains(handler.Requests, request => request.Uri == HoyoGenshinImage);
            Assert.DoesNotContain(handler.Requests, request => request.Uri == HoyoGenshinVideo);
        });
    }

    [Fact]
    public async Task Genshin_rejects_incompatible_Pengo_WebM_and_uses_the_current_official_image()
    {
        await WithRoot(async root =>
        {
            var video = Media("video/webm", "incompatible Genshin animation");
            var image = Media("image/webp", "current official Genshin image");
            var hash = Convert.ToHexString(SHA256.HashData(video)).ToLowerInvariant();
            var url = $"https://assets.pengo.gg/launcher-visuals/{hash}.webm";
            var manifest = SingleAssetManifest(url, video, "video/webm");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                HoyoEndpoint => JsonResponse(HoyoPayload()),
                var value when value == LauncherVisualsCache.DefaultManifestUri.AbsoluteUri => JsonResponse(manifest),
                HoyoGenshinImage => MediaResponse(image, "image/webp"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("gi");

            Assert.NotNull(selection);
            Assert.EndsWith(".webp", Assert.Single(selection.Files), StringComparison.Ordinal);
            Assert.DoesNotContain(handler.Requests, request => request.Uri == url || request.Uri == HoyoGenshinVideo);
        });
    }

    [Fact]
    public async Task Wuwa_refresh_follows_the_current_official_launcher_background_chain()
    {
        await WithRoot(async root =>
        {
            var video = Media("video/mp4", "official WuWa background");
            var image = Media("image/webp", "official WuWa first frame");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                WuwaIndexEndpoint => JsonResponse(WuwaIndexPayload()),
                WuwaBackgroundEndpoint => JsonResponse(WuwaPayload(WuwaVideo, WuwaImage)),
                WuwaVideo => MediaResponse(video, "video/mp4"),
                WuwaImage => MediaResponse(image, "image/webp"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("wuwa");

            Assert.NotNull(selection);
            Assert.Equal([WuwaIndexEndpoint, WuwaBackgroundEndpoint, WuwaVideo, WuwaImage], handler.Requests.Select(static request => request.Uri));
            Assert.Equal(2, selection.Files.Count);
            Assert.EndsWith(".mp4", selection.Files[0], StringComparison.Ordinal);
            Assert.EndsWith(".webp", selection.Files[1], StringComparison.Ordinal);
            Assert.DoesNotContain(handler.Requests, static request => request.Uri == LauncherVisualsCache.DefaultManifestUri.AbsoluteUri);
        });
    }

    [Theory]
    [InlineData(true, false, "video")]
    [InlineData(false, true, "image")]
    public async Task Wuwa_accepts_video_only_or_first_frame_only(
        bool includeVideo,
        bool includeImage,
        string expectedKind)
    {
        await WithRoot(async root =>
        {
            var video = Media("video/mp4", "official WuWa animation");
            var image = Media("image/webp", "official WuWa static art");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                WuwaIndexEndpoint => JsonResponse(WuwaIndexPayload()),
                WuwaBackgroundEndpoint => JsonResponse(WuwaPayload(
                    includeVideo ? WuwaVideo : null,
                    includeImage ? WuwaImage : null)),
                WuwaVideo => MediaResponse(video, "video/mp4"),
                WuwaImage => MediaResponse(image, "image/webp"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("wuwa");

            Assert.NotNull(selection);
            Assert.Equal(expectedKind, selection.Kind);
            Assert.Single(selection.Files);
        });
    }

    [Fact]
    public async Task Wuwa_rejects_a_first_frame_outside_the_existing_asset_contract()
    {
        await WithRoot(async root =>
        {
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                WuwaIndexEndpoint => JsonResponse(WuwaIndexPayload()),
                WuwaBackgroundEndpoint => JsonResponse(WuwaPayload(null, "https://example.com/launcher/clientUpload/static.webp")),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("wuwa"));
            Assert.Equal(
                [WuwaIndexEndpoint, WuwaBackgroundEndpoint, LauncherVisualsCache.DefaultManifestUri.AbsoluteUri],
                handler.Requests.Select(static request => request.Uri));
        });
    }

    [Fact]
    public async Task Cache_preloads_all_games_from_one_manifest_and_accepts_WuWa_MP4()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-visual-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var video = Media("video/mp4", "verified wuwa mp4");
            var hash = Convert.ToHexString(SHA256.HashData(video)).ToLowerInvariant();
            var manifest = JsonSerializer.Serialize(new
            {
                schema = 1,
                revision = new string('c', 64),
                games = new Dictionary<string, object>
                {
                    ["wuwa"] = new
                    {
                        kind = "video",
                        assets = new[]
                        {
                            new
                            {
                                url = $"https://assets.pengo.gg/launcher-visuals/{hash}.mp4",
                                sha256 = hash,
                                size = video.Length,
                                mediaType = "video/mp4",
                            },
                        },
                    },
                },
            });
            var handler = new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifest)),
                [$"https://assets.pengo.gg/launcher-visuals/{hash}.mp4"] = (HttpStatusCode.OK, video),
            });
            using var http = new HttpClient(handler);
            var ready = new List<string>();

            var selections = await new LauncherVisualsCache(root, http).RefreshAllAsync(
                selection => ready.Add(selection.GameId));

            var wuwa = Assert.Single(selections);
            Assert.Equal("wuwa", wuwa.Key);
            Assert.EndsWith(".mp4", Assert.Single(wuwa.Value.Files), StringComparison.Ordinal);
            Assert.Equal(["wuwa"], ready);
            Assert.Equal(1, handler.ManifestRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cache_verifies_asset_then_keeps_last_good_when_manifest_is_offline()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-visual-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var video = Media("video/mp4", "verified launcher animation");
            var hash = Convert.ToHexString(SHA256.HashData(video)).ToLowerInvariant();
            var manifest = $$"""
            {
              "schema": 1,
              "revision": "{{new string('a', 64)}}",
              "games": {
                "gi": {
                  "kind": "video",
                  "assets": [{
                    "url": "https://assets.pengo.gg/launcher-visuals/{{hash}}.mp4",
                    "sha256": "{{hash}}",
                    "size": {{video.Length}},
                    "mediaType": "video/mp4"
                  }]
                }
              }
            }
            """;
            using var online = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifest)),
                [$"https://assets.pengo.gg/launcher-visuals/{hash}.mp4"] = (HttpStatusCode.OK, video),
            }));
            var cache = new LauncherVisualsCache(root, online);

            var first = await cache.RefreshAsync("gi");

            Assert.True(first is not null, cache.LastFailure);
            Assert.Equal("video", first.Kind);
            Assert.Single(first.Files);
            Assert.Equal(video, await File.ReadAllBytesAsync(first.Files[0]));

            using var offline = new HttpClient(new MapHandler(
                new Dictionary<string, (HttpStatusCode, byte[])>()));
            var reopened = new LauncherVisualsCache(root, offline);
            var fallback = await reopened.RefreshAsync("gi");

            Assert.NotNull(fallback);
            Assert.Equal(first.Revision, fallback.Revision);
            Assert.Equal(first.Kind, fallback.Kind);
            Assert.Equal(first.Files, fallback.Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cache_rejects_non_pengo_asset_hosts()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-visual-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("x");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var manifest = JsonSerializer.Serialize(new
            {
                schema = 1,
                revision = new string('b', 64),
                games = new Dictionary<string, object>
                {
                    ["gi"] = new
                    {
                        kind = "video",
                        assets = new[]
                        {
                            new
                            {
                                url = "https://example.com/x.webm",
                                sha256 = hash,
                                size = 1,
                                mediaType = "video/webm",
                            },
                        },
                    },
                },
            });
            using var http = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifest)),
            }));

            var result = await new LauncherVisualsCache(root, http).RefreshAsync("gi");

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cache_deletes_superseded_media_only_after_the_new_asset_is_verified()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-visual-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            static (byte[] Bytes, string Hash) Asset(string value)
            {
                var bytes = Media("video/mp4", value);
                return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
            static string Manifest(char revision, byte[] bytes, string hash) => JsonSerializer.Serialize(new
            {
                schema = 1,
                revision = new string(revision, 64),
                games = new Dictionary<string, object>
                {
                    ["gi"] = new
                    {
                        kind = "video",
                        assets = new[]
                        {
                            new
                            {
                                url = $"https://assets.pengo.gg/launcher-visuals/{hash}.mp4",
                                sha256 = hash,
                                size = bytes.Length,
                                mediaType = "video/mp4",
                            },
                        },
                    },
                },
            });

            var old = Asset("old launcher animation");
            using (var http = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(Manifest('a', old.Bytes, old.Hash))),
                [$"https://assets.pengo.gg/launcher-visuals/{old.Hash}.mp4"] = (HttpStatusCode.OK, old.Bytes),
            })))
            {
                Assert.NotNull(await new LauncherVisualsCache(root, http).RefreshAsync("gi"));
            }
            var oldPath = Path.Combine(root, "ContentCache", "LauncherVisuals", "gi", old.Hash + ".mp4");
            Assert.True(File.Exists(oldPath));

            var next = Asset("new launcher animation");
            using (var http = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(Manifest('b', next.Bytes, next.Hash))),
                [$"https://assets.pengo.gg/launcher-visuals/{next.Hash}.mp4"] = (HttpStatusCode.OK, next.Bytes),
            })))
            {
                var refreshed = await new LauncherVisualsCache(root, http).RefreshAsync("gi");
                Assert.NotNull(refreshed);
                Assert.Equal(next.Hash + ".mp4", Path.GetFileName(Assert.Single(refreshed.Files)));
            }

            Assert.False(File.Exists(oldPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://user@assets.pengo.gg/launcher-visuals/{hash}.mp4")]
    [InlineData("https://assets.pengo.gg:444/launcher-visuals/{hash}.mp4")]
    [InlineData("https://assets.pengo.gg/launcher-visuals/{hash}.mp4?x=1")]
    [InlineData("https://assets.pengo.gg/launcher-visuals/{hash}.mp4#x")]
    [InlineData("https://assets.pengo.gg/wrong/{hash}.mp4")]
    [InlineData("https://assets.pengo.gg/launcher-visuals/wrong.mp4")]
    [InlineData("https://assets.pengo.gg/launcher-visuals/{hash}.webm")]
    [InlineData("https://assets.pengo.gg/launcher-visuals/{HASH}.mp4")]
    [InlineData("https://assets.pengo.gg/launcher-visuals/%2f{hash}.mp4")]
    [InlineData("https://assets.pengo.gg/launcher-visuals/%5c{hash}.mp4")]
    [InlineData("https://assets.pengo.gg/launcher-visuals/%2e%2e/{hash}.mp4")]
    public async Task Cache_rejects_noncanonical_Pengo_asset_URLs(string template)
    {
        await WithRoot(async root =>
        {
            var bytes = Media("video/mp4", "canonical asset");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var url = template.Replace("{hash}", hash, StringComparison.Ordinal)
                .Replace("{HASH}", hash.ToUpperInvariant(), StringComparison.Ordinal);
            var manifest = SingleAssetManifest(url, bytes, "video/mp4");
            var handler = new RecordingHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsoluteUri == "https://assets.pengo.gg/launcher-visuals-v1.json"
                    ? JsonResponse(manifest)
                    : MediaResponse(bytes, "video/mp4")));
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("gi"));
            Assert.Equal([HoyoEndpoint, LauncherVisualsCache.DefaultManifestUri.AbsoluteUri], handler.Requests.Select(static request => request.Uri));
        });
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData("video/webm")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public async Task Cache_accepts_each_supported_media_signature(string mediaType)
    {
        await WithRoot(async root =>
        {
            var bytes = Media(mediaType, "valid media");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var extension = MediaExtension(mediaType);
            var url = $"https://assets.pengo.gg/launcher-visuals/{hash}{extension}";
            var manifest = SingleAssetManifest(url, bytes, mediaType, gameId: "hsr");
            using var http = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifest)),
                [url] = (HttpStatusCode.OK, bytes),
            }));

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("hsr");

            Assert.NotNull(selection);
            Assert.Equal(mediaType.StartsWith("video/", StringComparison.Ordinal) ? "video" : "image", selection.Kind);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(selection.Files)));
        });
    }

    [Theory]
    [MemberData(nameof(InvalidMediaSignatures))]
    public async Task Cache_rejects_bad_or_short_media_signatures(string mediaType, byte[] bytes)
    {
        await WithRoot(async root =>
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var extension = MediaExtension(mediaType);
            var url = $"https://assets.pengo.gg/launcher-visuals/{hash}{extension}";
            var manifest = SingleAssetManifest(url, bytes, mediaType, gameId: "hsr");
            using var http = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifest)),
                [url] = (HttpStatusCode.OK, bytes),
            }));

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("hsr"));
            Assert.DoesNotContain(Directory.Exists(CacheRoot(root, "hsr"))
                    ? Directory.EnumerateFiles(CacheRoot(root, "hsr"))
                    : [],
                path => Path.GetFileName(path) != "state.json");
        });
    }

    public static TheoryData<string, byte[]> InvalidMediaSignatures() => new()
    {
        { "video/mp4", [0, 0, 0, 8, (byte)'f'] },
        { "video/mp4", Encoding.ASCII.GetBytes("0000nope0000") },
        { "video/webm", [0x1a, 0x45, 0xdf] },
        { "video/webm", Encoding.ASCII.GetBytes("not-webm") },
        { "image/png", [0x89, (byte)'P', (byte)'N'] },
        { "image/png", Encoding.ASCII.GetBytes("not-a-png") },
        { "image/webp", Encoding.ASCII.GetBytes("RIFFshort") },
        { "image/webp", Encoding.ASCII.GetBytes("RIFF0000NOPE") },
    };

    [Fact]
    public async Task Cache_rejects_asset_content_type_mismatch()
    {
        await WithRoot(async root =>
        {
            var bytes = Media("video/webm", "wrong response type");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var url = $"https://assets.pengo.gg/launcher-visuals/{hash}.webm";
            var manifest = SingleAssetManifest(url, bytes, "video/webm", gameId: "hsr");
            var handler = new RecordingHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsoluteUri.EndsWith(".json", StringComparison.Ordinal)
                    ? JsonResponse(manifest)
                    : MediaResponse(bytes, "video/mp4")));
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("hsr"));
            Assert.DoesNotContain(Directory.Exists(CacheRoot(root, "hsr"))
                    ? Directory.EnumerateFiles(CacheRoot(root, "hsr"))
                    : [],
                path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/problem+json")]
    public async Task Cache_rejects_wrong_manifest_content_type_and_keeps_last_good(string? mediaType)
    {
        await WithRoot(async root =>
        {
            var oldBytes = Media("video/webm", "manifest LKG");
            var oldHash = Convert.ToHexString(SHA256.HashData(oldBytes)).ToLowerInvariant();
            var oldUrl = $"https://assets.pengo.gg/launcher-visuals/{oldHash}.webm";
            using (var oldHttp = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(SingleAssetManifest(oldUrl, oldBytes, "video/webm", 'c', "hsr"))),
                [oldUrl] = (HttpStatusCode.OK, oldBytes),
            })))
            {
                Assert.NotNull(await new LauncherVisualsCache(root, oldHttp).RefreshAsync("hsr"));
            }

            var nextBytes = Media("video/webm", "must not parse");
            var nextHash = Convert.ToHexString(SHA256.HashData(nextBytes)).ToLowerInvariant();
            var nextUrl = $"https://assets.pengo.gg/launcher-visuals/{nextHash}.webm";
            var nextManifest = SingleAssetManifest(nextUrl, nextBytes, "video/webm", 'd', "hsr");
            var handler = new RecordingHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(nextManifest)),
                };
                if (mediaType is not null) response.Content.Headers.ContentType = new(mediaType);
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);

            var fallback = await new LauncherVisualsCache(root, http).RefreshAsync("hsr");

            Assert.NotNull(fallback);
            Assert.Equal(new string('c', 64), fallback.Revision);
            Assert.True(File.Exists(Path.Combine(CacheRoot(root, "hsr"), oldHash + ".webm")));
            Assert.False(File.Exists(Path.Combine(CacheRoot(root, "hsr"), nextHash + ".webm")));
            Assert.Equal([HoyoEndpoint, LauncherVisualsCache.DefaultManifestUri.AbsoluteUri], handler.Requests.Select(static request => request.Uri));
        });
    }

    [Fact]
    public async Task Cache_keeps_last_good_when_new_media_signature_is_invalid()
    {
        await WithRoot(async root =>
        {
            var oldBytes = Media("video/webm", "old valid media");
            var oldHash = Convert.ToHexString(SHA256.HashData(oldBytes)).ToLowerInvariant();
            var oldUrl = $"https://assets.pengo.gg/launcher-visuals/{oldHash}.webm";
            using (var oldHttp = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(SingleAssetManifest(oldUrl, oldBytes, "video/webm", 'a', "hsr"))),
                [oldUrl] = (HttpStatusCode.OK, oldBytes),
            })))
            {
                Assert.NotNull(await new LauncherVisualsCache(root, oldHttp).RefreshAsync("hsr"));
            }

            var badBytes = Encoding.ASCII.GetBytes("not a webm signature");
            var badHash = Convert.ToHexString(SHA256.HashData(badBytes)).ToLowerInvariant();
            var badUrl = $"https://assets.pengo.gg/launcher-visuals/{badHash}.webm";
            using var badHttp = new HttpClient(new MapHandler(new Dictionary<string, (HttpStatusCode, byte[])>
            {
                ["https://assets.pengo.gg/launcher-visuals-v1.json"] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(SingleAssetManifest(badUrl, badBytes, "video/webm", 'b', "hsr"))),
                [badUrl] = (HttpStatusCode.OK, badBytes),
            }));

            var fallback = await new LauncherVisualsCache(root, badHttp).RefreshAsync("hsr");

            Assert.NotNull(fallback);
            Assert.Equal(new string('a', 64), fallback.Revision);
            Assert.True(File.Exists(Path.Combine(CacheRoot(root, "hsr"), oldHash + ".webm")));
            Assert.False(File.Exists(Path.Combine(CacheRoot(root, "hsr"), badHash + ".webm")));
        });
    }

    [Fact]
    public async Task Endfield_uses_the_exact_public_POST_and_verified_MP4_without_request_secrets()
    {
        await WithRoot(async root =>
        {
            var video = Media("video/mp4", "official endfield launcher video");
            var handler = new RecordingHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsoluteUri == OfficialEndpoint
                    ? JsonResponse(OfficialPayload(OfficialVideo))
                    : request.RequestUri.AbsoluteUri == OfficialVideo
                        ? VideoResponse(video)
                        : new HttpResponseMessage(HttpStatusCode.NotFound)));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("ae");

            Assert.NotNull(selection);
            Assert.Equal("video", selection.Kind);
            var hash = Convert.ToHexString(SHA256.HashData(video)).ToLowerInvariant();
            Assert.Equal(hash, selection.Revision);
            Assert.Equal(hash + ".mp4", Path.GetFileName(Assert.Single(selection.Files)));
            Assert.Equal(video, await File.ReadAllBytesAsync(selection.Files[0]));
            Assert.Collection(handler.Requests,
                request =>
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal(OfficialEndpoint, request.Uri);
                    Assert.Equal("application/json", request.MediaType);
                    Assert.Equal("utf-8", request.CharSet);
                    Assert.Equal(OfficialRequest, request.Body);
                    Assert.False(request.HasAuthorization);
                    Assert.False(request.HasCookie);
                },
                request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal(OfficialVideo, request.Uri);
                    Assert.False(request.HasAuthorization);
                    Assert.False(request.HasCookie);
                });
        });
    }

    [Theory]
    [InlineData(true, true, "video", 2)]
    [InlineData(true, false, "video", 1)]
    [InlineData(false, true, "image", 1)]
    public async Task Endfield_accepts_combined_video_only_or_image_only_backgrounds(
        bool includeVideo,
        bool includeImage,
        string expectedKind,
        int expectedFiles)
    {
        await WithRoot(async root =>
        {
            var video = Media("video/mp4", "official Endfield animation");
            var image = Media("image/png", "official Endfield static art");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                OfficialEndpoint => JsonResponse(OfficialPayload(
                    includeVideo ? OfficialVideo : null,
                    includeImage ? OfficialImage : "unused")),
                OfficialVideo => MediaResponse(video, "video/mp4"),
                OfficialImage => MediaResponse(image, "image/png"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            var cache = new LauncherVisualsCache(root, http);
            var selection = await cache.RefreshAsync("ae");

            Assert.True(selection is not null, cache.LastFailure);
            Assert.Equal(expectedKind, selection.Kind);
            Assert.Equal(expectedFiles, selection.Files.Count);
            Assert.Equal(includeVideo ? ".mp4" : ".png", Path.GetExtension(selection.Files[0]));
        });
    }

    [Theory]
    [MemberData(nameof(InvalidOfficialResponses))]
    public async Task Endfield_rejects_malformed_or_ambiguous_official_responses(string payload)
    {
        await WithRoot(async root =>
        {
            using var http = new HttpClient(new RecordingHandler((_, _) =>
                Task.FromResult(JsonResponse(payload))));

            var cache = new LauncherVisualsCache(root, http);
            var selection = await cache.RefreshAsync("ae");

            Assert.Null(selection);
            Assert.Empty(Directory.Exists(CacheRoot(root, "ae"))
                ? Directory.EnumerateFiles(CacheRoot(root, "ae"))
                : []);
        });
    }

    public static TheoryData<string> InvalidOfficialResponses() => new()
    {
        "{",
        "{}",
        "{\"proxy_rsps\":[]}",
        "{\"proxy_rsps\":[{\"kind\":\"get_main_bg_image\",\"get_main_bg_image_rsp\":{\"data_version\":\"1\",\"main_bg_image\":{\"url\":\"x\",\"md5\":\"x\"}}}]}",
        "{\"proxy_rsps\":[{\"kind\":\"wrong\",\"get_main_bg_image_rsp\":{\"data_version\":\"1\",\"main_bg_image\":{\"url\":\"x\",\"md5\":\"x\",\"video_url\":\"" + OfficialVideo + "\"}}}]}",
        "{\"proxy_rsps\":[{\"kind\":\"get_main_bg_image\",\"get_main_bg_image_rsp\":{\"data_version\":\"1\",\"main_bg_image\":{\"url\":\"x\",\"md5\":\"x\",\"video_url\":\"" + OfficialVideo + "\"}}},{\"kind\":\"get_main_bg_image\",\"get_main_bg_image_rsp\":{\"data_version\":\"1\",\"main_bg_image\":{\"url\":\"x\",\"md5\":\"x\",\"video_url\":\"" + OfficialVideo + "\"}}}]}",
    };

    [Theory]
    [InlineData("http://gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.mp4")]
    [InlineData("https://example.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.mp4")]
    [InlineData("https://gl-utils-public.hg-cdn.com/not-the-contract/a.mp4")]
    [InlineData("https://gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.mp4?token=no")]
    [InlineData("https://gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.mp4#no")]
    [InlineData("https://gl-utils-public.hg-cdn.com:444/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.mp4")]
    [InlineData("https://user@gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.mp4")]
    [InlineData("https://gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.webm")]
    public async Task Endfield_rejects_video_URLs_outside_the_fixed_CDN_contract(string videoUrl)
    {
        await WithRoot(async root =>
        {
            var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(OfficialPayload(videoUrl))));
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("ae"));
            Assert.Equal(
                [OfficialEndpoint, LauncherVisualsCache.DefaultManifestUri.AbsoluteUri],
                handler.Requests.Select(static request => request.Uri));
        });
    }

    [Theory]
    [InlineData("http://gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.png")]
    [InlineData("https://example.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.png")]
    [InlineData("https://gl-utils-public.hg-cdn.com/not-the-contract/a.png")]
    [InlineData("https://gl-utils-public.hg-cdn.com/hg-utils/prod/eppcsuwqpaueijqk/YDUTE5gscDZ229CW/a.jpg")]
    public async Task Endfield_rejects_static_URLs_outside_the_fixed_CDN_contract(string imageUrl)
    {
        await WithRoot(async root =>
        {
            var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(OfficialPayload(null, imageUrl))));
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("ae"));
            Assert.Equal(
                [OfficialEndpoint, LauncherVisualsCache.DefaultManifestUri.AbsoluteUri],
                handler.Requests.Select(static request => request.Uri));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endfield_refuses_redirects(bool redirectVideo)
    {
        await WithRoot(async root =>
        {
            var handler = new RecordingHandler((request, _) =>
            {
                if (redirectVideo && request.RequestUri!.AbsoluteUri == OfficialEndpoint)
                    return Task.FromResult(JsonResponse(OfficialPayload(OfficialVideo)));
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://example.com/should-not-be-followed");
                return Task.FromResult(redirect);
            });
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("ae"));
            Assert.Equal(redirectVideo ? 3 : 2, handler.Requests.Count);
            Assert.DoesNotContain(handler.Requests, request => request.Uri == "https://example.com/should-not-be-followed");
        });
    }

    [Fact]
    public async Task Endfield_rejects_a_response_that_arrived_after_a_followed_redirect()
    {
        await WithRoot(async root =>
        {
            var handler = new RecordingHandler((_, _) =>
            {
                var response = JsonResponse(OfficialPayload(OfficialVideo));
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/final");
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("ae"));
            Assert.Equal(
                [OfficialEndpoint, LauncherVisualsCache.DefaultManifestUri.AbsoluteUri],
                handler.Requests.Select(static request => request.Uri));
        });
    }

    [Theory]
    [InlineData("text/plain", 12)]
    [InlineData("video/mp4", 41943041)]
    [InlineData("video/mp4", 0)]
    public async Task Endfield_rejects_wrong_video_content_type_or_length(string mediaType, long length)
    {
        await WithRoot(async root =>
        {
            var handler = new RecordingHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsoluteUri == OfficialEndpoint
                    ? JsonResponse(OfficialPayload(OfficialVideo))
                    : HeaderOnlyResponse(mediaType, length)));
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("ae"));
            Assert.DoesNotContain(Directory.Exists(CacheRoot(root, "ae"))
                    ? Directory.EnumerateFiles(CacheRoot(root, "ae"))
                    : [],
                path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Endfield_requires_a_declared_video_content_length()
    {
        await WithRoot(async root =>
        {
            var handler = new RecordingHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsoluteUri == OfficialEndpoint
                    ? JsonResponse(OfficialPayload(OfficialVideo))
                    : UnknownLengthVideoResponse()));
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(root, http).RefreshAsync("ae"));
        });
    }

    [Fact]
    public async Task Endfield_rejects_oversized_official_response_without_exposing_raw_content()
    {
        await WithRoot(async root =>
        {
            var rawMarker = "raw-secret-marker";
            using (var malformedHttp = new HttpClient(new RecordingHandler((_, _) =>
                Task.FromResult(JsonResponse("{\"proxy_rsps\":[],\"ignored\":\"" + rawMarker + "\"}")))))
            {
                var malformed = new LauncherVisualsCache(root, malformedHttp);
                Assert.Null(await malformed.RefreshAsync("ae"));
                Assert.DoesNotContain(rawMarker, malformed.LastFailure, StringComparison.Ordinal);
            }
            var oversized = Encoding.UTF8.GetBytes("{\"proxy_rsps\":[],\"ignored\":\"" + rawMarker
                + new string('x', 128 * 1024) + "\"}");
            using var http = new HttpClient(new RecordingHandler((_, _) =>
                Task.FromResult(JsonResponse(oversized))));
            var cache = new LauncherVisualsCache(root, http);

            Assert.Null(await cache.RefreshAsync("ae"));
            Assert.NotNull(cache.LastRefreshDuration);
            Assert.DoesNotContain(rawMarker, cache.LastFailure, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Endfield_timeout_and_caller_cancellation_leave_no_partial_files()
    {
        await WithRoot(async root =>
        {
            using (var timedHttp = new HttpClient(new RecordingHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            })) { Timeout = TimeSpan.FromMilliseconds(20) })
            {
                Assert.Null(await new LauncherVisualsCache(root, timedHttp).RefreshAsync("ae"));
            }

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            using var canceledHttp = new HttpClient(new RecordingHandler((_, cancellationToken) =>
                Task.FromCanceled<HttpResponseMessage>(cancellationToken)));
            var canceledCache = new LauncherVisualsCache(root, canceledHttp);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                canceledCache.RefreshAsync("ae", canceled.Token));
            Assert.NotNull(canceledCache.LastRefreshDuration);
            Assert.DoesNotContain(Directory.Exists(CacheRoot(root, "ae"))
                    ? Directory.EnumerateFiles(CacheRoot(root, "ae"))
                    : [],
                path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Endfield_cancellation_during_static_fallback_keeps_the_previous_last_good()
    {
        await WithRoot(async root =>
        {
            var previous = await DownloadOfficialAsync(root, Media("video/mp4", "previous working background"));
            var nextVideo = Media("video/mp4", "new animation awaiting its fallback");
            var imageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var canceled = new CancellationTokenSource();
            using var http = new HttpClient(new RecordingHandler(async (request, cancellationToken) =>
            {
                if (request.RequestUri!.AbsoluteUri == OfficialEndpoint)
                    return JsonResponse(OfficialPayload(OfficialVideo, OfficialImage));
                if (request.RequestUri.AbsoluteUri == OfficialVideo)
                    return MediaResponse(nextVideo, "video/mp4");
                imageStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }));
            var refresh = new LauncherVisualsCache(root, http).RefreshAsync("ae", canceled.Token);

            await imageStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            canceled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            var retained = new LauncherVisualsCache(root).TryLoadLastGood("ae");
            Assert.NotNull(retained);
            Assert.Equal(previous.Revision, retained.Revision);
            Assert.Equal(previous.Files, retained.Files);
            Assert.DoesNotContain(Directory.EnumerateFiles(CacheRoot(root, "ae")),
                path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Canceled_preload_does_not_publish_a_last_good_callback()
    {
        await WithRoot(async root =>
        {
            _ = await DownloadOfficialAsync(root, Media("video/mp4", "must stay private after cancellation"));
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new RecordingHandler(async (_, cancellationToken) =>
            {
                requestStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }));
            using var canceled = new CancellationTokenSource();
            var callbacks = 0;
            var preload = new LauncherVisualsCache(root, http).RefreshAllAsync(
                _ => Interlocked.Increment(ref callbacks),
                canceled.Token);

            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            canceled.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preload);
            Assert.Equal(0, Volatile.Read(ref callbacks));
        });
    }

    [Fact]
    public async Task Disposal_drains_canceled_preload_and_disposes_only_the_default_client()
    {
        await WithRoot(async root =>
        {
            var owned = new LauncherVisualsCache(root);
            var ownedHttp = (HttpClient)typeof(LauncherVisualsCache)
                .GetField("http", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(owned)!;
            await owned.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => ownedHttp.GetAsync("https://example.invalid/"));

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = 0;
            using var injected = new HttpClient(new RecordingHandler(async (_, _) =>
            {
                if (Interlocked.Increment(ref requests) == 1)
                {
                    entered.SetResult();
                    await release.Task;
                    throw new OperationCanceledException();
                }
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }));
            var cache = new LauncherVisualsCache(root, injected);
            using var canceled = new CancellationTokenSource();
            var preload = cache.RefreshAsync("ae", canceled.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var disposal = cache.DisposeAsync().AsTask();
            canceled.Cancel();
            await Task.Delay(40);
            Assert.False(disposal.IsCompleted);
            release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preload);
            await disposal.WaitAsync(TimeSpan.FromSeconds(1));

            using var response = await injected.GetAsync("https://example.invalid/");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        });
    }

    [Fact]
    public async Task Endfield_keeps_old_gallery_and_partial_downloads_out_until_official_promotion_succeeds()
    {
        await WithRoot(async root =>
        {
            var oldFiles = SeedGallery(root);
            var truncated = Encoding.UTF8.GetBytes("short");
            using (var badHttp = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsoluteUri == OfficialEndpoint
                    ? JsonResponse(OfficialPayload(OfficialVideo))
                    : VideoResponse(truncated, declaredLength: truncated.Length + 1)))))
            {
                var fallback = await new LauncherVisualsCache(root, badHttp).RefreshAsync("ae");
                Assert.NotNull(fallback);
                Assert.Equal("gallery", fallback.Kind);
            }
            Assert.All(oldFiles, path => Assert.True(File.Exists(path)));
            Assert.DoesNotContain(Directory.EnumerateFiles(CacheRoot(root, "ae")),
                path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));

            var video = Media("video/mp4", "complete official video");
            using var goodHttp = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsoluteUri == OfficialEndpoint
                    ? JsonResponse(OfficialPayload(OfficialVideo))
                    : VideoResponse(video))));
            var promoted = await new LauncherVisualsCache(root, goodHttp).RefreshAsync("ae");

            Assert.NotNull(promoted);
            Assert.Equal("video", promoted.Kind);
            Assert.All(oldFiles, path => Assert.False(File.Exists(path)));
            Assert.Equal(video, await File.ReadAllBytesAsync(Assert.Single(promoted.Files)));
        });
    }

    [Fact]
    public async Task Endfield_official_video_wins_without_downloading_the_Pengo_gallery()
    {
        await WithRoot(async root =>
        {
            var gallery = Encoding.UTF8.GetBytes("pengo gallery");
            var galleryHash = Convert.ToHexString(SHA256.HashData(gallery)).ToLowerInvariant();
            var manifest = ManifestWithEndfieldGallery(galleryHash, gallery.Length);
            var video = Media("video/mp4", "official wins");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                OfficialEndpoint => JsonResponse(OfficialPayload(OfficialVideo)),
                OfficialVideo => VideoResponse(video),
                "https://assets.pengo.gg/launcher-visuals-v1.json" => JsonResponse(manifest),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            var selections = await new LauncherVisualsCache(root, http).RefreshAllAsync();

            Assert.Equal("video", selections["ae"].Kind);
            Assert.DoesNotContain(handler.Requests, request => request.Uri.Contains(galleryHash, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Endfield_first_run_uses_verified_Pengo_gallery_when_official_refresh_fails()
    {
        await WithRoot(async root =>
        {
            var gallery = Enumerable.Range(1, 3)
                .Select(index => Media("image/webp", "fallback gallery " + index))
                .ToArray();
            var assets = gallery.Select(bytes => (
                Hash: Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Size: bytes.Length)).ToArray();
            var manifest = ManifestWithEndfieldGallery(assets);
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                OfficialEndpoint => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                var url when url == LauncherVisualsCache.DefaultManifestUri.AbsoluteUri => JsonResponse(manifest),
                var url when Array.FindIndex(assets, asset => url.EndsWith(asset.Hash + ".webp", StringComparison.Ordinal)) is var index and >= 0 =>
                    MediaResponse(gallery[index], "image/webp"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            }));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("ae");

            Assert.NotNull(selection);
            Assert.Equal("gallery", selection.Kind);
            Assert.Equal(3, selection.Files.Count);
            Assert.Equal(3, selection.Files.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(
                [OfficialEndpoint, LauncherVisualsCache.DefaultManifestUri.AbsoluteUri, .. assets.Select(asset =>
                    $"https://assets.pengo.gg/launcher-visuals/{asset.Hash}.webp")],
                handler.Requests.Select(static request => request.Uri));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endfield_both_sources_failing_returns_only_a_healthy_last_good(bool seedLastGood)
    {
        await WithRoot(async root =>
        {
            LauncherVisualSelection? expected = null;
            if (seedLastGood)
                expected = await DownloadOfficialAsync(root, Media("video/mp4", "healthy last good"));
            var handler = new RecordingHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            using var http = new HttpClient(handler);

            var selection = await new LauncherVisualsCache(root, http).RefreshAsync("ae");

            if (expected is null)
            {
                Assert.Null(selection);
            }
            else
            {
                Assert.NotNull(selection);
                Assert.Equal(expected.GameId, selection.GameId);
                Assert.Equal(expected.Revision, selection.Revision);
                Assert.Equal(expected.Kind, selection.Kind);
                Assert.Equal(expected.Character, selection.Character);
                Assert.Equal(expected.Files, selection.Files);
            }
            Assert.Equal(
                [OfficialEndpoint, LauncherVisualsCache.DefaultManifestUri.AbsoluteUri],
                handler.Requests.Select(static request => request.Uri));
        });
    }

    [Fact]
    public async Task Endfield_official_refresh_is_independent_of_Pengo_manifest_failure()
    {
        await WithRoot(async root =>
        {
            var video = Media("video/mp4", "official despite Pengo outage");
            var handler = new RecordingHandler((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                OfficialEndpoint => JsonResponse(OfficialPayload(OfficialVideo)),
                OfficialVideo => VideoResponse(video),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            }));
            using var http = new HttpClient(handler);

            var selections = await new LauncherVisualsCache(root, http).RefreshAllAsync();

            Assert.Equal("video", selections["ae"].Kind);
            Assert.Contains(handler.Requests, request => request.Uri == "https://assets.pengo.gg/launcher-visuals-v1.json");
        });
    }

    [Fact]
    public async Task Endfield_callback_arrives_while_Pengo_manifest_is_blocked()
    {
        await WithRoot(async root =>
        {
            var video = Media("video/mp4", "independent callback");
            var manifestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseManifest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callback = new TaskCompletionSource<LauncherVisualSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new RecordingHandler(async (request, cancellationToken) =>
            {
                if (request.RequestUri!.AbsoluteUri == OfficialEndpoint)
                    return JsonResponse(OfficialPayload(OfficialVideo));
                if (request.RequestUri.AbsoluteUri == OfficialVideo)
                    return VideoResponse(video);
                manifestStarted.TrySetResult(true);
                await releaseManifest.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });
            using var http = new HttpClient(handler);
            var refresh = new LauncherVisualsCache(root, http).RefreshAllAsync(
                selection =>
                {
                    if (selection.GameId == "ae") callback.TrySetResult(selection);
                });

            await manifestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            try
            {
                var ae = await callback.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal("video", ae.Kind);
                Assert.False(refresh.IsCompleted);
            }
            finally
            {
                releaseManifest.TrySetResult(true);
            }

            Assert.Equal("video", (await refresh)["ae"].Kind);
        });
    }

    [Fact]
    public async Task Endfield_cached_official_video_survives_offline_restart()
    {
        await WithRoot(async root =>
        {
            var video = Media("video/mp4", "offline official video");
            using (var online = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(
                request.RequestUri!.AbsoluteUri == OfficialEndpoint
                    ? JsonResponse(OfficialPayload(OfficialVideo))
                    : VideoResponse(video)))))
            {
                Assert.NotNull(await new LauncherVisualsCache(root, online).RefreshAsync("ae"));
            }
            using (var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(CacheRoot(root, "ae"), "state.json"))))
            {
                var metadata = Assert.Single(state.RootElement.GetProperty("FileMetadata").EnumerateArray());
                Assert.Equal(video.Length, metadata.GetProperty("Size").GetInt64());
                Assert.Equal(64, metadata.GetProperty("Sha256").GetString()!.Length);
            }
            using var offline = new HttpClient(new RecordingHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));

            var cached = await new LauncherVisualsCache(root, offline).RefreshAsync("ae");

            Assert.NotNull(cached);
            Assert.Equal("video", cached.Kind);
            Assert.Equal(video, await File.ReadAllBytesAsync(Assert.Single(cached.Files)));
        });
    }

    [Theory]
    [InlineData("same-length")]
    [InlineData("truncated")]
    public async Task Last_good_rejects_tampered_official_bytes_without_deleting_cache(string corruption)
    {
        await WithRoot(async root =>
        {
            var original = Media("video/mp4", "authenticated official bytes");
            var selection = await DownloadOfficialAsync(root, original);
            var path = Assert.Single(selection.Files);
            await File.WriteAllBytesAsync(path, corruption == "same-length"
                ? Enumerable.Repeat((byte)'x', original.Length).ToArray()
                : original[..^1]);

            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(Path.Combine(CacheRoot(root, "ae"), "state.json")));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(41943041)]
    public async Task Last_good_rejects_empty_or_oversized_video(long size)
    {
        await WithRoot(root =>
        {
            var hash = new string('a', 64);
            var path = Path.Combine(CacheRoot(root, "ae"), hash + ".mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.SetLength(size);
            WriteState(root, "ae", hash, "video", [new(hash + ".mp4", size, hash)]);

            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));
            Assert.True(File.Exists(path));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("extension")]
    [InlineData("revision")]
    public async Task Last_good_rejects_wrong_authenticated_metadata(string invalid)
    {
        await WithRoot(root =>
        {
            var bytes = Encoding.UTF8.GetBytes("cached video");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var extension = invalid == "extension" ? ".webm" : ".mp4";
            var name = hash + extension;
            Directory.CreateDirectory(CacheRoot(root, "ae"));
            File.WriteAllBytes(Path.Combine(CacheRoot(root, "ae"), name), bytes);
            WriteState(
                root,
                "ae",
                invalid == "revision" ? new string('b', 64) : hash,
                "video",
                [new(name, invalid == "size" ? bytes.Length + 1 : bytes.Length,
                    invalid == "hash" ? new string('c', 64) : hash)]);

            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));
            Assert.True(File.Exists(Path.Combine(CacheRoot(root, "ae"), name)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Last_good_rejects_duplicate_gallery_entries()
    {
        await WithRoot(root =>
        {
            var bytes = Encoding.UTF8.GetBytes("gallery frame");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var file = new StateFile(hash + ".webp", bytes.Length, hash);
            Directory.CreateDirectory(CacheRoot(root, "ae"));
            File.WriteAllBytes(Path.Combine(CacheRoot(root, "ae"), file.Name), bytes);
            WriteState(root, "ae", new string('d', 64), "gallery", [file, file, file]);

            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));
            Assert.True(File.Exists(Path.Combine(CacheRoot(root, "ae"), file.Name)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Last_good_rejects_one_corrupt_file_from_an_authenticated_gallery()
    {
        await WithRoot(root =>
        {
            var files = Enumerable.Range(1, 3).Select(index =>
            {
                var bytes = Media("image/webp", "frame " + index);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var name = hash + ".webp";
                Directory.CreateDirectory(CacheRoot(root, "ae"));
                File.WriteAllBytes(Path.Combine(CacheRoot(root, "ae"), name), bytes);
                return new StateFile(name, bytes.Length, hash);
            }).ToArray();
            WriteState(root, "ae", new string('e', 64), "gallery", files);
            File.WriteAllBytes(
                Path.Combine(CacheRoot(root, "ae"), files[1].Name),
                Enumerable.Repeat((byte)'z', (int)files[1].Size).ToArray());

            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));
            Assert.All(files, file => Assert.True(File.Exists(Path.Combine(CacheRoot(root, "ae"), file.Name))));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Last_good_rejects_unverifiable_legacy_state_without_deleting_it()
    {
        await WithRoot(root =>
        {
            var directory = CacheRoot(root, "ae");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "legacy.webp");
            File.WriteAllText(path, "legacy bytes");
            WriteState(root, "ae", new string('f', 64), "image",
                [new("legacy.webp", new FileInfo(path).Length, new string('a', 64))], includeMetadata: false);

            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(Path.Combine(directory, "state.json")));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Last_good_rejects_malformed_or_future_state_metadata()
    {
        await WithRoot(root =>
        {
            var directory = CacheRoot(root, "ae");
            Directory.CreateDirectory(directory);
            var statePath = Path.Combine(directory, "state.json");
            File.WriteAllText(statePath, "{");
            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));

            var bytes = Encoding.UTF8.GetBytes("future metadata");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var name = hash + ".mp4";
            File.WriteAllBytes(Path.Combine(directory, name), bytes);
            File.WriteAllText(statePath, JsonSerializer.Serialize(new
            {
                GameId = "ae",
                Revision = hash,
                Kind = "video",
                Character = (string?)null,
                Files = new[] { name },
                FileMetadata = new[] { new { Name = name, Size = bytes.Length, Sha256 = hash, Future = true } },
            }));

            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));
            Assert.True(File.Exists(statePath));
            Assert.True(File.Exists(Path.Combine(directory, name)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Last_good_rejects_reparse_point_media_when_supported()
    {
        await WithRoot(root =>
        {
            var directory = CacheRoot(root, "ae");
            Directory.CreateDirectory(directory);
            var bytes = Media("video/mp4", "linked video");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var target = Path.Combine(directory, "target.bin");
            var link = Path.Combine(directory, hash + ".mp4");
            File.WriteAllBytes(target, bytes);
            try { File.CreateSymbolicLink(link, target); }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException) { return Task.CompletedTask; }
            WriteState(root, "ae", hash, "video", [new(hash + ".mp4", bytes.Length, hash)]);

            Assert.Null(new LauncherVisualsCache(root).TryLoadLastGood("ae"));
            Assert.True(File.Exists(link));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("game")]
    [InlineData("ancestor")]
    [InlineData("swapped-game")]
    public async Task Cache_never_traverses_or_writes_through_cache_junctions(string mode)
    {
        var parent = Path.Combine(Path.GetTempPath(), "nyx-visual-junction-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(parent, "data");
        var external = Path.Combine(parent, "external");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var link = mode == "ancestor"
            ? Path.Combine(data, "ContentCache")
            : CacheRoot(data, "ae");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(external);
        File.WriteAllText(sentinel, "keep");
        try
        {
            var probe = Path.Combine(parent, "probe");
            try { Directory.CreateSymbolicLink(probe, external); Directory.Delete(probe); }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException) { return; }

            if (mode == "swapped-game") Directory.CreateDirectory(link);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                Directory.CreateSymbolicLink(link, external);
            }

            var video = Media("video/mp4", "must stay contained");
            var handler = new RecordingHandler((request, _) =>
            {
                if (mode == "swapped-game" && request.RequestUri!.AbsoluteUri == OfficialVideo)
                {
                    Directory.Delete(link);
                    Directory.CreateSymbolicLink(link, external);
                }
                return Task.FromResult(request.RequestUri!.AbsoluteUri == OfficialEndpoint
                    ? JsonResponse(OfficialPayload(OfficialVideo))
                    : VideoResponse(video));
            });
            using var http = new HttpClient(handler);

            Assert.Null(await new LauncherVisualsCache(data, http).RefreshAsync("ae"));
            Assert.Equal("keep", File.ReadAllText(sentinel));
            Assert.Equal([sentinel], Directory.EnumerateFiles(external).ToArray());
            Assert.Empty(Directory.EnumerateDirectories(external));
        }
        finally
        {
            try
            {
                if (Directory.Exists(link)
                    && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(link);
            }
            catch { }
            Directory.Delete(parent, recursive: true);
        }
    }

    private static string OfficialPayload(string? videoUrl, string imageUrl = "unused") => JsonSerializer.Serialize(new
    {
        proxy_rsps = new[]
        {
            new
            {
                kind = "get_main_bg_image",
                get_main_bg_image_rsp = new
                {
                    data_version = "1",
                    main_bg_image = new { url = imageUrl, md5 = "unused", video_url = videoUrl },
                },
            },
        },
    });

    private static string SingleAssetManifest(
        string url,
        byte[] bytes,
        string mediaType,
        char revision = 'a',
        string gameId = "gi") => JsonSerializer.Serialize(new
    {
        schema = 1,
        revision = new string(revision, 64),
        games = new Dictionary<string, object>
        {
            [gameId] = new
            {
                kind = mediaType.StartsWith("video/", StringComparison.Ordinal) ? "video" : "image",
                assets = new[]
                {
                    new
                    {
                        url,
                        sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        size = bytes.Length,
                        mediaType,
                    },
                },
            },
        },
    });

    private static string MediaExtension(string mediaType) => mediaType switch
    {
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "image/webp" => ".webp",
        _ => ".png",
    };

    private static byte[] Media(string mediaType, string payload)
    {
        var suffix = Encoding.UTF8.GetBytes(payload);
        var prefix = mediaType switch
        {
            "video/mp4" => new byte[] { 0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p' },
            "video/webm" => new byte[] { 0x1a, 0x45, 0xdf, 0xa3 },
            "image/png" => new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a },
            _ => new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' },
        };
        return [.. prefix, .. suffix];
    }

    private static string ManifestWithEndfieldGallery(string hash, int size) =>
        ManifestWithEndfieldGallery([(hash, size), (hash, size), (hash, size)]);

    private static string ManifestWithEndfieldGallery(params (string Hash, int Size)[] assets) => JsonSerializer.Serialize(new
    {
        schema = 1,
        revision = new string('d', 64),
        games = new Dictionary<string, object>
        {
            ["ae"] = new
            {
                kind = "gallery",
                assets = assets.Select(asset => new
                {
                    url = $"https://assets.pengo.gg/launcher-visuals/{asset.Hash}.webp",
                    sha256 = asset.Hash,
                    size = asset.Size,
                    mediaType = "image/webp",
                }),
            },
        },
    });

    private static string[] SeedGallery(string root)
    {
        var directory = CacheRoot(root, "ae");
        Directory.CreateDirectory(directory);
        var files = Enumerable.Range(1, 3).Select(index =>
        {
            var bytes = Media("image/webp", "old gallery " + index);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var path = Path.Combine(directory, hash + ".webp");
            File.WriteAllBytes(path, bytes);
            return path;
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(new
        {
            GameId = "ae",
            Revision = new string('e', 64),
            Kind = "gallery",
            Character = "Old",
            Files = files.Select(Path.GetFileName).ToArray(),
        }));
        return files;
    }

    private static async Task<LauncherVisualSelection> DownloadOfficialAsync(string root, byte[] video)
    {
        using var http = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsoluteUri == OfficialEndpoint
                ? JsonResponse(OfficialPayload(OfficialVideo))
                : VideoResponse(video))));
        return Assert.IsType<LauncherVisualSelection>(
            await new LauncherVisualsCache(root, http).RefreshAsync("ae"));
    }

    private static void WriteState(
        string root,
        string gameId,
        string revision,
        string kind,
        IReadOnlyList<StateFile> files,
        bool includeMetadata = true)
    {
        var directory = CacheRoot(root, gameId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(new
        {
            GameId = gameId,
            Revision = revision,
            Kind = kind,
            Character = (string?)null,
            Files = files.Select(static file => file.Name).ToArray(),
            FileMetadata = includeMetadata ? files.Select(static file => new
            {
                file.Name,
                file.Size,
                file.Sha256,
            }).ToArray() : null,
        }));
    }

    private sealed record StateFile(string Name, long Size, string Sha256);

    private static async Task WithRoot(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-visual-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await test(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CacheRoot(string root, string gameId) =>
        Path.Combine(root, "ContentCache", "LauncherVisuals", gameId);

    private static string WuwaIndexPayload() => JsonSerializer.Serialize(new
    {
        @default = new { resource = new { version = "2.6.5.0" } },
        functionCode = new { background = WuwaBackgroundId },
        experiment = new { updater = new { enableCDNSelect = false } },
        crashInitSwitch = 1,
    });

    private static string WuwaPayload(string? videoUrl, string? imageUrl) => JsonSerializer.Serialize(new
    {
        functionSwitch = 1,
        backgroundFile = videoUrl,
        backgroundFileType = 2,
        firstFrameImage = imageUrl,
        slogan = "https://hw-pcdownload-qcloud.aki-game.net/launcher/clientUpload/gsamyapds9ruiv7erm.png",
    });

    private static string HoyoPayload(
        string? genshinVideo = HoyoGenshinVideo,
        string? genshinImage = HoyoGenshinImage,
        string? hsrVideo = HoyoHsrVideo,
        string? hsrImage = HoyoHsrImage,
        string? zzzVideo = HoyoZzzVideo,
        string? zzzImage = HoyoZzzImage) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                game_info_list = new[]
                {
                    new
                    {
                        game = new { id = "gopR6Cufr3" },
                        backgrounds = new[]
                        {
                            new { background = new { url = genshinImage }, video = new { url = genshinVideo } },
                        },
                    },
                    new
                    {
                        game = new { id = "4ziysqXOQ8" },
                        backgrounds = new[]
                        {
                            new { background = new { url = hsrImage }, video = new { url = hsrVideo } },
                        },
                    },
                    new
                    {
                        game = new { id = "U5hbdsT9W7" },
                        backgrounds = new[]
                        {
                            new { background = new { url = zzzImage }, video = new { url = zzzVideo } },
                        },
                    },
                },
            },
        });

    private static HttpResponseMessage JsonResponse(string body) => JsonResponse(Encoding.UTF8.GetBytes(body));

    private static HttpResponseMessage JsonResponse(byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }

    private static HttpResponseMessage VideoResponse(byte[] body, long? declaredLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new("video/mp4");
        if (declaredLength is not null) response.Content.Headers.ContentLength = declaredLength;
        return response;
    }

    private static HttpResponseMessage MediaResponse(byte[] body, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new(mediaType);
        return response;
    }

    private static HttpResponseMessage HeaderOnlyResponse(string mediaType, long length)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentType = new(mediaType);
        response.Content.Headers.ContentLength = length;
        return response;
    }

    private static HttpResponseMessage UnknownLengthVideoResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(Encoding.UTF8.GetBytes("video")),
        };
        response.Content.Headers.ContentType = new("video/mp4");
        return response;
    }

    private sealed record RequestRecord(
        HttpMethod Method,
        string Uri,
        string? MediaType,
        string? CharSet,
        string? Body,
        bool HasAuthorization,
        bool HasCookie);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly List<RequestRecord> requests = [];
        public IReadOnlyList<RequestRecord> Requests => requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (requests)
            {
                requests.Add(new(
                    request.Method,
                    request.RequestUri!.AbsoluteUri,
                    request.Content?.Headers.ContentType?.MediaType,
                    request.Content?.Headers.ContentType?.CharSet,
                    body,
                    request.Headers.Authorization is not null,
                    request.Headers.Contains("Cookie")));
            }
            return await respond(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class MapHandler(IReadOnlyDictionary<string, (HttpStatusCode Status, byte[] Body)> responses)
        : HttpMessageHandler
    {
        public int ManifestRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri == "https://assets.pengo.gg/launcher-visuals-v1.json")
                ManifestRequests++;
            if (!responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var response))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var content = new ByteArrayContent(response.Body);
            content.Headers.ContentType = new(request.RequestUri.AbsolutePath switch
            {
                var path when path.EndsWith(".mp4", StringComparison.Ordinal) => "video/mp4",
                var path when path.EndsWith(".webm", StringComparison.Ordinal) => "video/webm",
                var path when path.EndsWith(".webp", StringComparison.Ordinal) => "image/webp",
                var path when path.EndsWith(".png", StringComparison.Ordinal) => "image/png",
                _ => "application/json",
            });
            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = content,
            });
        }
    }
}
