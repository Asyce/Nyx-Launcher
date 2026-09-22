using System.Text;
using System.Text.Json;

namespace Nyx.Desktop.Core.AccountStatus;

public enum HoyoLabZzzBuildReadStatus
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

public sealed record HoyoLabZzzBuildReadResult(HoyoLabZzzBuildReadStatus Status, HoyoLabZzzBuildSnapshot? Snapshot = null)
{
    public override string ToString() => nameof(HoyoLabZzzBuildReadResult);
}

/// <summary>Bounded, read-only capture in the existing selected-role publisher session.</summary>
public static class HoyoLabZzzBuildCapture
{
    public const int MaximumResultBytes = 3 * 1024 * 1024;
    public const int TimeoutSeconds = 120;

    public static HoyoLabZzzBuildReadResult ParseResult(string json, PublisherRoleBinding expectedRole)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("zzz", expectedRole)
            || string.IsNullOrEmpty(json) || json.Length > MaximumResultBytes
            || Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
            return new(HoyoLabZzzBuildReadStatus.NeedsReview);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(HoyoLabZzzBuildReadStatus.NeedsReview);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) return new(HoyoLabZzzBuildReadStatus.NeedsReview);
            if (!root.TryGetProperty("status", out var state) || state.ValueKind != JsonValueKind.String)
                return new(HoyoLabZzzBuildReadStatus.NeedsReview);
            if (state.GetString() != "done")
                return new(names.SetEquals(["status"]) ? state.GetString() switch
                {
                    "login-required" => HoyoLabZzzBuildReadStatus.LoginRequired,
                    "canceled" => HoyoLabZzzBuildReadStatus.Canceled,
                    "timed-out" => HoyoLabZzzBuildReadStatus.TimedOut,
                    "too-large" => HoyoLabZzzBuildReadStatus.TooLarge,
                    _ => HoyoLabZzzBuildReadStatus.NeedsReview,
                } : HoyoLabZzzBuildReadStatus.NeedsReview);
            if (!names.SetEquals(["status", "roleId", "server", "count", "builds"])
                || root.GetProperty("roleId").ValueKind != JsonValueKind.String
                || root.GetProperty("roleId").GetString() != expectedRole.RoleId
                || root.GetProperty("server").ValueKind != JsonValueKind.String
                || root.GetProperty("server").GetString() != expectedRole.Server
                || !root.GetProperty("count").TryGetInt32(out var count))
                return new(HoyoLabZzzBuildReadStatus.NeedsReview);
            var snapshot = new HoyoLabZzzBuildSnapshot(root.GetProperty("builds"));
            return HoyoLabZzzBuildRules.IsValid(snapshot)
                && snapshot.Data.GetProperty("avatars").GetArrayLength() == count
                ? new(HoyoLabZzzBuildReadStatus.Completed, HoyoLabZzzBuildRules.Normalize(snapshot))
                : new(HoyoLabZzzBuildReadStatus.NeedsReview);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(HoyoLabZzzBuildReadStatus.NeedsReview);
        }
    }

    public static string CreateScript(string controllerKey, PublisherRoleBinding role)
    {
        if (!PublisherAccountCatalog.IsValidRoleBinding("zzz", role))
            throw new ArgumentException("Invalid Zenless role.", nameof(role));
        if (controllerKey is null || !controllerKey.StartsWith("__pengoNyxZzzBuild_", StringComparison.Ordinal)
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
            maximumAvatars = HoyoLabZzzBuildRules.MaximumAvatars,
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
          const finite = (value, minimum, maximum) => typeof value === 'number' && Number.isFinite(value)
            && value >= minimum && value <= maximum ? value : failure('needs-review');
          const sourceText = (value, maximum) => typeof value === 'string' && value.length <= maximum
            && !/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(value) ? value : failure('needs-review');
          const statValue = (value, empty=false) => {
            text(value,empty?0:1,64);
            if (!(empty&&value==='')&&!/^[+-]?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?%?$/.test(value)) failure('needs-review');
            return value;
          };
          const exact = (value,names) => {
            if(!plain(value)||Object.keys(value).length!==names.length||!names.every(name=>Object.hasOwn(value,name))) failure('needs-review');
            return value;
          };
          const bounded = (value,minimum,maximum) => {integer(value,minimum);return value<=maximum?value:failure('needs-review');};
          function unique(value,maximum,project,identity) {
            const result=rows(value,maximum).map(project);
            if(new Set(result.map(item=>item[identity])).size!==result.length) failure('needs-review');
            return result;
          }
          function stat(raw) {
            exact(raw,['property_id','property_name','base','add','final']);
            return {id:integer(raw.property_id,1),name:text(raw.property_name,1,256),base:statValue(raw.base,true),added:statValue(raw.add,true),final:statValue(raw.final)};
          }
          function gearStat(raw) {
            exact(raw,['property_id','property_name','base','add','level','system_id','valid']);
            return {id:integer(raw.property_id,1),name:text(raw.property_name,1,256),base:statValue(raw.base),
              added:finite(raw.add,-1000000000,1000000000),level:bounded(raw.level,0,100),systemId:integer(raw.system_id),valid:flag(raw.valid)};
          }
          function equipmentSet(raw) {
            exact(raw,['desc1','desc2','name','own','suit_id']);
            return {id:integer(raw.suit_id,1),name:text(raw.name,1,256),count:bounded(raw.own,0,6),twoPiece:sourceText(raw.desc1,8192),fourPiece:sourceText(raw.desc2,8192)};
          }
          function equipment(raw) {
            exact(raw,['all_hit','equip_suit','equipment_type','icon','id','invalid_property_cnt','level','main_properties','name','properties','rarity']);
            return {id:integer(raw.id,1),name:text(raw.name,1,256),rarity:text(raw.rarity,1,16),level:bounded(raw.level,0,100),
              position:bounded(raw.equipment_type,1,6),allHit:flag(raw.all_hit),invalidProperties:bounded(raw.invalid_property_cnt,0,1000),
              mainStats:unique(raw.main_properties,16,gearStat,'id'),stats:unique(raw.properties,16,gearStat,'id'),set:equipmentSet(raw.equip_suit)};
          }
          function weapon(raw) {
            if(raw===null)return null;
            exact(raw,['icon','id','level','main_properties','name','profession','properties','rarity','star','talent_content','talent_title']);
            return {id:integer(raw.id,1),name:text(raw.name,1,256),rarity:text(raw.rarity,1,16),level:bounded(raw.level,1,100),profession:integer(raw.profession),stars:bounded(raw.star,1,5),
              mainStats:unique(raw.main_properties,16,gearStat,'id'),stats:unique(raw.properties,16,gearStat,'id'),effectName:text(raw.talent_title,0,256),effect:sourceText(raw.talent_content,8192)};
          }
          function skill(raw) {
            exact(raw,['awaken_state','items','level','skill_type']);
            return {type:bounded(raw.skill_type,0,64),level:bounded(raw.level,0,100),awakenState:text(raw.awaken_state,1,64),items:rows(raw.items,64).map(item=>{
              exact(item,['awaken','title','text']);return {awakened:flag(item.awaken),title:text(item.title,0,256),text:sourceText(item.text,16384)};
            })};
          }
          function rank(raw) {
            exact(raw,['desc','id','is_unlocked','name','pos']);
            return {id:integer(raw.id,1),position:bounded(raw.pos,1,6),name:text(raw.name,1,256),description:sourceText(raw.desc,8192),unlocked:flag(raw.is_unlocked)};
          }
          function planProperty(raw) {
            exact(raw,['full_name','id','is_select','name','system_id']);
            return {id:integer(raw.id,1),name:text(raw.name,0,256),fullName:text(raw.full_name,0,256),systemId:integer(raw.system_id),selected:flag(raw.is_select)};
          }
          function plan(raw) {
            if(raw===null)return null;
            exact(raw,['cultivate_info','custom_info','equip_rating','equip_rating_score','game_default','plan_effective_property_list','plan_only_special_property','type','valid_property_cnt']);
            exact(raw.custom_info,['property_list']);exact(raw.game_default,['property_list']);
            const cultivation=exact(raw.cultivate_info,['is_delete','name','old_plan','plan_id']);
            return {type:integer(raw.type),rating:text(raw.equip_rating,0,64),score:finite(raw.equip_rating_score,0,10000),validProperties:bounded(raw.valid_property_cnt,0,10000),onlySpecial:flag(raw.plan_only_special_property),
              defaults:unique(raw.game_default.property_list,128,planProperty,'id'),custom:unique(raw.custom_info.property_list,128,planProperty,'id'),effective:unique(raw.plan_effective_property_list,128,planProperty,'id'),
              cultivation:{id:text(cultivation.plan_id,0,256),name:sourceText(cultivation.name,256),deleted:flag(cultivation.is_delete),old:flag(cultivation.old_plan)} };
          }
          function skin(raw) {
            exact(raw,['is_original','rarity','skin_hollow_icon_path','skin_id','skin_name','skin_square_url','skin_vertical_painting_color','skin_vertical_painting_url','unlocked']);
            return {id:integer(raw.skin_id),name:text(raw.skin_name,1,256),rarity:text(raw.rarity,1,16),original:flag(raw.is_original),unlocked:flag(raw.unlocked)};
          }
          function awakening(raw) {
            exact(raw,['awaken_level','awaken_max_level','has_awaken_system','skill_awaken_items']);
            const result={available:flag(raw.has_awaken_system),level:bounded(raw.awaken_level,0,100),maxLevel:bounded(raw.awaken_max_level,0,100),levels:unique(raw.skill_awaken_items,64,level=>{
              exact(level,['awaken_level','awaken_skill_items','level_show_name']);
              return {level:bounded(level.awaken_level,1,100),name:text(level.level_show_name,0,256),skills:unique(level.awaken_skill_items,64,part=>{
                exact(part,['awaken_simple_info','skill_items','skill_type']);
                return {type:bounded(part.skill_type,0,64),summary:sourceText(part.awaken_simple_info,8192),items:rows(part.skill_items,64).map(item=>{
                  exact(item,['text','title']);return {title:text(item.title,0,256),text:sourceText(item.text,16384)};
                })};
              },'type')};
            },'level')};
            if(result.level>result.maxLevel)failure('needs-review');return result;
          }
          const sharedKeys=['id','level','name_mi18n','full_name_mi18n','element_type','sub_element_type','camp_name_mi18n','avatar_profession','rarity','rank','awaken_state'];
          function avatar(raw) {
            exact(raw,['avatar_profession','awaken_state','camp_name_mi18n','element_type','equip','equip_plan_info','full_name_mi18n','group_icon_path','hollow_icon_path','id','level','name_mi18n','properties','rank','ranks','rarity','role_square_url','role_vertical_painting_url','skill_awaken','skills','skin_list','sub_element_type','us_full_name','vertical_painting_color','weapon']);
            return {id:integer(raw.id,1),name:text(raw.name_mi18n,1,256),fullName:text(raw.full_name_mi18n,0,256),englishName:text(raw.us_full_name,0,256),camp:text(raw.camp_name_mi18n,0,256),
              level:bounded(raw.level,1,100),rank:bounded(raw.rank,0,6),rarity:text(raw.rarity,1,16),element:integer(raw.element_type),subElement:integer(raw.sub_element_type),profession:integer(raw.avatar_profession),awakenState:text(raw.awaken_state,1,64),
              stats:unique(raw.properties,128,stat,'id'),equipment:unique(raw.equip,6,equipment,'position'),weapon:weapon(raw.weapon),skills:unique(raw.skills,64,skill,'type'),ranks:unique(raw.ranks,6,rank,'position'),
              plan:plan(raw.equip_plan_info),skins:unique(raw.skin_list,64,skin,'id'),awakening:awakening(raw.skill_awaken)};
          }
          function roster(raw,count) {
            exact(raw,['avatar_list']);
            const result=unique(raw.avatar_list,config.maximumAvatars,row=>{
              exact(row,['id','level','name_mi18n','full_name_mi18n','element_type','camp_name_mi18n','avatar_profession','rarity','group_icon_path','hollow_icon_path','rank','is_chosen','role_square_url','sub_element_type','awaken_state']);
              integer(row.id,1);bounded(row.level,1,100);bounded(row.rank,0,6);integer(row.element_type);integer(row.sub_element_type);integer(row.avatar_profession);flag(row.is_chosen);
              text(row.name_mi18n,1,256);text(row.full_name_mi18n,0,256);text(row.camp_name_mi18n,0,256);text(row.rarity,1,16);text(row.awaken_state,1,64);
              return Object.fromEntries(sharedKeys.map(key=>[key,row[key]]));
            },'id').sort((a,b)=>a.id-b.id);
            if(result.length!==count)failure('needs-review');return result;
          }
          function detail(raw,expected) {
            exact(raw,['avatar_list','equip_wiki','weapon_wiki','avatar_wiki','strategy_wiki','cultivate_index','cultivate_equip','special_skill_icon']);
            if(rows(raw.avatar_list,1).length!==1)failure('needs-review');
            const row=raw.avatar_list[0];
            if(!plain(row)||!sharedKeys.every(key=>row[key]===expected[key]))failure('needs-review');
            return avatar(row);
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
          const recordUrl = path => {
            const url=new URL(recordBase+path);url.searchParams.set('role_id',config.roleId);url.searchParams.set('server',config.server);return url;
          };
          async function readRoster() {
            const index=await request(recordUrl('index').href);
            if(!plain(index.stats))failure('needs-review');
            const count=bounded(index.stats.avatar_num,0,config.maximumAvatars);
            return roster(await request(recordUrl('avatar/basic').href),count);
          }
          void (async () => {
            let builds=null;
            try {
              await verifyRole();
              const before=await readRoster();
              const avatars=[];
              for(const expected of before) {
                const url=recordUrl('avatar/info');url.searchParams.set('id_list[]',String(expected.id));url.searchParams.set('need_wiki','true');
                avatars.push(detail(await request(url.href),expected));
              }
              const after=await readRoster();
              if(JSON.stringify(before)!==JSON.stringify(after))failure('needs-review');
              await verifyRole();current();
              builds={avatars};
              const result={status:'done',roleId:config.roleId,server:config.server,count:avatars.length,builds};
              const encoded=new TextEncoder().encode(JSON.stringify(result));
              try {if(encoded.length>config.maximumResultBytes)failure('too-large');} finally {encoded.fill(0);}
              state.result=result;
            } catch(error) {
              builds=null;
              if(window[config.key]===state)state.result={status:timedOut?'timed-out':controller.signal.aborted?'canceled'
                :['login-required','too-large','timed-out','needs-review'].includes(error)?error:'needs-review'};
            } finally {clearTimeout(timeout);controller.abort();}
          })();
          return 'started';
        })()
        """;
    }
}
