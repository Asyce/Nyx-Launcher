import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const roleId = '123456789';
const server = 'os_euro';
const key = '__pengoNyxGenshinEvents_fixture';
const roleEndpoint = 'https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/genshin/api/';
const roleUrl = `${roleEndpoint}?game_biz=hk4e_global&region=${server}`;
const calendarUrl = `${recordBase}act_calendar`;
const config = {
  key,
  roleId,
  server,
  roleEndpoint,
  gameBiz: 'hk4e_global',
  maximumEvents: 512,
  maximumResultBytes: 3 * 1024 * 1024,
  timeoutMilliseconds: 45_000,
};

function extractScript() {
  const sourcePath = new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabGenshinEventsCapture.cs', import.meta.url);
  const source = fs.readFileSync(sourcePath, 'utf8');
  const matches = [...source.matchAll(/return \$\$"""/g)];
  assert.equal(matches.length, 1, 'the source must contain exactly one raw events script');
  const start = matches[0].index + matches[0][0].length;
  const end = source.indexOf('\n        """;', start);
  assert.ok(end > start, 'the raw events script must have a closing delimiter');
  const body = source.slice(start + 1, end).split(/\r?\n/);
  const indent = Math.min(...body.filter(line => line.trim()).map(line => line.match(/^ */)[0].length));
  return body.map(line => line.slice(Math.min(indent, line.length))).join('\n')
    .replaceAll('{{configuration}}', JSON.stringify(config));
}

const script = extractScript();

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function reward(id, name = 'Synthetic Reward') {
  return { item_id: id, name, num: 0, rarity: '5', homepage_show: true };
}

function sourceActivity(id, type, name, options = {}) {
  const value = (property, fallback) => Object.hasOwn(options, property) ? options[property] : fallback;
  return {
    id,
    type,
    name,
    label: 'Raw synthetic label',
    icon: 'raw-synthetic-icon',
    description: 'Raw synthetic description',
    start_timestamp: value('startTimestamp', '1709164800'),
    end_timestamp: value('endTimestamp', '1709251200'),
    start_time: value('startTime', { year: 2024, month: 3, day: 1, hour: 0, minute: 0, second: 0 }),
    end_time: value('endTime', { year: 2024, month: 3, day: 2, hour: 0, minute: 0, second: 0 }),
    countdown_seconds: value('countdown', 0),
    status: value('status', 0),
    is_finished: value('finished', false),
    reward_list: value('rewards', []),
    ...(Object.hasOwn(options, 'exploreDetail') ? { explore_detail: options.exploreDetail } : {}),
    ...(Object.hasOwn(options, 'doubleDetail') ? { double_detail: options.doubleDetail } : {}),
    ...(Object.hasOwn(options, 'towerDetail') ? { tower_detail: options.towerDetail } : {}),
    ...(Object.hasOwn(options, 'roleCombatDetail') ? { role_combat_detail: options.roleCombatDetail } : {}),
    ...(Object.hasOwn(options, 'hardChallengeDetail') ? { hard_challenge_detail: options.hardChallengeDetail } : {}),
  };
}

function baseCalendarData() {
  const activities = [
    sourceActivity(101, 'ActTypeExplore', 'Synthetic Expedition', {
      startTimestamp: '1709164800',
      endTimestamp: '1709251200',
      startTime: { year: 2024, month: 2, day: 29, hour: 0, minute: 0, second: 0 },
      endTime: { year: 2024, month: 2, day: 29, hour: 23, minute: 59, second: 59 },
      status: 1,
      rewards: [reward(1)],
      exploreDetail: { explore_percent: 1234.5, is_finished: false },
      doubleDetail: { total: 2, left: 1 },
      towerDetail: null,
      roleCombatDetail: null,
      hardChallengeDetail: null,
    }),
    sourceActivity(102, 'ActTypeChallenge', 'Synthetic Challenge', {
      startTimestamp: '0',
      endTimestamp: '0',
      startTime: null,
      endTime: null,
      hardChallengeDetail: {
        is_unlock: true,
        difficulty: 0,
        second: 123,
        sub: { seconds: 12.25, x: 0.125, y: 2147483647 },
      },
      exploreDetail: null,
      doubleDetail: null,
      towerDetail: null,
      roleCombatDetail: null,
    }),
  ];
  const fixed = [
    sourceActivity(0, 'ActTypeTower', 'Synthetic Tower', {
      startTimestamp: '1709251200',
      endTimestamp: '1709337600',
      startTime: { year: 2024, month: 3, day: 1, hour: 0, minute: 0, second: 0 },
      endTime: { year: 2024, month: 3, day: 2, hour: 0, minute: 0, second: 0 },
      countdown: 42,
      status: 2,
      finished: true,
      towerDetail: { is_unlock: true, max_star: 36, total_star: 0, has_data: true },
      exploreDetail: null,
      doubleDetail: null,
      roleCombatDetail: null,
      hardChallengeDetail: null,
    }),
    sourceActivity(0, 'ActTypeRoleCombat', 'Synthetic Theater', {
      startTimestamp: '1720000000',
      endTimestamp: '1720086400',
      startTime: { year: 2024, month: 7, day: 3, hour: 12, minute: 30, second: 0 },
      endTime: { year: 2024, month: 7, day: 4, hour: 12, minute: 30, second: 0 },
      countdown: 7,
      status: 3,
      roleCombatDetail: {
        is_unlock: false,
        max_round_id: 10,
        has_data: false,
        tarot_finished_cnt: 0,
        difficulty_id: 3,
      },
      exploreDetail: null,
      doubleDetail: null,
      towerDetail: null,
      hardChallengeDetail: null,
    }),
  ];
  return {
    act_list: activities,
    fixed_act_list: fixed,
    selected_act_list: [clone(activities[0]), clone(fixed[0])],
  };
}

function calendarData(overrides = {}) {
  return { ...baseCalendarData(), ...overrides };
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
    url: options.responseUrl ?? url,
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
  const data = overrides.calendarData ?? calendarData();
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
    if (url === calendarUrl)
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
  assert.equal(Object.hasOwn(result, 'events'), false);
}

test('happy path uses exact role calls, scoped calendar POST, both fixed kinds, and canonical projection', async () => {
  const scenario = createScenario();
  const result = await resultOf(scenario);

  assert.equal(result.status, 'done');
  assert.equal(result.roleId, roleId);
  assert.equal(result.server, server);
  assert.equal(result.count, 4);
  assert.deepEqual([...result.events.activities].map(row => [row.id, row.kind]), [
    [101, 'ActTypeExplore'], [102, 'ActTypeChallenge'],
  ]);
  assert.deepEqual([...result.events.fixed].map(row => [row.id, row.kind]), [
    [0, 'ActTypeTower'], [0, 'ActTypeRoleCombat'],
  ]);
  assert.deepEqual(JSON.parse(JSON.stringify(result.events.selected)), [
    { id: 101, kind: 'ActTypeExplore' },
    { id: 0, kind: 'ActTypeTower' },
  ]);
  assert.equal(result.events.activities[0].startTimestamp, '1709164800');
  assert.deepEqual(JSON.parse(JSON.stringify(result.events.activities[0].startTime)), {
    year: 2024, month: 2, day: 29, hour: 0, minute: 0, second: 0,
  });
  assert.equal(result.events.activities[0].exploration.percentage, 1234.5);
  assert.equal(result.events.activities[0].double.total, 2);
  assert.equal(result.events.activities[1].startTimestamp, '0');
  assert.equal(result.events.activities[1].startTime, null);
  assert.equal(result.events.activities[1].onslaught.sub.seconds, 12.25);
  assert.equal(result.events.activities[1].onslaught.sub.x, 0.125);
  assert.equal(result.events.activities[1].onslaught.sub.y, 2147483647);
  assert.equal(result.events.fixed[0].abyss.stars, 0);
  assert.equal(result.events.fixed[1].theater.tarotFinished, 0);
  assert.equal(result.events.activities[0].rewards[0].quantity, 0);
  assert.equal(Object.keys(result.events).join(','), 'activities,fixed,selected');
  const projected = JSON.stringify(result.events);
  assert.equal(projected.includes('Raw synthetic label'), false);
  assert.equal(projected.includes('raw-synthetic-icon'), false);
  assert.equal(projected.includes('Raw synthetic description'), false);
  assert.equal(projected.includes('explore_detail'), false);

  assert.deepEqual(scenario.requests.map(({ url, method, body }) => ({ url, method, body })), [
    { url: roleUrl, method: 'GET', body: undefined },
    { url: calendarUrl, method: 'POST', body: '{"server":"os_euro","role_id":"123456789"}' },
    { url: roleUrl, method: 'GET', body: undefined },
  ]);
  assert.equal(scenario.requests.some(request => request.method === 'GET' && request.url === calendarUrl), false);
  assert.ok(scenario.requests.every(request => request.credentials === 'include'));
  assert.ok(scenario.requests.every(request => request.redirect === 'error'));
  assert.ok(scenario.requests.every(request => request.cache === 'no-store'));
  assert.ok(scenario.requests.every(request => request.referrerPolicy === 'no-referrer'));
  assert.ok(scenario.requests.every(request => request.language === 'en-us'));
});

test('known absent and null summaries stay nullable while unknown detail keys fail closed', async () => {
  const data = calendarData();
  delete data.fixed_act_list[1].explore_detail;
  delete data.fixed_act_list[1].double_detail;
  delete data.fixed_act_list[1].tower_detail;
  delete data.fixed_act_list[1].hard_challenge_detail;
  const result = await resultOf(createScenario({ calendarData: data }));
  assert.equal(result.status, 'done');
  assert.equal(result.events.fixed[1].exploration, null);
  assert.equal(result.events.fixed[1].double, null);
  assert.equal(result.events.fixed[1].abyss, null);
  assert.equal(result.events.fixed[1].onslaught, null);

  const unknown = calendarData();
  unknown.act_list[0].future_detail = {};
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: unknown })));
});

