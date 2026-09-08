using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabGenshinEventsReadStatus
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

public sealed record HoyoLabGenshinEventsReadResult(HoyoLabGenshinEventsReadStatus Status, HoyoLabGenshinEventsSnapshot? Snapshot = null)
{
    public override string ToString() => nameof(HoyoLabGenshinEventsReadResult);
}

/// <summary>Bounded, read-only capture in the existing selected-role publisher session.</summary>
public static class HoyoLabGenshinEventsCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 45;

    public static HoyoLabGenshinEventsReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("gi", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabGenshinEventsReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabGenshinEventsReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabGenshinEventsReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabGenshinEventsReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabGenshinEventsReadStatus.LoginRequired,
                    "canceled" => HoyoLabGenshinEventsReadStatus.Canceled,
                    "timed-out" => HoyoLabGenshinEventsReadStatus.TimedOut,
                    "too-large" => HoyoLabGenshinEventsReadStatus.TooLarge,
                    _ => HoyoLabGenshinEventsReadStatus.NeedsReview,
                } : HoyoLabGenshinEventsReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "events"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || !root.GetProperty("count").TryGetInt32(out var count))
                return new(HoyoLabGenshinEventsReadStatus.NeedsReview);
            var snapshot = new HoyoLabGenshinEventsSnapshot(root.GetProperty("events"));
            return HoyoLabGenshinEventsRules.IsValid(snapshot)
                && snapshot.Data.GetProperty("activities").GetArrayLength() + snapshot.Data.GetProperty("fixed").GetArrayLength() == count
                ? new(HoyoLabGenshinEventsReadStatus.Completed, HoyoLabGenshinEventsRules.Normalize(snapshot))
                : new(HoyoLabGenshinEventsReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabGenshinEventsReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("gi", role))
            throw new ArgumentException("Invalid Genshin role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxGenshinEvents_", StringComparison.Ordinal)
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
            maximumEvents = HoyoLabGenshinEventsRules.MaximumEvents,
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
          const number = value => typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 2147483647
            ? value : failure('needs-review');
          const timestamp = value => typeof value === 'string' && /^(0|[1-9][0-9]{0,11})$/.test(value)
            && Number(value) <= 253402300799 ? value : failure('needs-review');
          const optional = (value, project) => value === null || value === undefined ? null
            : plain(value) ? project(value) : failure('needs-review');
          function calendarTime(value) {
            return optional(value, item => {
              const year = integer(item.year, 1), month = integer(item.month, 1), day = integer(item.day, 1);
              const hour = integer(item.hour), minute = integer(item.minute), second = integer(item.second);
              const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
              if (year > 9999 || month > 12 || day > [31,leap?29:28,31,30,31,30,31,31,30,31,30,31][month-1]
                || hour > 23 || minute > 59 || second > 59) failure('needs-review');
              return { year, month, day, hour, minute, second };
            });
          }
          function activity(raw) {
            if (!plain(raw)) failure('needs-review');
            const details = ['explore_detail', 'double_detail', 'tower_detail', 'role_combat_detail', 'hard_challenge_detail'];
            if (Object.keys(raw).some(key => key.endsWith('_detail') && !details.includes(key))) failure('needs-review');
            return {
              id:integer(raw.id), kind:text(raw.type, 1, 64), name:text(raw.name, 1, 256),
              startTimestamp:timestamp(raw.start_timestamp), endTimestamp:timestamp(raw.end_timestamp),
              startTime:calendarTime(raw.start_time), endTime:calendarTime(raw.end_time),
              countdown:integer(raw.countdown_seconds), status:integer(raw.status), finished:flag(raw.is_finished),
              rewards:rows(raw.reward_list, 1024).map(item => ({
                id:integer(item.item_id, 1), name:text(item.name, 1, 256), quantity:integer(item.num),
                rarity:text(item.rarity, 1, 64), overview:flag(item.homepage_show),
              })),
              exploration:optional(raw.explore_detail, item => ({
                percentage:number(item.explore_percent), finished:flag(item.is_finished),
              })),
              double:optional(raw.double_detail, item => ({ total:integer(item.total), remaining:integer(item.left) })),
              abyss:optional(raw.tower_detail, item => ({
                unlocked:flag(item.is_unlock), maximumStars:integer(item.max_star), stars:integer(item.total_star),
                hasData:flag(item.has_data),
              })),
              theater:optional(raw.role_combat_detail, item => ({
                unlocked:flag(item.is_unlock), maximumRound:integer(item.max_round_id), hasData:flag(item.has_data),
                tarotFinished:integer(item.tarot_finished_cnt), difficulty:integer(item.difficulty_id),
              })),
              onslaught:optional(raw.hard_challenge_detail, item => {
                if (!plain(item.sub)) failure('needs-review');
                return { unlocked:flag(item.is_unlock), difficulty:integer(item.difficulty), seconds:integer(item.second),
                  sub:{ seconds:number(item.sub.seconds), x:number(item.sub.x), y:number(item.sub.y) } };
              }),
            };
          }
          function project(data) {
            if (!plain(data)) failure('needs-review');
            const activities = rows(data.act_list, config.maximumEvents).map(activity);
            const fixed = rows(data.fixed_act_list, config.maximumEvents).map(activity);
            const identity = item => item.kind + ':' + item.id;
            const byIdentity = new Map();
            for (const item of [...activities, ...fixed]) {
              const key = identity(item);
              if (byIdentity.has(key)) failure('needs-review');
              byIdentity.set(key, item);
            }
            const seen = new Set();
            const selected = rows(data.selected_act_list, config.maximumEvents).map(raw => {
              const item = activity(raw), key = identity(item);
              if (seen.has(key) || !byIdentity.has(key)
                || JSON.stringify(item) !== JSON.stringify(byIdentity.get(key))) failure('needs-review');
              seen.add(key);
              return { id:item.id, kind:item.kind };
            });
            return { activities, fixed, selected };
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
            let events = null;
            try {
              await verifyRole();
              events = project(await request(recordBase + 'act_calendar', { server:config.server, role_id:config.roleId }));
              await verifyRole();
              current();
              const result = { status: 'done', roleId: config.roleId, server: config.server,
                count: events.activities.length + events.fixed.length, events };
              const encoded = new TextEncoder().encode(JSON.stringify(result));
              try { if (encoded.length > config.maximumResultBytes) failure('too-large'); }
              finally { encoded.fill(0); }
              state.result = result;
            } catch (error) {
              events = null;
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
