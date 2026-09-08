import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const roleId = '123456789';
const server = 'os_euro';
const key = '__pengoNyxGenshinBuilds_fixture';
const roleEndpoint = 'https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/';
const calculatorUrl = 'https://sg-act-public-api.hoyolab.com/event/e20200928calculate/v1/sync/avatar/list';
const roleUrl = `${roleEndpoint}?game_biz=hk4e_global&region=${server}`;
const characterListUrl = `${recordBase}character/list`;
const characterDetailUrl = `${recordBase}character/detail`;
const config = {
  key,
  roleId,
  server,
  roleEndpoint,
  gameBiz: 'hk4e_global',
  maximumCharacters: 512,
  maximumResultBytes: 3 * 1024 * 1024,
  timeoutMilliseconds: 90_000,
};

function extractScript() {
  const sourcePath = new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabGenshinBuildCapture.cs', import.meta.url);
  const source = fs.readFileSync(sourcePath, 'utf8');
  const matches = [...source.matchAll(/return \$\$"""/g)];
  assert.equal(matches.length, 1, 'the source must contain exactly one raw capture script');
  const start = matches[0].index + matches[0][0].length;
  const end = source.indexOf('\n        """;', start);
  assert.ok(end > start, 'the raw capture script must have a closing delimiter');
  const body = source.slice(start + 1, end).split(/\r?\n/);
  const indent = Math.min(...body.filter(line => line.trim()).map(line => line.match(/^ */)[0].length));
  return body.map(line => line.slice(Math.min(indent, line.length))).join('\n')
    .replaceAll('{{configuration}}', JSON.stringify(config));
}

const script = extractScript();

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
    headers: { get: name => options.contentType ?? (name.toLowerCase() === 'content-type' ? 'application/json' : null) },
    body: options.body === false ? null : { getReader: () => reader },
  };
}

function responseEnvelope(data, retcode = 0) {
  return { retcode, data };
}

function officialPropertyMap() {
  const result = Object.fromEntries([
    [1, 'Base Stat'],
    [3, 'Extra Stat'],
    [4, 'Weapon ATK'],
    [5, 'Weapon Bonus'],
    [7, 'Element Bonus'],
    [20, 'Zero Unit'],
    [22, 'Crit Rate'],
    [2001, 'Flat Artifact'],
    [2002, 'Percent Artifact'],
  ].map(([id, name]) => [id, { property_type: id, name }]));
  result['4'].icon = 'synthetic-icon';
  result['4'].filter_name = 'synthetic-filter-name';
  return result;
}

function character(id, element = 'Ice') {
  const suffix = id - 100;
  return {
    base: { id, level: 95, fetter: null, element },
    weapon: {
      id: 200 + suffix,
      level: 90,
      promote_level: 6,
      affix_level: 1,
      main_property: { property_type: 4, base: 100, add: 23, final: 123 },
      sub_property: { property_type: 5, base: '0%', add: '0%', final: '0%' },
    },
    skills: [
      { skill_id: 2, skill_type: 2, level: 1, is_unlock: false },
      { skill_id: 1, skill_type: 1, level: 13, is_unlock: true },
    ],
    constellations: [
      { id: 600 + suffix * 10 + 2, pos: 2, is_actived: false },
      { id: 600 + suffix * 10 + 1, pos: 1, is_actived: true },
    ],
    relics: [
      {
        id: 300 + suffix * 10 + 2,
        set: { id: 400 + suffix },
        pos: 2,
        rarity: 5,
        level: 20,
        main_property: { property_type: 2002, value: '46.6%', times: null },
        sub_property_list: [{ property_type: 22, value: '18.7%', times: 3 }],
      },
      {
        id: 300 + suffix * 10 + 1,
        set: { id: 400 + suffix },
        pos: 1,
        rarity: 5,
        level: 20,
        main_property: { property_type: 2001, value: '311', times: null },
        sub_property_list: [],
      },
    ],
    selected_properties: [
      { property_type: 22, base: '5%', add: '8.6%', final: '13.6%' },
      { property_type: 20, base: '0', add: '0', final: '0%' },
    ],
    base_properties: [{ property_type: 1, base: 100, add: 23, final: 123 }],
    extra_properties: [{ property_type: 3, base: 2, add: 3, final: 5 }],
    element_properties: [{ property_type: 7, base: '0%', add: '0%', final: '0%' }],
  };
}