for (const [name, mutate] of [
  ['foreign selected event', data => { data.selected_act_list[0].id = 999; }],
  ['mismatched selected event', data => { data.selected_act_list[0].name = 'Changed synthetic name'; }],
  ['duplicate selected event', data => { data.selected_act_list.push(clone(data.selected_act_list[0])); }],
  ['global duplicate composite identity', data => { data.fixed_act_list[1].type = 'ActTypeTower'; }],
]) {
  test(`rejects ${name} without partial event output`, async () => {
    const data = calendarData();
    mutate(data);
    assertNoPartialSuccess(await resultOf(createScenario({ calendarData: data })));
  });
}

for (const [name, mutate] of [
  ['wrong ID type', data => { data.act_list[0].id = '101'; }],
  ['fractional ID', data => { data.act_list[0].id = 101.5; }],
  ['negative ID', data => { data.act_list[0].id = -1; }],
  ['wrong timestamp type', data => { data.act_list[0].start_timestamp = 1709164800; }],
  ['leading-zero timestamp', data => { data.act_list[0].start_timestamp = '01'; }],
  ['negative timestamp', data => { data.act_list[0].start_timestamp = '-1'; }],
  ['timestamp overflow', data => { data.act_list[0].start_timestamp = '253402300800'; }],
  ['invalid leap-day date', data => { data.act_list[0].start_time.year = 2023; }],
  ['missing required field', data => { delete data.act_list[0].reward_list; }],
  ['fractional countdown', data => { data.act_list[0].countdown_seconds = 1.5; }],
  ['negative exploration percentage', data => { data.act_list[0].explore_detail.explore_percent = -0.1; }],
  ['wrong exploration percentage type', data => { data.act_list[0].explore_detail.explore_percent = '1234.5'; }],
  ['wrong onslaught sub type', data => { data.act_list[1].hard_challenge_detail.sub = []; }],
  ['nonfinite onslaught value', data => { data.act_list[1].hard_challenge_detail.sub.x = Number.POSITIVE_INFINITY; }],
]) {
  test(`rejects malformed ${name} without partial event output`, async () => {
    const data = calendarData();
    mutate(data);
    assertNoPartialSuccess(await resultOf(createScenario({ calendarData: data })));
  });
}

