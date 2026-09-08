import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const roleId = '123456789';
const server = 'os_euro';
const key = '__pengoNyxGenshinExploration_fixture';
const roleEndpoint = 'https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/';
const roleUrl = `${roleEndpoint}?game_biz=hk4e_global&region=${server}`;
const indexUrl = `${recordBase}index?server=${server}&role_id=${roleId}`;
const config = {
  key,
  roleId,
  server,
  roleEndpoint,
  gameBiz: 'hk4e_global',
  maximumWorlds: 512,
  maximumResultBytes: 3 * 1024 * 1024,
  timeoutMilliseconds: 45_000,
};
const countFields = {
  anemoculi: 'anemoculus_number',
  geoculi: 'geoculus_number',
  electroculi: 'electroculus_number',
  dendroculi: 'dendroculus_number',
  hydroculi: 'hydroculus_number',
  pyroculi: 'pyroculus_number',
  lunoculi: 'moonoculus_number',
  cryoculi: 'iceculus_number',
  commonChests: 'common_chest_number',
  exquisiteChests: 'exquisite_chest_number',
  preciousChests: 'precious_chest_number',
  luxuriousChests: 'luxurious_chest_number',
  remarkableChests: 'magic_chest_number',
  waypoints: 'way_point_number',
  domains: 'domain_number',
};

