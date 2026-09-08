import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const roleId = '123456789';
const server = 'prod_official_eur';
const key = '__pengoNyxHsrEvents_fixture';
const roleEndpoint = 'https://api-account-os.hoyolab.com/binding/api/getUserGameRolesByLtoken';
const recordBase = 'https://sg-act-public-api.hoyolab.com/event/game_record/hkrpg/api/';
const roleUrl = `${roleEndpoint}?game_biz=hkrpg_global&region=${server}`;
const calendarUrl = `${recordBase}get_act_calender?server=${server}&role_id=${roleId}`;
const config = {
  key,
  roleId,
  server,
  roleEndpoint,
  gameBiz: 'hkrpg_global',
  maximumEvents: 512,
  maximumResultBytes: 3 * 1024 * 1024,
  timeoutMilliseconds: 45_000,
};

function extractScript() {
  const sourcePath = new URL('../../src/Nyx.Desktop.Core/AccountStatus/HoyoLabHsrEventsCapture.cs', import.meta.url);
  const source = fs.readFileSync(sourcePath, 'utf8');
  const matches = [...source.matchAll(/return \$\$"""/g)];
  assert.equal(matches.length, 1, 'the source must contain exactly one raw HSR events script');
  const start = matches[0].index + matches[0][0].length;
  const end = source.indexOf('\n        """;', start);
  assert.ok(end > start, 'the raw HSR events script must have a closing delimiter');
  const body = source.slice(start + 1, end).split(/\r?\n/);
  const indent = Math.min(...body.filter(line => line.trim()).map(line => line.match(/^ */)[0].length));
  return body.map(line => line.slice(Math.min(indent, line.length))).join('\n')
    .replaceAll('{{configuration}}', JSON.stringify(config));
}

const script = extractScript();

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function reward(id, name = 'Synthetic Reward', kind) {
  const value = { item_id: id, name, num: 0, rarity: '5' };
  if (kind !== undefined) value.reward_type = kind;
  return value;
}

function sourceTime(options = {}) {
  const value = (property, fallback) => Object.hasOwn(options, property) ? options[property] : fallback;
  return {
    start_ts: value('startTimestamp', '1709164800'),
    end_ts: value('endTimestamp', '1709251200'),
    start_time: value('startTime', '2024-03-01 00:00:00'),
    end_time: value('endTime', '2024-03-02 00:00:00'),
    now: value('now', '1709164800'),
  };
}

function sourceActivity(id, type, name, options = {}) {
  const value = (property, fallback) => Object.hasOwn(options, property) ? options[property] : fallback;
  return {
    id,
    version: value('version', '3.7'),
    name,
    label: 'Raw synthetic label',
    icon: 'https://synthetic.invalid/raw-icon',
    url: 'https://synthetic.invalid/raw-event',
    description: 'Raw synthetic source description',
    act_type: type,
    act_status: value('status', 'Ready'),
    total_progress: value('total', 0),
    current_progress: value('progress', 0),
    time_info: value('time', sourceTime()),
    reward_list: value('rewards', []),
    special_reward: value('specialReward', null),
    all_finished: value('finished', false),
    show_text: value('showText', ''),
    act_time_type: value('timeKind', 'None'),
    panel_desc: value('description', ''),
    multiple_drop_type: value('dropType', 0),
    multiple_drop_type_list: value('dropTypes', []),
    count_refresh_type: value('refreshType', 0),
    count_value: value('count', 0),
    drop_multiple: value('multiplier', 0),
    is_after_version: value('afterVersion', false),
    ...(Object.hasOwn(options, 'futureDetail') ? { future_detail: options.futureDetail } : {}),
  };
}

function sourceChallenge(id, kind, name, options = {}) {
  const value = (property, fallback) => Object.hasOwn(options, property) ? options[property] : fallback;
  return {
    group_id: id,
    name_mi18n: name,
    challenge_type: kind,
    status: value('status', 'Locked'),
    total_progress: value('total', 10),
    current_progress: value('progress', 0),
    extra_progress: value('extraProgress', 0),
    time_info: value('time', sourceTime()),
    reward_list: value('rewards', []),
    special_reward: value('specialReward', null),
    show_text: value('showText', '0.0%'),
    challenge_peak_rank_icon_type: value('rankKind', ''),
    challenge_peak_start_version: value('startVersion', ''),
    ...(Object.hasOwn(options, 'futureList') ? { future_list: options.futureList } : {}),
  };
}

function baseCalendarData() {
  const activities = [
    sourceActivity(1, 'Activity', 'Synthetic Double Reward', {
      status: '0.0%',
      total: 100,
      progress: 0,
      time: sourceTime({
        startTimestamp: '1709164800',
        endTimestamp: '1709251200',
        startTime: '2024-02-29 00:00:00',
        endTime: '2024-02-29 23:59:59',
        now: '1709164800',
      }),
      rewards: [reward(1, 'Synthetic Reward', 'Material')],
      specialReward: reward(0, '', undefined),
      finished: false,
      showText: '0.0%',
      timeKind: 'Calendar',
      description: '',
      dropType: 1,
      dropTypes: [1.0, 2],
      refreshType: 0,
      count: 0,
      multiplier: 0,
      afterVersion: false,
    }),
    sourceActivity(2, 'Activity', 'Synthetic Unscheduled Activity', {
      status: 'Ready',
      total: 0,
      progress: 0,
      time: sourceTime({ startTimestamp: '0', endTimestamp: '0', startTime: '', endTime: '', now: '0' }),
      rewards: [],
      specialReward: null,
      finished: true,
      showText: '',
      timeKind: 'None',
      description: '0.0%',
      dropType: 0,
      dropTypes: [],
      refreshType: 0,
      count: 0,
      multiplier: 0,
      afterVersion: true,
    }),
  ];
  const challenges = [
    sourceChallenge(1, 'Challenge', 'Synthetic Challenge', {
      time: sourceTime({
        startTimestamp: '1720000000',
        endTimestamp: '1720086400',
        startTime: '2024-07-03 12:30:00',
        endTime: '2024-07-04 12:30:00',
        now: '1720000000',
      }),
      rewards: [reward(2)],
      specialReward: null,
    }),
  ];
  return {
    act_list: activities,
    challenge_list: challenges,
    avatar_card_pool_list: [{ url: 'https://synthetic.invalid/avatar-card' }],
    equip_card_pool_list: [{ url: 'https://synthetic.invalid/equip-card' }],
    now: '1709164800',
    cur_game_version: '3.7',
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
    { game_biz: 'hkrpg_global', region: server, game_uid: roleId },
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

test('failure diagnostics contain only bounded script lines and stay outside the result', async () => {
  const scenario = createScenario({ retcode: 42 });
  const result = await resultOf(scenario);
  assertNoPartialSuccess(result);
  const frames = scenario.state().failureFrames;
  assert.ok(frames.length > 0 && frames.length <= 3);
  assert.ok(frames.every(line => Number.isInteger(line) && line > 0 && line <= 4096));
  assert.equal(Object.hasOwn(result, 'failureFrames'), false);
});

test('happy path uses exact role GETs, the misspelled calendar endpoint, and a canonical projection', async () => {
  const scenario = createScenario();
  const result = await resultOf(scenario);

  assert.equal(result.status, 'done');
  assert.equal(result.roleId, roleId);
  assert.equal(result.server, server);
  assert.equal(result.count, 3);
  assert.deepEqual([...result.events.activities].map(row => [row.id, row.kind]), [
    [1, 'Activity'], [2, 'Activity'],
  ]);
  assert.deepEqual([...result.events.challenges].map(row => [row.id, row.kind]), [[1, 'Challenge']]);
  assert.equal(result.events.now, '1709164800');
  assert.equal(result.events.version, '3.7');
  assert.equal(result.events.activities[0].status, '0.0%');
  assert.equal(result.events.activities[0].showText, '0.0%');
  assert.equal(result.events.activities[0].time.startTime, '2024-02-29 00:00:00');
  assert.equal(result.events.activities[0].dropTypes[0], 1);
  assert.deepEqual(JSON.parse(JSON.stringify(result.events.activities[0].dropTypes)), [1, 2]);
  assert.equal(result.events.activities[0].specialReward.id, 0);
  assert.equal(result.events.activities[0].specialReward.name, '');
  assert.equal(result.events.activities[0].specialReward.quantity, 0);
  assert.equal(result.events.activities[0].specialReward.kind, null);
  assert.equal(result.events.activities[1].time.startTimestamp, '0');
  assert.equal(result.events.activities[1].time.startTime, '');
  assert.equal(result.events.activities[1].specialReward, null);
  assert.equal(result.events.challenges[0].showText, '0.0%');
  assert.equal(result.events.challenges[0].rankKind, '');
  assert.equal(result.events.challenges[0].startVersion, '');
  assert.equal(result.events.activities[0].rewards[0].quantity, 0);
  assert.equal(result.events.activities[0].rewards[0].kind, 'Material');
  assert.equal(result.events.challenges[0].rewards[0].kind, null);
  assert.equal(Object.keys(result.events).join(','), 'activities,challenges,now,version');
  const projected = JSON.stringify(result.events);
  assert.equal(projected.includes('Raw synthetic label'), false);
  assert.equal(projected.includes('raw-synthetic-icon'), false);
  assert.equal(projected.includes('synthetic.invalid'), false);
  assert.equal(projected.includes('Raw synthetic source description'), false);
  assert.equal(projected.includes('avatar_card_pool_list'), false);
  assert.equal(projected.includes('equip_card_pool_list'), false);

  assert.deepEqual(scenario.requests.map(({ url, method, body }) => ({ url, method, body })), [
    { url: roleUrl, method: 'GET', body: undefined },
    { url: calendarUrl, method: 'GET', body: undefined },
    { url: roleUrl, method: 'GET', body: undefined },
  ]);
  assert.equal(scenario.requests.some(request => request.url.includes('get_act_calendar')), false);
  assert.ok(scenario.requests.every(request => request.credentials === 'include'));
  assert.ok(scenario.requests.every(request => request.redirect === 'error'));
  assert.ok(scenario.requests.every(request => request.cache === 'no-store'));
  assert.ok(scenario.requests.every(request => request.referrerPolicy === 'no-referrer'));
  assert.ok(scenario.requests.every(request => request.language === 'en-us'));
});

for (const [name, mutate] of [
  ['unknown list field', data => { data.future_list = []; }],
  ['duplicate activity identity', data => { data.act_list.push(clone(data.act_list[0])); }],
  ['duplicate challenge identity', data => { data.challenge_list.push(clone(data.challenge_list[0])); }],
]) {
  test(`rejects ${name} without partial event output`, async () => {
    const data = calendarData();
    mutate(data);
    assertNoPartialSuccess(await resultOf(createScenario({ calendarData: data })));
  });
}

for (const [name, mutate] of [
  ['wrong activity ID type', data => { data.act_list[0].id = '1'; }],
  ['fractional activity ID', data => { data.act_list[0].id = 1.5; }],
  ['zero activity ID', data => { data.act_list[0].id = 0; }],
  ['empty activity version', data => { data.act_list[0].version = ''; }],
  ['empty activity status', data => { data.act_list[0].act_status = ''; }],
  ['wrong timestamp type', data => { data.act_list[0].time_info.start_ts = 1709164800; }],
  ['leading-zero timestamp', data => { data.act_list[0].time_info.start_ts = '01'; }],
  ['negative timestamp', data => { data.act_list[0].time_info.start_ts = '-1'; }],
  ['timestamp overflow', data => { data.act_list[0].time_info.start_ts = '253402300800'; }],
  ['invalid server date', data => { data.act_list[0].time_info.start_time = '2023-02-29 00:00:00'; }],
  ['wrong server date type', data => { data.act_list[0].time_info.start_time = 1; }],
  ['missing time field', data => { delete data.act_list[0].time_info.now; }],
  ['total overflow', data => { data.act_list[0].total_progress = 2147483648; }],
  ['fractional drop type', data => { data.act_list[0].multiple_drop_type = 1.5; }],
  ['fractional drop type list item', data => { data.act_list[0].multiple_drop_type_list[0] = 1.5; }],
  ['wrong drop type list', data => { data.act_list[0].multiple_drop_type_list = ['1']; }],
  ['special placeholder name', data => { data.act_list[0].special_reward.name = 'not empty'; }],
  ['special placeholder quantity', data => { data.act_list[0].special_reward.num = 1; }],
  ['empty regular reward name', data => { data.act_list[0].reward_list[0].name = ''; }],
  ['empty regular reward type', data => { data.act_list[0].reward_list[0].reward_type = ''; }],
  ['wrong challenge field', data => { data.challenge_list[0].extra_progress = '0'; }],
]) {
  test(`rejects malformed ${name} without partial event output`, async () => {
    const data = calendarData();
    mutate(data);
    assertNoPartialSuccess(await resultOf(createScenario({ calendarData: data })));
  });
}

test('array and reward bounds are enforced before output', async () => {
  const tooManyActivities = calendarData({
    act_list: Array.from({ length: 513 }, (_, index) =>
      sourceActivity(1000 + index, 'Activity', `Synthetic Activity ${index}`)),
    challenge_list: [],
  });
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: tooManyActivities })));

  const tooManyChallenges = calendarData({
    act_list: [],
    challenge_list: Array.from({ length: 513 }, (_, index) =>
      sourceChallenge(1000 + index, 'Challenge', `Synthetic Challenge ${index}`)),
  });
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: tooManyChallenges })));

  const tooManyRewards = calendarData();
  tooManyRewards.act_list[0].reward_list = Array.from({ length: 1025 }, (_, index) => reward(index + 1));
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: tooManyRewards })));

  const tooManyDropTypes = calendarData();
  tooManyDropTypes.act_list[0].multiple_drop_type_list = Array.from({ length: 65 }, (_, index) => index);
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: tooManyDropTypes })));
});