function createScenario(overrides = {}) {
  const initialRoster = overrides.initialRoster ?? [{ id: 202 }, { id: 101 }];
  const finalRoster = overrides.finalRoster ?? initialRoster;
  const details = overrides.details ?? [character(202, 'Fire'), character(101)];
  const propertyMap = overrides.propertyMap ?? officialPropertyMap();
  const requests = [];
  const calculatorPages = overrides.calculatorPages ?? new Map([
    [1, [{ id: 202, promote_level: 6 }]],
    [2, [{ id: 101, promote_level: null }]],
  ]);
  const timers = [];
  let nextTimerId = 1;

  const setTimeoutFake = (callback, delay) => {
    const timer = { callback, delay, cleared: false, id: nextTimerId++ };
    timers.push(timer);
    if (delay === 250) queueMicrotask(() => { if (!timer.cleared) callback(); });
    return timer;
  };
  const clearTimeoutFake = timer => { if (timer) timer.cleared = true; };

  const fetchImpl = overrides.fetch ?? (async (url, options) => {
    const body = options.body === undefined ? undefined : JSON.parse(options.body);
    requests.push({
      url,
      method: options.method,
      body,
      credentials: options.credentials,
      redirect: options.redirect,
      cache: options.cache,
      referrerPolicy: options.referrerPolicy,
      language: options.headers?.['x-rpc-language'],
      contentType: options.headers?.['Content-Type'],
    });
    if (url === roleUrl)
      return jsonResponse(url, responseEnvelope({ list: overrides.roles ?? [
        { game_biz: 'hk4e_global', region: server, game_uid: roleId },
      ] }));
    if (url === characterListUrl)
      return jsonResponse(url, responseEnvelope({ list: requests.filter(request => request.url === characterListUrl).length === 1 ? initialRoster : finalRoster }));
    if (url === calculatorUrl) {
      const page = body.page;
      return jsonResponse(url, responseEnvelope({
        total: overrides.calculatorTotal ?? 2,
        list: calculatorPages.get(page) ?? [],
      }));
    }
    if (url === characterDetailUrl)
      return jsonResponse(url, responseEnvelope({ list: details, property_map: propertyMap }));
    throw new Error(`unexpected request: ${url}`);
  });

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
  for (let index = 0; index < 4; index++) await Promise.resolve();
}

async function resultOf(scenario, attempts = 200) {
  for (let index = 0; index < attempts; index++) {
    await flush();
    const result = scenario.state()?.result;
    if (result) return result;
  }
  throw new Error(`capture did not finish; requests=${scenario.requests.length}`);
}

function assertNoPartialSuccess(result, expectedStatus = 'needs-review') {
  assert.equal(result.status, expectedStatus);
  assert.equal(Object.hasOwn(result, 'characters'), false);
}

