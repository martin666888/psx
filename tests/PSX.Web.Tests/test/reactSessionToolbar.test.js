// reactSessionToolbar.test.js — React-only session metadata and context usage.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, appModule } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;
const WS = '88888888-8888-4888-8888-888888888888';
await appModule('workspace/sessionIsland.js');

function stateEvent(overrides = {}) {
  return {
    type: 'agent_state',
    workspaceId: WS,
    status: 'ready',
    cwd: '/tmp/project',
    sessionId: 'abcdef123456',
    threadId: 'thread-9',
    busy: false,
    isDraft: false,
    supportsImage: false,
    contextUsedTokens: 4200,
    contextWindowTokens: 100000,
    contextCostAmount: 0.23,
    contextCostCurrency: 'USD',
    ...overrides
  };
}

async function fixture() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS), posted: runtime.postedMessages };
}

async function settle(app, panel, event) {
  await act(async () => {
    app.handle(event);
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  // The context-usage island mounts after the ComposerView island commits
  // (MutationObserver replay), so wait for both island outputs.
  const ready = () =>
    panel.querySelector('[data-role="status"]') && panel.querySelector('[data-role="context-used"]');
  for (let i = 0; i < 50 && !ready(); i++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(ready());
}

test('session status, cwd, session id and context bands update semantically', async () => {
  const { app, panel } = await fixture();
  await settle(app, panel, stateEvent());
  assert.equal(panel.querySelector('[data-role="status"]').dataset.status, 'ready');
  assert.equal(panel.querySelector('[data-role="cwd"]').textContent, '/tmp/project');
  assert.match(panel.querySelector('[data-role="session"]').textContent, /abcdef12/);
  assert.equal(panel.querySelector('[data-role="context-used"]').dataset.contextState, 'accent');
  assert.match(panel.querySelector('[data-role="context-used"]').getAttribute('aria-label'), /4\.2K/);

  await settle(app, panel, {
    type: 'agent_usage_update',
    workspaceId: WS,
    contextUsedTokens: 85,
    contextWindowTokens: 100
  });
  assert.equal(panel.querySelector('[data-role="context-used"]').dataset.contextState, 'warning');
  await settle(app, panel, {
    type: 'agent_usage_update',
    workspaceId: WS,
    contextUsedTokens: 95,
    contextWindowTokens: 100
  });
  assert.equal(panel.querySelector('[data-role="context-used"]').dataset.contextState, 'error');
});

test('draft Change button emits pick_cwd and locks after session start', async () => {
  const { app, panel, posted } = await fixture();
  await settle(app, panel, stateEvent({ isDraft: true, sessionId: '', cwd: '' }));
  const change = panel.querySelector('[data-role="change-cwd"]');
  assert.equal(change.disabled, false);
  await act(async () => change.click());
  assert.deepEqual(posted.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'pick_cwd',
    value: '',
    requestId: ''
  });

  await settle(app, panel, stateEvent({ isDraft: false }));
  assert.equal(change.disabled, true);
  assert.match(change.title, /new Agent tab/);
});

test('repeated updates preserve the context ring DOM node', async () => {
  const { app, panel } = await fixture();
  await settle(app, panel, stateEvent());
  const ring = panel.querySelector('[data-role="context-ring-progress"]');
  await settle(app, panel, {
    type: 'agent_usage_update',
    workspaceId: WS,
    contextUsedTokens: 50,
    contextWindowTokens: 100
  });
  assert.equal(panel.querySelector('[data-role="context-ring-progress"]'), ring);
});
