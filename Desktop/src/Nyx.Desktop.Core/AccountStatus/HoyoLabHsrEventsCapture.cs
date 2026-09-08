using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabHsrEventsReadStatus
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

public sealed record HoyoLabHsrEventsReadResult(HoyoLabHsrEventsReadStatus Status, HoyoLabHsrEventsSnapshot? Snapshot = null)
{
    public string? Diagnostic { get; init; }
    public override string ToString() => nameof(HoyoLabHsrEventsReadResult);
}

/// <summary>Bounded, read-only capture in the existing selected-role publisher session.</summary>
public static class HoyoLabHsrEventsCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 45;

    public static HoyoLabHsrEventsReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabHsrEventsReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabHsrEventsReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabHsrEventsReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabHsrEventsReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabHsrEventsReadStatus.LoginRequired,
                    "canceled" => HoyoLabHsrEventsReadStatus.Canceled,
                    "timed-out" => HoyoLabHsrEventsReadStatus.TimedOut,
                    "too-large" => HoyoLabHsrEventsReadStatus.TooLarge,
                    _ => HoyoLabHsrEventsReadStatus.NeedsReview,
                } : HoyoLabHsrEventsReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "events"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || !root.GetProperty("count").TryGetInt32(out var count))
                return new(HoyoLabHsrEventsReadStatus.NeedsReview);
            var snapshot = new HoyoLabHsrEventsSnapshot(root.GetProperty("events"));
            return HoyoLabHsrEventsRules.IsValid(snapshot)
                && snapshot.Data.GetProperty("activities").GetArrayLength() + snapshot.Data.GetProperty("challenges").GetArrayLength() == count
                ? new(HoyoLabHsrEventsReadStatus.Completed, HoyoLabHsrEventsRules.Normalize(snapshot))
                : new(HoyoLabHsrEventsReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabHsrEventsReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("hsr", role))
            throw new ArgumentException("Invalid Star Rail role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxHsrEvents_", StringComparison.Ordinal)
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
            maximumEvents = HoyoLabHsrEventsRules.MaximumEvents,
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
          const rememberFailure = error => {
            state.failureFrames = String(error?.stack || '').split('\n').slice(2, 5)
              .map(line => Number(line.match(/:(\d+):\d+\)?$/)?.[1] || 0))
              .filter(line => Number.isInteger(line) && line > 0 && line <= 4096);
          };
          const failure = code => { rememberFailure(new Error()); throw code; };
          const plain = value => value !== null && typeof value === 'object' && !Array.isArray(value)
            && (Object.getPrototypeOf(value) === Object.prototype || Object.getPrototypeOf(value) === null);
          const integer = (value, minimum = 0) => Number.isInteger(value) && value >= minimum && value <= 2147483647
            ? value : failure('needs-review');
          const flag = value => typeof value === 'boolean' ? value : failure('needs-review');
          const rows = (value, maximum) => Array.isArray(value) && value.length <= maximum ? value : failure('needs-review');
          const text = (value, minimum = 0, maximum = 64) => typeof value === 'string'
            && value.length >= minimum && value.length <= maximum && value === value.trim()
            && !/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(value) ? value : failure('needs-review');
          const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/';
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
          const timestamp = value => typeof value === 'string' && /^(0|[1-9][0-9]{0,11})$/.test(value)
            && Number(value) <= 253402300799 ? value : failure('needs-review');
          function serverTime(value) {
            if (value === '') return value;
            if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$/.test(value))
              failure('needs-review');
            const [year, month, day, hour, minute, second] = value.split(/[- :]/).map(Number);
            const leap = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
            if (year < 1 || month < 1 || month > 12 || day < 1
              || day > [31,leap?29:28,31,30,31,30,31,31,30,31,30,31][month-1]
              || hour > 23 || minute > 59 || second > 59) failure('needs-review');
            return value;
          }
          function time(raw) {
            if (!plain(raw)) failure('needs-review');
            return { startTimestamp:timestamp(raw.start_ts), endTimestamp:timestamp(raw.end_ts),
              startTime:serverTime(raw.start_time), endTime:serverTime(raw.end_time), now:timestamp(raw.now) };
          }
          function reward(raw, special = false) {
            if (special && raw === null) return null;
            if (!plain(raw)) failure('needs-review');
            const id = integer(raw.item_id, special ? 0 : 1), quantity = integer(raw.num);
            const name = text(raw.name, id === 0 ? 0 : 1, 256);
            if (id === 0 && (name !== '' || quantity !== 0)) failure('needs-review');
            return { id, name, quantity, rarity:text(raw.rarity, 1, 64),
              kind:raw.reward_type === undefined ? null : text(raw.reward_type, 1, 64) };
          }
          function activity(raw) {
            if (!plain(raw)) failure('needs-review');
            return {
              id:integer(raw.id, 1), version:text(raw.version, 1, 64), name:text(raw.name, 1, 256),
              kind:text(raw.act_type, 1, 64), status:text(raw.act_status, 1, 64),
              total:integer(raw.total_progress), progress:integer(raw.current_progress), time:time(raw.time_info),
              rewards:rows(raw.reward_list, 1024).map(item => reward(item)),
              specialReward:reward(raw.special_reward, true), finished:flag(raw.all_finished),
              showText:text(raw.show_text, 0, 1024), timeKind:text(raw.act_time_type, 1, 64),
              description:text(raw.panel_desc, 0, 4096), dropType:integer(raw.multiple_drop_type),
              dropTypes:rows(raw.multiple_drop_type_list, 64).map(item => integer(item)),
              refreshType:integer(raw.count_refresh_type), count:integer(raw.count_value),
              multiplier:integer(raw.drop_multiple), afterVersion:flag(raw.is_after_version),
            };
          }
          function challenge(raw) {
            if (!plain(raw)) failure('needs-review');
            return {
              id:integer(raw.group_id, 1), name:text(raw.name_mi18n, 1, 256),
              kind:text(raw.challenge_type, 1, 64), status:text(raw.status, 1, 64),
              total:integer(raw.total_progress), progress:integer(raw.current_progress),
              extraProgress:integer(raw.extra_progress), time:time(raw.time_info),
              rewards:rows(raw.reward_list, 1024).map(item => reward(item)),
              specialReward:reward(raw.special_reward, true), showText:text(raw.show_text, 0, 1024),
              rankKind:text(raw.challenge_peak_rank_icon_type, 0, 64),
              startVersion:text(raw.challenge_peak_start_version, 0, 64),
            };
          }
          function collection(raw, project) {
            const seen = new Set();
            return rows(raw, config.maximumEvents).map(value => {
              const item = project(value), key = item.kind + ':' + item.id;
              if (seen.has(key)) failure('needs-review');
              seen.add(key);
              return item;
            });
          }
          function project(data) {
            if (!plain(data) || Object.keys(data).some(key => key.endsWith('_list')
              && !['act_list','challenge_list','avatar_card_pool_list','equip_card_pool_list'].includes(key)))
              failure('needs-review');
            return { activities:collection(data.act_list, activity), challenges:collection(data.challenge_list, challenge),
              now:timestamp(data.now), version:text(data.cur_game_version, 1, 64) };
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
              const url = new URL(recordBase + 'get_act_calender');
              url.searchParams.set('server', config.server);
              url.searchParams.set('role_id', config.roleId);
              events = project(await request(url.href));
              await verifyRole();
              current();
              const result = { status: 'done', roleId: config.roleId, server: config.server,
                count: events.activities.length + events.challenges.length, events };
              const encoded = new TextEncoder().encode(JSON.stringify(result));
              try { if (encoded.length > config.maximumResultBytes) failure('too-large'); }
              finally { encoded.fill(0); }
              state.result = result;
            } catch (error) {
              if (!state.failureFrames?.length) rememberFailure(error);
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
