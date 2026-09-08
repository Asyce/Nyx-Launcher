import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const roleId = '123456789';
const server = 'prod_official_eur';
const key = '__pengoNyxHsrBuilds_fixture';
const roleEndpoint = 'https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/';
const roleUrl = `${roleEndpoint}?game_biz=hkrpg_global&region=${server}`;
const indexUrl = `${recordBase}index?server=${server}&role_id=${roleId}`;
const avatarInfoUrl = `${recordBase}avatar/info?server=${server}&role_id=${roleId}&need_wiki=true`;
const config = {
  key,
  roleId,
  server,
  roleEndpoint,
  gameBiz: 'hkrpg_global',
  maximumCharacters: 512,
  maximumResultBytes: 3 * 1024 * 1024,
  timeoutMilliseconds: 45_000,
};

function extractScript() {
  const sourcePath = new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabHsrBuildCapture.cs', import.meta.url);
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

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function propertyInfo() {
  const result = Object.fromEntries([
    [1, 'HP'],
    [2, 'CRIT Rate'],
    [3, 'Hidden Stat'],
    [11, 'Relic Main'],
    [12, 'Relic Sub'],
    [13, 'Orb Main'],
  ].map(([id, name]) => [id, { property_type: id, name }]));
  result['1'].icon = 'synthetic-icon';
  result['1'].filter_name = 'synthetic-filter-name';
  result['1'].description = 'synthetic-description';
  return result;
}

function rawProperty(id, base, add = null, final = base) {
  return { property_type: id, base, add, final };
}

function rawStage(kind = 'ordinary') {
  if (kind === 'ordinary') {
    return {
      skill_id: 'stage-ordinary',
      name: 'Ordinary Stage',
      level: 13,
      is_activated: true,
      is_rank_work: true,
      special_point_type: '',
      exclusive_skill: { name: '', desc: 'Synthetic source description' },
      linked_avatar: { avatar_id: 'avatar-ordinary', name: 'Linked Ordinary Avatar' },
      linked_skill_id: 'linked-skill-ordinary',
    };
  }
  if (kind === 'new-list') {
    return {
      skill_id: 'stage-list',
      name: 'List Stage',
      level: 1,
      is_activated: false,
      is_rank_work: false,
      special_point_type: 'special',
      exclusive_skill: { name: 'Explicit Stage', desc: 'Synthetic source description' },
      linked_avatar_list: [],
      elation_skill_priority: '',
    };
  }
  return {
    skill_id: 'stage-servant',
    name: 'Servant Stage',
    level: 1,
    is_activated: true,
    is_rank_work: true,
    special_point_type: 'servant',
    exclusive_skill: { name: 'Servant Stage', desc: 'Synthetic source description' },
    linked_avatar_list: [{ avatar_id: 'avatar-servant', name: 'Linked Servant Avatar' }],
    elation_skill_priority: 'high',
  };
}

function rawTrace(id, kind = 'ordinary', withStage = true) {
  const trace = {
    point_id: id,
    point_type: kind === 'special' ? 7 : 1,
    level: kind === 'special' ? 1 : 13,
    is_activated: kind !== 'special',
    is_rank_work: true,
    pre_point: '',
    anchor: kind,
    special_point_type: kind === 'special' ? 'special' : '',
    skill_stages: [],
  };
  if (withStage)
    trace.skill_stages = [rawStage(kind === 'ordinary' ? 'ordinary' : kind === 'special' ? 'new-list' : 'servant')];
  return trace;
}

function rawRelic(id, slot, mainId, subId) {
  return {
    id,
    name: `Synthetic Relic ${id}`,
    pos: slot,
    rarity: 5,
    level: 15,
    main_property: { property_type: mainId, value: slot === 1 ? '705' : '10.0%', times: 0, is_preview: true },
    properties: subId === undefined
      ? []
      : [{ property_type: subId, value: '1,234.5', times: 2, is_preview: false }],
    icon: 'synthetic-relic-icon',
    description: 'synthetic relic description',
  };
}

function rawCharacter(id, options = {}) {
  const equipped = options.equipped ?? id === 202;
  const memo = options.memo ?? id === 202;
  return {
    id,
    name: `Synthetic Trailblazer ${id}`,
    level: 95,
    rarity: 5,
    element: id === 202 ? 'Fire' : 'Ice',
    base_type: 3,
    rank: 1,
    cur_enhanced_id: 100000 + id,
    avatar_ld_type: 'Girl',
    equip: equipped
      ? { id: 2000 + id, name: 'Synthetic Light Cone', level: 80, rarity: 5, rank: 2, icon: 'synthetic-cone-icon' }
      : {},
    servant_detail: memo
      ? {
        servant_id: '9001',
        servant_name: 'Synthetic Memosprite',
        is_health_secret: true,
        servant_properties: [rawProperty(2, '0.0%', null, '0.0%')],
        servant_skills: [rawTrace(`servant-${id}`, 'servant')],
      }
      : { servant_id: '0', servant_properties: [], servant_skills: [] },
    ranks: [{ id: 6000 + id, name: 'Synthetic Eidolon', pos: 1, is_unlocked: true }],
    relics: [rawRelic(3000 + id, 1, 11, 12)],
    ornaments: [rawRelic(4000 + id, 5, 13)],
    properties: [
      rawProperty(1, '12,345', '0', '12,345'),
      rawProperty(2, '5.0%', null, '5.0%'),
      rawProperty(3, '< 1.0%', '≈ 2.0%', '< 3.0%'),
    ],
    skills: [rawTrace(`normal-${id}`, 'ordinary')],
    special_skills: [rawTrace(`special-${id}`, 'special')],
    icon: 'synthetic-character-icon',
    description: 'synthetic character description',
    filter_name: 'synthetic-filter-name',
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
  const details = overrides.details ?? [rawCharacter(202), rawCharacter(101)];
  const initialRosterIds = overrides.initialRosterIds ?? [101, 202];
  const finalRosterIds = overrides.finalRosterIds ?? initialRosterIds;
  const initialCount = overrides.initialCount ?? initialRosterIds.length;
  const finalCount = overrides.finalCount ?? finalRosterIds.length;
  const roles = overrides.roles ?? [
    { game_biz: 'hkrpg_global', region: server, game_uid: roleId },
  ];
  const roleDataByCall = overrides.roleDataByCall ?? [];
  const propertyInfoData = overrides.propertyInfo ?? propertyInfo();
  const requests = [];
  const timers = [];
  let nextTimerId = 1;
  let roleCalls = 0;
  let indexCalls = 0;
  const setTimeoutFake = (callback, delay) => {
    const timer = { callback, delay, cleared: false, id: nextTimerId++ };
    timers.push(timer);
    if (delay === 250) queueMicrotask(() => { if (!timer.cleared) callback(); });
    return timer;
  };
  const clearTimeoutFake = timer => { if (timer) timer.cleared = true; };

  const padded = (url, data, retcode = 0) => {
    let body = JSON.stringify(responseEnvelope(data, retcode));
    if (overrides.responsePaddingBytes) body += ' '.repeat(overrides.responsePaddingBytes);
    return jsonResponse(url, body);
  };
  const defaultFetch = async url => {
    if (url === roleUrl) {
      const data = roleDataByCall[roleCalls++] ?? { list: roles };
      return padded(url, data, overrides.retcode ?? 0);
    }
    if (url === indexUrl) {
      const indexCall = indexCalls++;
      const data = overrides.indexDataByCall?.[indexCall]
        ?? overrides.indexData
        ?? {
          stats: { avatar_num: indexCall === 0 ? initialCount : finalCount },
          avatar_list: (indexCall === 0 ? initialRosterIds : finalRosterIds).map(id => ({ id })),
        };
      return padded(url, data, overrides.retcode ?? 0);
    }
    if (url === avatarInfoUrl) {
      const data = overrides.detailData ?? { avatar_list: details, property_info: propertyInfoData };
      return padded(url, data, overrides.retcode ?? 0);
    }
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
  assert.equal(Object.hasOwn(result, 'characters'), false);
}

test('happy path verifies the selected role, exact GET sequence, and complete sorted projection', async () => {
  const scenario = createScenario();
  const result = await resultOf(scenario);

  assert.equal(result.status, 'done');
  assert.equal(result.roleId, roleId);
  assert.equal(result.server, server);
  assert.equal(result.count, 2);
  assert.deepEqual([...result.characters].map(row => row.id), [101, 202]);

  const noEquipment = result.characters[0];
  assert.equal(noEquipment.level, 95);
  assert.equal(noEquipment.lightCone, null);
  assert.equal(noEquipment.memosprite, null);
  assert.equal(noEquipment.traces[0].level, 13);
  assert.equal(noEquipment.traces[0].stages[0].exclusiveName, '');
  assert.equal(noEquipment.traces[0].stages[0].linkedAvatar.name, 'Linked Ordinary Avatar');
  assert.equal(noEquipment.traces[0].stages[0].linkedSkillId, 'linked-skill-ordinary');
  assert.equal(noEquipment.traces[0].stages[0].linkedAvatars, null);
  assert.equal(noEquipment.traces[0].stages[0].elationPriority, null);

  const equipped = result.characters[1];
  assert.equal(equipped.lightCone.id, 2202);
  assert.equal(equipped.lightCone.level, 80);
  assert.equal(equipped.memosprite.name, 'Synthetic Memosprite');
  assert.equal(equipped.memosprite.traces[0].stages[0].linkedAvatars[0].name, 'Linked Servant Avatar');
  assert.equal(equipped.memosprite.traces[0].stages[0].linkedAvatar, null);
  assert.equal(equipped.memosprite.traces[0].stages[0].elationPriority, 'high');
  assert.equal(equipped.specialTraces[0].stages[0].linkedAvatars.length, 0);
  assert.equal(equipped.specialTraces[0].stages[0].linkedAvatar, null);
  assert.equal(equipped.specialTraces[0].stages[0].linkedSkillId, null);
  assert.equal(equipped.specialTraces[0].stages[0].elationPriority, '');
  assert.equal(equipped.relics[0].main.times, 0);
  assert.equal(equipped.relics[0].main.preview, true);
  assert.equal(equipped.relics[0].sub[0].value, '1,234.5');
  assert.equal(equipped.relics[0].sub[0].times, 2);
  assert.equal(equipped.relics[0].sub[0].preview, false);
  assert.equal(equipped.properties.find(row => row.id === 3).base, '< 1.0%');
  assert.equal(equipped.properties.find(row => row.id === 3).added, '≈ 2.0%');
  assert.equal(equipped.properties.find(row => row.id === 3).final, '< 3.0%');
  assert.equal(equipped.properties.find(row => row.id === 1).name, 'HP');

  const projection = JSON.stringify(result.characters);
  assert.equal(projection.includes('property_info'), false);
  assert.equal(projection.includes('icon'), false);
  assert.equal(projection.includes('filter_name'), false);
  assert.equal(projection.includes('description'), false);

  assert.deepEqual(scenario.requests.map(({ url, method, body }) => ({ url, method, body })), [
    { url: roleUrl, method: 'GET', body: undefined },
    { url: indexUrl, method: 'GET', body: undefined },
    { url: avatarInfoUrl, method: 'GET', body: undefined },
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

test('an explicitly empty Chronicle roster is a valid complete observation', async () => {
  const scenario = createScenario({ details: [], initialRosterIds: [], finalRosterIds: [] });
  const result = await resultOf(scenario);
  assert.equal(result.status, 'done');
  assert.equal(result.count, 0);
  assert.equal(result.characters.length, 0);
});

test('embedded capture rejects an unpaired stat label and accepts paired and replacement characters', async () => {
  const malformedMap = propertyInfo();
  malformedMap['1'].name = '\uD800';
  assertNoPartialSuccess(await resultOf(createScenario({ propertyInfo: malformedMap })));

  const validMap = propertyInfo();
  validMap['1'].name = 'HP \uD83D\uDE00';
  validMap['2'].name = '\uFFFD Crit Rate';
  const result = await resultOf(createScenario({ propertyInfo: validMap }));
  const equipped = JSON.parse(JSON.stringify(result.characters[1]));
  assert.equal(result.status, 'done');
  assert.equal(equipped.properties.find(row => row.id === 1).name, 'HP 😀');
  assert.equal(equipped.properties.find(row => row.id === 2).name, '� Crit Rate');
});

test('busy controller state is left untouched and capture has no permission or logging side effects', () => {
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
  ['partial detail list', { details: [rawCharacter(101)] }],
  ['duplicate Chronicle character ID', { details: [rawCharacter(101), rawCharacter(101)] }],
  ['duplicate index character ID', { initialRosterIds: [101, 101], finalRosterIds: [101, 101] }],
  ['duplicate relic slot', (() => {
    const first = rawCharacter(101);
    first.relics.push(clone(first.relics[0]));
    return { details: [first, rawCharacter(202)] };
  })()],
  ['duplicate normal skill ID', (() => {
    const first = rawCharacter(101);
    first.skills.push(clone(first.skills[0]));
    return { details: [first, rawCharacter(202)] };
  })()],
  ['wrong type in Chronicle list', { detailData: { avatar_list: {}, property_info: propertyInfo() } }],
  ['wrong type in property map', { detailData: { avatar_list: [rawCharacter(101), rawCharacter(202)], property_info: [] } }],
  ['wrong type in role discovery', { roleDataByCall: [{ list: {} }] }],
  ['wrong type in index count', { indexData: { stats: { avatar_num: '2' } } }],
]) {
  test(`rejects ${name} without publishing partial success`, async () => {
    assertNoPartialSuccess(await resultOf(createScenario(overrides)));
  });
}

test('partial-other-player detail response is rejected rather than admitted as the selected roster', async () => {
  const result = await resultOf(createScenario({ details: [rawCharacter(101), rawCharacter(999)] }));
  assertNoPartialSuccess(result);
});

test('final role loss and Chronicle roster count drift fail closed', async () => {
  const roleLoss = createScenario({ roleDataByCall: [
    { list: [{ game_biz: 'hkrpg_global', region: server, game_uid: roleId }] },
    { list: [] },
  ] });
  assertNoPartialSuccess(await resultOf(roleLoss));

  const sameCountIdDrift = createScenario({ finalRosterIds: [101, 999] });
  assertNoPartialSuccess(await resultOf(sameCountIdDrift));

  const countDrift = createScenario({ finalRosterIds: [101, 202, 999] });
  assertNoPartialSuccess(await resultOf(countDrift));
});

test('nonzero API retcodes fail closed and login retcodes remain typed', async () => {
  assertNoPartialSuccess(await resultOf(createScenario({ retcode: 42 })));
  assertNoPartialSuccess(await resultOf(createScenario({ retcode: -100 })), 'login-required');
});

test('nonfinite source values, malformed UTF-8, invalid response types, and oversized responses publish no result', async () => {
  const nonfinite = createScenario({
    fetch: async (url, options, fallback) => {
      if (url === avatarInfoUrl) {
        const data = { avatar_list: [rawCharacter(101), rawCharacter(202)], property_info: propertyInfo() };
        const body = JSON.stringify(responseEnvelope(data)).replace('"final":"12,345"', '"final":1e9999');
        return jsonResponse(url, body);
      }
      return fallback(url, options);
    },
  });
  assertNoPartialSuccess(await resultOf(nonfinite));

  const invalidUtf8 = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, {
      rawBytes: new Uint8Array([0x7b, 0xc3, 0x28, 0x7d]),
    }),
  });
  assertNoPartialSuccess(await resultOf(invalidUtf8));

  const badStatus = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, { status: 204 }),
  });
  assertNoPartialSuccess(await resultOf(badStatus));

  const tooLarge = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, {
      rawBytes: new Uint8Array(8 * 1024 * 1024 + 1),
      chunkSize: 8 * 1024 * 1024 + 1,
    }),
  });
  assertNoPartialSuccess(await resultOf(tooLarge), 'too-large');
});

