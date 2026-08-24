// reactUsagePanel.test.js — settings-opened Usage panel + profile/usage seam.
//
// Drives the real app so the footer, Usage island, request broker and host
// event seam are exercised together. The report fixture mirrors the compact
// public bridge contract: exact total tokens, one 365-day series and one
// folded completeness object.

import { afterEach, beforeEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import {
  completeness,
  dailyTokens,
  fixture,
  openPanelWith,
  providerReport,
  report,
  settle,
  usageCommands,
  configCommands,
  settingsCommands
} from './usagePanelFixture.js';

beforeEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = false;
});


test('terminal-only startup fetches the saved profile without waiting for History', async () => {
  const rig = await fixture({ withWorkspace: false });

  const bootstrap = rig.posted.find(
    (message) => message.type === 'agent_global_command' && message.command === 'profile_get'
  );
  assert.ok(bootstrap, 'profile bootstrap must not wait for an Agent workspace');
  await settle(
    () => rig.app.handle({
      type: 'agent_profile',
      requestId: bootstrap.requestId,
      revision: 1,
      displayName: 'neo wang'
    }),
    () => true
  );
  await settle(
    () => rig.app.openSettings('profile'),
    () => document.querySelector('[data-role="usage-panel"]')
  );
  const fallback = document.querySelector('.agent-usage-avatar-fallback');
  assert.ok(fallback);
  assert.equal(fallback.textContent, 'N');
  assert.equal(document.querySelector('[data-role="usage-panel"] img'), null);

  const avatar = 'data:image/png;base64,iVBORw0KGgo=';
  await settle(
    () =>
      rig.app.handle({
        type: 'agent_profile',
        revision: 2,
        displayName: 'neo wang',
        avatarDataUrl: avatar
      }),
    () => document.querySelector('[data-role="usage-panel"] img')
  );
  const image = document.querySelector('[data-role="usage-panel"] img');
  assert.equal(image.getAttribute('src'), avatar);
  assert.equal(image.getAttribute('alt'), '');
});

test('panel: a failed profile save stays on the homepage as an alert', async () => {
  const rig = await fixture({ withWorkspace: false });
  const bootstrap = rig.posted.find(
    (message) => message.type === 'agent_global_command' && message.command === 'profile_get'
  );
  await settle(
    () => rig.app.handle({
      type: 'agent_profile',
      requestId: bootstrap.requestId,
      revision: 1,
      displayName: 'neo wang'
    }),
    () => true
  );
  await settle(
    () => rig.app.openSettings('profile'),
    () => document.querySelector('[data-role="usage-profile-name"]')
  );

  await settle(
    () => document.querySelector('[data-role="usage-profile-name"]').click(),
    () => document.querySelector('[data-role="usage-profile-name-input"]')
  );
  const input = document.querySelector('[data-role="usage-profile-name-input"]');
  const setValue = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
  await settle(
    () => {
      setValue.call(input, 'trinity');
      input.dispatchEvent(new Event('input', { bubbles: true }));
    },
    () => input.value === 'trinity'
  );
  await settle(
    () => input.blur(),
    () => rig.posted.some(
      (message) => message.type === 'agent_global_command' && message.command === 'profile_set_name'
    ),
    'profile_set_name command'
  );
  const mutation = rig.posted.find(
    (message) => message.type === 'agent_global_command' && message.command === 'profile_set_name'
  );
  assert.equal(mutation.value, 'trinity');
  assert.equal(document.querySelector('[data-role="usage-profile-error"]'), null);

  await settle(
    () =>
      rig.app.handle({
        type: 'agent_profile',
        requestId: mutation.requestId,
        error: 'Unable to write profile.',
        revision: 9,
        displayName: ''
      }),
    () => document.querySelector('[data-role="usage-profile-error"]')
  );
  const alert = document.querySelector('[data-role="usage-profile-error"]');
  assert.equal(alert.getAttribute('role'), 'alert');
  assert.equal(alert.textContent, 'Unable to write profile.');
  assert.equal(document.querySelector('[data-role="usage-profile-name"]').textContent, 'neo wang');
});