test('array and reward bounds are enforced before output', async () => {
  const tooManyEvents = calendarData({
    act_list: Array.from({ length: 513 }, (_, index) =>
      sourceActivity(1000 + index, `SyntheticType${index}`, `Synthetic Event ${index}`)),
    selected_act_list: [],
  });
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: tooManyEvents })));

  const tooManySelected = calendarData({
    selected_act_list: Array.from({ length: 513 }, () => clone(baseCalendarData().act_list[0])),
  });
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: tooManySelected })));

  const tooManyRewards = calendarData();
  tooManyRewards.act_list[0].reward_list = Array.from({ length: 1025 }, (_, index) => reward(index + 1));
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: tooManyRewards })));
});

test('embedded script rejects lone UTF-16 labels while accepting paired supplementary and replacement characters', async () => {
  const malformed = calendarData();
  malformed.act_list[0].name = '\uD800';
  malformed.selected_act_list[0].name = '\uD800';
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: malformed })));

  const valid = calendarData();
  valid.act_list[0].name = 'Synthetic 😀 Expedition';
  valid.selected_act_list[0].name = 'Synthetic 😀 Expedition';
  valid.act_list[0].icon = 'icon �';
  const result = await resultOf(createScenario({ calendarData: valid }));
  assert.equal(result.status, 'done');
  assert.equal(result.events.activities[0].name, 'Synthetic 😀 Expedition');
});

