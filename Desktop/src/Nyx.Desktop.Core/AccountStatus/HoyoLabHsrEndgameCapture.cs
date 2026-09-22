using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabHsrEndgameReadStatus
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

public sealed record HoyoLabHsrEndgameReadResult(HoyoLabHsrEndgameReadStatus Status, HoyoLabHsrEndgameSnapshot? Snapshot = null)
{
    public override string ToString() => nameof(HoyoLabHsrEndgameReadResult);
}

/// <summary>Bounded, read-only capture in the existing selected-role publisher session.</summary>
public static class HoyoLabHsrEndgameCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 45;

    public static HoyoLabHsrEndgameReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("hsr", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabHsrEndgameReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabHsrEndgameReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabHsrEndgameReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabHsrEndgameReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabHsrEndgameReadStatus.LoginRequired,
                    "canceled" => HoyoLabHsrEndgameReadStatus.Canceled,
                    "timed-out" => HoyoLabHsrEndgameReadStatus.TimedOut,
                    "too-large" => HoyoLabHsrEndgameReadStatus.TooLarge,
                    _ => HoyoLabHsrEndgameReadStatus.NeedsReview,
                } : HoyoLabHsrEndgameReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "endgame"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || !root.GetProperty("count").TryGetInt32(out var count))
                return new(HoyoLabHsrEndgameReadStatus.NeedsReview);
            var snapshot = new HoyoLabHsrEndgameSnapshot(root.GetProperty("endgame"));
            return HoyoLabHsrEndgameRules.IsValid(snapshot)
                && snapshot.Data.GetProperty("challenges").GetArrayLength() == count
                ? new(HoyoLabHsrEndgameReadStatus.Completed, HoyoLabHsrEndgameRules.Normalize(snapshot))
                : new(HoyoLabHsrEndgameReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabHsrEndgameReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("hsr", role))
            throw new ArgumentException("Invalid Star Rail role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxHsrEndgame_", StringComparison.Ordinal)
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
            maximumFloors = HoyoLabHsrEndgameRules.MaximumFloors,
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
                cache: 'no-store', referrerPolicy: 'no-referrer', headers: { 'x-rpc-language': 'en-us' },
                signal: requestController.signal });
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
            exact(value, ['year','month','day','hour','minute']);
            const year=bounded(value.year,1,9999),month=bounded(value.month,1,12);
            const leap=year%4===0&&(year%100!==0||year%400===0);
            return {year,month,day:bounded(value.day,1,[31,leap?29:28,31,30,31,30,31,31,30,31,30,31][month-1]),
              hour:bounded(value.hour,0,23),minute:bounded(value.minute,0,59)};
          }
          function decimal(value) {
            if (typeof value!=='string'||!/^(0|[1-9][0-9]{0,39})$/.test(value)) failure('needs-review');
            return value;
          }
          function sourceStars(value, kind) {
            return integer(kind==='apocalyptic-shadow'?Number(decimal(value)):value);
          }
          function boss(raw) {
            if (raw===null) return null;
            exact(raw,['id','name_mi18n','icon']);
            return {id:integer(raw.id,1),name:text(raw.name_mi18n,1,256)};
          }
          function group(raw) {
            exact(raw,['schedule_id','begin_time','end_time','status','name_mi18n','upper_boss','lower_boss','tierce_boss']);
            return {id:integer(raw.schedule_id,1),start:calendarTime(raw.begin_time),end:calendarTime(raw.end_time),
              status:text(raw.status,1,64),name:text(raw.name_mi18n,0,256),upperBoss:boss(raw.upper_boss),
              lowerBoss:boss(raw.lower_boss),thirdBoss:boss(raw.tierce_boss)};
          }
          function avatar(raw) {
            exact(raw,['id','level','icon','rarity','element','rank','is_return_assist']);
            return {id:integer(raw.id,1),level:bounded(raw.level,1,100),rarity:bounded(raw.rarity,1,5),
              rank:bounded(raw.rank,0,6),element:text(raw.element,1,64),returnAssist:flag(raw.is_return_assist)};
          }
          function buff(raw,kind) {
            if (raw===null) return null;
            exact(raw,['id','name_mi18n','desc_mi18n','icon',...(kind==='pure-fiction'?['simple_desc_mi18m']:[])]);
            const result={id:integer(raw.id,1),name:text(raw.name_mi18n,1,256),description:text(raw.desc_mi18n,0,8192)};
            if (kind==='pure-fiction') result.summary=text(raw.simple_desc_mi18m,0,8192);
            return result;
          }
          function node(raw,kind,third=false) {
            if (third&&raw===null) return null;
            exact(raw,['challenge_time','avatars',...(kind==='forgotten-hall'?[]:['score','buff']),
              ...(kind==='apocalyptic-shadow'?['boss_defeated']:[])]);
            const result={time:calendarTime(raw.challenge_time,kind==='apocalyptic-shadow'),avatars:unique(raw.avatars,4,avatar,'id')};
            if (kind!=='forgotten-hall') { result.score=decimal(raw.score);result.buff=buff(raw.buff,kind); }
            if (kind==='apocalyptic-shadow') result.bossDefeated=flag(raw.boss_defeated);
            return result;
          }
          function floor(raw,kind) {
            exact(raw,['name','star_num','node_1','node_2','maze_id','is_fast','node_3','extra_star_num','is_tierce',
              ...(kind==='apocalyptic-shadow'?['last_update_time']:['round_num']),...(kind==='forgotten-hall'?['is_chaos']:[])]);
            const result={id:integer(raw.maze_id,1),name:text(raw.name,1,256),stars:sourceStars(raw.star_num,kind),
              extraStars:sourceStars(raw.extra_star_num,kind),fast:flag(raw.is_fast),starward:flag(raw.is_tierce),
              nodes:[node(raw.node_1,kind),node(raw.node_2,kind),node(raw.node_3,kind,true)]};
            if (result.extraStars>result.stars) failure('needs-review');
            if (kind==='apocalyptic-shadow') result.updatedAt=calendarTime(raw.last_update_time);
            else result.rounds=integer(raw.round_num);
            if (kind==='forgotten-hall') result.chaos=flag(raw.is_chaos);
            return result;
          }
          function project(raw,kind,period) {
            exact(raw,['groups','star_num','max_floor','battle_num','has_data','all_floor_detail','max_floor_id','extra_star_num',
              ...(kind==='forgotten-hall'?['schedule_id','begin_time','end_time','max_floor_detail']:[])]);
            const result={kind,period,groups:unique(raw.groups,2,group,'id'),stars:integer(raw.star_num),
              extraStars:integer(raw.extra_star_num),maxFloor:text(raw.max_floor,0,256),maxFloorId:integer(raw.max_floor_id),
              battles:integer(raw.battle_num),hasData:flag(raw.has_data),floors:unique(raw.all_floor_detail,config.maximumFloors,row=>floor(row,kind),'id')};
            if (result.groups.length!==2) failure('needs-review');
            if (kind==='forgotten-hall') {
              if (raw.max_floor_detail!==null) failure('needs-review');
              result.id=integer(raw.schedule_id,1);result.start=calendarTime(raw.begin_time);result.end=calendarTime(raw.end_time);
              const selected=result.groups[period-1];
              if (result.id!==selected.id||JSON.stringify(result.start)!==JSON.stringify(selected.start)
                ||JSON.stringify(result.end)!==JSON.stringify(selected.end)) failure('needs-review');
            }
            if (result.floors.reduce((sum,row)=>sum+row.stars,0)!==result.stars+result.extraStars
              ||result.floors.reduce((sum,row)=>sum+row.extraStars,0)!==result.extraStars) failure('needs-review');
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
              endgame = { challenges:[] };
              for (const [kind,endpoint] of [['forgotten-hall','challenge'],['pure-fiction','challenge_story'],['apocalyptic-shadow','challenge_boss']]) {
                let first=null;
                for (const period of [1,2]) {
                  const url = new URL(recordBase + endpoint);
                  url.searchParams.set('role_id',config.roleId);
                  url.searchParams.set('server',config.server);
                  url.searchParams.set('schedule_type',String(period));
                  url.searchParams.set('need_all','true');
                  const data=project(await request(url.href),kind,period);
                  if (first&&JSON.stringify(first.groups)!==JSON.stringify(data.groups)) failure('needs-review');
                  if (kind==='forgotten-hall'&&first&&first.id===data.id) failure('needs-review');
                  if (!first) first=data;
                  endgame.challenges.push(data);
                }
              }
              await verifyRole();
              current();
              const result = { status: 'done', roleId: config.roleId, server: config.server,
                count: endgame.challenges.length, endgame };
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
