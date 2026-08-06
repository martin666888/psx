// reactUsagePanel.test.js — dock footer semantics + the global Usage panel.
//
// Drives the real app so the footer, Usage island, request broker and host
// event seam are exercised together. The report fixture mirrors the compact
// public bridge contract: exact total tokens, one 365-day series and one
// folded completeness object.

import { afterEach, beforeEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import {
  installAgentRuntime,
  installBreakpoint,
  agentTemplateMarkup,
  createAgentWorkspace,
  registerAgentCleanup,
  appModule
} from './agentHarness.js';

beforeEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = false;
});

const WS = '88888888-8888-4888-8888-888888888888';

const dailyTokens = (entries = {}) =>
  Array.from({ length: 365 }, (_, index) => entries[index] ?? 0);

const completeness = (overrides = {}) => ({
  status: 'available',
  reasons: [],
  expectedSessions: 1,
  matchedSessions: 1,
  skippedFiles: 0,
  badLines: 0,
  untrackedThreads: 0,
  ...overrides
});

const providerReport = (overrides = {}) => ({
  providerKey: 'acp-claude',
  displayName: 'Claude Code',
  iconKey: 'claude',
  dailyTokens: dailyTokens({ 0: 1, 1: 2, 364: 3 }),
  today: { totalTokens: 200 },
  last7Days: { totalTokens: 1500 },
  last30Days: { totalTokens: 3290258 },
  completeness: completeness(),
  ...overrides
});

const report = (overrides = {}) => ({
  heatmapStartDate: '2026-01-01',
  dailyTokens: dailyTokens({ 0: 1, 1: 2, 364: 3 }),
  today: { totalTokens: 200 },
  last7Days: { totalTokens: 1500 },
  last30Days: { totalTokens: 3290258 },
  providers: [providerReport()],
  ...overrides
});

async function fixture() {
  const runtime = installAgentRuntime();
  await appModule('history/historyIsland.js');
  await appModule('usage/usageIsland.js');
  installBreakpoint(true);
  document.body.innerHTML = `<div id="agents"></div>${agentTemplateMarkup()}`;
  window.localStorage.setItem('psx.agent.historyDockOpen', '1');
  const { createAgentApp } = await appModule('entry.js');
  const app = createAgentApp({
    terminalManager: { setViewVisible() {} },
    container: document.getElementById('agents'),
    template: document.getElementById('agent-workspace-template')
  });
  registerAgentCleanup(() => app.dispose());
  createAgentWorkspace(app, WS);
  const footer = () => document.querySelector('[data-role="history-profile"]');
  for (let index = 0; index < 100 && !footer(); index++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(footer(), 'history dock footer did not mount');
  return { app, posted: runtime.postedMessages, footer };
}

async function settle(run, predicate, what = 'usage island') {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let index = 0; index < 50 && !predicate(); index++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(predicate(), what + ' did not settle');
}

function usageCommands(posted) {
  return posted.filter(
    (message) => message.type === 'agent_command' && message.command === 'usage_report'
  );
}

async function openPanelWith(rig, payload) {
  await settle(
    () => rig.footer().click(),
    () => usageCommands(rig.posted).length > 0,
    'usage_report command'
  );
  const request = usageCommands(rig.posted).at(-1);
  assert.equal(request.value, 'cached');
  await settle(
    () =>
      rig.app.handle({
        type: 'agent_usage_report',
        requestId: request.requestId,
        generatedAt: '2026-07-20T10:00:00Z',
        timezone: 'Asia/Shanghai',
        ...payload
      }),
    () => document.querySelector('[data-role="usage-overview"], [data-role="usage-error"]')
  );
  return request;
}

test('footer: keeps button semantics and the first-character avatar fallback', async () => {
  const rig = await fixture();
  const footer = rig.footer();

  assert.equal(footer.tagName, 'BUTTON');
  assert.ok(footer.getAttribute('aria-label'));
  assert.ok(footer.querySelector('svg[aria-hidden="true"]'));

  await settle(
    () => rig.app.handle({ type: 'agent_profile', revision: 1, displayName: 'neo wang' }),
    () => rig.footer().textContent.includes('neo wang')
  );
  const fallback = rig.footer().querySelector('.agent-history-dock-footer-avatar-fallback');
  assert.ok(fallback);
  assert.equal(fallback.textContent, 'N');
  assert.equal(rig.footer().querySelector('img'), null);

  const avatar = 'data:image/png;base64,iVBORw0KGgo=';
  await settle(
    () =>
      rig.app.handle({
        type: 'agent_profile',
        revision: 2,
        displayName: 'neo wang',
        avatarDataUrl: avatar
      }),
    () => rig.footer().querySelector('img')
  );
  const image = rig.footer().querySelector('img');
  assert.equal(image.getAttribute('src'), avatar);
  assert.equal(image.getAttribute('alt'), '');
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

test('panel: config tab lazy-loads config_report and switches agent sub-tabs', async () => {
  const rig = await fixture();
  await openPanelWith(rig, {
    report: report(),
    completeness: completeness()
  });

  const title = document.querySelector('.agent-usage-title');
  assert.ok(title);
  assert.equal(title.textContent, '用量与配置');

  const configTab = () =>
    document.querySelector('[data-role="usage-tab"][data-tab="config"]');
  assert.ok(configTab());

  function configCommands(posted) {
    return posted.filter(
      (message) => message.type === 'agent_command' && message.command === 'config_report'
    );
  }

  await settle(
    () => configTab().click(),
    () => configCommands(rig.posted).length > 0,
    'config_report command'
  );
  const request = configCommands(rig.posted).at(-1);
  assert.equal(request.value, 'cached');

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
              facts: [{ label: '默认模型', value: 'sonnet' }],
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
              facts: [{ label: '默认模型', value: 'kimi-k2' }],
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