for (const [name, overrides] of [
  ['foreign role', { roles: [{ game_biz: 'hk4e_global', region: server, game_uid: '987654321' }] }],
  ['wrong role game', { roles: [{ game_biz: 'other_game', region: server, game_uid: roleId }] }],
  ['duplicate role', { roles: [
    { game_biz: 'hk4e_global', region: server, game_uid: roleId },
    { game_biz: 'hk4e_global', region: server, game_uid: roleId },
  ] }],
  ['too many roles', { roles: Array.from({ length: 9 }, (_, index) => ({
    game_biz: 'hk4e_global', region: server, game_uid: String(123456780 + index),
  })) }],
  ['final role loss', { roleDataByCall: [
    { list: [{ game_biz: 'hk4e_global', region: server, game_uid: roleId }] },
    { list: [] },
  ] }],
  ['final role changed', { roleDataByCall: [
    { list: [{ game_biz: 'hk4e_global', region: server, game_uid: roleId }] },
    { list: [{ game_biz: 'hk4e_global', region: server, game_uid: '987654321' }] },
  ] }],
]) {
  test(`rejects ${name} without partial event output`, async () => {
    assertNoPartialSuccess(await resultOf(createScenario(overrides)));
  });
}

test('nonzero API retcodes fail closed and login retcodes remain typed', async () => {
  assertNoPartialSuccess(await resultOf(createScenario({ retcode: 42 })));
  assertNoPartialSuccess(await resultOf(createScenario({ retcode: -100 })), 'login-required');
});

test('malformed UTF-8, invalid responses, and per-response oversize fail without partial output', async () => {
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

  const badContentType = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, { contentType: 'text/html' }),
  });
  assertNoPartialSuccess(await resultOf(badContentType));

  const badUrl = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, { responseUrl: `${url}/redirected` }),
  });
  assertNoPartialSuccess(await resultOf(badUrl));

  const missingBody = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, { body: false }),
  });
  assertNoPartialSuccess(await resultOf(missingBody));

  const tooLarge = createScenario({
    fetch: async (url, _options) => jsonResponse(url, null, {
      rawBytes: new Uint8Array(8 * 1024 * 1024 + 1),
      chunkSize: 8 * 1024 * 1024 + 1,
    }),
  });
  assertNoPartialSuccess(await resultOf(tooLarge), 'too-large');
});

test('result-size limit stops a large individually bounded projection', async () => {
  const events = Array.from({ length: 512 }, (_, index) => sourceActivity(
    1000 + index,
    `SyntheticType${index}`,
    'Synthetic Event '.padEnd(256, 'x'),
    { rewards: Array.from({ length: 32 }, (_, rewardIndex) => reward(rewardIndex + 1, 'Synthetic Reward'.padEnd(256, 'r'))) },
  ));
  const result = await resultOf(createScenario({
    calendarData: { act_list: events, fixed_act_list: [], selected_act_list: [] },
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
