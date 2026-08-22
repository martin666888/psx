// usageBroker.test.js — drives the UsageStore + UsageRequestBroker pair with a
// fake root bridge. Unlike History,
// every profile/usage command carries a requestId and only the matching reply
// resolves it: usage replies for a superseded (or never-issued) requestId are
// dropped, profile broadcasts with no requestId still apply through the
// monotonic revision guard, and one 30s timeout triggers one root-bridge retry.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { appModule } from './agentHarness.js';

const { UsageStore } = await appModule('usage/UsageStore.js');
const { UsageRequestBroker } = await appModule('usage/UsageRequestBroker.js');

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// A generous default timeout so a slow (coverage-instrumented) run never trips
// the in-flight retry mid-test; the timeout tests pass their own tight value.
function makeRig({ timeoutMs = 5000 } = {}) {
  const store = new UsageStore();
  const commands = [];
  const host = {
    sendGlobalCommand(command, value, requestId) {
      commands.push({ command, value, requestId });
    },
    sendAppSettingsCommand(action, requestId, registry) {
      commands.push({ action, requestId, registry });
    }
  };
  const broker = new UsageRequestBroker(host, store, { timeoutMs });
  return { store, broker, host, commands };
}

function usageCommands(commands) {
  return commands.filter((entry) => entry.command === 'usage_report');
}

function profileGets(commands) {
  return commands.filter((entry) => entry.command === 'profile_get');
}

function minimalReport() {
  return {
    dailyTokens: Array.from({ length: 365 }, (_, index) => (index < 3 ? index + 1 : 0)),
    heatmapStartDate: '2024-01-01',
    today: { totalTokens: 3 },
    last7Days: { totalTokens: 6 },
    last30Days: { totalTokens: 6 },
    providers: [{
      providerKey: 'acp-claude',
      displayName: 'Claude Code',
      iconKey: 'claude',
      dailyTokens: Array.from({ length: 365 }, (_, index) => (index < 3 ? index + 1 : 0)),
      today: { totalTokens: 3 },
      last7Days: { totalTokens: 6 },
      last30Days: { totalTokens: 6 },
      completeness: completeness()
    }]
  };
}

function completeness(overrides = {}) {
  return {
    status: 'available',
    reasons: [],
    expectedSessions: 1,
    matchedSessions: 1,
    skippedFiles: 0,
    badLines: 0,
    untrackedThreads: 0,
    ...overrides
  };
}

// --- profile bootstrap ---------------------------------------------------------

test('broker: fetches the profile immediately through the process-wide bridge', () => {
  const rig = makeRig();
  const gets = profileGets(rig.commands);
  assert.equal(gets.length, 1, 'construction triggers exactly one bootstrap fetch');
  assert.ok(gets[0].requestId, 'the bootstrap profile_get carries a requestId');
});

// --- requestId matching --------------------------------------------------------

test('broker: a usage reply with the in-flight requestId lands in the store', () => {
  const rig = makeRig();

  rig.broker.requestUsage(false);
  assert.equal(rig.store.getState().status, 'loading');
  const sent = usageCommands(rig.commands).at(-1);
  assert.equal(sent.value, 'cached');

  rig.broker.handleUsageReport({
    requestId: sent.requestId,
    generatedAt: 'now',
    timezone: 'UTC',
    report: minimalReport(),
    completeness: completeness()
  });

  const state = rig.store.getState();
  assert.equal(state.status, 'idle');
  assert.equal(state.report.dailyTokens.length, 365);
  assert.deepEqual(state.report.dailyTokens.slice(0, 3), [1, 2, 3]);
  assert.equal(state.report.providers[0].displayName, 'Claude Code');
  assert.equal(state.completeness.status, 'available');
  assert.equal(state.generatedAt, 'now');
});

test('broker: drops a usage reply whose requestId does not match the in-flight one', () => {
  const rig = makeRig();

  rig.broker.requestUsage(false);
  rig.broker.handleUsageReport({ requestId: 'stale-id', report: minimalReport() });

  const state = rig.store.getState();
  assert.equal(state.status, 'loading', 'a mismatched reply is ignored');
  assert.equal(state.report, null);
});

test('broker: drops a usage reply that arrives without any in-flight request', () => {
  const rig = makeRig();

  // No requestUsage yet: a spontaneous reply must not clobber the idle state.
  rig.broker.handleUsageReport({ requestId: 'u1', report: minimalReport() });
  const state = rig.store.getState();
  assert.equal(state.status, 'idle');
  assert.equal(state.report, null);
});

// --- consecutive force never crosses ids --------------------------------------

