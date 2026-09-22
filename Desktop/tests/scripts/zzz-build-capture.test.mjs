import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const fixture=JSON.parse(fs.readFileSync(new URL('../../../contracts/hoyolab-zzz-builds-v1.fixture.json',import.meta.url),'utf8'));
const roleId='123456789',server='prod_gf_eu',key='__pengoNyxZzzBuild_fixture';
const roleEndpoint='https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const config={key,roleId,server,roleEndpoint,gameBiz:'nap_global',maximumAvatars:512,maximumResultBytes:3145728,timeoutMilliseconds:120000};
const source=fs.readFileSync(new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabZzzBuildCapture.cs',import.meta.url),'utf8');
const start=source.indexOf('return $$"""')+'return $$"""'.length;
assert.ok(start>12);
const script=source.slice(start,source.indexOf('\n        """;',start)).replace('{{configuration}}',JSON.stringify(config));
const clone=value=>JSON.parse(JSON.stringify(value));
function scenario(options={}){
  const context={window:{},AbortController,TextEncoder,TextDecoder,Uint8Array,URL};
  const requests=[],timers=[],counts=new Map();
  context.setTimeout=(callback,delay)=>{const timer={callback,delay,cleared:false};timers.push(timer);if(delay===250)queueMicrotask(()=>{if(!timer.cleared)callback();});return timer;};
  context.clearTimeout=timer=>{timer.cleared=true;};
  context.fetch=async(url,init)=>{
    requests.push({url,init});const parsed=new URL(url);const path=parsed.pathname;
    const occurrence=(counts.get(path)||0)+1;counts.set(path,occurrence);let data;
    if(parsed.origin+path===roleEndpoint){
      data={list:[{game_biz:config.gameBiz,region:server,game_uid:options.changeRole&&occurrence===2?'999999999':roleId}]};
    }else{
      assert.equal(parsed.origin,'https://sg-act-public-api.hoyolab.com');
      assert.equal(parsed.searchParams.get('role_id'),roleId);assert.equal(parsed.searchParams.get('server'),server);
      if(path.endsWith('/avatar/basic'))data=clone(fixture.source.basic);
      else if(path.endsWith('/index'))data=clone(fixture.source.index);
      else {
        assert.equal(path,'/event/game_record_zzz/api/zzz/avatar/info');
        assert.equal(parsed.searchParams.get('need_wiki'),'true');assert.equal(parsed.searchParams.getAll('id_list[]').length,1);
        const id=Number(parsed.searchParams.get('id_list[]'));data=clone(fixture.source.details.find(row=>row.avatar_list[0].id===id));
      }
      options.mutate?.(data,path,occurrence,parsed);
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
test('complete owned roster preserves empty equipment, awakening, skins and exact source text',async()=>{
  const run=scenario(),output=await result(run);assert.equal(output.status,'done');assert.equal(output.count,2);assert.deepEqual(output.builds,fixture.snapshot);
  assert.equal(run.requests.length,8);
  assert.equal(new URL(run.requests[0].url).pathname,new URL(roleEndpoint).pathname);assert.equal(new URL(run.requests.at(-1).url).pathname,new URL(roleEndpoint).pathname);
  for(const {init} of run.requests){assert.equal(init.method,'GET');assert.equal(init.body,undefined);assert.equal(init.credentials,'include');assert.equal(init.redirect,'error');}
  assert.equal(JSON.stringify(output).includes('example.invalid'),false);
  assert.equal(output.builds.avatars[0].stats[0].base,'');assert.equal(output.builds.avatars[0].stats[0].final,'12.30%');
  assert.equal(output.builds.avatars[0].plan.score,86.17);assert.equal(output.builds.avatars[1].weapon,null);assert.deepEqual(output.builds.avatars[1].equipment,[]);
});
for(const [name,mutate] of [
  ['overview count proves a partial basic list is incomplete',(data,path)=>{if(path.endsWith('/index'))data.stats.avatar_num=39;}],
  ['duplicate roster IDs',(data,path)=>{if(path.endsWith('/avatar/basic'))data.avatar_list[1]=clone(data.avatar_list[0]);}],
  ['new roster continuation field',(data,path)=>{if(path.endsWith('/avatar/basic'))data.next='unqualified';}],
  ['roster changed after capture',(data,path,occurrence)=>{if(path.endsWith('/avatar/basic')&&occurrence===2)data.avatar_list[0].level=59;}],
  ['overview changed after capture',(data,path,occurrence)=>{if(path.endsWith('/index')&&occurrence===2)data.stats.avatar_num=3;}],
  ['wrong detail identity',(data,path)=>{if(path.endsWith('/avatar/info'))data.avatar_list[0].id=9999;}],
  ['empty detail response',(data,path)=>{if(path.endsWith('/avatar/info'))data.avatar_list=[];}],
  ['unexpected extra detail row',(data,path)=>{if(path.endsWith('/avatar/info'))data.avatar_list.push(clone(data.avatar_list[0]));}],
  ['unknown Agent field',(data,path)=>{if(path.endsWith('/avatar/info'))data.avatar_list[0].future_payload={};}],
  ['missing equipment field',(data,path)=>{if(path.endsWith('/avatar/info'))delete data.avatar_list[0].weapon;}],
  ['duplicate equipment slot',(data,path)=>{if(path.endsWith('/avatar/info')){const row=data.avatar_list[0];if(row.equip.length)row.equip.push(clone(row.equip[0]));}}],
  ['numeric stat loses original precision',(data,path)=>{if(path.endsWith('/avatar/info'))data.avatar_list[0].properties[0].final=12.3;}],
  ['invalid awakening level',(data,path)=>{if(path.endsWith('/avatar/info'))data.avatar_list[0].skill_awaken.awaken_level=7;}],
  ['unsafe control in effect text',(data,path)=>{if(path.endsWith('/avatar/info'))data.avatar_list[0].ranks[0].desc='bad\u202Etext';}],
])test(name+' rejects without publishing a partial build',async()=>{const output=await result(scenario({mutate}));assert.deepEqual(output,{status:'needs-review'});});
test('an explicitly empty full roster stays an empty snapshot, not missing data',async()=>{
  const run=scenario({mutate:(data,path)=>{if(path.endsWith('/index'))data.stats.avatar_num=0;if(path.endsWith('/avatar/basic'))data.avatar_list=[];}});
  assert.deepEqual((await result(run)).builds,{avatars:[]});assert.equal(run.requests.length,6);
});
test('row order changes do not lose or invent ownership',async()=>{
  const run=scenario({mutate:(data,path,occurrence)=>{if(path.endsWith('/avatar/basic')&&occurrence===2)data.avatar_list.reverse();}});
  assert.deepEqual((await result(run)).builds,fixture.snapshot);
});
for(const [name,options,status] of [
  ['role changes',{changeRole:true},'needs-review'],['login response',{retcode:-100},'login-required'],
  ['unauthorized HTTP',{status:401},'login-required'],['redirect',{redirect:true},'needs-review'],
  ['wrong media type',{contentType:'text/html'},'needs-review'],['cancellation',{cancel:true},'canceled'],
  ['oversized response',{padding:8388609,chunkBytes:65536},'too-large'],
])test(name+' never publishes private partial rows',async()=>{assert.deepEqual(await result(scenario(options)),{status});});
test('whole capture deadline remains bounded despite per-Agent requests',async()=>{
  const run=scenario();run.timers.find(timer=>timer.delay===120000).callback();assert.deepEqual(await result(run),{status:'timed-out'});
});
