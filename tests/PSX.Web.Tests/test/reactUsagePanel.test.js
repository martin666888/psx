// reactUsagePanel.test.js — dock footer semantics + the global Usage panel.
//
// Drives the real app (createAgentApp) so the footer, the UsagePanelController
// island, the UsageRequestBroker and the registry seams are exercised
// together. Backend replies are injected as agent_profile/agent_usage_report
// host events answering the requestId captured from the posted bridge command.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import {
  installAgentRuntime,
  installBreakpoint,
  agentTemplateMarkup,
  createAgentWorkspace,
  appModule
} from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;
const WS = '88888888-8888-4888-8888-888888888888';

const tokens = (input = 0, output = 0, cacheRead = 0, cacheCreation = 0) => ({
  input,
  output,
  cacheRead,
  cacheCreation
});

const usageWindow = (overrides = {}) => ({
  activeThreads: 1,
  turns: 2,
  tokens: tokens(100, 50),
  cacheHitRate: null,
  providerSections: [],
  ...overrides
});

const report = (overrides = {}) => ({
  heatmap: [0, 1, 0, 3, 8],
  heatmapStartDate: '2026-01-01',
  today: usageWindow(),
  last7Days: usageWindow(),
  last30Days: usageWindow(),
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
  createAgentWorkspace(app, WS);
  const footer = () => document.querySelector('[data-role="history-profile"]');
  for (let i = 0; i < 100 && !footer(); i++) {
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
  for (let i = 0; i < 50 && !predicate(); i++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(predicate(), what + ' did not settle');
}

function usageCommands(posted) {
  return posted.filter(
    (message) => message.type === 'agent_command' && message.command === 'usage_report'
  );
}

/** Click the footer, answer the cached usage_report with the given payload,
 * and wait for the panel dialog to commit. */
async function openPanelWith(rig, payload) {
  await settle(
    () => rig.footer().click(),
    () => usageCommands(rig.posted).length > 0,
    'usage_report command'
  );
  const request = usageCommands(rig.posted).at(-1);
  assert.equal(request.value, 'cached', 'opening the panel loads from the backend cache');
  await settle(
    () =>
      rig.app.handle({
        type: 'agent_usage_report',
        requestId: request.requestId,
        generatedAt: '2026-07-20 10:00:00Z',
        timezone: 'Asia/Shanghai',
        ...payload
      }),
    () => document.querySelector('[data-role="usage-overview"], [data-role="usage-error"]')
  );
  return request;
}

// --- footer semantics ------------------------------------------------------------

test('footer: real button semantics with the first-character avatar fallback', async () => {
  const rig = await fixture();
  const footer = rig.footer();

  assert.equal(footer.tagName, 'BUTTON');
  assert.equal(footer.getAttribute('aria-label'), '打开用量面板');
  // The chart icon is decorative; the accessible name is the aria-label.
  assert.ok(footer.querySelector('svg[aria-hidden="true"]'), 'chart icon is aria-hidden');

  // No avatar yet: the fallback circle carries the first character.
  await settle(
    () => rig.app.handle({ type: 'agent_profile', revision: 1, displayName: 'neo wang' }),
    () => rig.footer().textContent.includes('neo wang')
  );
  const fallback = rig.footer().querySelector('.agent-history-dock-footer-avatar-fallback');
  assert.ok(fallback, 'fallback circle renders while no avatar is set');
  assert.equal(fallback.textContent, 'N', 'first character, uppercased');
  assert.equal(rig.footer().querySelector('img'), null);

  // An avatar broadcast swaps the fallback for the real image (empty alt).
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
  const img = rig.footer().querySelector('img');
  assert.equal(img.getAttribute('src'), avatar);
  assert.equal(img.getAttribute('alt'), '');
});

// --- open + window switch ----------------------------------------------------------

test('panel: opening loads cached usage and the window switch syncs overview + model rows', async () => {
  const rig = await fixture();
  await openPanelWith(rig, {
    report: report({
      today: usageWindow({
        activeThreads: 1,
        tokens: tokens(100, 50, 40, 10),
        cacheHitRate: 0.25,
        providerSections: [
          {
            providerKey: 'claude-code',
            iconKey: 'claude',
            exactUsage: {
              modelRows: [{ model: 'model-today', input: 100, output: 50, cacheRead: 40, cacheCreation: 10 }]
            },
            contextSnapshots: null,
            sourceKey: 'acp-claude'
          }
        ]
      }),
      last7Days: usageWindow({
        activeThreads: 4,
        tokens: tokens(1000, 500, 0, 0),
        cacheHitRate: null,
        providerSections: [
          {
            providerKey: 'claude-code',
            iconKey: 'claude',
            exactUsage: {
              modelRows: [{ model: 'model-7d', input: 1000, output: 500, cacheRead: 0, cacheCreation: 0 }]
            },
            contextSnapshots: null,
            sourceKey: 'acp-claude'
          }
        ]
      })
    }),
    sources: [{ key: 'acp-claude', status: 'available' }]
  });

  const panel = document.querySelector('[data-role="usage-panel"]');
  assert.ok(panel, 'usage panel dialog is open');

  // Today first: totals, output, hit rate, active threads + today's model row.
  assert.equal(panel.querySelector('[data-role="usage-total-tokens"]').textContent, '200');
  assert.equal(panel.querySelector('[data-role="usage-output-tokens"]').textContent, '50');
  assert.equal(panel.querySelector('[data-role="usage-cache-rate"]').textContent, '25.0%');
  assert.equal(panel.querySelector('[data-role="usage-active-threads"]').textContent, '1');
  assert.ok(panel.textContent.includes('model-today'));
  assert.ok(!panel.textContent.includes('model-7d'));

  // Switching the window swaps BOTH the overview and the model detail.
  await settle(
    () => panel.querySelector('[data-window="last7Days"]').click(),
    () => panel.textContent.includes('model-7d')
  );
  assert.equal(panel.querySelector('[data-role="usage-total-tokens"]').textContent, '1,500');
  assert.equal(panel.querySelector('[data-role="usage-active-threads"]').textContent, '4');
  // A zero denominator renders the em dash, never NaN.
  assert.equal(panel.querySelector('[data-role="usage-cache-rate"]').textContent, '—');
  assert.ok(!panel.textContent.includes('model-today'));
});

// --- field-driven provider sections --------------------------------------------------

test('panel: provider sections render purely by which fields exist', async () => {
  const rig = await fixture();
  await openPanelWith(rig, {
    report: report({
      today: usageWindow({
        providerSections: [
          {
            providerKey: 'exact-only',
            iconKey: 'agent',
            exactUsage: { modelRows: [{ model: 'm1', input: 1, output: 2, cacheRead: 3, cacheCreation: 4 }] },
            contextSnapshots: null,
            sourceKey: 'src-partial'
          },
          {
            providerKey: 'snapshot-only',
            iconKey: 'agent',
            exactUsage: null,
            contextSnapshots: [{ threadTitle: 'Kimi thread', usedTokens: 1200, windowTokens: 200000 }],
            sourceKey: ''
          },
          {
            providerKey: 'synthetic-both',
            iconKey: 'agent',
            exactUsage: { modelRows: [{ model: 'm2', input: 5, output: 6, cacheRead: 7, cacheCreation: 8 }] },
            contextSnapshots: [{ threadTitle: 'Hybrid thread', usedTokens: 10, windowTokens: null }],
            sourceKey: 'src-unavailable'
          }
        ]
      })
    }),
    sources: [
      {
        key: 'src-partial',
        status: 'partial',
        detail: '仅匹配到部分会话',
        scannedFiles: 5,
        skippedFiles: 2,
        badLines: 3
      },
      { key: 'src-unavailable', status: 'unavailable', detail: '未找到 .claude/projects' }
    ]
  });

  const sections = document.querySelectorAll('[data-role="usage-provider-section"]');
  assert.equal(sections.length, 3);

  const byKey = (key) =>
    document.querySelector(`[data-role="usage-provider-section"][data-provider-key="${key}"]`);

  // exactUsage → model table with the four token columns; no snapshot list.
  const exactOnly = byKey('exact-only');
  assert.ok(exactOnly.querySelector('[data-role="usage-model-table"]'));
  assert.equal(exactOnly.querySelector('[data-role="usage-context-snapshots"]'), null);
  const cells = [...exactOnly.querySelectorAll('[data-role="usage-model-row"] td')].map(
    (cell) => cell.textContent
  );
  assert.deepEqual(cells, ['m1', '1', '2', '3', '4']);

  // contextSnapshots → snapshot list flagged as excluded from the totals.
  const snapshotOnly = byKey('snapshot-only');
  assert.equal(snapshotOnly.querySelector('[data-role="usage-model-table"]'), null);
  assert.ok(snapshotOnly.querySelector('[data-role="usage-context-snapshots"]'));
  assert.ok(snapshotOnly.textContent.includes('不计入合计'));
  assert.ok(snapshotOnly.textContent.includes('Kimi thread'));

  // A synthetic provider carrying both fields renders both blocks — proof the
  // renderer never branches on the provider name.
  const both = byKey('synthetic-both');
  assert.ok(both.querySelector('[data-role="usage-model-table"]'));
  assert.ok(both.querySelector('[data-role="usage-context-snapshots"]'));

  // Source badges: partial exposes detail + skipped/bad counters,
  // unavailable exposes its detail.
  const partialBadge = exactOnly.querySelector('[data-role="usage-source-badge"]');
  assert.equal(partialBadge.dataset.status, 'partial');
  assert.ok(partialBadge.textContent.includes('仅匹配到部分会话'));
  assert.ok(partialBadge.textContent.includes('2'));
  assert.ok(partialBadge.textContent.includes('3'));
  const unavailableBadge = both.querySelector('[data-role="usage-source-badge"]');
  assert.equal(unavailableBadge.dataset.status, 'unavailable');
  assert.ok(unavailableBadge.textContent.includes('未找到 .claude/projects'));
});

// --- heatmap ---------------------------------------------------------------------------

test('panel: heatmap is one focus stop with an aria summary and hidden cells', async () => {
  const rig = await fixture();
  const heatmap = Array.from({ length: 365 }, (_, index) => (index % 30 === 0 ? 2 : 0));
  await openPanelWith(rig, { report: report({ heatmap }), sources: [] });

  const region = document.querySelector('[data-role="usage-heatmap"]');
  assert.ok(region);
  assert.equal(region.getAttribute('tabindex'), '0');
  const activeDays = heatmap.filter((count) => count > 0).length;
  assert.equal(region.getAttribute('aria-label'), `过去 365 天活跃 ${activeDays} 天`);
  const grid = region.querySelector('[aria-hidden="true"]');
  assert.ok(grid, 'the cell grid is hidden from readers');
  assert.equal(grid.children.length, 365);
});

// --- error + retry ------------------------------------------------------------------------

test('panel: an error reply renders inline and retry issues a fresh cached request', async () => {
  const rig = await fixture();
  await openPanelWith(rig, { error: '扫描失败' });

  const error = document.querySelector('[data-role="usage-error"]');
  assert.ok(error, 'error state renders inside the panel');
  assert.ok(error.textContent.includes('扫描失败'));

  // Retry re-requests with a fresh requestId; the reply then lands normally.
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
        sources: []
      }),
    () => document.querySelector('[data-role="usage-overview"]')
  );
  assert.equal(document.querySelector('[data-role="usage-error"]'), null);
});

// --- refresh -------------------------------------------------------------------------------

test('panel: the refresh button forces a rescan', async () => {
  const rig = await fixture();
  await openPanelWith(rig, { report: report(), sources: [] });

  await settle(
    () => document.querySelector('[data-role="usage-refresh"]').click(),
    () => usageCommands(rig.posted).length === 2
  );
  assert.equal(usageCommands(rig.posted).at(-1).value, 'force');
});