function extractScript() {
  const sourcePath = new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabGenshinExplorationCapture.cs', import.meta.url);
  const source = fs.readFileSync(sourcePath, 'utf8');
  const matches = [...source.matchAll(/return \$\$"""/g)];
  assert.equal(matches.length, 1, 'the source must contain exactly one raw exploration script');
  const start = matches[0].index + matches[0][0].length;
  const end = source.indexOf('\n        """;', start);
  assert.ok(end > start, 'the raw exploration script must have a closing delimiter');
  const body = source.slice(start + 1, end).split(/\r?\n/);
  const indent = Math.min(...body.filter(line => line.trim()).map(line => line.match(/^ */)[0].length));
  return body.map(line => line.slice(Math.min(indent, line.length))).join('\n')
    .replaceAll('{{configuration}}', JSON.stringify(config));
}

const script = extractScript();

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function stats() {
  return Object.fromEntries(Object.entries(countFields).map(([name, sourceName], index) => [
    sourceName,
    index === 0 ? 1234 : index + 1,
  ]));
}

function world(id, options = {}) {
  return {
    id,
    parent_id: options.parentId ?? 0,
    name: options.name ?? `Synthetic World ${id}`,
    type: options.kind ?? 'Nation',
    world_type: options.worldType ?? 1,
    level: options.level ?? id,
    exploration_percentage: options.percentage ?? (id === 1 ? 1234 : id + 1000),
    seven_statue_level: options.statueLevel ?? id,
    index_active: options.indexActive ?? true,
    detail_active: options.detailActive ?? true,
    offerings: options.offerings ?? [],
    area_exploration_list: options.areas ?? [],
    boss_list: options.bosses ?? [],
    natan_reputation: options.reputation ?? null,
  };
}

function offering(name, level, state) {
  return { name, level, open_state: state };
}

function area(name, percentage) {
  return { name, exploration_percentage: percentage };
}

function boss(name, kills) {
  return { name, kill_num: kills };
}

function baseWorlds() {
  return [
    world(1, {
      name: 'Mondstadt 😀',
      percentage: 1234,
      offerings: [offering('Locked Offering', 0, 'Locked'), offering('Unknown Offering', 1, 'Unknow')],
      areas: [area('Stormterror\'s Lair �', 2345)],
      bosses: [boss('Synthetic Boss', 0)],
      reputation: { tribal_list: [{ id: 11, name: 'Synthetic Tribe', level: 3 }] },
    }),
    world(2, {
      parentId: 1,
      name: 'Liyue',
      percentage: 9876,
      indexActive: false,
      reputation: { tribal_list: [] },
    }),
    world(3, {
      parentId: 1,
      name: 'Synthetic Subregion',
      kind: 'Subregion',
      worldType: 2,
      level: 1,
      percentage: 1001,
      statueLevel: 0,
      detailActive: false,
      reputation: null,
    }),
  ];
}

function displayGroups() {
  return [
    { exploration_id: 1, group: { items: [{ area_ids: [1, 2], exploration_percentage: 3456 }] } },
    { exploration_id: 2, group: { items: [{ area_ids: [3], exploration_percentage: 4567 }] } },
  ];
}

function indexData(overrides = {}) {
  return {
    stats: overrides.stats ?? stats(),
    world_explorations: overrides.worlds ?? baseWorlds(),
    world_exploration_display: overrides.displayGroups ?? displayGroups(),
    achievements: [{ id: 'raw-achievement-fixture' }],
    inventory: [{ id: 'raw-inventory-fixture' }],
  };
}

function responseEnvelope(data, retcode = 0) {
  return { retcode, data };
}

function jsonResponse(url, body, options = {}) {
  const bytes = options.rawBytes ?? new TextEncoder().encode(
    typeof body === 'string' ? body : JSON.stringify(body));
  let offset = 0;
  const chunkSize = options.chunkSize ?? bytes.length;
  const reader = {
    async read() {
      if (offset >= bytes.length) return { done: true, value: undefined };
      const value = bytes.slice(offset, offset + chunkSize);
      offset += value.length;
      return { done: false, value };
    },
    async cancel() {
      offset = bytes.length;
    },
    releaseLock() {},
  };
  return {
    status: options.status ?? 200,
    url,
    redirected: options.redirected ?? false,
    headers: {
      get: name => name.toLowerCase() === 'content-type'
        ? options.contentType ?? 'application/json'
        : null,
    },
    body: options.body === false ? null : { getReader: () => reader },
  };
}

function createScenario(overrides = {}) {
  const roles = overrides.roles ?? [
    { game_biz: 'hk4e_global', region: server, game_uid: roleId },
  ];
  const roleDataByCall = overrides.roleDataByCall ?? [];
  const data = overrides.indexData ?? indexData();
  const requests = [];
  const timers = [];
  let roleCalls = 0;
  let nextTimerId = 1;
  const setTimeoutFake = (callback, delay) => {
    const timer = { callback, delay, cleared: false, id: nextTimerId++ };
    timers.push(timer);
    if (delay === 250) queueMicrotask(() => { if (!timer.cleared) callback(); });
    return timer;
  };
  const clearTimeoutFake = timer => { if (timer) timer.cleared = true; };
  const padded = (url, body, retcode = 0) => {
    let text = JSON.stringify(responseEnvelope(body, retcode));
    if (overrides.responsePaddingBytes) text += ' '.repeat(overrides.responsePaddingBytes);
    return jsonResponse(url, text);
  };
  const defaultFetch = async url => {
    if (url === roleUrl)
      return padded(url, roleDataByCall[roleCalls++] ?? { list: roles }, overrides.retcode ?? 0);
    if (url === indexUrl)
      return padded(url, data, overrides.retcode ?? 0);
    throw new Error(`unexpected request: ${url}`);
  };
  const fetchImpl = async (url, options) => {
    requests.push({
      url,
      method: options.method,
      body: options.body,
      credentials: options.credentials,
      redirect: options.redirect,
      cache: options.cache,
      referrerPolicy: options.referrerPolicy,
      language: options.headers?.['x-rpc-language'],
    });
    return overrides.fetch
      ? overrides.fetch(url, options, defaultFetch)
      : defaultFetch(url, options);
  };
  const context = vm.createContext({
    AbortController,
    TextDecoder,
    TextEncoder,
    Uint8Array,
    URL,
    fetch: fetchImpl,
    setTimeout: setTimeoutFake,
    clearTimeout: clearTimeoutFake,
    console: { log: () => { throw new Error('capture script logged source text'); } },
  });
  context.window = {};
  assert.equal(vm.runInContext(script, context), 'started');
  return {
    context,
    requests,
    timers,
    state: () => context.window[key],
    fire: delay => timers.filter(timer => timer.delay === delay && !timer.cleared)
      .forEach(timer => timer.callback()),
  };
}

async function flush() {
  for (let index = 0; index < 6; index++) await Promise.resolve();
}

async function resultOf(scenario, attempts = 300) {
  for (let index = 0; index < attempts; index++) {
    await flush();
    const result = scenario.state()?.result;
    if (result) return result;
  }
  throw new Error(`capture did not finish; requests=${scenario.requests.length}`);
}

function assertNoPartialSuccess(result, expectedStatus = 'needs-review') {
  assert.equal(result.status, expectedStatus);
  assert.equal(Object.hasOwn(result, 'exploration'), false);
}

test('happy path uses the exact selected role, full index collection, and fifteen-count projection', async () => {
  const scenario = createScenario();
  const result = await resultOf(scenario);

  assert.equal(result.status, 'done');
  assert.equal(result.roleId, roleId);
  assert.equal(result.server, server);
  assert.equal(result.count, 3);
  assert.deepEqual([...result.exploration.worlds].map(row => row.id), [1, 2, 3]);
  assert.deepEqual(Object.keys(result.exploration.counts), Object.keys(countFields));
  assert.equal(Object.keys(result.exploration.counts).length, 15);
  assert.equal(result.exploration.counts.anemoculi, 1234);
  assert.equal(result.exploration.counts.domains, 15);
  assert.equal(result.exploration.worlds[0].percentage, 1234);
  assert.equal(result.exploration.worlds[0].areas[0].percentage, 2345);
  assert.equal(result.exploration.worlds[0].offerings[0].state, 'Locked');
  assert.equal(result.exploration.worlds[0].offerings[1].state, 'Unknow');
  assert.equal(result.exploration.worlds[0].bosses[0].kills, 0);
  assert.equal(result.exploration.worlds[0].tribes[0].name, 'Synthetic Tribe');
  assert.equal(result.exploration.worlds[1].tribes.length, 0);
  assert.equal(result.exploration.worlds[2].tribes, null);
  assert.equal(result.exploration.displayGroups[0].areas[0].percentage, 3456);
  assert.deepEqual([...result.exploration.displayGroups[0].areas[0].ids], [1, 2]);
  assert.equal(result.exploration.worlds[0].name, 'Mondstadt 😀');
  assert.equal(result.exploration.worlds[0].areas[0].name, "Stormterror's Lair �");
  assert.equal(Object.hasOwn(result.exploration, 'achievements'), false);
  assert.equal(Object.hasOwn(result.exploration, 'inventory'), false);

  assert.deepEqual(scenario.requests.map(({ url, method, body }) => ({ url, method, body })), [
    { url: roleUrl, method: 'GET', body: undefined },
    { url: indexUrl, method: 'GET', body: undefined },
    { url: roleUrl, method: 'GET', body: undefined },
  ]);
  assert.ok(scenario.requests.every(request => request.credentials === 'include'));
  assert.ok(scenario.requests.every(request => request.redirect === 'error'));
  assert.ok(scenario.requests.every(request => request.cache === 'no-store'));
  assert.ok(scenario.requests.every(request => request.referrerPolicy === 'no-referrer'));
  assert.ok(scenario.requests.every(request => request.language === 'en-us'));
  assert.ok(scenario.requests.every(request => request.body === undefined));
});

test('embedded script rejects lone UTF-16 labels while accepting paired supplementary and replacement characters', async () => {
  const malformed = indexData();
  malformed.world_explorations[0].name = '\uD800';
  assertNoPartialSuccess(await resultOf(createScenario({ indexData: malformed })));

  const valid = indexData();
  valid.world_explorations[0].name = 'Mondstadt \uD83D\uDE00';
  valid.world_explorations[0].area_exploration_list[0].name = 'Area \uFFFD';
  const result = await resultOf(createScenario({ indexData: valid }));
  assert.equal(result.status, 'done');
  assert.equal(result.exploration.worlds[0].name, 'Mondstadt 😀');
  assert.equal(result.exploration.worlds[0].areas[0].name, 'Area �');
});

test('busy state is untouched and the script has no UI, permission, or logging side effects', () => {
  assert.doesNotMatch(script, /permission|localStorage|sessionStorage|console\./i);
  const requests = [];
  const context = vm.createContext({
    AbortController,
    TextDecoder,
    TextEncoder,
    Uint8Array,
    URL,
    fetch: async (...args) => { requests.push(args); throw new Error('fetch must not run while busy'); },
    setTimeout,
    clearTimeout,
  });
  const existing = { result: 'existing' };
  context.window = { [key]: existing };
  assert.equal(vm.runInContext(script, context), 'busy');
  assert.equal(context.window[key], existing);
  assert.equal(requests.length, 0);
});

for (const [name, overrides] of [
  ['foreign role', { roles: [{ game_biz: 'hk4e_global', region: server, game_uid: '987654321' }] }],
  ['duplicate role', { roles: [
    { game_biz: 'hk4e_global', region: server, game_uid: roleId },
    { game_biz: 'hk4e_global', region: server, game_uid: roleId },
  ] }],
  ['final role loss', { roleDataByCall: [
    { list: [{ game_biz: 'hk4e_global', region: server, game_uid: roleId }] },
    { list: [] },
  ] }],
  ['missing stats', { indexData: { world_explorations: baseWorlds(), world_exploration_display: displayGroups() } }],
  ['wrong count type', { indexData: indexData({ stats: { ...stats(), anemoculus_number: '1234' } }) }],
  ['duplicate world ID', { indexData: indexData({ worlds: [...baseWorlds(), clone(baseWorlds()[0])] }) }],
  ['duplicate display group ID', { indexData: indexData({ displayGroups: [...displayGroups(), clone(displayGroups()[0])] }) }],
  ['duplicate tribe ID', (() => {
    const worlds = baseWorlds();
    worlds[0].natan_reputation.tribal_list.push(clone(worlds[0].natan_reputation.tribal_list[0]));
    return { indexData: indexData({ worlds }) };
  })()],
  ['foreign parent', (() => {
    const worlds = baseWorlds();
    worlds[1].parent_id = 999;
    return { indexData: indexData({ worlds }) };
  })()],
  ['foreign display group', { indexData: indexData({ displayGroups: [{ exploration_id: 999, group: { items: [] } }] }) }],
  ['foreign display area', { indexData: indexData({ displayGroups: [{ exploration_id: 1, group: { items: [{ area_ids: [999], exploration_percentage: 1 }] } }] }) }],
  ['parent cycle', (() => {
    const worlds = baseWorlds();
    worlds[0].parent_id = 2;
    worlds[1].parent_id = 1;
    return { indexData: indexData({ worlds }) };
  })()],
  ['missing world field', (() => {
    const worlds = baseWorlds();
    delete worlds[0].area_exploration_list;
    return { indexData: indexData({ worlds }) };
  })()],
]) {
  test(`rejects ${name} without partial exploration output`, async () => {
    assertNoPartialSuccess(await resultOf(createScenario(overrides)));
  });
}

test('nonzero API retcodes fail closed and login retcodes remain typed', async () => {
  assertNoPartialSuccess(await resultOf(createScenario({ retcode: 42 })));
  assertNoPartialSuccess(await resultOf(createScenario({ retcode: -100 })), 'login-required');
});

test('malformed UTF-8, invalid responses, and per-response oversize fail without partial output', async () => {
  const invalidUtf8 = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, { rawBytes: new Uint8Array([0x7b, 0xc3, 0x28, 0x7d]) }),
  });
  assertNoPartialSuccess(await resultOf(invalidUtf8));

  const badStatus = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, { status: 204 }),
  });
  assertNoPartialSuccess(await resultOf(badStatus));

  const badContentType = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, { contentType: 'text/html' }),
  });
  assertNoPartialSuccess(await resultOf(badContentType));

  const tooLarge = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, {
      rawBytes: new Uint8Array(8 * 1024 * 1024 + 1),
      chunkSize: 8 * 1024 * 1024 + 1,
    }),
  });
  assertNoPartialSuccess(await resultOf(tooLarge), 'too-large');
});