test('panel: Escape closes the usage panel', async () => {
  const rig = await fixture({ withWorkspace: false });
  await openPanelWith(rig, {
    report: report(),
    completeness: completeness()
  });
  assert.ok(document.querySelector('[data-role="usage-panel"]'));

  await act(async () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await new Promise((resolve) => setTimeout(resolve, 0));
  });

  assert.equal(document.querySelector('[data-role="usage-panel"]'), null);
});

test('panel: window switching changes only the backend-owned total', async () => {
  const rig = await fixture();
  const year = dailyTokens({ 0: 1, 20: 12, 364: 300 });
  await openPanelWith(rig, {
    report: report({ dailyTokens: year }),
    completeness: completeness()
  });

  const panel = document.querySelector('[data-role="usage-panel"]');
  const heatmap = panel.querySelector('[data-role="usage-heatmap"]');
  const heatmapMarkup = heatmap.innerHTML;
  assert.equal(panel.querySelector('[data-role="usage-total-tokens"]').textContent, '200');

  await settle(
    () => panel.querySelector('[data-window="last7Days"]').click(),
    () => panel.querySelector('[data-role="usage-total-tokens"]').textContent === '1,500'
  );
  assert.equal(heatmap.innerHTML, heatmapMarkup, 'the annual heatmap is window-independent');

  await settle(
    () => panel.querySelector('[data-window="last30Days"]').click(),
    () => panel.querySelector('[data-role="usage-total-tokens"]').textContent === '3,290,258'
  );
  assert.equal(panel.querySelectorAll('[data-role="usage-provider-row"]').length, 1);
  for (const forbidden of [
    'contextSnapshots',
    'cacheHitRate',
    'activeThreads',
    'model-today',
    'acp-claude'
  ]) {
    assert.ok(!panel.innerHTML.includes(forbidden), `${forbidden} must not enter the DOM`);
  }
});

test('panel: heatmap is Monday-aligned, logarithmic and one focus stop', async () => {
  const rig = await fixture();
  const year = dailyTokens({ 0: 1, 1: 1000000, 2: 100 });
  await openPanelWith(rig, {
    report: report({ heatmapStartDate: '2026-01-01', dailyTokens: year }),
    completeness: completeness()
  });

  const region = document.querySelector('[data-role="usage-heatmap"]');
  const grid = region.querySelector('.agent-usage-heatmap-grid');
  const cells = [...region.querySelectorAll('[data-role="usage-heatmap-cell"]')];
  assert.equal(region.getAttribute('tabindex'), '0');
  assert.equal(grid.getAttribute('aria-hidden'), 'true');
  assert.equal(grid.children.length, 371, '53 columns × 7 rows');
  assert.equal(cells.length, 365);
  assert.equal(grid.children[0].className, 'agent-usage-heatmap-placeholder');
  assert.equal(grid.children[2].className, 'agent-usage-heatmap-placeholder');
  assert.equal(grid.children[3].dataset.index, '0', 'Thursday starts after three Monday slots');
  assert.equal(cells[0].title, '2026年1月1日 · 1 Token');
  assert.equal(cells[364].title, '2026年12月31日 · 0 Token');
  assert.equal(cells[0].dataset.level, '1');
  assert.equal(cells[1].dataset.level, '4');
  assert.ok(cells.every((cell) => !cell.hasAttribute('tabindex')));
  assert.match(region.getAttribute('aria-label'), /Asia\/Shanghai/);
  assert.match(region.getAttribute('aria-label'), /1,000,101 Token/);
  assert.ok(!region.textContent.includes('低 → 高'));
  assert.ok(region.textContent.includes('颜色越深，当天 Token 越多'));
});

