import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const fixture = JSON.parse(fs.readFileSync(new URL('../../../contracts/hoyolab-genshin-abyss-v1.fixture.json',import.meta.url),'utf8'));
const roleId = '123456789', server = 'os_euro', key = '__pengoNyxGenshinEndgame_fixture';
const roleEndpoint = 'https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const config = { key,roleId,server,roleEndpoint,gameBiz:'hk4e_global',maximumFloors:128,maximumResultBytes:3145728,timeoutMilliseconds:45000 };
const source = fs.readFileSync(new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabGenshinEndgameCapture.cs',import.meta.url),'utf8');
const start = source.indexOf('return $$"""') + 'return $$"""'.length;
assert.ok(start > 12);
const script = source.slice(start,source.indexOf('\n        """;',start)).replace('{{configuration}}',JSON.stringify(config));
const clone = value => JSON.parse(JSON.stringify(value));

function scenario(options = {}) {
  const context = { window:{}, AbortController, TextEncoder, TextDecoder, Uint8Array, URL };
  const requests = [], timers = [];
  let roleCalls = 0;
  context.setTimeout = (callback,delay) => {
    const timer = {callback,delay,cleared:false}; timers.push(timer);
    if (delay === 250) queueMicrotask(() => { if (!timer.cleared) callback(); });
    return timer;
  };
  context.clearTimeout = timer => { timer.cleared = true; };
  context.fetch = async (url,init) => {
    requests.push({url,init});
    const parsed = new URL(url);
    let data;
    if (parsed.origin + parsed.pathname === roleEndpoint) {
      roleCalls++;
      data = {list:[{game_biz:'hk4e_global',region:server,game_uid:options.changeRole && roleCalls === 2 ? '999999999' : roleId}]};
    } else {
      assert.equal(parsed.origin + parsed.pathname,'https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/spiralAbyss');
      assert.equal(parsed.searchParams.get('role_id'),roleId);
      assert.equal(parsed.searchParams.get('server'),server);
      const period = Number(parsed.searchParams.get('schedule_type'));
      assert.ok(period === 1 || period === 2);
      data = clone(fixture.source[period-1]);
      options.mutate?.(data,period);
      if (options.cancel) context.window[key].abort();
    }
    const bytes = new TextEncoder().encode(JSON.stringify({retcode:options.retcode ?? 0,data}));
    let offset=0;
    return {status:200,url:options.redirect ? url + '&redirect=1' : url,redirected:!!options.redirect,
      headers:{get:() => 'application/json'},body:{getReader:() => ({
        async read(){ if(offset >= bytes.length) return {done:true}; const value=bytes.slice(offset,offset+97);offset+=value.length;return {done:false,value}; },
        async cancel(){ offset=bytes.length; },releaseLock(){},
      })}};
  };
  vm.createContext(context);
  assert.equal(vm.runInContext(script,context),'started');
  return {context,requests,timers,state:() => context.window[key]};
}
async function result(scenario) {
  for(let i=0;i<30000;i++){await Promise.resolve();if(scenario.state().result)return clone(scenario.state().result);}
  throw Error('Capture did not settle');
}

test('both periods use exact read-only URLs between two account-binding checks',async () => {
  const run=scenario(); const output=await result(run);
  assert.equal(output.status,'done');assert.equal(output.count,2);
  assert.deepEqual(output.endgame,fixture.snapshot);
  assert.equal(run.requests.length,4);
  for(const {init} of run.requests){assert.equal(init.method,'GET');assert.equal(init.body,undefined);assert.equal(init.credentials,'include');assert.equal(init.redirect,'error');}
  assert.equal(JSON.stringify(output).includes('example.invalid'),false,'presentation URLs must not enter the account copy');
});

for (const [name,mutate] of [
  ['missing chamber data',data => {delete data.floors[0].levels;}],
  ['unknown continuation',data => {data.next_cursor='synthetic-next';}],
  ['duplicate period identity',(data) => {data.schedule_id=49;}],
  ['duplicate floor',data => {data.floors.push(clone(data.floors[0]));}],
  ['duplicate avatar',data => {data.floors[0].levels[0].battles[0].avatars.push(clone(data.floors[0].levels[0].battles[0].avatars[0]));}],
  ['invalid calendar day',data => {data.floors[0].levels[0].battles[0].settle_date_time.day=30;}],
  ['inconsistent stars',data => {data.floors[0].star=4;}],
  ['unknown ranking fields',data => {data.damage_rank[0].account_id='synthetic';}],
  ['oversize source list',data => {data.floors=Array.from({length:129},() => clone(data.floors[0]));}],
]) test(name+' rejects the entire observation',async () => {
  assert.deepEqual(await result(scenario({mutate})),{status:'needs-review'});
});

test('a role change before publication discards both successful periods',async () => {
  assert.deepEqual(await result(scenario({changeRole:true})),{status:'needs-review'});
});
test('cancellation and authentication failures never return a partial copy',async () => {
  assert.deepEqual(await result(scenario({cancel:true})),{status:'canceled'});
  assert.deepEqual(await result(scenario({retcode:-100})),{status:'login-required'});
  assert.deepEqual(await result(scenario({redirect:true})),{status:'needs-review'});
});
test('explicit empty periods and skip-only records remain distinct from missing data',async () => {
  const output=await result(scenario({mutate(data){
    data.floors=[];data.is_just_skipped_floor=true;data.total_star=18;
    for(const name of ['reveal_rank','defeat_rank','damage_rank','take_damage_rank','normal_skill_rank','energy_skill_rank'])data[name]=[];
  }}));
  assert.equal(output.status,'done');assert.equal(output.endgame.abyss[0].justSkipped,true);
  assert.equal(output.endgame.abyss[0].skippedFloor,'10-3');assert.equal(output.endgame.abyss[0].floors.length,0);
});
