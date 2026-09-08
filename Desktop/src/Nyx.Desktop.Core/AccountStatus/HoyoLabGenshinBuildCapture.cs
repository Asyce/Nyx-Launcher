using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabGenshinBuildReadStatus
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

public sealed record HoyoLabGenshinBuildReadResult(
    HoyoLabGenshinBuildReadStatus Status,
    HoyoLabGenshinBuildSnapshot? Snapshot = null)
{
    public override string ToString() => nameof(HoyoLabGenshinBuildReadResult);
}

/// <summary>Read-only official requests in Nyx's existing consented HoYo session.</summary>
public static class HoyoLabGenshinBuildCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 90;

    public static HoyoLabGenshinBuildReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("gi", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabGenshinBuildReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabGenshinBuildReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabGenshinBuildReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabGenshinBuildReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabGenshinBuildReadStatus.LoginRequired,
                    "canceled" => HoyoLabGenshinBuildReadStatus.Canceled,
                    "timed-out" => HoyoLabGenshinBuildReadStatus.TimedOut,
                    "too-large" => HoyoLabGenshinBuildReadStatus.TooLarge,
                    _ => HoyoLabGenshinBuildReadStatus.NeedsReview,
                } : HoyoLabGenshinBuildReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "characters"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || root.GetProperty("count").ValueKind != JsonValueKind.Number
                || !root.GetProperty("count").TryGetInt32(out var count)
                || root.GetProperty("characters").ValueKind != JsonValueKind.Array
                || root.GetProperty("characters").GetArrayLength() != count)
                return new(HoyoLabGenshinBuildReadStatus.NeedsReview);
            var snapshot = new HoyoLabGenshinBuildSnapshot(root.GetProperty("characters"));
            return HoyoLabGenshinBuildRules.IsValid(snapshot)
                ? new(HoyoLabGenshinBuildReadStatus.Completed, HoyoLabGenshinBuildRules.Normalize(snapshot))
                : new(HoyoLabGenshinBuildReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabGenshinBuildReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("gi", role))
            throw new ArgumentException("Invalid Genshin role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxGenshinBuilds_", StringComparison.Ordinal)
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
            maximumCharacters = HoyoLabGenshinBuildRules.MaximumCharacters,
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
          const optionalInteger = value => value === undefined || value === null ? null : integer(value);
          const flag = value => typeof value === 'boolean' ? value : failure('needs-review');
          const rows = (value, maximum) => Array.isArray(value) && value.length <= maximum ? value : failure('needs-review');
          const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/';
          const calculatorUrl = 'https://sg-act-public-api.hoyolab.com/event/e20200928calculate/v1/sync/avatar/list';
          const bind = { role_id: config.roleId, server: config.server };
          let totalBytes = 0;
          let timedOut = false;
          const timeout = setTimeout(() => { timedOut = true; controller.abort(); }, config.timeoutMilliseconds);

          function current() {
            if (controller.signal.aborted || window[config.key] !== state)
              failure(timedOut ? 'timed-out' : 'canceled');
          }

          async function request(url, body) {
            current();
            await new Promise(resolve => setTimeout(resolve, 250));
            current();
            const requestController = new AbortController();
            const abort = () => requestController.abort();
            controller.signal.addEventListener('abort', abort, { once: true });
            let requestTimedOut = false;
            const requestTimeout = setTimeout(() => { requestTimedOut = true; abort(); }, 8000);
            let reader = null;
            let text = '';
            try {
              const response = await fetch(url, {
                method: body === undefined ? 'GET' : 'POST', credentials: 'include',
                redirect: 'error', cache: 'no-store', referrerPolicy: 'no-referrer',
                headers: { 'x-rpc-language': 'en-us', ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
                ...(body === undefined ? {} : { body: JSON.stringify(body) }),
                signal: requestController.signal,
              });
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
                  if (bytes > 2097152 || totalBytes > 33554432) failure('too-large');
                  text += decoder.decode(part.value, { stream: true });
                } finally { if (part.value instanceof Uint8Array) part.value.fill(0); }
              }
              text += decoder.decode();
              const result = JSON.parse(text);
              if (!plain(result) || !Number.isInteger(result.retcode)) failure('needs-review');
              if (result.retcode === -100 || result.retcode === 10001) failure('login-required');
              if (result.retcode !== 0 || !plain(result.data)) failure('needs-review');
              return result.data;
            } catch (error) {
              if (requestTimedOut) failure('timed-out');
              throw error;
            } finally {
              text = '';
              if (reader) { try { await reader.cancel(); } catch {} reader.releaseLock(); }
              clearTimeout(requestTimeout);
              controller.signal.removeEventListener('abort', abort);
            }
          }

          function ids(list, selector) {
            const result = new Set();
            for (const item of list) {
              const id = integer(selector(item), 1);
              if (result.has(id)) failure('needs-review');
              result.add(id);
            }
            return result;
          }
          function sameIds(left, right) {
            if (left.size !== right.size || Array.from(left).some(id => !right.has(id))) failure('needs-review');
          }

          // Keep the precision and units supplied by HoYoLAB. Do not recompute
          // displayed totals from rounded components or apply in-game buffs.
          function number(value, optional = false) {
            if (optional && (value === undefined || value === null || value === '')) return null;
            if (typeof value === 'number' && Number.isFinite(value)) return { value, percent: false };
            if (typeof value !== 'string' || value.length > 80) failure('needs-review');
            const normalized = value.trim();
            if (!/^[+-]?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d+)?%?$/.test(normalized)) failure('needs-review');
            const percent = normalized.endsWith('%');
            const parsed = Number(normalized.replaceAll(',', '').replace(/%$/, ''));
            if (!Number.isFinite(parsed)) failure('needs-review');
            return { value: parsed, percent };
          }
          function statName(id, propertyMap) {
            const info = propertyMap?.[String(id)];
            return plain(info) && info.property_type === id && typeof info.name === 'string'
              && info.name.length > 0 && info.name.length <= 128 && info.name === info.name.trim()
              && !/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(info.name) ? { name: info.name } : {};
          }
          function stat(raw, propertyMap) {
            if (!plain(raw)) failure('needs-review');
            const final = number(raw.final);
            const base = number(raw.base, true);
            const added = number(raw.add, true);
            // HoYoLAB may display a zero component without its percent suffix.
            if ((base && base.value !== 0 && base.percent !== final.percent)
              || (added && added.value !== 0 && added.percent !== final.percent)) failure('needs-review');
            const id = integer(raw.property_type, 1);
            return { id, base: base?.value ?? null,
              added: added?.value ?? null, final: final.value, percent: final.percent, ...statName(id, propertyMap) };
          }
          function artifactStat(raw, propertyMap) {
            if (!plain(raw)) failure('needs-review');
            const parsed = number(raw.value);
            const id = integer(raw.property_type, 1);
            return { id, value: parsed.value,
              percent: parsed.percent, rolls: optionalInteger(raw.times), ...statName(id, propertyMap) };
          }
          function project(raw, promotion, propertyMap) {
            if (!plain(raw) || !plain(raw.base) || !plain(raw.weapon)) failure('needs-review');
            const base = raw.base;
            const weapon = raw.weapon;
            if (typeof base.element !== 'string' || !/^[A-Za-z]{1,32}$/.test(base.element)) failure('needs-review');
            const skills = rows(raw.skills, 64).map(skill => ({ id: integer(skill.skill_id, 1),
              type: integer(skill.skill_type), level: integer(skill.level), unlocked: flag(skill.is_unlock) }))
              .sort((left, right) => left.id - right.id);
            ids(skills, row => row.id);
            const constellations = rows(raw.constellations, 64).map(item => ({ id: integer(item.id, 1),
              position: integer(item.pos, 1), active: flag(item.is_actived) }))
              .sort((left, right) => left.position - right.position);
            ids(constellations, row => row.id);
            ids(constellations, row => row.position);
            const artifacts = rows(raw.relics, 5).map(item => {
              if (!plain(item) || !plain(item.set)) failure('needs-review');
              const sub = rows(item.sub_property_list, 4).map(value => artifactStat(value, propertyMap)).sort((left, right) => left.id - right.id);
              ids(sub, row => row.id);
              return { id: integer(item.id, 1), setId: integer(item.set.id, 1), slot: integer(item.pos, 1),
                rarity: integer(item.rarity, 1), level: integer(item.level), main: artifactStat(item.main_property, propertyMap), sub };
            }).sort((left, right) => left.slot - right.slot);
            ids(artifacts, row => row.slot);
            const properties = [];
            for (const [group, name] of ['selected_properties', 'base_properties', 'extra_properties', 'element_properties'].entries()) {
              const values = rows(raw[name], 64).map(value => stat(value, propertyMap)).sort((left, right) => left.id - right.id);
              ids(values, row => row.id);
              for (const value of values) properties.push({ group, ...value });
            }
            return { id: integer(base.id, 1), level: integer(base.level, 1),
              promotion: optionalInteger(promotion), friendship: optionalInteger(base.fetter), element: base.element,
              weapon: { id: integer(weapon.id, 1), level: integer(weapon.level, 1),
                promotion: optionalInteger(weapon.promote_level), refinement: integer(weapon.affix_level, 1),
                main: stat(weapon.main_property, propertyMap), sub: weapon.sub_property == null ? null : stat(weapon.sub_property, propertyMap) },
              skills, constellations, artifacts, properties };
          }

          void (async () => {
            let characters = [];
            try {
              const roleUrl = new URL(config.roleEndpoint);
              roleUrl.searchParams.set('game_biz', config.gameBiz);
              roleUrl.searchParams.set('region', config.server);
              const roles = rows((await request(roleUrl.href)).list, 8);
              const bindings = new Set();
              for (const role of roles) {
                if (!plain(role) || role.game_biz !== config.gameBiz || role.region !== config.server
                  || typeof role.game_uid !== 'string' || !/^[1-9][0-9]{0,19}$/.test(role.game_uid)
                  || bindings.has(role.game_uid)) failure('needs-review');
                bindings.add(role.game_uid);
              }
              if (!bindings.has(config.roleId)) failure('needs-review');
              const listed = rows((await request(recordBase + 'character/list', bind)).list, config.maximumCharacters);
              const ownedIds = ids(listed, item => item.id);
              const promotions = new Map();
              let expectedTotal = null;
              for (let page = 1; page <= 8; page++) {
                const data = await request(calculatorUrl, { uid: config.roleId, region: config.server, page, size: 200 });
                const total = integer(data.total);
                if (total > config.maximumCharacters) failure('too-large');
                if (expectedTotal !== null && expectedTotal !== total) failure('needs-review');
                expectedTotal = total;
                const list = rows(data.list, 200);
                for (const item of list) {
                  const id = integer(item.id, 1);
                  if (promotions.has(id)) failure('needs-review');
                  promotions.set(id, optionalInteger(item.promote_level));
                }
                if (promotions.size > total) failure('needs-review');
                if (promotions.size === total) break;
                if (list.length === 0 || page === 8) failure('needs-review');
              }
              sameIds(ownedIds, new Set(promotions.keys()));
              const requested = Array.from(ownedIds).sort((left, right) => left - right);
              for (let index = 0; index < requested.length; index += 10) {
                const batch = requested.slice(index, index + 10);
                const detail = await request(recordBase + 'character/detail', { ...bind, character_ids: batch });
                const list = rows(detail.list, batch.length);
                sameIds(new Set(batch), ids(list, item => item.base?.id));
                for (const raw of list) characters.push(project(raw, promotions.get(raw.base.id), detail.property_map));
              }
              // A changed roster during a multi-request refresh must not be saved as complete.
              sameIds(ownedIds, ids(rows((await request(recordBase + 'character/list', bind)).list,
                config.maximumCharacters), item => item.id));
              characters.sort((left, right) => left.id - right.id);
              current();
              const result = { status: 'done', roleId: config.roleId, server: config.server,
                count: characters.length, characters };
              const encoded = new TextEncoder().encode(JSON.stringify(result));
              try { if (encoded.length > config.maximumResultBytes) failure('too-large'); }
              finally { encoded.fill(0); }
              state.result = result;
            } catch (error) {
              characters = [];
              if (window[config.key] === state) state.result = { status: timedOut ? 'timed-out'
                : controller.signal.aborted ? 'canceled'
                : ['login-required', 'too-large', 'timed-out', 'needs-review'].includes(error) ? error : 'needs-review' };
            } finally {
              clearTimeout(timeout);
              controller.abort();
            }
          })();
          return 'started';
        })()
        """;
    }
}
