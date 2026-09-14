using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabGenshinExplorationReadStatus
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

public sealed record HoyoLabGenshinExplorationReadResult(HoyoLabGenshinExplorationReadStatus Status, HoyoLabGenshinExplorationSnapshot? Snapshot = null)
{
    public override string ToString() => nameof(HoyoLabGenshinExplorationReadResult);
}

/// <summary>Bounded, read-only capture in the existing selected-role publisher session.</summary>
public static class HoyoLabGenshinExplorationCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 45;

    public static HoyoLabGenshinExplorationReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("gi", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabGenshinExplorationReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabGenshinExplorationReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabGenshinExplorationReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabGenshinExplorationReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabGenshinExplorationReadStatus.LoginRequired,
                    "canceled" => HoyoLabGenshinExplorationReadStatus.Canceled,
                    "timed-out" => HoyoLabGenshinExplorationReadStatus.TimedOut,
                    "too-large" => HoyoLabGenshinExplorationReadStatus.TooLarge,
                    _ => HoyoLabGenshinExplorationReadStatus.NeedsReview,
                } : HoyoLabGenshinExplorationReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "exploration"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || !root.GetProperty("count").TryGetInt32(out var count))
                return new(HoyoLabGenshinExplorationReadStatus.NeedsReview);
            var snapshot = new HoyoLabGenshinExplorationSnapshot(root.GetProperty("exploration"));
            return HoyoLabGenshinExplorationRules.IsValid(snapshot)
                && snapshot.Data.GetProperty("worlds").GetArrayLength() == count
                ? new(HoyoLabGenshinExplorationReadStatus.Completed, HoyoLabGenshinExplorationRules.Normalize(snapshot))
                : new(HoyoLabGenshinExplorationReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabGenshinExplorationReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("gi", role))
            throw new ArgumentException("Invalid Genshin role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxGenshinExploration_", StringComparison.Ordinal)
            || controllerKey.Length > 80 || controllerKey.Any(static value => value != '_' && !char.IsAsciiLetterOrDigit(value)))
            throw new ArgumentException("Invalid capture key.", nameof(controllerKey));
        var contract = PublisherAccountCatalog.GetResourceFetchContract("gi");
        var configuration = JsonSerializer.Serialize(new
        {
            key = controllerKey,
            roleId = role.RoleId,
            server = role.Server,
            roleEndpoint = contract.RoleDiscoveryEndpoint.AbsoluteUri,
            gameBiz = contract.GameBusiness,
            maximumWorlds = HoyoLabGenshinExplorationRules.MaximumWorlds,
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
          const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/';
          const recordUrl = path => {
            const url = new URL(recordBase + path);
            url.searchParams.set('server', config.server);
            url.searchParams.set('role_id', config.roleId);
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
          function project(data) {
            if (!plain(data) || !plain(data.stats)) failure('needs-review');
            const countFields = {
              anemoculi:'anemoculus_number', geoculi:'geoculus_number', electroculi:'electroculus_number',
              dendroculi:'dendroculus_number', hydroculi:'hydroculus_number', pyroculi:'pyroculus_number',
              lunoculi:'moonoculus_number', cryoculi:'iceculus_number', commonChests:'common_chest_number',
              exquisiteChests:'exquisite_chest_number', preciousChests:'precious_chest_number',
              luxuriousChests:'luxurious_chest_number', remarkableChests:'magic_chest_number',
              waypoints:'way_point_number', domains:'domain_number',
            };
            const counts = Object.fromEntries(Object.entries(countFields).map(([name, key]) => [name, integer(data.stats[key])]));
            const worlds = unique(rows(data.world_explorations, config.maximumWorlds).map(raw => {
              if (!plain(raw)) failure('needs-review');
              const reputation = raw.natan_reputation;
              if (reputation !== null && reputation !== undefined && !plain(reputation)) failure('needs-review');
              const tribes = reputation === null || reputation === undefined || !Object.hasOwn(reputation, 'tribal_list')
                ? null : unique(rows(reputation.tribal_list, 64).map(item => ({
                    id:integer(item.id, 1), name:text(item.name, 1, 256), level:integer(item.level),
                  })), 'id');
              return {
                id:integer(raw.id, 1), parentId:integer(raw.parent_id), name:text(raw.name, 1, 256),
                kind:text(raw.type, 1, 64), worldType:integer(raw.world_type), level:integer(raw.level),
                percentage:integer(raw.exploration_percentage), statueLevel:integer(raw.seven_statue_level),
                indexActive:flag(raw.index_active), detailActive:flag(raw.detail_active),
                offerings:rows(raw.offerings, 128).map(item => ({
                  name:text(item.name, 1, 256), level:integer(item.level), state:text(item.open_state, 1, 64),
                })),
                areas:rows(raw.area_exploration_list, 512).map(item => ({
                  name:text(item.name, 1, 256), percentage:integer(item.exploration_percentage),
                })),
                bosses:rows(raw.boss_list, 256).map(item => ({
                  name:text(item.name, 1, 256), kills:integer(item.kill_num),
                })), tribes,
              };
            }), 'id');
            const displayGroups = unique(rows(data.world_exploration_display, config.maximumWorlds).map(raw => ({
              id:integer(raw.exploration_id, 1),
              areas:rows(raw.group.items, config.maximumWorlds).map(item => {
                const ids = rows(item.area_ids, config.maximumWorlds).map(id => integer(id, 1));
                if (new Set(ids).size !== ids.length) failure('needs-review');
                return { ids, percentage:integer(item.exploration_percentage) };
              }),
            })), 'id');
            const parents = new Map(worlds.map(world => [world.id, world.parentId]));
            for (const world of worlds) {
              const seen = new Set();
              let id = world.id;
              while (id !== 0) {
                if (seen.has(id) || !parents.has(id)) failure('needs-review');
                seen.add(id);
                id = parents.get(id);
              }
            }
            for (const group of displayGroups)
              if (!parents.has(group.id) || group.areas.some(area => area.ids.some(id => !parents.has(id))))
                failure('needs-review');
            return { counts, worlds, displayGroups };
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
          void (async () => {
            let exploration = null;
            try {
              await verifyRole();
              exploration = project(await request(recordUrl('index')));
              await verifyRole();
              current();
              const result = { status: 'done', roleId: config.roleId, server: config.server,
                count: exploration.worlds.length, exploration };
              const encoded = new TextEncoder().encode(JSON.stringify(result));
              try { if (encoded.length > config.maximumResultBytes) failure('too-large'); }
              finally { encoded.fill(0); }
              state.result = result;
            } catch (error) {
              exploration = null;
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