test('broker: consecutive force refreshes supersede — the older reply is dropped', () => {
  const rig = makeRig();

  rig.broker.requestUsage(true);
  const first = usageCommands(rig.commands).at(-1);
  assert.equal(first.value, 'force');

  rig.broker.requestUsage(true);
  const second = usageCommands(rig.commands).at(-1);
  assert.notEqual(first.requestId, second.requestId, 'each force refresh gets a fresh requestId');

  // The first (superseded) scan finally lands: it must be dropped.
  rig.broker.handleUsageReport({ requestId: first.requestId, report: { ...minimalReport(), heatmapStartDate: 'STALE' } });
  assert.equal(rig.store.getState().status, 'loading');
  assert.equal(rig.store.getState().report, null);

  // The current scan lands normally.
  rig.broker.handleUsageReport({
    requestId: second.requestId,
    report: { ...minimalReport(), heatmapStartDate: 'FRESH' },
    completeness: completeness()
  });
  const state = rig.store.getState();
  assert.equal(state.status, 'idle');
  assert.equal(state.report.heatmapStartDate, 'FRESH');
});

// --- usage error + terminal-only global channel -------------------------------

test('broker: a usage reply carrying an error surfaces the error text', () => {
  const rig = makeRig();

  rig.broker.requestUsage(false);
  const sent = usageCommands(rig.commands).at(-1);
  rig.broker.handleUsageReport({ requestId: sent.requestId, error: 'scan failed' });

  const state = rig.store.getState();
  assert.equal(state.status, 'error');
  assert.equal(state.errorText, 'scan failed');
});

test('broker: requesting usage without an Agent workspace uses the global bridge', () => {
  const rig = makeRig();
  rig.broker.requestUsage(false);
  const sent = usageCommands(rig.commands).at(-1);
  assert.equal(sent.value, 'cached');
  assert.ok(sent.requestId);
  assert.equal(rig.store.getState().status, 'loading');
});

// --- profile broadcasts apply by revision, never complete pending -------------

test('broker: a profile broadcast (no requestId) applies through the revision guard', () => {
  const rig = makeRig();

  // Broadcast from a set on another window: no requestId, monotonic revision.
  rig.broker.handleProfile({ revision: 2, displayName: 'Neo', avatarDataUrl: 'data:image/png;base64,AAA' });
  let profile = rig.store.getState().profile;
  assert.equal(profile.displayName, 'Neo');
  assert.equal(profile.revision, 2);

  // A stale broadcast (older revision) can never overwrite the newer value.
  rig.broker.handleProfile({ revision: 1, displayName: 'Old' });
  profile = rig.store.getState().profile;
  assert.equal(profile.displayName, 'Neo', 'the monotonic guard rejects the stale broadcast');
  assert.equal(profile.revision, 2);
});

test('broker: a profile reply carrying an error leaves the stored revision untouched', () => {
  const rig = makeRig();

  rig.broker.handleProfile({ revision: 3, displayName: 'Trinity' });
  assert.equal(rig.store.getState().profile.revision, 3);

  // A failed set echoes the requestId with an error: the store must not apply
  // its payload (which would otherwise jump the revision forward).
  rig.broker.setDisplayName('   ');
  assert.equal(rig.store.getState().profileSaving, true);
  const mutation = rig.commands.at(-1);
  assert.equal(mutation.command, 'profile_set_name');
  rig.broker.handleProfile({ requestId: mutation.requestId, error: 'blank name', revision: 9, displayName: '' });

  const state = rig.store.getState();
  assert.equal(state.profile.displayName, 'Trinity');
  assert.equal(state.profile.revision, 3);
  assert.equal(state.profileSaving, false);
  assert.equal(state.profileError, 'blank name');
});

test('broker: unmatched profile errors stay silent and never apply', () => {
  const rig = makeRig();
  rig.broker.handleProfile({ revision: 1, displayName: 'Neo' });
  rig.broker.setDisplayName('Trinity');
  const first = rig.commands.at(-1);
  rig.broker.setDisplayName('Morpheus');
  assert.equal(rig.store.getState().profileSaving, true);

  rig.broker.handleProfile({
    requestId: 'stale-profile',
    error: 'Access to the path C:\\Users\\neo\\.psx is denied.',
    revision: 9,
    displayName: ''
  });
  let state = rig.store.getState();
  assert.equal(state.profile.displayName, 'Neo');
  assert.equal(state.profileError, '', 'unmatched errors must not fill the homepage');
  assert.equal(state.profileSaving, true, 'an in-flight save stays saving');

  rig.broker.handleProfile({
    requestId: first.requestId,
    error: 'first failed',
    revision: 9
  });
  state = rig.store.getState();
  assert.equal(state.profileError, 'first failed');
  assert.equal(state.profileSaving, true, 'a newer in-flight save keeps the busy state');

  const second = rig.commands.at(-1);
  rig.broker.handleProfile({
    requestId: second.requestId,
    error: 'Access to the path C:\\Users\\neo\\.psx is denied.',
    revision: 9
  });
  state = rig.store.getState();
  assert.equal(state.profile.displayName, 'Neo');
  assert.equal(state.profileSaving, false);
  assert.equal(state.profileError, '无法保存个人资料，请重试。');
});