test('embedded script rejects lone UTF-16 display text while accepting paired supplementary and replacement characters', async () => {
  const malformed = calendarData();
  malformed.act_list[0].name = '\uD800';
  assertNoPartialSuccess(await resultOf(createScenario({ calendarData: malformed })));

  const valid = calendarData();
  valid.act_list[0].name = 'Synthetic 😀 Double Reward';
  const result = await resultOf(createScenario({ calendarData: valid }));
  assert.equal(result.status, 'done');
  assert.equal(result.events.activities[0].name, 'Synthetic 😀 Double Reward');

  const replacement = calendarData();
  replacement.act_list[0].name = 'Synthetic � Double Reward';
  const accepted = await resultOf(createScenario({ calendarData: replacement }));
  assert.equal(accepted.status, 'done');
  assert.equal(accepted.events.activities[0].name, 'Synthetic � Double Reward');
});

for (const [name, overrides] of [
  ['foreign role', { roles: [{ game_biz: 'hkrpg_global', region: server, game_uid: '987654321' }] }],
  ['wrong role game', { roles: [{ game_biz: 'other_game', region: server, game_uid: roleId }] }],
  ['duplicate role', { roles: [
    { game_biz: 'hkrpg_global', region: server, game_uid: roleId },
    { game_biz: 'hkrpg_global', region: server, game_uid: roleId },
  ] }],
  ['too many roles', { roles: Array.from({ length: 9 }, (_, index) => ({
    game_biz: 'hkrpg_global', region: server, game_uid: String(123456780 + index),
  })) }],
  ['final role loss', { roleDataByCall: [
    { list: [{ game_biz: 'hkrpg_global', region: server, game_uid: roleId }] },
    { list: [] },
  ] }],
  ['final role changed', { roleDataByCall: [
    { list: [{ game_biz: 'hkrpg_global', region: server, game_uid: roleId }] },
    { list: [{ game_biz: 'hkrpg_global', region: server, game_uid: '987654321' }] },
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
    'Activity',
    'Synthetic Event '.padEnd(256, 'x'),
    { rewards: Array.from({ length: 32 }, (_, rewardIndex) => reward(rewardIndex + 1, 'Synthetic Reward'.padEnd(256, 'r'))) },
  ));
  const result = await resultOf(createScenario({
    calendarData: {
      act_list: events,
      challenge_list: [],
      now: '1709164800',
      cur_game_version: '3.7',
    },
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