test('happy path uses the exact role, paginates calculator data, and preserves the sorted build projection', async () => {
  const scenario = createScenario();
  const result = await resultOf(scenario);

  assert.equal(result.status, 'done');
  assert.equal(result.roleId, roleId);
  assert.equal(result.server, server);
  assert.equal(result.count, 2);
  assert.deepEqual([...result.characters].map(row => row.id), [101, 202]);
  const first = JSON.parse(JSON.stringify(result.characters[0]));
  assert.equal(first.level, 95);
  assert.equal(first.promotion, null);
  assert.equal(first.friendship, null);
  assert.equal(first.skills[0].level, 13);
  assert.equal(first.weapon.id, 201);
  assert.equal(first.weapon.level, 90);
  assert.equal(first.weapon.promotion, 6);
  assert.equal(first.weapon.main.final, 123);
  assert.equal(first.weapon.main.name, 'Weapon ATK');
  assert.equal(Object.hasOwn(first.weapon.main, 'icon'), false);
  assert.equal(Object.hasOwn(first.weapon.main, 'filter_name'), false);
  assert.equal(first.weapon.sub.name, 'Weapon Bonus');
  assert.equal(first.artifacts[0].slot, 1);
  assert.equal(first.artifacts[0].main.name, 'Flat Artifact');
  assert.equal(first.artifacts[1].main.name, 'Percent Artifact');
  assert.equal(first.artifacts[1].sub[0].value, 18.7);
  assert.equal(first.artifacts[1].sub[0].name, 'Crit Rate');
  assert.deepEqual(first.properties.find(row => row.id === 20), {
    group: 0, id: 20, base: 0, added: 0, final: 0, percent: true, name: 'Zero Unit',
  });
  assert.deepEqual(first.properties.map(row => row.name), [
    'Zero Unit', 'Crit Rate', 'Base Stat', 'Extra Stat', 'Element Bonus',
  ]);
  assert.equal(Object.hasOwn(first, 'property_map'), false);

  assert.deepEqual(scenario.requests.map(({ url, method, body }) => ({ url, method, body })), [
    { url: roleUrl, method: 'GET', body: undefined },
    { url: characterListUrl, method: 'POST', body: { role_id: roleId, server } },
    { url: calculatorUrl, method: 'POST', body: { uid: roleId, region: server, page: 1, size: 200 } },
    { url: calculatorUrl, method: 'POST', body: { uid: roleId, region: server, page: 2, size: 200 } },
    { url: characterDetailUrl, method: 'POST', body: { role_id: roleId, server, character_ids: [101, 202] } },
    { url: characterListUrl, method: 'POST', body: { role_id: roleId, server } },
  ]);
  assert.equal(scenario.requests[0].credentials, 'include');
  assert.equal(scenario.requests[0].redirect, 'error');
  assert.equal(scenario.requests[0].cache, 'no-store');
  assert.equal(scenario.requests[0].referrerPolicy, 'no-referrer');
  assert.ok(scenario.requests.every(request => request.language === 'en-us'));
  assert.equal(scenario.requests[0].contentType, undefined);
  assert.ok(scenario.requests.slice(1).every(request => request.contentType === 'application/json'));
});

test('invalid property-map labels are omitted while every numeric stat remains usable', async () => {
  const propertyMap = officialPropertyMap();
  delete propertyMap['4'];
  propertyMap['5'] = { property_type: 999, name: 'Wrong property' };
  propertyMap['2001'].name = 'x'.repeat(129);
  propertyMap['2002'].name = '\u0000';
  propertyMap['3'].name = ' Extra Stat ';
  const scenario = createScenario({ propertyMap });
  const result = await resultOf(scenario);
  const first = JSON.parse(JSON.stringify(result.characters[0]));

  assert.equal(result.status, 'done');
  assert.equal(first.weapon.main.final, 123);
  assert.equal(Object.hasOwn(first.weapon.main, 'name'), false);
  assert.equal(first.weapon.sub.final, 0);
  assert.equal(Object.hasOwn(first.weapon.sub, 'name'), false);
  assert.equal(Object.hasOwn(first.artifacts[0].main, 'name'), false);
  assert.equal(Object.hasOwn(first.artifacts[1].main, 'name'), false);
  assert.equal(Object.hasOwn(first.properties.find(row => row.id === 3), 'name'), false);
  assert.equal(first.properties.find(row => row.id === 1).name, 'Base Stat');
  assert.equal(first.properties.find(row => row.id === 7).name, 'Element Bonus');
  assert.equal(first.properties.find(row => row.id === 22).name, 'Crit Rate');
  assert.equal(first.properties.find(row => row.id === 20).final, 0);
});

test('capture has no implicit permission enable and only calls the reviewed read endpoints', async () => {
  assert.doesNotMatch(script, /permission|enable/i);
  const scenario = createScenario();
  await resultOf(scenario);
  assert.deepEqual([...new Set(scenario.requests.map(request => request.url))].sort(), [
    characterDetailUrl,
    characterListUrl,
    calculatorUrl,
    roleUrl,
  ].sort());
  assert.ok(scenario.requests.every(request => request.method === 'GET' || request.method === 'POST'));
});