test('result-size limit stops a large individually bounded projection', async () => {
  const worlds = [];
  for (let index = 1; index <= 512; index++) {
    const current = world(index, { parentId: 0, name: `Synthetic World ${index}` });
    current.offerings = Array.from({ length: 64 }, (_, offeringIndex) =>
      offering(`Synthetic Offering ${index}-${offeringIndex}`, offeringIndex, 'Locked'));
    current.area_exploration_list = Array.from({ length: 64 }, (_, areaIndex) =>
      area(`Synthetic Area ${index}-${areaIndex}`, areaIndex));
    worlds.push(current);
  }
  const groups = worlds.map(current => ({
    exploration_id: current.id,
    group: { items: [{ area_ids: [current.id], exploration_percentage: current.id }] },
  }));
  const result = await resultOf(createScenario({
    indexData: indexData({ worlds, displayGroups: groups }),
  }));
  assertNoPartialSuccess(result, 'too-large');
});

test('caller cancellation, controller replacement, and both timeout layers publish no partial result', async () => {
  const canceled = createScenario({
    fetch: async (_url, options) => new Promise((_, reject) => {
      options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
    }),
  });
  for (let index = 0; index < 20 && canceled.requests.length === 0; index++) await flush();
  canceled.state().abort();
  assertNoPartialSuccess(await resultOf(canceled), 'canceled');

  let release;
  const replaced = createScenario({
    fetch: async (url, _options, fallback) => new Promise(resolve => {
      release = () => resolve(fallback(url, {}));
    }),
  });
  for (let index = 0; index < 20 && !release; index++) await flush();
  const oldState = replaced.state();
  Object.defineProperty(replaced.context.window, key, { configurable: true, value: { replacement: true } });
  release();
  for (let index = 0; index < 20; index++) await flush();
  assert.equal(replaced.context.window[key].replacement, true);
  assert.equal(oldState.result, null);

  for (const delay of [8_000, 45_000]) {
    const timedOut = createScenario({
      fetch: async (_url, options) => new Promise((_, reject) => {
        options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
      }),
    });
    for (let index = 0; index < 20 && timedOut.requests.length === 0; index++) await flush();
    timedOut.fire(delay);
    assertNoPartialSuccess(await resultOf(timedOut), 'timed-out');
  }
});
