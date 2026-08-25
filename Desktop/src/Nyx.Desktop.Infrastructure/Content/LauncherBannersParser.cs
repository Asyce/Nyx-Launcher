using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Nyx.Desktop.Core.Content;

namespace Nyx.Desktop.Infrastructure.Content;

public static class LauncherBannersManifestParser
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumDepth = 20;
    public static readonly TimeSpan MaximumRemoteAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly string[] Games = ["gi", "hsr", "zzz", "wuwa", "ae"];
    private static readonly string[] Regions = ["global", "america", "europe", "asia"];
    private static readonly LauncherOfficialTool[] ApprovedOfficialTools =
    [
        new("gi", "wiki", "Wiki", new Uri("https://wiki.hoyolab.com/pc/genshin/home")),
        new("gi", "material-calculator", "Material Calculator", new Uri("https://act.hoyolab.com/ys/event/calculator-sea/index.html")),
        new("gi", "battle-records", "Battle Records", new Uri("https://act.hoyolab.com/app/community-game-records-sea/index.html?gid=2#/ys")),
        new("gi", "upgrade-guide", "Upgrade Guide", new Uri("https://act.hoyolab.com/ys/event/bbs-lineup-ys-sea/index.html")),
        new("hsr", "wiki", "Wiki", new Uri("https://wiki.hoyolab.com/pc/hsr/home")),
        new("hsr", "material-calculator", "Material Calculator", new Uri("https://act.hoyolab.com/sr/event/calculator/index.html")),
        new("hsr", "battle-records", "Battle Records", new Uri("https://act.hoyolab.com/app/community-game-records-sea/index.html?gid=6#/hsr")),
        new("hsr", "upgrade-guide", "Upgrade Guide", new Uri("https://act.hoyolab.com/sr/event/cultivation-tool/#/tools/suggestion")),
        new("zzz", "wiki", "Wiki", new Uri("https://wiki.hoyolab.com/pc/zzz/home")),
        new("zzz", "battle-records", "Battle Records", new Uri("https://act.hoyolab.com/app/zzz-game-record/index.html")),
        new("ae", "wiki", "Wiki", new Uri("https://wiki.skport.com/endfield")),
        new("ae", "material-calculator", "Material Calculator", new Uri("https://game.skport.com/tools/endfield/cost-calculator?header=0")),
        new("ae", "team-recommendations", "Team Recommendations", new Uri("https://game.skport.com/tools/endfield/rec-team")),
    ];
    private static readonly IReadOnlyDictionary<string, string[]> OfficialHosts = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["gi"] = ["genshin.hoyoverse.com", "sg-hk4e-api.hoyoverse.com", "sg-hk4e-api.hoyolab.com"],
        ["hsr"] = ["honkai-star-rail.hoyoverse.com", "sg-hkrpg-api.hoyoverse.com", "sg-hkrpg-api.hoyolab.com"],
        ["zzz"] = ["zenless.hoyoverse.com", "sg-announcement-api.hoyoverse.com"],
        ["wuwa"] = ["wutheringwaves.kurogames.com"],
        ["ae"] = ["endfield.gryphline.com"],
    };

    public static LauncherBannersManifest Parse(byte[] payload, bool fallback = false, DateTimeOffset? observedAt = null)
    {
        using var document = ParseJson(payload);
        var root = document.RootElement;
        RequireProperties(root, "schemaVersion", "revision", "generatedAt", "health", "games");
        var version = RequiredInt(root, "schemaVersion");
        if (version != 1) throw new InvalidDataException("Unsupported launcher manifest schema.");
        var revision = RequiredText(root, "revision", 64);
        if (revision.Length != 64 || revision.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid launcher manifest revision.");
        var generatedAt = RequiredDate(root, "generatedAt");
        var observed = observedAt ?? DateTimeOffset.UtcNow;
        if (generatedAt > observed + MaximumFutureSkew
            || (!fallback && generatedAt < observed - MaximumRemoteAge))
            throw new InvalidDataException("Launcher manifest is outside the freshness window.");

        var health = ParseHealth(root.GetProperty("health"));
        var gameElement = root.GetProperty("games");
        if (gameElement.ValueKind is not JsonValueKind.Object) throw new InvalidDataException("Launcher manifest games must be an object.");
        var games = new Dictionary<string, LauncherBannersGame>(StringComparer.Ordinal);
        foreach (var game in Games)
        {
            if (!gameElement.TryGetProperty(game, out var value)) throw new InvalidDataException("Launcher manifest is missing a game.");
            games.Add(game, ParseGame(game, value, generatedAt));
        }
        if (gameElement.EnumerateObject().Any(entry => !Games.Contains(entry.Name, StringComparer.Ordinal))) throw new InvalidDataException("Launcher manifest has an unknown game.");
        foreach (var game in Games)
        {
            if (health.Games[game].NewsCount != games[game].News.Count)
                throw new InvalidDataException("Launcher health news count does not match the game content.");
        }
        var manifest = new LauncherBannersManifest(version, revision.ToLowerInvariant(), generatedAt, health, new ReadOnlyDictionary<string, LauncherBannersGame>(games));
        if (fallback) return manifest.ForDisplayAt(observed);
        if (health.Status != "ok" || health.Games.Values.Any(game => game.Status != "ok")) throw new InvalidDataException("Launcher manifest health is not safe for promotion.");
        foreach (var game in manifest.Games.Values)
        {
            if (game.Current is { } current && !(current.Start <= observed && (current.EffectiveEnd is null || observed < current.EffectiveEnd))) throw new InvalidDataException("Launcher current phase is not current.");
            if (game.Upcoming.Any(phase => !phase.Announced && phase.Start <= observed)) throw new InvalidDataException("Launcher upcoming phase is not in the future.");
        }
        return manifest;
    }

    public static LauncherCodesManifest ParseCodes(byte[] payload, bool fallback = false, DateTimeOffset? observedAt = null)
    {
        using var document = ParseJson(payload);
        var root = document.RootElement;
        RequireProperties(root, "schemaVersion", "revision", "generatedAt", "games");
        var version = RequiredInt(root, "schemaVersion");
        if (version != 1) throw new InvalidDataException("Unsupported launcher codes schema.");
        var revision = RequiredText(root, "revision", 64);
        if (revision.Length != 64 || revision.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid launcher codes revision.");
        var generatedAt = RequiredDate(root, "generatedAt");
        var observed = observedAt ?? DateTimeOffset.UtcNow;
        if (generatedAt > observed + MaximumFutureSkew
            || (!fallback && generatedAt < observed - MaximumRemoteAge))
            throw new InvalidDataException("Launcher codes are outside the freshness window.");
        var gamesElement = root.GetProperty("games");
        if (gamesElement.ValueKind is not JsonValueKind.Object) throw new InvalidDataException("Launcher codes games must be an object.");
        var games = new Dictionary<string, IReadOnlyList<LauncherRedemptionCode>>(StringComparer.Ordinal);
        foreach (var game in Games)
        {
            if (!gamesElement.TryGetProperty(game, out var codesElement)) throw new InvalidDataException("Launcher codes are missing a game.");
            games.Add(game, ParseCodes(codesElement));
        }
        if (gamesElement.EnumerateObject().Any(entry => !Games.Contains(entry.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("Launcher codes contain an unknown game.");
        return new LauncherCodesManifest(version, revision.ToLowerInvariant(), generatedAt, new ReadOnlyDictionary<string, IReadOnlyList<LauncherRedemptionCode>>(games));
    }

    public static LauncherToolsManifest ParseTools(byte[] payload, bool fallback = false, DateTimeOffset? observedAt = null)
    {
        using var document = ParseJson(payload);
        var root = document.RootElement;
        RequireProperties(root, "schemaVersion", "generatedAt", "tools");
        var version = RequiredInt(root, "schemaVersion");
        if (version != 1) throw new InvalidDataException("Unsupported launcher tools schema.");
        var generatedAt = RequiredDate(root, "generatedAt");
        var observed = observedAt ?? DateTimeOffset.UtcNow;
        if (generatedAt > observed + MaximumFutureSkew
            || (!fallback && generatedAt < observed - MaximumRemoteAge))
            throw new InvalidDataException("Launcher tools are outside the freshness window.");
        if (!root.TryGetProperty("tools", out var toolsElement)
            || toolsElement.ValueKind is not JsonValueKind.Array
            || toolsElement.GetArrayLength() > ApprovedOfficialTools.Length)
            throw new InvalidDataException("Invalid launcher tools.");

        var selected = new HashSet<(string Game, string Id)>();
        foreach (var item in toolsElement.EnumerateArray())
        {
            RequireProperties(item, "game", "id", "label", "url");
            var game = RequiredExactText(item, "game", 8);
            var id = RequiredExactText(item, "id", 64);
            var label = RequiredExactText(item, "label", 80);
            var rawUrl = RequiredExactText(item, "url", 2048);
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url)
                || !IsApprovedOfficialTool(game, id, label, url))
                throw new InvalidDataException("Launcher tool is not approved.");
            if (!selected.Add((game, id))) throw new InvalidDataException("Duplicate launcher tool.");
        }

        return new LauncherToolsManifest(
            version,
            generatedAt,
            ApprovedOfficialTools.Where(tool => selected.Contains((tool.Game, tool.Id))).ToArray());
    }

    public static bool IsApprovedOfficialTool(string game, string id, string label, Uri url) =>
        url is not null
        && ApprovedOfficialTools.Any(tool =>
            string.Equals(tool.Game, game, StringComparison.Ordinal)
            && string.Equals(tool.Id, id, StringComparison.Ordinal)
            && string.Equals(tool.Label, label, StringComparison.Ordinal)
            && string.Equals(tool.Url.OriginalString, url.OriginalString, StringComparison.Ordinal));

    private static LauncherBannersHealth ParseHealth(JsonElement element)
    {
        RequireProperties(element, "status", "games");
        var status = RequiredText(element, "status", 16);
        if (status is not ("ok" or "degraded" or "unavailable")) throw new InvalidDataException("Invalid launcher health status.");
        var entries = element.GetProperty("games");
        if (entries.ValueKind is not JsonValueKind.Object) throw new InvalidDataException("Invalid launcher game health.");
        var games = new Dictionary<string, LauncherBannersGameHealth>(StringComparer.Ordinal);
        foreach (var game in entries.EnumerateObject())
        {
            if (!Games.Contains(game.Name, StringComparer.Ordinal)) throw new InvalidDataException("Unknown launcher health game.");
            RequireProperties(game.Value, "status", "reason", "newsCount");
            var gameStatus = RequiredText(game.Value, "status", 16);
            if (gameStatus is not ("ok" or "degraded" or "missing")) throw new InvalidDataException("Invalid launcher game health status.");
            var reason = NullableText(game.Value, "reason", 64);
            var count = RequiredInt(game.Value, "newsCount");
            games.Add(game.Name, new LauncherBannersGameHealth(gameStatus, reason, count));
        }
        if (games.Count != 5) throw new InvalidDataException("Launcher health must cover all five games.");
        return new LauncherBannersHealth(status, games);
    }

    private static LauncherBannersGame ParseGame(string game, JsonElement element, DateTimeOffset generatedAt)
    {
        RequireProperties(element, "game", "region", "current", "upcoming", "collections", "news", "codes");
        var region = RequiredText(element, "region", 16);
        if (RequiredText(element, "game", 8) != game || !Regions.Contains(region, StringComparer.Ordinal)) throw new InvalidDataException("Launcher game identity mismatch.");
        LauncherBannersCurrentPhase? current = null;
        var currentElement = element.GetProperty("current");
        if (currentElement.ValueKind is JsonValueKind.Object)
        {
            current = ParseCurrent(game, currentElement, generatedAt);
        }
        else if (currentElement.ValueKind is not JsonValueKind.Null) throw new InvalidDataException("Invalid launcher current phase.");
        var newsElement = element.GetProperty("news");
        if (newsElement.ValueKind is not JsonValueKind.Array || newsElement.GetArrayLength() > 32) throw new InvalidDataException("Invalid launcher news.");
        var news = new List<LauncherBannersNewsItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in newsElement.EnumerateArray())
        {
            RequireProperties(item, "id", "title", "type", "start", "end", "url");
            var id = RequiredText(item, "id", 100);
            if (!ids.Add(id)) throw new InvalidDataException("Duplicate launcher news id.");
            var title = RequiredText(item, "title", 180);
            var type = RequiredText(item, "type", 32);
            var start = NullableDate(item, "start");
            var end = NullableDate(item, "end");
            if (start is not null && end is not null && end <= start) throw new InvalidDataException("Invalid launcher news window.");
            var rawUrl = NullableText(item, "url", 2048);
            var approved = rawUrl is null ? null : TryOfficialUrl(rawUrl, game);
            news.Add(new LauncherBannersNewsItem(id, title, type, start, end, rawUrl, approved, approved is not null));
        }
        var upcoming = new List<LauncherBannersUpcomingPhase>();
        if (element.TryGetProperty("upcoming", out var upcomingElement))
        {
            if (upcomingElement.ValueKind is not JsonValueKind.Array || upcomingElement.GetArrayLength() > 5)
                throw new InvalidDataException("Invalid launcher upcoming phases.");
            foreach (var phaseElement in upcomingElement.EnumerateArray())
            {
                RequireProperties(phaseElement, "phase", "announced", "start", "end", "characters");
                var announced = NullableBool(phaseElement, "announced") ?? false;
                var phaseStart = NullableDate(phaseElement, "start");
                var phaseEnd = NullableDate(phaseElement, "end");
                if (announced ? phaseStart is not null || phaseEnd is not null : phaseStart is null || phaseEnd is null || phaseEnd <= phaseStart)
                    throw new InvalidDataException("Invalid launcher upcoming window.");
                var charactersElement = phaseElement.GetProperty("characters");
                if (charactersElement.ValueKind is not JsonValueKind.Array || charactersElement.GetArrayLength() is < 1 or > 20)
                    throw new InvalidDataException("Invalid launcher upcoming characters.");
                var characters = charactersElement.EnumerateArray().Select(character => ParseCharacter(game, character)).ToArray();
                if (characters.Any(character => character.Icon?.Url is null))
                    throw new InvalidDataException("Launcher upcoming characters require downloadable icons.");
                upcoming.Add(new LauncherBannersUpcomingPhase(
                    NullableText(phaseElement, "phase", 48),
                    phaseStart,
                    phaseEnd,
                    characters,
                    announced));
            }
        }
        IReadOnlyList<LauncherRedemptionCode> codes = [];
        if (element.TryGetProperty("codes", out var codesElement))
        {
            codes = ParseCodes(codesElement);
        }
        if (!element.TryGetProperty("collections", out var collectionsElement)
            || collectionsElement.ValueKind is not JsonValueKind.Array
            || collectionsElement.GetArrayLength() != 0)
            throw new InvalidDataException("Launcher banner collections must be empty.");
        return new LauncherBannersGame(game, region, current, news, upcoming, codes);
    }

    private static IReadOnlyList<LauncherRedemptionCode> ParseCodes(JsonElement codesElement)
    {
        if (codesElement.ValueKind is not JsonValueKind.Array || codesElement.GetArrayLength() > 5)
            throw new InvalidDataException("Invalid launcher redemption codes.");
        var codes = new List<LauncherRedemptionCode>();
        var codeValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var codeElement in codesElement.EnumerateArray())
        {
            RequireProperties(codeElement, "code", "added", "amount", "currency");
            var code = RequiredText(codeElement, "code", 64);
            if (code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
                || !codeValues.Add(code))
                throw new InvalidDataException("Invalid launcher redemption code.");
            var addedText = RequiredText(codeElement, "added", 10);
            if (!DateOnly.TryParseExact(addedText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var added))
                throw new InvalidDataException("Invalid launcher redemption code date.");
            var amount = codeElement.TryGetProperty("amount", out _) ? RequiredInt(codeElement, "amount") : 0;
            if (amount is < 0 or > 100000) throw new InvalidDataException("Invalid premium currency amount.");
            var currency = codeElement.TryGetProperty("currency", out _)
                ? RequiredText(codeElement, "currency", 32)
                : string.Empty;
            if ((amount == 0) != string.IsNullOrEmpty(currency))
                throw new InvalidDataException("Incomplete premium currency metadata.");
            codes.Add(new LauncherRedemptionCode(code, added, amount, currency));
        }
        return codes;
    }

    private static LauncherBannersCurrentPhase ParseCurrent(string game, JsonElement element, DateTimeOffset generatedAt)
    {
        RequireProperties(element, "phase", "start", "end", "nextChangeAt", "timingMode", "channels", "remaining", "characters", "selectedCharacter", "selectedCharacterId", "selectionReason", "variants");
        var phase = NullableText(element, "phase", 48);
        var start = RequiredDate(element, "start");
        var end = NullableDate(element, "end");
        var nextChangeAt = element.TryGetProperty("nextChangeAt", out _)
            ? NullableDate(element, "nextChangeAt")
            : end;
        var timingMode = element.TryGetProperty("timingMode", out _)
            ? RequiredText(element, "timingMode", 16)
            : end is null ? "ongoing" : "shared-end";
        if (timingMode is not ("shared-end" or "next-change" or "ongoing")) throw new InvalidDataException("Invalid launcher banner timing mode.");
        var remaining = element.GetProperty("remaining");
        RequireProperties(remaining, "startsAt", "endsAt", "durationSeconds");
        if (RequiredDate(remaining, "startsAt") != start || NullableDate(remaining, "endsAt") != nextChangeAt) throw new InvalidDataException("Launcher remaining bounds mismatch.");
        var remainingSeconds = RequiredLong(remaining, "durationSeconds");
        if ((end is not null && end <= start)
            || (nextChangeAt is not null && nextChangeAt <= start)
            || (end is not null && nextChangeAt is not null && nextChangeAt.Value > end.Value)
            || remainingSeconds < 0) throw new InvalidDataException("Invalid launcher current window.");
        var expectedRemainingSeconds = nextChangeAt is null
            ? 0
            : Math.Max(0, (long)Math.Floor((nextChangeAt.Value - generatedAt).TotalSeconds));
        if (remainingSeconds != expectedRemainingSeconds) throw new InvalidDataException("Launcher remaining countdown mismatch.");
        var charsElement = element.GetProperty("characters");
        if (charsElement.ValueKind is not JsonValueKind.Array || charsElement.GetArrayLength() is < 1 or > 20) throw new InvalidDataException("Invalid launcher characters.");
        var characters = charsElement.EnumerateArray().Select(character => ParseCharacter(game, character)).ToArray();
        if (characters.Any(character => character.Icon?.Url is null))
            throw new InvalidDataException("Launcher current characters require downloadable icons.");
        IReadOnlyList<LauncherBannersChannel> channels;
        if (element.TryGetProperty("channels", out var channelsElement))
        {
            if (channelsElement.ValueKind is not JsonValueKind.Array || channelsElement.GetArrayLength() is < 1 or > 20)
                throw new InvalidDataException("Invalid launcher banner channels.");
            channels = channelsElement.EnumerateArray().Select(ParseChannel).ToArray();
        }
        else
        {
            channels = characters.Length == 0
                ? []
                :
                [
                    new LauncherBannersChannel(
                        $"legacy:{game}:{start:yyyyMMddHHmmss}",
                        "Character Event",
                        start,
                        end,
                        characters.Select(character => character.Name).ToArray()),
                ];
        }
        var selectedId = NullableText(element, "selectedCharacterId", 96);
        var selectedElement = element.GetProperty("selectedCharacter");
        if (selectedElement.ValueKind is not JsonValueKind.Object || selectedId is null) throw new InvalidDataException("Invalid selected launcher character.");
        var selected = ParseCharacter(game, selectedElement);
        var rosterCharacter = characters.SingleOrDefault(character => character.Id == selectedId);
        if (rosterCharacter is null || !CharacterMatches(selected, rosterCharacter)) throw new InvalidDataException("Selected launcher character mismatch.");
        var selectionReason = NullableText(element, "selectionReason", 64);
        var variants = ParseAssets(game, element.GetProperty("variants"));
        if (variants.Count == 0 || variants.Any(asset => asset.Url is null))
            throw new InvalidDataException("Launcher current phase requires downloadable art.");
        return new LauncherBannersCurrentPhase(phase, start, end, remainingSeconds, characters, selectedId, selectionReason, variants, channels, nextChangeAt, timingMode);
    }

    private static bool CharacterMatches(LauncherBannersCharacter left, LauncherBannersCharacter right) =>
        left.Id == right.Id
        && left.Name == right.Name
        && left.Rarity == right.Rarity
        && left.Limited == right.Limited
        && left.Debut == right.Debut
        && AssetMatches(left.Icon, right.Icon)
        && left.Variants.Count == right.Variants.Count
        && left.Variants.Zip(right.Variants).All(pair => AssetMatches(pair.First, pair.Second));

    private static bool AssetMatches(LauncherBannersAsset? left, LauncherBannersAsset? right) =>
        left is null ? right is null : right is not null
            && left.Id == right.Id
            && left.Path == right.Path
            && left.Url == right.Url
            && left.Mime == right.Mime
            && left.Size == right.Size
            && left.Sha256 == right.Sha256;

    private static LauncherBannersChannel ParseChannel(JsonElement element)
    {
        RequireProperties(element, "recordId", "category", "start", "end", "characters");
        var namesElement = element.GetProperty("characters");
        if (namesElement.ValueKind is not JsonValueKind.Array || namesElement.GetArrayLength() is < 1 or > 20)
            throw new InvalidDataException("Invalid launcher banner channel characters.");
        var names = namesElement.EnumerateArray()
            .Select(value =>
            {
                if (value.ValueKind is not JsonValueKind.String) throw new InvalidDataException("Invalid launcher banner channel character.");
                var name = value.GetString()?.Trim() ?? string.Empty;
                if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new InvalidDataException("Invalid launcher banner channel character.");
                return name;
            })
            .ToArray();
        return new LauncherBannersChannel(
            RequiredText(element, "recordId", 240),
            RequiredText(element, "category", 64),
            RequiredDate(element, "start"),
            NullableDate(element, "end"),
            names);
    }

    private static LauncherBannersCharacter ParseCharacter(string game, JsonElement element)
    {
        RequireProperties(element, "id", "name", "rarity", "limited", "debut", "characterUrl", "icon", "variants");
        var rarity = NullableInt(element, "rarity");
        if (rarity is < 1 or > 6) throw new InvalidDataException("Invalid launcher character rarity.");
        var limited = NullableBool(element, "limited");
        var debut = NullableDate(element, "debut");
        LauncherBannersAsset? icon = null;
        if (element.TryGetProperty("icon", out var iconElement)
            && iconElement.ValueKind is not JsonValueKind.Null)
        {
            icon = ParseAsset(game, iconElement);
        }
        Uri? characterUrl = null;
        var rawCharacterUrl = NullableText(element, "characterUrl", 2048);
        if (rawCharacterUrl is not null
            && (!Uri.TryCreate(rawCharacterUrl, UriKind.Absolute, out characterUrl)
                || characterUrl.Scheme != Uri.UriSchemeHttps
                || !characterUrl.IsDefaultPort
                || !string.IsNullOrEmpty(characterUrl.UserInfo)
                || !characterUrl.Host.Equals("pengo.gg", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unsafe launcher character URL.");
        return new LauncherBannersCharacter(
            RequiredText(element, "id", 96),
            RequiredText(element, "name", 80),
            rarity,
            limited,
            debut,
            ParseAssets(game, element.GetProperty("variants")),
            icon,
            characterUrl);
    }

    private static IReadOnlyList<LauncherBannersAsset> ParseAssets(string game, JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Array || element.GetArrayLength() > 32) throw new InvalidDataException("Invalid launcher assets.");
        var assets = new List<LauncherBannersAsset>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            var asset = ParseAsset(game, item);
            var id = asset.Id;
            if (!ids.Add(id)) throw new InvalidDataException("Duplicate launcher asset id.");
            assets.Add(asset);
        }
        return assets;
    }

    private static LauncherBannersAsset ParseAsset(string game, JsonElement item)
    {
        RequireProperties(item, "id", "source", "path", "url", "mime", "size", "dimensions", "sha256", "transparentBounds", "placement", "alphaCentroid", "opaqueOccupancy", "edgeCoverage", "alphaCoverage");
        var id = RequiredText(item, "id", 128);
        var path = RequiredText(item, "path", 512);
        if (!path.StartsWith('/') || path.Contains('\\') || path[1..].Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException("Unsafe launcher asset path.");
        var sha256 = RequiredText(item, "sha256", 64);
        if (sha256.Length != 64 || sha256.Any(character => !char.IsAsciiHexDigit(character)) || sha256.Any(char.IsUpper))
            throw new InvalidDataException("Invalid launcher asset hash.");
        var rawUrl = NullableText(item, "url", 2048);
        Uri? url = null;
        if (rawUrl is not null)
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed)
                || !LauncherBannersTransport.IsApprovedManifestAssetEndpoint(parsed)) throw new InvalidDataException("Unsafe launcher asset URL.");
            var expectedUrl = parsed.Host.Equals("assets.pengo.gg", StringComparison.OrdinalIgnoreCase)
                ? $"https://assets.pengo.gg/legacy{path}"
                : $"https://pengo.gg/dist/launcher-art/{sha256}.webp";
            if (!string.Equals(rawUrl, expectedUrl, StringComparison.Ordinal))
                throw new InvalidDataException("Launcher asset URL does not match its identity.");
            if (parsed.Host.Equals("pengo.gg", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, $"/launcher-art/{sha256}.webp", StringComparison.Ordinal))
                throw new InvalidDataException("Launcher asset path does not match its identity.");
            url = parsed;
        }
        var dimensions = ParseDimensions(item.GetProperty("dimensions"));
        var bounds = ParseBounds(item.GetProperty("transparentBounds"), dimensions);
        var placement = ParsePlacement(item.GetProperty("placement"));
        var centroid = item.TryGetProperty("alphaCentroid", out var centroidElement)
            ? ParsePoint(centroidElement)
            : null;
        double? occupancy = item.TryGetProperty("opaqueOccupancy", out _)
            ? RequiredDouble(item, "opaqueOccupancy")
            : null;
        var edgeCoverage = item.TryGetProperty("edgeCoverage", out var edgeElement)
            ? ParseEdgeCoverage(edgeElement)
            : null;
        var alphaCoverage = item.TryGetProperty("alphaCoverage", out var coverageElement)
            ? ParseAlphaCoverage(coverageElement)
            : null;
        var size = RequiredLong(item, "size");
        if (size is <= 0 or > LauncherBannersTransport.MaximumAssetBytes) throw new InvalidDataException("Invalid launcher asset size.");
        var mime = RequiredText(item, "mime", 16);
        if (mime is not ("image/webp" or "image/png")) throw new InvalidDataException("Invalid launcher asset MIME.");
        return new LauncherBannersAsset(id, RequiredText(item, "source", 64), path, url, mime, size, dimensions, sha256, bounds, placement, centroid, occupancy, edgeCoverage, alphaCoverage);
    }

    private static LauncherBannersPoint ParsePoint(JsonElement element)
    {
        RequireProperties(element, "x", "y");
        var x = RequiredDouble(element, "x");
        var y = RequiredDouble(element, "y");
        if (x is < 0 or > 1 || y is < 0 or > 1) throw new InvalidDataException("Invalid launcher alpha centroid.");
        return new LauncherBannersPoint(x, y);
    }

    private static LauncherBannersEdgeCoverage ParseEdgeCoverage(JsonElement element)
    {
        RequireProperties(element, "top", "right", "bottom", "left");
        var result = new LauncherBannersEdgeCoverage(
            RequiredDouble(element, "top"),
            RequiredDouble(element, "right"),
            RequiredDouble(element, "bottom"),
            RequiredDouble(element, "left"));
        if (result.Top is < 0 or > 1 || result.Right is < 0 or > 1 || result.Bottom is < 0 or > 1 || result.Left is < 0 or > 1)
            throw new InvalidDataException("Invalid launcher edge coverage.");
        return result;
    }

    private static LauncherBannersAlphaCoverage ParseAlphaCoverage(JsonElement element)
    {
        RequireProperties(element, "width", "height", "cells");
        var width = RequiredInt(element, "width");
        var height = RequiredInt(element, "height");
        if (width is < 1 or > 64 || height is < 1 or > 64) throw new InvalidDataException("Invalid launcher alpha coverage dimensions.");
        var encoded = RequiredText(element, "cells", 8192);
        byte[] cells;
        try
        {
            cells = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Invalid launcher alpha coverage.", exception);
        }
        if (cells.Length != width * height) throw new InvalidDataException("Invalid launcher alpha coverage length.");
        return new LauncherBannersAlphaCoverage(width, height, cells);
    }

    private static LauncherBannersDimensions ParseDimensions(JsonElement element)
    {
        RequireProperties(element, "width", "height");
        var width = RequiredInt(element, "width");
        var height = RequiredInt(element, "height");
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new InvalidDataException("Invalid launcher image dimensions.");
        return new(width, height);
    }

    private static LauncherBannersBounds ParseBounds(JsonElement element, LauncherBannersDimensions dimensions)
    {
        RequireProperties(element, "left", "top", "right", "bottom");
        var bounds = new LauncherBannersBounds(RequiredInt(element, "left"), RequiredInt(element, "top"), RequiredInt(element, "right"), RequiredInt(element, "bottom"));
        if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > dimensions.Width || bounds.Bottom > dimensions.Height || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) throw new InvalidDataException("Invalid launcher transparent bounds.");
        return bounds;
    }

    private static LauncherBannersPlacement ParsePlacement(JsonElement element)
    {
        RequireProperties(element, "anchor", "fit", "x", "y");
        var x = RequiredDouble(element, "x");
        var y = RequiredDouble(element, "y");
        if (x is < 0 or > 1 || y is < 0 or > 1) throw new InvalidDataException("Invalid launcher placement.");
        return new(RequiredText(element, "anchor", 32), RequiredText(element, "fit", 32), x, y);
    }

    private static JsonDocument ParseJson(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0 || payload.Length > MaximumBytes) throw new InvalidDataException("Launcher manifest exceeds the byte limit.");
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaximumDepth });
        var objects = new Stack<HashSet<string>>();
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType is JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType is JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw new InvalidDataException("Duplicate launcher manifest property.");
            }
            return JsonDocument.Parse(payload, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaximumDepth });
        }
        catch (JsonException exception) { throw new InvalidDataException("Invalid launcher manifest JSON.", exception); }
    }

    private static void RequireProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind is not JsonValueKind.Object || element.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal))) throw new InvalidDataException("Unexpected launcher manifest field.");
    }

    private static string RequiredText(JsonElement element, string name, int max) { if (!element.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.String) throw new InvalidDataException($"Missing launcher field: {name}."); var text = value.GetString()?.Trim() ?? ""; if (text.Length == 0 || text.Length > max || text.Any(char.IsControl)) throw new InvalidDataException($"Invalid launcher field: {name}."); return text; }
    private static string RequiredExactText(JsonElement element, string name, int max) { var text = RequiredText(element, name, max); if (!string.Equals(element.GetProperty(name).GetString(), text, StringComparison.Ordinal)) throw new InvalidDataException($"Invalid launcher field: {name}."); return text; }
    private static string? NullableText(JsonElement element, string name, int max) { if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null) return null; var text = RequiredText(element, name, max); return text; }
    private static int RequiredInt(JsonElement element, string name) { if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result)) throw new InvalidDataException($"Invalid launcher integer: {name}."); return result; }
    private static long RequiredLong(JsonElement element, string name) { if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var result)) throw new InvalidDataException($"Invalid launcher integer: {name}."); return result; }
    private static int? NullableInt(JsonElement element, string name) { if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null) return null; return RequiredInt(element, name); }
    private static bool? NullableBool(JsonElement element, string name) { if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null) return null; if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new InvalidDataException($"Invalid launcher boolean: {name}."); return value.GetBoolean(); }
    private static double RequiredDouble(JsonElement element, string name) { if (!element.TryGetProperty(name, out var value) || !value.TryGetDouble(out var result) || double.IsNaN(result) || double.IsInfinity(result)) throw new InvalidDataException($"Invalid launcher number: {name}."); return result; }
    private static DateTimeOffset RequiredDate(JsonElement element, string name) { var text = RequiredText(element, name, 40); if (!TryDate(text, out var result)) throw new InvalidDataException($"Invalid launcher date: {name}."); return result; }
    private static DateTimeOffset? NullableDate(JsonElement element, string name) { if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null) return null; return RequiredDate(element, name); }
    private static bool TryDate(string text, out DateTimeOffset result) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
    private static Uri? TryOfficialUrl(string raw, string game) => Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && uri.IsDefaultPort && OfficialHosts[game].Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) ? uri : null;
}

// Short contract name used by callers that do not need to distinguish the
// schema parser from the manifest model.
public static class LauncherBannersParser
{
    public static LauncherBannersManifest Parse(byte[] payload, bool fallback = false, DateTimeOffset? observedAt = null) =>
        LauncherBannersManifestParser.Parse(payload, fallback, observedAt);
}
