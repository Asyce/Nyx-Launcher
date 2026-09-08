using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabHsrBuildReadStatus
{
    Completed,
    NotEnabled,
    LocalStorageUnavailable,
    NeedsReview,
    LoginRequired,
    Canceled,
    TimedOut,
    TooLarge,
}

public sealed record HoyoLabHsrBuildReadResult(HoyoLabHsrBuildReadStatus Status, HoyoLabHsrBuildSnapshot? Snapshot = null)
{
    public override string ToString() => nameof(HoyoLabHsrBuildReadResult);
}

/// <summary>Bounded, read-only capture in the existing selected-role publisher session.</summary>
public static class HoyoLabHsrBuildCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 45;

    public static HoyoLabHsrBuildReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabHsrBuildReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabHsrBuildReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabHsrBuildReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabHsrBuildReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabHsrBuildReadStatus.LoginRequired,
                    "canceled" => HoyoLabHsrBuildReadStatus.Canceled,
                    "timed-out" => HoyoLabHsrBuildReadStatus.TimedOut,
                    "too-large" => HoyoLabHsrBuildReadStatus.TooLarge,
                    _ => HoyoLabHsrBuildReadStatus.NeedsReview,
                } : HoyoLabHsrBuildReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "characters"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || !root.GetProperty("count").TryGetInt32(out var count)
                || root.GetProperty("characters").ValueKind != JsonValueKind.Array
                || root.GetProperty("characters").GetArrayLength() != count)
                return new(HoyoLabHsrBuildReadStatus.NeedsReview);
            var snapshot = new HoyoLabHsrBuildSnapshot(root.GetProperty("characters"));
            return HoyoLabHsrBuildRules.IsValid(snapshot)
                ? new(HoyoLabHsrBuildReadStatus.Completed, HoyoLabHsrBuildRules.Normalize(snapshot))
                : new(HoyoLabHsrBuildReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabHsrBuildReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("hsr", role))
            throw new ArgumentException("Invalid Star Rail role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxHsrBuilds_", StringComparison.Ordinal)
            || controllerKey.Length > 80 || controllerKey.Any(static value => value != '_' && !char.IsAsciiLetterOrDigit(value)))
            throw new ArgumentException("Invalid capture key.", nameof(controllerKey));
        var contract = PublisherAccountCatalog.GetResourceFetchContract("hsr");
        var configuration = JsonSerializer.Serialize(new
        {
            key = controllerKey,
            roleId = role.RoleId,
            server = role.Server,
            roleEndpoint = contract.RoleDiscoveryEndpoint.AbsoluteUri,
            gameBiz = contract.GameBusiness,
            maximumCharacters = HoyoLabHsrBuildRules.MaximumCharacters,
            maximumResultBytes = MaximumResultBytes,
            timeoutMilliseconds = TimeoutSeconds * 1000,
        });
        return $$"""
        (() => {
          const config = {{configuration}};
          if (Object.hasOwn(window, config.key)) return 'busy';
          const controller = new AbortController();
          const state = { result: null, abort: () => controller.abort() };
          Object.defineProperty(window, config.key, { configurable: true, value: state });
          const failure = code => { throw code; };
          const plain = value => value !== null && typeof value === 'object' && !Array.isArray(value)
            && (Object.getPrototypeOf(value) === Object.prototype || Object.getPrototypeOf(value) === null);
          const integer = (value, minimum = 0) => Number.isInteger(value) && value >= minimum && value <= 2147483647
            ? value : failure('needs-review');
          const flag = value => typeof value === 'boolean' ? value : failure('needs-review');
          const rows = (value, maximum) => Array.isArray(value) && value.length <= maximum ? value : failure('needs-review');
          const text = (value, minimum = 0, maximum = 64) => typeof value === 'string'
            && value.length >= minimum && value.length <= maximum && value === value.trim()
            && !/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(value) ? value : failure('needs-review');
          const optionalDisplay = value => value === undefined || value === null || value === '' ? null : text(value, 1, 80);
          const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/';
          const recordUrl = path => {
            const url = new URL(recordBase + path);
            url.searchParams.set('server', config.server);
            url.searchParams.set('role_id', config.roleId);
            if (path === 'avatar/info') url.searchParams.set('need_wiki', 'true');
            return url.href;
          };
          let totalBytes = 0;
          let timedOut = false;
          const timeout = setTimeout(() => { timedOut = true; controller.abort(); }, config.timeoutMilliseconds);
          function current() {
            if (controller.signal.aborted || window[config.key] !== state)
              failure(timedOut ? 'timed-out' : 'canceled');
          }
          async function request(url) {
            current();
            await new Promise(resolve => setTimeout(resolve, 250));
            current();
            const requestController = new AbortController();
            const abort = () => requestController.abort();
            controller.signal.addEventListener('abort', abort, { once: true });
            let requestTimedOut = false;
            const requestTimeout = setTimeout(() => { requestTimedOut = true; abort(); }, 8000);
            let reader = null;
            let body = '';
            try {
              const response = await fetch(url, { method: 'GET', credentials: 'include', redirect: 'error',
                cache: 'no-store', referrerPolicy: 'no-referrer', headers: { 'x-rpc-language': 'en-us' }, signal: requestController.signal });
              current();
              if (response.status === 401) failure('login-required');
              if (response.status !== 200 || response.url !== url || response.redirected
                || response.headers.get('content-type')?.split(';')[0].trim().toLowerCase() !== 'application/json'
                || !response.body) failure('needs-review');
              reader = response.body.getReader();
              const decoder = new TextDecoder('utf-8', { fatal: true });
              let bytes = 0;
              while (true) {
                const part = await reader.read();
                try {
                  current();
                  if (part.done) break;
                  if (!(part.value instanceof Uint8Array)) failure('needs-review');
                  bytes += part.value.byteLength;
                  totalBytes += part.value.byteLength;
                  if (bytes > 8388608 || totalBytes > 25165824) failure('too-large');
                  body += decoder.decode(part.value, { stream: true });
                } finally { if (part.value instanceof Uint8Array) part.value.fill(0); }
              }
              body += decoder.decode();
              const result = JSON.parse(body);
              if (!plain(result) || !Number.isInteger(result.retcode)) failure('needs-review');
              if (result.retcode === -100 || result.retcode === 10001) failure('login-required');
              if (result.retcode !== 0 || !plain(result.data)) failure('needs-review');
              return result.data;
            } catch (error) {
              if (requestTimedOut) failure('timed-out');
              throw error;
            } finally {
              body = '';
              if (reader) { try { await reader.cancel(); } catch {} reader.releaseLock(); }
              clearTimeout(requestTimeout);
              controller.signal.removeEventListener('abort', abort);
            }
          }
          function unique(list, key) {
            const seen = new Set();
            for (const item of list) {
              if (seen.has(item[key])) failure('needs-review');
              seen.add(item[key]);
            }
            return list;
          }
          function propertyName(id, map) {
            if (!plain(map) || !Object.hasOwn(map, String(id)) || !plain(map[id])
              || map[id].property_type !== id) failure('needs-review');
            return text(map[id].name, 1, 128);
          }
          function property(raw, map) {
            if (!plain(raw)) failure('needs-review');
            const id = integer(raw.property_type, 1);
            return { id, name: propertyName(id, map), base: optionalDisplay(raw.base),
              added: optionalDisplay(raw.add), final: text(raw.final, 1, 80) };
          }
          function relicProperty(raw, map) {
            if (!plain(raw)) failure('needs-review');
            const id = integer(raw.property_type, 1);
            return { id, name: propertyName(id, map), value: text(raw.value, 1, 80),
              times: integer(raw.times), preview: flag(raw.is_preview) };
          }
          function relic(raw, map, minimum, maximum) {
            if (!plain(raw)) failure('needs-review');
            const slot = integer(raw.pos, minimum);
            if (slot > maximum) failure('needs-review');
            return { id: integer(raw.id, 1), name: text(raw.name, 1, 256), slot,
              rarity: integer(raw.rarity, 1), level: integer(raw.level), main: relicProperty(raw.main_property, map),
              sub: unique(rows(raw.properties, 4).map(value => relicProperty(value, map)), 'id') };
          }
          function trace(raw) {
            if (!plain(raw)) failure('needs-review');
            return { id: text(raw.point_id, 1), type: integer(raw.point_type), level: integer(raw.level),
              active: flag(raw.is_activated), rankWorks: flag(raw.is_rank_work), parent: text(raw.pre_point),
              anchor: text(raw.anchor), specialType: text(raw.special_point_type),
              stages: rows(raw.skill_stages, 64).map(stage => {
                if (!plain(stage) || !plain(stage.exclusive_skill)) failure('needs-review');
                return { id: text(stage.skill_id, 1), name: text(stage.name, 0, 256), level: integer(stage.level),
                  active: flag(stage.is_activated), rankWorks: flag(stage.is_rank_work),
                  specialType: text(stage.special_point_type),
                  exclusiveName: Object.keys(stage.exclusive_skill).length === 0 ? null : text(stage.exclusive_skill.name, 0, 256),
                  linkedAvatars: stage.linked_avatar_list === undefined ? null
                    : unique(rows(stage.linked_avatar_list, 32).map(item => ({ id: text(item.avatar_id, 1), name: text(item.name, 1, 256) })), 'id'),
                  linkedAvatar: stage.linked_avatar === undefined ? null
                    : { id: text(stage.linked_avatar.avatar_id, 1), name: text(stage.linked_avatar.name, 1, 256) },
                  linkedSkillId: stage.linked_skill_id === undefined ? null : text(stage.linked_skill_id, 1),
                  elationPriority: stage.elation_skill_priority === undefined ? null : text(stage.elation_skill_priority) };
              }) };
          }
          function project(raw, map) {
            if (!plain(raw) || !plain(raw.servant_detail)) failure('needs-review');
            const equipment = raw.equip;
            if (equipment !== null && !plain(equipment)) failure('needs-review');
            let lightCone = null;
            if (equipment !== null && Object.keys(equipment).length !== 0)
              lightCone = { id: integer(equipment.id, 1), name: text(equipment.name, 1, 256), level: integer(equipment.level, 1),
                rarity: integer(equipment.rarity, 1), rank: integer(equipment.rank, 1) };
            const servant = raw.servant_detail;
            if (typeof servant.servant_id !== 'string' || !/^(?:0|[1-9][0-9]*)$/.test(servant.servant_id)) failure('needs-review');
            const servantId = integer(Number(servant.servant_id));
            let memosprite = null;
            if (servantId > 0)
              memosprite = { id: servantId, name: text(servant.servant_name, 1, 256), healthHidden: flag(servant.is_health_secret),
                properties: unique(rows(servant.servant_properties, 128).map(value => property(value, map)), 'id'),
                traces: unique(rows(servant.servant_skills, 128).map(trace), 'id') };
            else if (rows(servant.servant_properties, 128).length || rows(servant.servant_skills, 128).length)
              failure('needs-review');
            return { id: integer(raw.id, 1), name: text(raw.name, 1, 256), level: integer(raw.level, 1),
              rarity: integer(raw.rarity, 1), element: text(raw.element, 1, 32), path: integer(raw.base_type, 1),
              rank: integer(raw.rank), enhancedId: integer(raw.cur_enhanced_id), avatarType: text(raw.avatar_ld_type), lightCone,
              eidolons: unique(unique(rows(raw.ranks, 64).map(item => ({ id: integer(item.id, 1), name: text(item.name, 1, 256),
                position: integer(item.pos, 1), active: flag(item.is_unlocked) })), 'id'), 'position'),
              relics: unique(rows(raw.relics, 4).map(value => relic(value, map, 1, 4)), 'slot'),
              ornaments: unique(rows(raw.ornaments, 2).map(value => relic(value, map, 5, 6)), 'slot'),
              properties: unique(rows(raw.properties, 128).map(value => property(value, map)), 'id'),
              traces: unique(rows(raw.skills, 128).map(trace), 'id'),
              specialTraces: unique(rows(raw.special_skills, 128).map(trace), 'id'), memosprite };
          }
          async function verifyRole() {
            const url = new URL(config.roleEndpoint);
            url.searchParams.set('game_biz', config.gameBiz);
            url.searchParams.set('region', config.server);
            const bindings = new Set();
            for (const role of rows((await request(url.href)).list, 8)) {
              if (!plain(role) || role.game_biz !== config.gameBiz || role.region !== config.server
                || typeof role.game_uid !== 'string' || !/^[1-9][0-9]{0,19}$/.test(role.game_uid)
                || bindings.has(role.game_uid)) failure('needs-review');
              bindings.add(role.game_uid);
            }
            if (!bindings.has(config.roleId)) failure('needs-review');
          }
          function roster(data) {
            const count = integer(data.stats?.avatar_num);
            if (count > config.maximumCharacters) failure('too-large');
            const ids = rows(data.avatar_list, config.maximumCharacters).map(item => integer(item.id, 1));
            const result = new Set(ids);
            if (result.size !== ids.length || ids.length !== count) failure('needs-review');
            return result;
          }
          function sameIds(left, right) {
            if (left.size !== right.size || Array.from(left).some(id => !right.has(id))) failure('needs-review');
          }
          void (async () => {
            let characters = [];
            try {
              await verifyRole();
              const before = roster(await request(recordUrl('index')));
              const data = await request(recordUrl('avatar/info'));
              characters = unique(rows(data.avatar_list, config.maximumCharacters).map(raw => project(raw, data.property_info)), 'id');
              sameIds(before, new Set(characters.map(item => item.id)));
              sameIds(before, roster(await request(recordUrl('index'))));
              await verifyRole();
              characters.sort((left, right) => left.id - right.id);
              current();
              const result = { status: 'done', roleId: config.roleId, server: config.server, count: characters.length, characters };
              const encoded = new TextEncoder().encode(JSON.stringify(result));
              try { if (encoded.length > config.maximumResultBytes) failure('too-large'); }
              finally { encoded.fill(0); }
              state.result = result;
            } catch (error) {
              characters = [];
              if (window[config.key] === state) state.result = { status: timedOut ? 'timed-out'
                : controller.signal.aborted ? 'canceled'
                : ['login-required', 'too-large', 'timed-out', 'needs-review'].includes(error) ? error : 'needs-review' };
            } finally { clearTimeout(timeout); controller.abort(); }
          })();
          return 'started';
        })()
        """;
    }
}