test('panel: partial overall report keeps readable provider usage selectable', async () => {
  const rig = await fixture();
  const claude = providerReport();
  const kimi = providerReport({
    providerKey: 'acp-kimi',
    displayName: 'Kimi Code',
    iconKey: 'kimi',
    dailyTokens: dailyTokens({ 10: 250 }),
    today: { totalTokens: 40 },
    last7Days: { totalTokens: 250 },
    last30Days: { totalTokens: 250 },
    completeness: completeness({
      status: 'partial',
      reasons: ['unmatched_sessions'],
      expectedSessions: 2,
      matchedSessions: 1
    })
  });
  await openPanelWith(rig, {
    report: report({ providers: [claude, kimi] }),
    completeness: completeness({
      status: 'partial',
      reasons: ['unmatched_sessions'],
      expectedSessions: 3,
      matchedSessions: 2
    })
  });

  const panel = document.querySelector('[data-role="usage-panel"]');
  assert.equal(panel.querySelector('[data-role="usage-total-tokens"]').textContent, '200');
  assert.match(panel.querySelector('.agent-usage-overview-note').textContent, /统计不完整/);
  assert.match(panel.querySelector('.agent-usage-overview-note').textContent, /仅包含已读取到的精确 Token/);
  assert.equal(panel.querySelector('[data-role="usage-all-provider"]').disabled, true);

  const rows = [...panel.querySelectorAll('[data-role="usage-provider-row"]')];
  const claudeRow = rows.find((row) => row.textContent.includes('Claude Code'));
  const kimiRow = rows.find((row) => row.textContent.includes('Kimi Code'));
  assert.equal(claudeRow.getAttribute('aria-pressed'), 'true');
  assert.match(kimiRow.textContent, /已记录 40 Token/);
  assert.match(kimiRow.title, /部分会话未计入/);

  await act(async () => {
    kimiRow.click();
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
  assert.equal(kimiRow.getAttribute('aria-pressed'), 'true');
  assert.match(panel.querySelector('[data-role="usage-heatmap"]').getAttribute('aria-label'), /Kimi Code/);

  const html = panel.innerHTML;
  assert.ok(!html.includes('acp-claude'));
  assert.ok(!html.includes('acp-kimi'));
  assert.ok(!html.includes('.claude'));
  assert.ok(!html.includes('C:\\'));
});

test('panel: unavailable differs from a real available zero', async () => {
  const rig = await fixture();
  const unavailableProvider = providerReport({
    dailyTokens: dailyTokens(),
    today: { totalTokens: 0 },
    last7Days: { totalTokens: 0 },
    last30Days: { totalTokens: 0 },
    completeness: completeness({
      status: 'unavailable',
      reasons: ['missing_session_logs'],
      expectedSessions: 1,
      matchedSessions: 0
    })
  });
  await openPanelWith(rig, {
    report: report({
      dailyTokens: dailyTokens(),
      today: { totalTokens: 0 },
      providers: [unavailableProvider]
    }),
    completeness: completeness({
      status: 'unavailable',
      reasons: ['missing_session_logs'],
      expectedSessions: null,
      matchedSessions: null,
      untrackedThreads: 1
    })
  });

  assert.equal(
    document.querySelector('[data-role="usage-total-tokens"]').textContent,
    '0'
  );
  assert.ok(document.querySelector('[data-role="usage-heatmap-empty"]'));
  assert.equal(document.querySelector('[data-role="usage-heatmap-cell"]'), null);

  await settle(
    () => document.querySelector('[data-role="usage-refresh"]').click(),
    () => usageCommands(rig.posted).length === 2
  );
  const refresh = usageCommands(rig.posted).at(-1);
  await settle(
    () =>
      rig.app.handle({
        type: 'agent_usage_report',
        requestId: refresh.requestId,
        generatedAt: 'now',
        timezone: 'Asia/Shanghai',
        report: report({ dailyTokens: dailyTokens(), today: { totalTokens: 0 } }),
        completeness: completeness()
      }),
    () => document.querySelectorAll('[data-role="usage-heatmap-cell"]').length === 365
  );
  assert.equal(
    document.querySelector('[data-role="usage-total-tokens"]').textContent,
    '0'
  );
  assert.equal(document.querySelectorAll('[data-role="usage-heatmap-cell"]').length, 365);
});

test('panel: an error reply renders inline and retry preserves the request flow', async () => {
  const rig = await fixture();
  await openPanelWith(rig, { error: '扫描失败' });

  const error = document.querySelector('[data-role="usage-error"]');
  assert.ok(error);
  assert.match(error.textContent, /扫描失败/);

  await settle(
    () => document.querySelector('[data-role="usage-retry"]').click(),
    () => usageCommands(rig.posted).length === 2
  );
  const retry = usageCommands(rig.posted).at(-1);
  assert.equal(retry.value, 'cached');
  await settle(
    () =>
      rig.app.handle({
        type: 'agent_usage_report',
        requestId: retry.requestId,
        generatedAt: 'now',
        timezone: 'UTC',
        report: report(),
        completeness: completeness()
      }),
    () => document.querySelector('[data-role="usage-overview"]')
  );
  assert.equal(document.querySelector('[data-role="usage-error"]'), null);
});

test('panel: refresh forces a rescan and accepts the matching reply', async () => {
  const rig = await fixture();
  await openPanelWith(rig, {
    report: report(),
    completeness: completeness()
  });

  await settle(
    () => document.querySelector('[data-role="usage-refresh"]').click(),
    () => usageCommands(rig.posted).length === 2
  );
  const forced = usageCommands(rig.posted).at(-1);
  assert.equal(forced.value, 'force');

  await settle(
    () =>
      rig.app.handle({
        type: 'agent_usage_report',
        requestId: forced.requestId,
        generatedAt: 'now',
        timezone: 'UTC',
        report: report({ today: { totalTokens: 201 } }),
        completeness: completeness()
      }),
    () => document.querySelector('[data-role="usage-total-tokens"]').textContent === '201'
  );
});

test('panel: config opens as its own surface without a usage/config switch', async () => {
  const rig = await fixture();
  await settle(
    () => rig.app.openUsage('config'),
    () => configCommands(rig.posted).length > 0,
    'config_report command'
  );
  const request = configCommands(rig.posted).at(-1);
  assert.equal(request.value, 'cached');

  const title = document.querySelector('[data-role="settings-heading"]');
  assert.ok(title);
  assert.equal(title.textContent, '配置');
  assert.equal(document.querySelector('[data-role="usage-tab-switch"]'), null);
  assert.equal(document.querySelector('[data-role="usage-avatar-button"]'), null);
  const nav = [...document.querySelectorAll('[data-role="settings-nav-item"]')].map(
    (node) => node.getAttribute('data-section')
  );
  assert.deepEqual(nav, ['profile', 'usage', 'config', 'registry']);

  await settle(
    () =>
      rig.app.handle({
        type: 'agent_config_report',
        requestId: request.requestId,
        generatedAt: '2026-08-06T00:00:00Z',
        report: {
          providers: [
            {
              providerKey: 'acp-claude',
              displayName: 'Claude Code',
              iconKey: 'claude',
              state: 'available',
              facts: [{ labelKey: 'config.fact.default_model', value: 'sonnet' }],
              models: [],
              mcpServers: [{
                name: 'demo',
                transport: 'stdio',
                target: 'npx demo',
                enabled: true,
                envKeys: ['TOKEN'],
                headerKeys: []
              }],
              skills: [{ name: 'skill-a' }],
              notes: []
            },
            {
              providerKey: 'acp-kimi',
              displayName: 'Kimi Code',
              iconKey: 'kimi',
              state: 'available',
              facts: [{ labelKey: 'config.fact.default_model', value: 'kimi-k2' }],
              models: [],
              mcpServers: [],
              skills: [],
              notes: []
            }
          ]
        }
      }),
    () => document.querySelector('[data-role="config-provider-card"]'),
    'config detail'
  );

  const tabs = [...document.querySelectorAll('[data-role="config-provider-tab"]')];
  assert.equal(tabs.length, 2);
  assert.equal(tabs[0].getAttribute('data-provider'), 'acp-claude');
  assert.equal(tabs[0].getAttribute('aria-selected'), 'true');

  let card = document.querySelector('[data-role="config-provider-card"]');
  assert.equal(card.getAttribute('data-provider'), 'acp-claude');
  assert.match(card.textContent, /sonnet/);
  assert.equal(document.querySelectorAll('[data-role="config-provider-card"]').length, 1);

  await settle(
    () => tabs[1].click(),
    () =>
      document
        .querySelector('[data-role="config-provider-card"]')
        ?.getAttribute('data-provider') === 'acp-kimi',
    'switch to Kimi'
  );
  card = document.querySelector('[data-role="config-provider-card"]');
  assert.match(card.textContent, /kimi-k2/);
  assert.doesNotMatch(card.textContent, /sonnet/);
  assert.equal(document.querySelector('[data-role="usage-overview"]'), null);
});

test('panel: settings dialog uses a left nav for profile, usage, config and registry', async () => {
  const rig = await fixture({ withWorkspace: false });
  await settle(
    () => rig.app.openSettings(),
    () => document.querySelector('[data-role="usage-panel"]')
  );
  const nav = [...document.querySelectorAll('[data-role="settings-nav-item"]')];
  assert.deepEqual(
    nav.map((node) => node.getAttribute('data-section')),
    ['profile', 'usage', 'config', 'registry']
  );
  assert.equal(nav[0].getAttribute('aria-current'), 'page');
  assert.equal(document.querySelector('[data-role="settings-heading"]').textContent, '个人主页');
  assert.ok(document.querySelector('[data-role="usage-avatar-button"]'));
  assert.match(document.body.textContent, /目前可修改显示名称/);
});

test('panel: registry apply stays disabled until the npm source changes', async () => {
  const rig = await fixture({ withWorkspace: false });
  await settle(
    () => rig.app.openSettings('registry'),
    () => settingsCommands(rig.posted).some((message) => message.action === 'get'),
    'app_settings get'
  );
  const get = settingsCommands(rig.posted).find((message) => message.action === 'get');
  await settle(
    () =>
      rig.app.handle({
        type: 'app_settings_snapshot',
        revision: 1,
        dshRegistry: 'official',
        requestId: get.requestId
      }),
    () => document.querySelector('[data-role="settings-registry-apply"]')
  );
  const apply = document.querySelector('[data-role="settings-registry-apply"]');
  assert.equal(apply.disabled, true);
  assert.ok([...document.querySelectorAll('[data-role="settings-registry-choice"]')]
    .some((button) => button.textContent.includes('npmmirror.com')));

  await settle(
    () => document.querySelector('[data-registry="npmmirror"]').click(),
    () => document.querySelector('[data-role="settings-registry-apply"]')?.disabled === false
  );
});

test('panel: registry abandons a stale GET when a newer broadcast lands first', async () => {
  const rig = await fixture({ withWorkspace: false });
  await settle(
    () => rig.app.openSettings('registry'),
    () => settingsCommands(rig.posted).some((message) => message.action === 'get'),
    'app_settings get'
  );
  const getId = settingsCommands(rig.posted).find((message) => message.action === 'get').requestId;

  await settle(
    () =>
      rig.app.handle({
        type: 'app_settings_snapshot',
        revision: 2,
        dshRegistry: 'npmmirror'
      }),
    () => document.querySelector('[data-registry="npmmirror"]')?.getAttribute('data-selected') === 'true'
  );

  await settle(
    () =>
      rig.app.handle({
        type: 'app_settings_snapshot',
        revision: 1,
        dshRegistry: 'official',
        requestId: getId
      }),
    () => true
  );
  assert.equal(
    document.querySelector('[data-registry="npmmirror"]').getAttribute('data-selected'),
    'true',
    'stale GET must not revive the old draft'
  );
  assert.equal(document.querySelector('[data-role="settings-registry-apply"]').disabled, true);
});
