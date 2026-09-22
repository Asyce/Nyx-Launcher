import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const fixture=JSON.parse(fs.readFileSync(new URL('../../../contracts/hoyolab-zzz-endgame-v1.fixture.json',import.meta.url),'utf8'));
const roleId='123456789',server='prod_gf_eu',key='__pengoNyxZzzEndgame_fixture';
const roleEndpoint='https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const config={key,roleId,server,roleEndpoint,gameBiz:'nap_global',maximumResultBytes:3145728,timeoutMilliseconds:45000};
const source=fs.readFileSync(new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabZzzEndgameCapture.cs',import.meta.url),'utf8');
const start=source.indexOf('return $$"""')+'return $$"""'.length;
assert.ok(start>12);
const script=source.slice(start,source.indexOf('\n        """;',start)).replace('{{configuration}}',JSON.stringify(config));
const clone=value=>JSON.parse(JSON.stringify(value));
function scenario(options={}){
  const context={window:{},AbortController,TextEncoder,TextDecoder,Uint8Array,URL};
  const requests=[],timers=[];let roles=0;
  context.setTimeout=(callback,delay)=>{const timer={callback,delay,cleared:false};timers.push(timer);if(delay===250)queueMicrotask(()=>{if(!timer.cleared)callback();});return timer;};
  context.clearTimeout=timer=>{timer.cleared=true;};
  context.fetch=async(url,init)=>{
    requests.push({url,init});const parsed=new URL(url);let data;
    if(parsed.origin+parsed.pathname===roleEndpoint){
      roles++;data={list:[{game_biz:config.gameBiz,region:server,game_uid:options.changeRole&&roles===2?'999999999':roleId}]};
    }else{
      assert.equal(parsed.origin,'https://sg-act-public-api.hoyolab.com');
      assert.equal(parsed.pathname,'/event/game_record_zzz/api/zzz/hadal_info_v2');
      assert.equal(parsed.searchParams.get('role_id'),roleId);assert.equal(parsed.searchParams.get('server'),server);
      const period=Number(parsed.searchParams.get('schedule_type'));assert.ok([1,2].includes(period));
      data=clone(fixture.source[period-1]);options.mutate?.(data,period);
      if(options.cancel)context.window[key].abort();
    }
    const body={retcode:options.retcode??0,data};if(options.padding)body.padding='x'.repeat(options.padding);
    const bytes=new TextEncoder().encode(JSON.stringify(body));let offset=0;
    return {status:options.status??200,url:options.redirect?url+'&redirected=1':url,redirected:!!options.redirect,
      headers:{get:()=>options.contentType??'application/json'},body:{getReader:()=>({
        async read(){if(offset>=bytes.length)return {done:true};const value=bytes.slice(offset,offset+(options.chunkBytes??97));offset+=value.length;return {done:false,value};},
        async cancel(){offset=bytes.length;},releaseLock(){},
      })}};
  };
  vm.createContext(context);assert.equal(vm.runInContext(script,context),'started');
  return {requests,timers,state:()=>context.window[key]};
}
async function result(run){for(let i=0;i<40000;i++){await Promise.resolve();if(run.state().result)return clone(run.state().result);}throw Error('Capture did not settle');}
test('both Fourth/Fifth Frontier periods retain precise source semantics and own-role boundaries',async()=>{
  const run=scenario(),output=await result(run);assert.equal(output.status,'done');assert.equal(output.count,2);assert.deepEqual(output.endgame,fixture.snapshot);
  assert.equal(run.requests.length,4);
  assert.equal(new URL(run.requests[0].url).pathname,new URL(roleEndpoint).pathname);assert.equal(new URL(run.requests.at(-1).url).pathname,new URL(roleEndpoint).pathname);
  for(const {init} of run.requests){assert.equal(init.method,'GET');assert.equal(init.body,undefined);assert.equal(init.credentials,'include');assert.equal(init.redirect,'error');}
  assert.equal(JSON.stringify(output).includes('example.invalid'),false);assert.equal(JSON.stringify(output).includes('Synthetic only'),false);
  assert.equal(output.endgame.shiyu[0].summary.rankPercentHundredths,1234);
  assert.equal(output.endgame.shiyu[0].fourth.teams[0].score,undefined);
  assert.equal(output.endgame.shiyu[0].fifth.teams[0].time.second,7);
});
for(const [name,mutate] of [
  ['different source version',raw=>{raw.hadal_ver='v3';}],
  ['unknown continuation',raw=>{raw.next='unqualified';}],
  ['missing older period',(raw,period)=>{if(period===2)raw.hadal_info_v2=null;}],
  ['period identity repeated',raw=>{raw.hadal_info_v2.zone_id=1;}],
  ['fourth frontier absent',raw=>{raw.hadal_info_v2.fourth_layer_detail=null;}],
  ['fifth frontier partial',raw=>{raw.hadal_info_v2.fitfh_layer_detail.layer_challenge_info_list.pop();}],
  ['unexpected old floor',raw=>{raw.hadal_info_v2.third_layer_detail={};}],
  ['team duplicated',raw=>{const rows=raw.hadal_info_v2.fitfh_layer_detail.layer_challenge_info_list;rows[1].layer_id=rows[0].layer_id;}],
  ['cross-frontier team duplicated',raw=>{raw.hadal_info_v2.fitfh_layer_detail.layer_challenge_info_list[0].layer_id=1;}],
  ['Agent duplicated',raw=>{const rows=raw.hadal_info_v2.fourth_layer_detail.layer_challenge_info_list[0].avatar_list;rows[1]=clone(rows[0]);}],
  ['unqualified absent Buddy',raw=>{raw.hadal_info_v2.fourth_layer_detail.layer_challenge_info_list[0].buddy=null;}],
  ['score total incomplete',raw=>{raw.hadal_info_v2.brief.score++;}],
  ['maximum total incomplete',raw=>{raw.hadal_info_v2.brief.max_score++;}],
  ['rank percentage interpreted as decimal',raw=>{raw.hadal_info_v2.brief.rank_percent=12.34;}],
  ['rank exceeds 100 percent',raw=>{raw.hadal_info_v2.brief.rank_percent=10001;}],
  ['invalid calendar day',raw=>{raw.hadal_info_v2.hadal_begin_time.month=2;raw.hadal_info_v2.hadal_begin_time.day=30;}],
  ['missing source seconds',raw=>{delete raw.hadal_info_v2.hadal_begin_time.second;}],
  ['epoch numeric instead of exact string',raw=>{raw.hadal_info_v2.begin_time=1893456000;}],
  ['inverted period',raw=>{raw.hadal_info_v2.end_time=raw.hadal_info_v2.begin_time;}],
  ['unsafe directional control',raw=>{raw.hadal_info_v2.fourth_layer_detail.buffer.text='bad\u202Etext';}],
])test(name+' rejects atomically',async()=>{assert.deepEqual(await result(scenario({mutate})),{status:'needs-review'});});
for(const [name,options,status] of [
  ['own role changes',{changeRole:true},'needs-review'],['login response',{retcode:-100},'login-required'],
  ['unauthorized HTTP',{status:401},'login-required'],['redirect',{redirect:true},'needs-review'],
  ['wrong media type',{contentType:'text/html'},'needs-review'],['cancellation',{cancel:true},'canceled'],
  ['oversized response',{padding:8388609,chunkBytes:65536},'too-large'],
])test(name+' never returns partial periods',async()=>{assert.deepEqual(await result(scenario(options)),{status});});
test('the overall source deadline is bounded',async()=>{
  const run=scenario();run.timers.find(timer=>timer.delay===45000).callback();assert.deepEqual(await result(run),{status:'timed-out'});
});