test('broker: a late profile read cannot clear a newer mutation error', () => {
  const rig = makeRig();
  const bootstrap = rig.commands.find((entry) => entry.command === 'profile_get');
  assert.ok(bootstrap?.requestId);

  rig.broker.handleProfile({ revision: 3, displayName: 'Trinity' });
  rig.broker.setDisplayName('Neo');
  const mutation = rig.commands.at(-1);
  rig.broker.handleProfile({
    requestId: mutation.requestId,
    error: 'Unable to write profile.',
    revision: 3,
    displayName: 'Trinity'
  });
  assert.equal(rig.store.getState().profileError, 'Unable to write profile.');

  rig.broker.handleProfile({
    requestId: bootstrap.requestId,
    revision: 3,
    displayName: 'Trinity'
  });
  const state = rig.store.getState();
  assert.equal(state.profile.displayName, 'Trinity');
  assert.equal(state.profileError, 'Unable to write profile.');
  assert.equal(state.profileSaving, false);
});

test('broker: unmatched successful profile replies are dropped', () => {
  const rig = makeRig();
  rig.broker.handleProfile({ revision: 2, displayName: 'Neo' });
  rig.broker.handleProfile({
    requestId: 'never-issued',
    revision: 9,
    displayName: 'Mallory'
  });

  const profile = rig.store.getState().profile;
  assert.equal(profile.displayName, 'Neo');
  assert.equal(profile.revision, 2);
});

// --- timeout + retry -----------------------------------------------------------

test('broker: times out, retries once on the global bridge, then errors', async () => {
  const rig = makeRig({ timeoutMs: 30 });

  rig.broker.requestUsage(true);
  const first = usageCommands(rig.commands).at(-1);

  await sleep(50);
  const retried = usageCommands(rig.commands);
  assert.equal(retried.length, 2, 'one retry after the first timeout');
  assert.notEqual(retried[1].requestId, first.requestId, 'the retry uses a fresh requestId');

  await sleep(50);
  const state = rig.store.getState();
  assert.equal(state.status, 'error');
  assert.match(state.errorText, /timed out/i);
});

// --- config_report (Usage panel「配置」tab) -----------------------------------

function configCommands(commands) {
  return commands.filter((entry) => entry.command === 'config_report');
}

function minimalConfigReport() {
  return {
    providers: [{
      providerKey: 'acp-claude',
      displayName: 'Claude Code',
      iconKey: 'claude',
      state: 'available',
      facts: [{ label: '默认模型', value: 'sonnet' }],
      models: [],
      mcpServers: [],
      skills: [{ name: 'demo' }],
      notes: []
    }]
  };
}

test('broker: requestConfig sends config_report and applies a matching reply', () => {
  const rig = makeRig();
  rig.broker.requestConfig(false);
  const sent = configCommands(rig.commands).at(-1);
  assert.equal(sent.command, 'config_report');
  assert.equal(sent.value, 'cached');
  assert.equal(rig.store.getState().configStatus, 'loading');

  rig.broker.handleConfigReport({
    requestId: sent.requestId,
    generatedAt: '2026-08-06T00:00:00Z',
    report: minimalConfigReport()
  });
  const state = rig.store.getState();
  assert.equal(state.configStatus, 'idle');
  assert.equal(state.configLoadedOnce, true);
  assert.equal(state.configReport.providers[0].providerKey, 'acp-claude');
  assert.equal(state.configReport.providers[0].facts[0].value, 'sonnet');
});

test('broker: drops late or superseded config_report replies', () => {
  const rig = makeRig();
  rig.broker.requestConfig(false);
  const first = configCommands(rig.commands).at(-1);
  rig.broker.requestConfig(true);
  const second = configCommands(rig.commands).at(-1);
  assert.notEqual(first.requestId, second.requestId);
  assert.equal(second.value, 'force');

  rig.broker.handleConfigReport({
    requestId: first.requestId,
    report: minimalConfigReport()
  });
  assert.equal(rig.store.getState().configStatus, 'loading', 'late first reply is dropped');

  rig.broker.handleConfigReport({
    requestId: second.requestId,
    generatedAt: '2026-08-06T01:00:00Z',
    report: minimalConfigReport()
  });
  assert.equal(rig.store.getState().configStatus, 'idle');
});

test('broker: config error payload surfaces in the config slice', () => {
  const rig = makeRig();
  rig.broker.requestConfig(false);
  const sent = configCommands(rig.commands).at(-1);
  rig.broker.handleConfigReport({ requestId: sent.requestId, error: '无法读取配置信息，请重试。' });
  const state = rig.store.getState();
  assert.equal(state.configStatus, 'error');
  assert.match(state.configErrorText, /无法读取配置/);
  assert.equal(state.configLoadedOnce, true);
});

test('broker: a newer settings broadcast drops a stale GET reply', () => {
  const rig = makeRig();
  rig.broker.requestSettings();
  const get = rig.commands.find((entry) => entry.action === 'get');
  assert.ok(get?.requestId);

  rig.broker.handleAppSettings({ revision: 2, dshRegistry: 'npmmirror' });
  assert.equal(rig.store.getState().dshRegistry, 'npmmirror');
  assert.equal(rig.store.getState().settingsDraft, 'npmmirror');

  rig.broker.handleAppSettings({
    revision: 1,
    dshRegistry: 'official',
    requestId: get.requestId
  });
  assert.equal(rig.store.getState().dshRegistry, 'npmmirror');
  assert.equal(rig.store.getState().settingsDraft, 'npmmirror');
});
