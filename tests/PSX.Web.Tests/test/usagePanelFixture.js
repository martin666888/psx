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

const WS = '88888888-8888-4888-8888-888888888888';

export const dailyTokens = (entries = {}) =>
  Array.from({ length: 365 }, (_, index) => entries[index] ?? 0);

export const completeness = (overrides = {}) => ({
  status: 'available',
  reasons: [],
  expectedSessions: 1,
  matchedSessions: 1,
  skippedFiles: 0,
  badLines: 0,
  untrackedThreads: 0,
  ...overrides
});

export const providerReport = (overrides = {}) => ({
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

export const report = (overrides = {}) => ({
  heatmapStartDate: '2026-01-01',
  dailyTokens: dailyTokens({ 0: 1, 1: 2, 364: 3 }),
  today: { totalTokens: 200 },
  last7Days: { totalTokens: 1500 },
  last30Days: { totalTokens: 3290258 },
  providers: [providerReport()],
  ...overrides
});

export async function fixture({ withWorkspace = true } = {}) {
  const runtime = installAgentRuntime();
  await appModule('history/historyIsland.js');
  await appModule('usage/usageIsland.js');
  installBreakpoint(true);
  document.body.innerHTML = `<div id="agents"></div>${agentTemplateMarkup()}<button type="button" data-role="app-settings-toggle">设置</button>`;
  window.localStorage.setItem('psx.agent.historyDockOpen', '1');
  const { createAgentApp } = await appModule('entry.js');
  const app = createAgentApp({
    terminalManager: { setViewVisible() {} },
    container: document.getElementById('agents'),
    template: document.getElementById('agent-workspace-template')
  });
  registerAgentCleanup(() => app.dispose());
  if (withWorkspace) createAgentWorkspace(app, WS);
  const settings = () => document.querySelector('[data-role="app-settings-toggle"]');
  settings().addEventListener('click', () => app.toggleSettings());
  return { app, posted: runtime.postedMessages, settings };
}

export async function settle(run, predicate, what = 'usage island') {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let index = 0; index < 50 && !predicate(); index++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(predicate(), what + ' did not settle');
}

export function usageCommands(posted) {
  return posted.filter(
    (message) => message.type === 'agent_global_command' && message.command === 'usage_report'
  );
}

export function configCommands(posted) {
  return posted.filter(
    (message) => message.type === 'agent_global_command' && message.command === 'config_report'
  );
}

export function settingsCommands(posted) {
  return posted.filter((message) => message.type === 'app_settings_command');
}

export async function openPanelWith(rig, payload) {
  await settle(
    () => rig.app.openSettings('usage'),
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
