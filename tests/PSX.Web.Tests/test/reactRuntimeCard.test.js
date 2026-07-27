// reactRuntimeCard.test.js — React-only runtime-card behavior.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, appModule } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;
const WS = '33333333-3333-4333-8333-333333333333';
await appModule('workspace/runtimeIsland.js');

function runtimeEvent(state, overrides = {}) {
  return {
    type: 'runtime_status',
    workspaceId: WS,
    state,
    message: state,
    canInstall: state === 'missing' || state === 'failed' || state === 'cancelled',
    canCancel: state === 'installing',
    ...overrides
  };
}

async function fixture() {
  const { app, panelFor, runtime } = await mountAgentApp();
  await act(async () => {
    createAgentWorkspace(app, WS, { ready: false });
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  return { app, panel: panelFor(WS), posted: runtime.postedMessages };
}

async function settle(app, panel, event) {
  await act(async () => {
    app.handle(event);
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 50 && !panel.querySelector('[data-role="runtime-card"]'); i++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(panel.querySelector('[data-role="runtime-card"]'));
}

test('all runtime states render in one stable host', async () => {
  const { app, panel } = await fixture();
  const host = panel.querySelector('[data-role="runtime-host"]');
  for (const state of ['missing', 'installing', 'failed', 'cancelled', 'ready']) {
    await settle(app, panel, runtimeEvent(state));
    const card = panel.querySelector('[data-role="runtime-card"]');
    assert.equal(card.dataset.state, state);
    assert.equal(card.getAttribute('aria-busy'), state === 'installing' ? 'true' : 'false');
    assert.equal(card.hidden, state === 'ready');
    assert.equal(panel.querySelector('[data-role="runtime-host"]'), host);
    assert.equal(host.dataset.islandState, 'mounted');
  }
  assert.equal(panel.querySelectorAll('[data-role="runtime-card"]').length, 1);
});

test('install and cancel buttons emit exact bridge commands and respect state guards', async () => {
  const { app, panel, posted } = await fixture();
  await settle(app, panel, runtimeEvent('missing'));
  await act(async () => panel.querySelector('[data-role="runtime-install"]').click());
  assert.deepEqual(posted.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'install_runtime',
    value: '',
    requestId: ''
  });

  await settle(app, panel, runtimeEvent('installing'));
  await act(async () => panel.querySelector('[data-role="runtime-cancel"]').click());
  assert.deepEqual(posted.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'cancel_runtime_install',
    value: '',
    requestId: ''
  });
  const before = posted.length;
  panel.querySelector('[data-role="runtime-install"]').click();
  assert.equal(posted.length, before);
});

test('closing while the island import is in flight leaves no late content', async () => {
  const { app } = await fixture();
  await act(async () => {
    app.handle(runtimeEvent('installing'));
    app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
    await new Promise((resolve) => setTimeout(resolve, 30));
  });
  assert.equal(document.querySelector(`[data-workspace-id="${WS}"]`), null);
  assert.equal(document.querySelector('[data-role="runtime-card"]'), null);
});

test('disposing unmounts the root but never removes a live template host itself', async () => {
  const { app, panel } = await fixture();
  await settle(app, panel, runtimeEvent('missing'));
  const host = panel.querySelector('[data-role="runtime-host"]');
  assert.equal(host.dataset.islandState, 'mounted');
  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
  assert.equal(host.childElementCount, 0);
  assert.equal(host.dataset.islandState, undefined);
});