for (const [name, overrides] of [
  ['partial detail list', { details: [character(101)] }],
  ['duplicate Chronicle roster', { initialRoster: [{ id: 101 }, { id: 101 }] }],
  ['duplicate calculator ID', { calculatorPages: new Map([[1, [{ id: 202, promote_level: 6 }, { id: 202, promote_level: 6 }]]]) }],
  ['wrong detail ID', { details: [character(999), character(101)] }],
  ['changed roster', { finalRoster: [{ id: 202 }, { id: 999 }] }],
  ['non-progressing calculator page', { calculatorPages: new Map([[1, []]]) }],
  ['calculator total above limit', { calculatorTotal: 513, expectedStatus: 'too-large' }],
]) {
  test(`rejects ${name} without publishing partial success`, async () => {
    const scenario = createScenario(overrides);
    assertNoPartialSuccess(await resultOf(scenario), overrides.expectedStatus ?? 'needs-review');
  });
}

test('nonzero API retcodes fail closed and login retcodes remain typed', async () => {
  const nonzero = createScenario({
    fetch: async (url, options) => {
      const body = options.body === undefined ? undefined : JSON.parse(options.body);
      return jsonResponse(url, responseEnvelope({ list: [] }, 42));
    },
  });
  assertNoPartialSuccess(await resultOf(nonzero));

  const login = createScenario({
    fetch: async (url, options) => jsonResponse(url, responseEnvelope({}, -100)),
  });
  assertNoPartialSuccess(await resultOf(login), 'login-required');
});

test('nonfinite source numbers are rejected without partial success', async () => {
  const scenario = createScenario({
    fetch: async (url, options) => {
      const body = options.body === undefined ? undefined : JSON.parse(options.body);
      if (url === characterDetailUrl) {
        const raw = JSON.stringify(responseEnvelope({ list: [character(101), character(202)] }))
          .replace('"final":123', '"final":1e9999');
        return jsonResponse(url, raw);
      }
      if (url === roleUrl)
        return jsonResponse(url, responseEnvelope({ list: [{ game_biz: 'hk4e_global', region: server, game_uid: roleId }] }));
      if (url === characterListUrl)
        return jsonResponse(url, responseEnvelope({ list: [{ id: 101 }, { id: 202 }] }));
      if (url === calculatorUrl)
        return jsonResponse(url, responseEnvelope({ total: 2, list: body.page === 1 ? [{ id: 202, promote_level: 6 }] : [{ id: 101, promote_level: 6 }] }));
      throw new Error(`unexpected request: ${url}`);
    },
  });
  assertNoPartialSuccess(await resultOf(scenario));
});

test('oversize responses stop the capture before any result can be published', async () => {
  const scenario = createScenario({
    fetch: async url => jsonResponse(url, null, {
      rawBytes: new Uint8Array(2 * 1024 * 1024 + 1),
      chunkSize: 2 * 1024 * 1024 + 1,
    }),
  });
  assertNoPartialSuccess(await resultOf(scenario), 'too-large');
});

test('caller cancellation aborts an in-flight request and publishes no partial result', async () => {
  const scenario = createScenario({
    fetch: async (_url, options) => new Promise((_, reject) => {
      options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
    }),
  });
  for (let index = 0; index < 20 && scenario.requests.length === 0; index++) await flush();
  scenario.state().abort();
  assertNoPartialSuccess(await resultOf(scenario), 'canceled');
});

for (const delay of [8_000, 90_000]) {
  test(`request timeout at ${delay}ms aborts the capture without partial success`, async () => {
    const scenario = createScenario({
      fetch: async (_url, options) => new Promise((_, reject) => {
        options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
      }),
    });
    for (let index = 0; index < 20 && scenario.requests.length === 0; index++) await flush();
    scenario.fire(delay);
    assertNoPartialSuccess(await resultOf(scenario), 'timed-out');
  });
}
