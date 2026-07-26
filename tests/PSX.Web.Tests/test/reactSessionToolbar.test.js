// reactSessionToolbar.test.js — the React session toolbar islands (Group B).
//
// In react mode two independent roots own the children of .agent-meta (the
// status / cwd / change-cwd / session line) and .agent-hints (the Context
// usage ring); SessionRuntimeController keeps the toolbar frame and the
// legacy writes as the permanent fallback. DOM equivalence is asserted
// against the legacy renderer for the five session statuses, the context
// ring thresholds and the no-limit dot, plus the pick_cwd command parity.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, appModule } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;

const WS = '88888888-8888-4888-8888-888888888888';

// Warm the module cache so the islands' dynamic import resolves quickly and
// deterministically inside act scopes.
await appModule('workspace/sessionIsland.js');

function stateEvent(overrides) {
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

// Every session + context DOM fact the controller (or the islands) write.
function toolbarSnapshot(panel) {
  const role = (name) => panel.querySelector('[data-role="' + name + '"]');
  const status = role('status');
  const changeCwd = role('change-cwd');
  return {
    status: { text: status.textContent, state: status.dataset.status },
    cwd: role('cwd').textContent,
    session: role('session').textContent,
    changeCwd: { disabled: changeCwd.disabled, title: changeCwd.title },
    context: {
      state: role('context-used').dataset.contextState,
      ariaLabel: role('context-used').getAttribute('aria-label'),
      progress: role('context-ring-progress').getAttribute('stroke-dashoffset'),
      summary: role('context-tooltip-summary').textContent,
      detail: role('context-tooltip-detail').textContent,
      cost: role('context-tooltip-cost').textContent,
      costHidden: role('context-tooltip-cost').hidden
    }
  };
}

// The equivalence scenarios: five session statuses, the three ring bands,
// the omitted-limit dot and the draft cwd affordance.
const SCENARIOS = [
  ['ready', stateEvent({})],
  ['busy', stateEvent({ status: 'busy', busy: true })],
  ['restoring', stateEvent({ status: 'restoring', busy: true, isRestoring: true })],
  ['recovery_pending', stateEvent({ status: 'recovery_pending', sessionId: 'fake-session-new' })],
  ['stopping', stateEvent({ status: 'stopping', busy: true })],
  ['draft', stateEvent({ isDraft: true, sessionId: '', cwd: '' })],
  ['warning band', { type: 'agent_usage_update', workspaceId: WS, contextUsedTokens: 85, contextWindowTokens: 100 }],
  ['error band', { type: 'agent_usage_update', workspaceId: WS, contextUsedTokens: 95, contextWindowTokens: 100 }],
  ['no limit', stateEvent({ contextWindowTokens: null, contextCostAmount: null, contextCostCurrency: '' })]
];

async function actAndSettle(run, predicate) {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 100 && !predicate(); i++) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 5));
    });
  }
  assert.ok(predicate(), 'react session toolbar did not settle in time');
}

test('React session toolbar renders DOM equivalent to legacy across statuses and ring bands', async () => {
  const legacySnapshots = [];
  {
    const { app, panelFor } = await mountAgentApp();
    createAgentWorkspace(app, WS);
    for (const [, event] of SCENARIOS) {
      app.handle(event);
      legacySnapshots.push(toolbarSnapshot(panelFor(WS)));
    }
  }

  const reactSnapshots = [];
  {
    const { app, panelFor } = await mountAgentApp({ uiMode: 'react' });
    createAgentWorkspace(app, WS);
    const panel = panelFor(WS);
    // First event mounts both islands; settle on the React-owned status text.
    await actAndSettle(
      () => app.handle(SCENARIOS[0][1]),
      () => panel.querySelector('[data-role="status"]').textContent === 'Ready'
    );
    reactSnapshots.push(toolbarSnapshot(panel));
    for (const [, event] of SCENARIOS.slice(1)) {
      await act(async () => {
        app.handle(event);
      });
      reactSnapshots.push(toolbarSnapshot(panel));
    }
  }

  assert.equal(reactSnapshots.length, SCENARIOS.length);
  for (let i = 0; i < SCENARIOS.length; i++) {
    assert.deepEqual(reactSnapshots[i], legacySnapshots[i], SCENARIOS[i][0] + ' snapshot diverged');
  }
});

test('react mode: the Change button posts the same pick_cwd command as legacy', async () => {
  let legacyCommand;
  {
    const { app, panelFor, runtime } = await mountAgentApp();
    createAgentWorkspace(app, WS);
    app.handle(stateEvent({ isDraft: true }));
    panelFor(WS).querySelector('[data-role="change-cwd"]').click();
    legacyCommand = runtime.postedMessages.at(-1);
    assert.equal(legacyCommand.command, 'pick_cwd');
  }

  const { app, panelFor, runtime } = await mountAgentApp({ uiMode: 'react' });
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  await actAndSettle(
    () => app.handle(stateEvent({ isDraft: true })),
    () => panel.querySelector('[data-role="change-cwd"]').disabled === false
  );
  await act(async () => {
    panel.querySelector('[data-role="change-cwd"]').click();
  });
  assert.deepEqual(runtime.postedMessages.at(-1), legacyCommand, 'react posts the byte-identical pick_cwd command');
});

test('react mode: repeated updates keep the ring node identity (no remount churn)', async () => {
  const { app, panelFor } = await mountAgentApp({ uiMode: 'react' });
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  await actAndSettle(
    () => app.handle(stateEvent({})),
    () => panel.querySelector('[data-role="context-used"]')?.dataset.contextState === 'accent'
  );
  const ring = panel.querySelector('[data-role="context-used"]');
  const statusNode = panel.querySelector('[data-role="status"]');
  await act(async () => {
    app.handle(stateEvent({ status: 'busy', busy: true }));
    app.handle({ type: 'agent_usage_update', workspaceId: WS, contextUsedTokens: 95, contextWindowTokens: 100 });
  });
  assert.equal(panel.querySelector('[data-role="context-used"]'), ring, 'ring element identity preserved');
  assert.equal(panel.querySelector('[data-role="status"]'), statusNode, 'status element identity preserved');
  assert.equal(ring.dataset.contextState, 'error');
  assert.equal(statusNode.textContent, 'Busy');
});
