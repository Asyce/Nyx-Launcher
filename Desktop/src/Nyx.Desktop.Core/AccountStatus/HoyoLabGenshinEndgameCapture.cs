using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabGenshinEndgameReadStatus
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

public sealed record HoyoLabGenshinEndgameReadResult(HoyoLabGenshinEndgameReadStatus Status, HoyoLabGenshinEndgameSnapshot? Snapshot = null)
{
    public override string ToString() => nameof(HoyoLabGenshinEndgameReadResult);
}

/// <summary>Bounded, read-only capture in the existing selected-role publisher session.</summary>
public static class HoyoLabGenshinEndgameCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 45;

    public static HoyoLabGenshinEndgameReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("gi", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabGenshinEndgameReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabGenshinEndgameReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabGenshinEndgameReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabGenshinEndgameReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabGenshinEndgameReadStatus.LoginRequired,
                    "canceled" => HoyoLabGenshinEndgameReadStatus.Canceled,
                    "timed-out" => HoyoLabGenshinEndgameReadStatus.TimedOut,
                    "too-large" => HoyoLabGenshinEndgameReadStatus.TooLarge,
                    _ => HoyoLabGenshinEndgameReadStatus.NeedsReview,
                } : HoyoLabGenshinEndgameReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "endgame"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || !root.GetProperty("count").TryGetInt32(out var count))
                return new(HoyoLabGenshinEndgameReadStatus.NeedsReview);
            var snapshot = new HoyoLabGenshinEndgameSnapshot(root.GetProperty("endgame"));
            return HoyoLabGenshinEndgameRules.IsValid(snapshot)
                && snapshot.Data.GetProperty("abyss").GetArrayLength() == count
                ? new(HoyoLabGenshinEndgameReadStatus.Completed, HoyoLabGenshinEndgameRules.Normalize(snapshot))
                : new(HoyoLabGenshinEndgameReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabGenshinEndgameReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("gi", role))
            throw new ArgumentException("Invalid Genshin role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxGenshinEndgame_", StringComparison.Ordinal)
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
            maximumFloors = HoyoLabGenshinEndgameRules.MaximumFloors,
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
          let totalBytes = 0;
          let timedOut = false;
          const timeout = setTimeout(() => { timedOut = true; controller.abort(); }, config.timeoutMilliseconds);
          function current() {
            if (controller.signal.aborted || window[config.key] !== state)
              failure(timedOut ? 'timed-out' : 'canceled');
          }
          async function request(url, payload = null) {
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
              const response = await fetch(url, { method: payload === null ? 'GET' : 'POST', credentials: 'include', redirect: 'error',
                cache: 'no-store', referrerPolicy: 'no-referrer', headers: { 'x-rpc-language': 'en-us', ...(payload === null ? {} : { 'Content-Type': 'application/json' }) },
                body: payload === null ? undefined : JSON.stringify(payload), signal: requestController.signal });
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
          const timestamp = value => typeof value === 'string' && /^(0|[1-9][0-9]{0,11})$/.test(value)
            && Number(value) <= 253402300799 ? value : failure('needs-review');
          const exact = (value, names) => {
            if (!plain(value) || Object.keys(value).length !== names.length || !names.every(name => Object.hasOwn(value, name)))
              failure('needs-review');
            return value;
          };
          const bounded = (value, minimum, maximum) => {
            integer(value, minimum);
            return value <= maximum ? value : failure('needs-review');
          };
          function unique(value, maximum, project, identity) {
            const result = rows(value, maximum).map(project);
            if (new Set(result.map(item => item[identity])).size !== result.length) failure('needs-review');
            return result;
          }
          function calendarTime(value, nullable = false) {
            if (nullable && value === null) return null;
            exact(value, ['year','month','day','hour','minute','second']);
            const year = bounded(value.year, 1, 9999), month = bounded(value.month, 1, 12);
            const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
            const day = bounded(value.day, 1, [31,leap?29:28,31,30,31,30,31,31,30,31,30,31][month-1]);
            return { year, month, day, hour:bounded(value.hour,0,23), minute:bounded(value.minute,0,59), second:bounded(value.second,0,59) };
          }
          function effects(value) {
            return rows(value, 64).map(item => typeof item === 'string' && item.length <= 4096
              && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(item)
              ? item : failure('needs-review'));
          }
          function ranking(raw) {
            exact(raw, ['avatar_icon','avatar_id','rarity','value']);
            return { id:integer(raw.avatar_id,1), rarity:bounded(raw.rarity,1,5), value:integer(raw.value) };
          }
          function avatar(raw) {
            exact(raw, ['id','icon','level','rarity']);
            return { id:integer(raw.id,1), level:bounded(raw.level,1,100), rarity:bounded(raw.rarity,1,5) };
          }
          function enemy(raw) {
            exact(raw, ['icon','level','name']);
            return { name:text(raw.name,1,256), level:bounded(raw.level,1,1000) };
          }
          function battle(raw) {
            exact(raw, ['index','timestamp','settle_date_time','avatars']);
            const avatars = unique(raw.avatars,4,avatar,'id');
            if (!avatars.length) failure('needs-review');
            return { index:bounded(raw.index,1,2), timestamp:timestamp(raw.timestamp),
              settledTime:calendarTime(raw.settle_date_time), avatars };
          }
          function stars(raw) {
            const stars = integer(raw.star), maxStars = integer(raw.max_star);
            if (stars > maxStars) failure('needs-review');
            return { stars, maxStars };
          }
          function chamber(raw) {
            exact(raw, ['index','star','max_star','battles','top_half_floor_monster','bottom_half_floor_monster']);
            return { index:bounded(raw.index,1,16), ...stars(raw),
              upperEnemies:rows(raw.top_half_floor_monster,256).map(enemy),
              lowerEnemies:rows(raw.bottom_half_floor_monster,256).map(enemy),
              battles:unique(raw.battles,2,battle,'index') };
          }
          function floor(raw) {
            exact(raw, ['icon','index','is_unlock','star','max_star','settle_time','settle_date_time','ley_line_disorder',
              'ley_line_disorder_lower','ley_line_disorder_upper','levels']);
            const result = { index:bounded(raw.index,1,config.maximumFloors), unlocked:flag(raw.is_unlock), ...stars(raw),
              settledAt:timestamp(raw.settle_time), settledTime:calendarTime(raw.settle_date_time,true),
              effects:effects(raw.ley_line_disorder), upperEffects:effects(raw.ley_line_disorder_upper),
              lowerEffects:effects(raw.ley_line_disorder_lower), chambers:unique(raw.levels,16,chamber,'index') };
            if (result.chambers.reduce((sum,row) => sum + row.stars,0) !== result.stars
              || result.chambers.reduce((sum,row) => sum + row.maxStars,0) > result.maxStars) failure('needs-review');
            return result;
          }
          function project(raw, period) {
            exact(raw, ['schedule_id','start_time','end_time','total_battle_times','total_win_times','max_floor','total_star',
              'is_unlock','is_just_skipped_floor','skipped_floor','reveal_rank','defeat_rank','damage_rank','take_damage_rank',
              'normal_skill_rank','energy_skill_rank','floors']);
            const result = { period, id:integer(raw.schedule_id,1), start:timestamp(raw.start_time), end:timestamp(raw.end_time),
              battles:integer(raw.total_battle_times), wins:integer(raw.total_win_times), maxFloor:text(raw.max_floor,0,32),
              stars:integer(raw.total_star), unlocked:flag(raw.is_unlock), justSkipped:flag(raw.is_just_skipped_floor),
              skippedFloor:text(raw.skipped_floor,0,32), rankings:{
                reveal:unique(raw.reveal_rank,512,ranking,'id'), defeat:unique(raw.defeat_rank,512,ranking,'id'),
                damage:unique(raw.damage_rank,512,ranking,'id'), damageTaken:unique(raw.take_damage_rank,512,ranking,'id'),
                normalSkills:unique(raw.normal_skill_rank,512,ranking,'id'), elementalBursts:unique(raw.energy_skill_rank,512,ranking,'id'),
              }, floors:unique(raw.floors,config.maximumFloors,floor,'index') };
            if (Number(result.start) >= Number(result.end)
              || result.floors.reduce((sum,row) => sum + row.stars,0) > result.stars) failure('needs-review');
            return result;
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
            let endgame = null;
            try {
              await verifyRole();
              endgame = { abyss:[] };
              for (const period of [1,2]) {
                const url = new URL(recordBase + 'spiralAbyss');
                url.searchParams.set('role_id',config.roleId);
                url.searchParams.set('server',config.server);
                url.searchParams.set('schedule_type',String(period));
                endgame.abyss.push(project(await request(url.href),period));
              }
              if (endgame.abyss[0].id === endgame.abyss[1].id) failure('needs-review');
              await verifyRole();
              current();
              const result = { status: 'done', roleId: config.roleId, server: config.server,
                count: endgame.abyss.length, endgame };
              const encoded = new TextEncoder().encode(JSON.stringify(result));
              try { if (encoded.length > config.maximumResultBytes) failure('too-large'); }
              finally { encoded.fill(0); }
              state.result = result;
            } catch (error) {
              endgame = null;
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
