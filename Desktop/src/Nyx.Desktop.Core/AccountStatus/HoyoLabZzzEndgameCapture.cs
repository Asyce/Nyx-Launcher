using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabZzzEndgameReadStatus
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

public sealed record HoyoLabZzzEndgameReadResult(HoyoLabZzzEndgameReadStatus Status, HoyoLabZzzEndgameSnapshot? Snapshot = null)
{
    public override string ToString() => nameof(HoyoLabZzzEndgameReadResult);
}

/// <summary>Bounded, read-only capture in the existing selected-role publisher session.</summary>
public static class HoyoLabZzzEndgameCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 45;

    public static HoyoLabZzzEndgameReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("zzz", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabZzzEndgameReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabZzzEndgameReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabZzzEndgameReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabZzzEndgameReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabZzzEndgameReadStatus.LoginRequired,
                    "canceled" => HoyoLabZzzEndgameReadStatus.Canceled,
                    "timed-out" => HoyoLabZzzEndgameReadStatus.TimedOut,
                    "too-large" => HoyoLabZzzEndgameReadStatus.TooLarge,
                    _ => HoyoLabZzzEndgameReadStatus.NeedsReview,
                } : HoyoLabZzzEndgameReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "endgame"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || !root.GetProperty("count").TryGetInt32(out var count))
                return new(HoyoLabZzzEndgameReadStatus.NeedsReview);
            var snapshot = new HoyoLabZzzEndgameSnapshot(root.GetProperty("endgame"));
            return HoyoLabZzzEndgameRules.IsValid(snapshot)
                && snapshot.Data.GetProperty("shiyu").GetArrayLength() == count
                ? new(HoyoLabZzzEndgameReadStatus.Completed, HoyoLabZzzEndgameRules.Normalize(snapshot))
                : new(HoyoLabZzzEndgameReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabZzzEndgameReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("zzz", role))
            throw new ArgumentException("Invalid Zenless role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxZzzEndgame_", StringComparison.Ordinal)
            || controllerKey.Length > 80 || controllerKey.Any(static value => value != '_' && !char.IsAsciiLetterOrDigit(value)))
            throw new ArgumentException("Invalid capture key.", nameof(controllerKey));
        var contract = PublisherAccountCatalog.GetResourceFetchContract("zzz");
        var configuration = JsonSerializer.Serialize(new
        {
            key = controllerKey,
            roleId = role.RoleId,
            server = role.Server,
            roleEndpoint = contract.RoleDiscoveryEndpoint.AbsoluteUri,
            gameBiz = contract.GameBusiness,
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
          const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record_zzz/api/zzz/';
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
          function calendarTime(value) {
            exact(value,['year','month','day','hour','minute','second']);
            const year=bounded(value.year,1,9999),month=bounded(value.month,1,12);
            const leap=year%4===0&&(year%100!==0||year%400===0);
            return {year,month,day:bounded(value.day,1,[31,leap?29:28,31,30,31,30,31,31,30,31,30,31][month-1]),
              hour:bounded(value.hour,0,23),minute:bounded(value.minute,0,59),second:bounded(value.second,0,59)};
          }
          function epoch(value) {
            if(typeof value!=='string'||! /^[1-9][0-9]{9}$/.test(value))failure('needs-review');
            return value;
          }
          function buff(raw) {
            exact(raw,['title','text']);
            if(typeof raw.text!=='string'||raw.text.length>16384||/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(raw.text))failure('needs-review');
            return {title:text(raw.title,0,256),text:raw.text};
          }
          function avatar(raw) {
            exact(raw,['id','level','rank','rarity','element_type','sub_element_type','avatar_profession','role_square_url']);
            return {id:integer(raw.id,1),level:bounded(raw.level,1,100),rank:bounded(raw.rank,0,6),rarity:text(raw.rarity,1,16),
              element:integer(raw.element_type),subElement:integer(raw.sub_element_type),profession:integer(raw.avatar_profession)};
          }
          function buddy(raw) {
            exact(raw,['id','level','rarity','bangboo_rectangle_url']);
            return {id:integer(raw.id,1),level:bounded(raw.level,1,100),rarity:text(raw.rarity,1,16)};
          }
          function team(raw,fifth) {
            exact(raw,['layer_id','challenge_time','avatar_list','buddy',...(fifth?['buffer','score','max_score','rating','monster_pic']:[])]);
            const result={id:integer(raw.layer_id,1),time:calendarTime(raw.challenge_time),avatars:unique(raw.avatar_list,3,avatar,'id'),buddy:buddy(raw.buddy)};
            if(!result.avatars.length)failure('needs-review');
            if(fifth) {
              result.buff=buff(raw.buffer);result.score=integer(raw.score);result.maxScore=integer(raw.max_score);result.rating=text(raw.rating,1,64);
              if(result.score>result.maxScore)failure('needs-review');
            }
            return result;
          }
          function project(raw,period) {
            exact(raw,['hadal_ver','hadal_info_v2','nick_name','icon']);
            if(raw.hadal_ver!=='v2')failure('needs-review');
            const data=exact(raw.hadal_info_v2,['zone_id','hadal_begin_time','hadal_end_time','pass_fifth_floor','brief',
              'fitfh_layer_detail','fourth_layer_detail','begin_time','end_time']);
            const brief=exact(data.brief,['cur_period_zone_layer_count','score','max_score','rank_percent','rating']);
            const fourth=exact(data.fourth_layer_detail,['buffer','challenge_time','layer_challenge_info_list','rating']);
            const fifth=exact(data.fitfh_layer_detail,['layer_challenge_info_list']);
            const result={period,zoneId:integer(data.zone_id,1),start:calendarTime(data.hadal_begin_time),end:calendarTime(data.hadal_end_time),
              startEpoch:epoch(data.begin_time),endEpoch:epoch(data.end_time),passedFifth:flag(data.pass_fifth_floor),
              summary:{layerCount:integer(brief.cur_period_zone_layer_count),score:integer(brief.score),maxScore:integer(brief.max_score),
                rankPercentHundredths:bounded(brief.rank_percent,0,10000),rating:text(brief.rating,1,64)},
              fourth:{buff:buff(fourth.buffer),time:calendarTime(fourth.challenge_time),rating:text(fourth.rating,1,64),
                teams:unique(fourth.layer_challenge_info_list,2,row=>team(row,false),'id')},
              fifth:{teams:unique(fifth.layer_challenge_info_list,3,row=>team(row,true),'id')} };
            if(result.startEpoch>=result.endEpoch||result.summary.score>result.summary.maxScore
              ||result.fourth.teams.length!==2||result.fifth.teams.length!==3
              ||new Set([...result.fourth.teams,...result.fifth.teams].map(row=>row.id)).size!==5
              ||result.fifth.teams.reduce((sum,row)=>sum+row.score,0)!==result.summary.score
              ||result.fifth.teams.reduce((sum,row)=>sum+row.maxScore,0)!==result.summary.maxScore)failure('needs-review');
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
              endgame = { shiyu:[] };
              for (const period of [1,2]) {
                const url = new URL(recordBase + 'hadal_info_v2');
                url.searchParams.set('role_id',config.roleId);
                url.searchParams.set('server',config.server);
                url.searchParams.set('schedule_type',String(period));
                endgame.shiyu.push(project(await request(url.href),period));
              }
              if(endgame.shiyu[0].zoneId===endgame.shiyu[1].zoneId)failure('needs-review');
              await verifyRole();
              current();
              const result = { status: 'done', roleId: config.roleId, server: config.server,
                count: endgame.shiyu.length, endgame };
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
