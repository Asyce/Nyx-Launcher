import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const fixture=JSON.parse(fs.readFileSync(new URL('../../../contracts/hoyolab-hsr-challenges-v1.fixture.json',import.meta.url),'utf8'));
const roleId='123456789',server='prod_official_eur',key='__pengoNyxHsrEndgame_fixture';
const roleEndpoint='https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const config={key,roleId,server,roleEndpoint,gameBiz:'hkrpg_global',maximumFloors:128,maximumResultBytes:3145728,timeoutMilliseconds:45000};
const source=fs.readFileSync(new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabHsrEndgameCapture.cs',import.meta.url),'utf8');
const start=source.indexOf('return $$"""')+'return $$"""'.length;
assert.ok(start>12);
const script=source.slice(start,source.indexOf('\n        """;',start)).replace('{{configuration}}',JSON.stringify(config));
const clone=value=>JSON.parse(JSON.stringify(value));

function scenario(options={}){
  const context={window:{},AbortController,TextEncoder,TextDecoder,Uint8Array,URL};
  const requests=[],timers=[];
  let roleCalls=0;
  context.setTimeout=(callback,delay)=>{
    const timer={callback,delay,cleared:false};timers.push(timer);
    if(delay===250)queueMicrotask(()=>{if(!timer.cleared)callback();});
    return timer;
  };
  context.clearTimeout=timer=>{timer.cleared=true;};
  context.fetch=async(url,init)=>{
    requests.push({url,init});const parsed=new URL(url);let data;
    if(parsed.origin+parsed.pathname===roleEndpoint){
      roleCalls++;
      data={list:[{game_biz:config.gameBiz,region:server,game_uid:options.changeRole&&roleCalls===2?'999999999':roleId}]};
    }else{
      assert.equal(parsed.origin,'https://sg-act-public-api.hoyolab.com');
      assert.equal(parsed.searchParams.get('role_id'),roleId);
      assert.equal(parsed.searchParams.get('server'),server);
      assert.equal(parsed.searchParams.get('need_all'),'true');
      const period=Number(parsed.searchParams.get('schedule_type'));
      const row=fixture.source.find(row=>parsed.pathname==='/event/game_record/hkrpg/api/'+row.endpoint&&row.period===period);
      assert.ok(row);data=clone(row.data);options.mutate?.(data,period,row.kind);
      if(options.cancel)context.window[key].abort();
    }
    const bytes=new TextEncoder().encode(JSON.stringify({retcode:options.retcode??0,data}));let offset=0;
    return {status:200,url:options.redirect?url+'&redirect=1':url,redirected:!!options.redirect,
      headers:{get:()=> 'application/json'},body:{getReader:()=>({
        async read(){if(offset>=bytes.length)return {done:true};const value=bytes.slice(offset,offset+(options.chunkBytes??97));offset+=value.length;return {done:false,value};},
        async cancel(){offset=bytes.length;},releaseLock(){},
      })}};
  };
  vm.createContext(context);assert.equal(vm.runInContext(script,context),'started');
  return {requests,timers,state:()=>context.window[key]};
}
async function result(run){
  for(let i=0;i<30000;i++){await Promise.resolve();if(run.state().result)return clone(run.state().result);}
  throw Error('Capture did not settle');
}

test('six complete periods retain third teams, quick clears and exact source scope',async()=>{
  const run=scenario(),output=await result(run);
  assert.equal(output.status,'done');assert.equal(output.count,6);assert.deepEqual(output.endgame,fixture.snapshot);
  assert.equal(run.requests.length,8);
  assert.equal(new URL(run.requests[0].url).pathname,new URL(roleEndpoint).pathname);
  assert.equal(new URL(run.requests.at(-1).url).pathname,new URL(roleEndpoint).pathname);
  for(const {init} of run.requests){assert.equal(init.method,'GET');assert.equal(init.body,undefined);assert.equal(init.credentials,'include');assert.equal(init.redirect,'error');}
  assert.equal(JSON.stringify(output).includes('example.invalid'),false);
});
for(const [name,mutate] of [
  ['missing required floor',d=>{delete d.all_floor_detail;}],
  ['unknown continuation',d=>{d.next_cursor='synthetic';}],
  ['inconsistent paired schedules',(d,p)=>{if(p===2)d.groups[0].name_mi18n='Changed schedule';}],
  ['duplicate floor',d=>{d.all_floor_detail.push(clone(d.all_floor_detail[0]));}],
  ['duplicate team member',d=>{d.all_floor_detail[0].node_1.avatars.push(clone(d.all_floor_detail[0].node_1.avatars[0]));}],
  ['invalid calendar',d=>{d.all_floor_detail[0].node_1.challenge_time.day=30;}],
  ['missing third-team marker',d=>{delete d.all_floor_detail[0].node_3;}],
  ['wrong base-star equation',d=>{d.star_num=7;}],
  ['wrong extra-star equation',d=>{d.extra_star_num=0;}],
  ['unsupported private field',d=>{d.all_floor_detail[0].node_1.avatars[0].account_id='synthetic';}],
  ['noncanonical decimal star',(d,p,k)=>{if(k==='apocalyptic-shadow')d.all_floor_detail[0].star_num='04';}],
  ['numeric score instead of source text',(d,p,k)=>{if(k==='pure-fiction')d.all_floor_detail[0].node_1.score=4001;}],
  ['oversized source list',d=>{d.all_floor_detail=Array.from({length:129},()=>clone(d.all_floor_detail[0]));}],
])test(name+' rejects the entire six-period copy',async()=>{
  assert.deepEqual(await result(scenario({mutate})),{status:'needs-review'});
});
test('large decimal scores retain precision',async()=>{
  const score='9007199254740993123456789';
  const output=await result(scenario({mutate(d,p,k){if(k!=='forgotten-hall')d.all_floor_detail[0].node_1.score=score;}}));
  assert.equal(output.status,'done');assert.equal(output.endgame.challenges[4].floors[0].nodes[0].score,score);
});
test('role changes, authentication, redirects, cancellation and deadlines publish no partial result',async()=>{
  assert.deepEqual(await result(scenario({changeRole:true})),{status:'needs-review'});
  assert.deepEqual(await result(scenario({retcode:-100})),{status:'login-required'});
  assert.deepEqual(await result(scenario({redirect:true})),{status:'needs-review'});
  assert.deepEqual(await result(scenario({cancel:true})),{status:'canceled'});
  const timed=scenario();timed.timers.find(timer=>timer.delay===45000).callback();
  assert.deepEqual(await result(timed),{status:'timed-out'});
});
test('transport byte budget rejects an oversized response before projection',async()=>{
  const output=await result(scenario({chunkBytes:65536,mutate(d){d.padding='x'.repeat(8388608);}}));
  assert.deepEqual(output,{status:'too-large'});
});
test('explicit empty periods remain distinct from a missing period response',async()=>{
  const output=await result(scenario({mutate(d){d.all_floor_detail=[];d.has_data=false;d.star_num=0;d.extra_star_num=0;d.max_floor='';d.max_floor_id=0;d.battle_num=0;}}));
  assert.equal(output.status,'done');assert.equal(output.endgame.challenges.length,6);
  assert.ok(output.endgame.challenges.every(p=>!p.hasData&&p.floors.length===0));
});