test('the aggregate 24 MiB response limit stops a capture before publication', async () => {
  const scenario = createScenario({ responsePaddingBytes: 5_100_000 });
  assertNoPartialSuccess(await resultOf(scenario), 'too-large');
});

test('result-size limit stops a large but individually bounded projection', async () => {
  const details = [];
  for (let index = 0; index < 512; index++) {
    const row = rawCharacter(10_000 + index, { equipped: false, memo: false });
    row.skills = [];
    for (let skill = 0; skill < 64; skill++)
      row.skills.push(rawTrace(`large-${index}-${skill}`, 'ordinary', false));
    row.special_skills = [];
    details.push(row);
  }
  const rosterIds = details.map(row => row.id);
  const scenario = createScenario({ details, initialRosterIds: rosterIds, finalRosterIds: rosterIds });
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

test('replacement of the controller state cancels the capture without partial success', async () => {
  let release;
  const scenario = createScenario({
    fetch: async (url, _options, fallback) => new Promise(resolve => {
      release = () => resolve(fallback(url, {}));
    }),
  });
  for (let index = 0; index < 20 && !release; index++) await flush();
  assert.equal(typeof release, 'function');
  const oldState = scenario.state();
  Object.defineProperty(scenario.context.window, key, { configurable: true, value: { replacement: true } });
  release();
  for (let index = 0; index < 20; index++) await flush();
  assert.equal(scenario.state().replacement, true);
  assert.equal(oldState.result, null);
});

for (const delay of [8_000, 45_000]) {
  test(`request/global timeout at ${delay}ms aborts the capture without partial success`, async () => {
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
