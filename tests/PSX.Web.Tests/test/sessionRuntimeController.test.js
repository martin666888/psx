import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '22222222-2222-4222-8222-222222222222';

function stateEvent(workspaceId) {
  return {
    type: 'agent_state',
    workspaceId,
    status: 'restoring',
    cwd: '/tmp/project',
    sessionId: 'abcdef123456',
    threadId: 'thread-9',
    busy: true,
    isDraft: false,
    supportsImage: false,
    contextUsedTokens: 4200,
    contextWindowTokens: 100000,
    contextCostAmount: 0.23,
    contextCostCurrency: 'USD'
  };
}

function runtimeEvent(workspaceId) {
  return {
    type: 'runtime_status',
    workspaceId,
    state: 'installing',
    message: 'Downloading',
    canInstall: false,
    canCancel: true
  };
}

// Snapshot every session + runtime DOM node the controller owns.
function sessionRuntimeSnapshot(panel) {
  const role = (name) => panel.querySelector('[data-role="' + name + '"]');
  const status = role('status');
  const changeCwd = role('change-cwd');
  const card = role('runtime-card');
  const install = role('runtime-install');
  const cancel = role('runtime-cancel');
  return {
    status: { text: status.textContent, state: status.dataset.status },
    cwd: role('cwd').textContent,
    session: role('session').textContent,
    contextUsed: {
      state: role('context-used').dataset.contextState,
      ariaLabel: role('context-used').getAttribute('aria-label'),
      progress: role('context-ring-progress').getAttribute('stroke-dashoffset'),
      summary: role('context-tooltip-summary').textContent,
      detail: role('context-tooltip-detail').textContent,
      cost: role('context-tooltip-cost').textContent,
      costHidden: role('context-tooltip-cost').hidden
    },
    changeCwd: { disabled: changeCwd.disabled, title: changeCwd.title },
    runtimeCard: {
      hidden: card.hidden,
      state: card.dataset.state,
      ariaBusy: card.getAttribute('aria-busy')
    },
    runtimeTitle: role('runtime-title').textContent,
    runtimeMessage: role('runtime-message').textContent,
    runtimeInstall: { hidden: install.hidden, disabled: install.disabled, text: install.textContent },
    runtimeCancel: { hidden: cancel.hidden, disabled: cancel.disabled }
  };
}

// Drive the compiled Agent app the way production main.js does. The workspace is
// created without the default ready runtime so the installing runtime state
// under test is the one the controller renders.
async function drive(events) {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS, { ready: false });
  for (const event of events) {
    app.handle(event);
  }
  return panelFor(WS);
}

test('SessionRuntimeController renders the session line and the installing runtime card', async () => {
  const panel = await drive([runtimeEvent(WS), stateEvent(WS)]);
  const controlled = sessionRuntimeSnapshot(panel);

  assert.equal(controlled.cwd, '/tmp/project');
  assert.equal(controlled.runtimeCard.hidden, false);
  assert.equal(controlled.runtimeCard.state, 'installing');
  assert.equal(controlled.runtimeCard.ariaBusy, 'true');
  assert.equal(controlled.runtimeCancel.hidden, false);
  assert.equal(controlled.runtimeInstall.hidden, true);
  assert.equal(controlled.contextUsed.state, 'accent');
  assert.match(controlled.contextUsed.summary, /4\.2K \/ 100K/);
  assert.equal(controlled.contextUsed.detail, '95.8K remaining');
  assert.equal(controlled.contextUsed.cost, 'Cost · 0.23 USD');
  assert.equal(controlled.contextUsed.costHidden, false);
});

test('SessionRuntimeController renders a neutral Context dot when the Agent omits its limit', async () => {
  const event = stateEvent(WS);
  delete event.contextWindowTokens;
  delete event.contextCostAmount;
  delete event.contextCostCurrency;
  const panel = await drive([event]);
  const context = sessionRuntimeSnapshot(panel).contextUsed;

  assert.equal(context.state, 'unknown');
  assert.match(context.summary, /4\.2K used/);
  assert.match(context.detail, /did not report a context limit/);
  assert.equal(context.costHidden, true);
});

test('SessionRuntimeController changes Context ring color at warning and error thresholds', async () => {
  const panel = await drive([
    { type: 'agent_usage_update', workspaceId: WS, contextUsedTokens: 85, contextWindowTokens: 100 },
    { type: 'agent_usage_update', workspaceId: WS, contextUsedTokens: 95, contextWindowTokens: 100 }
  ]);

  const context = sessionRuntimeSnapshot(panel).contextUsed;
  assert.equal(context.state, 'error');
  assert.match(context.summary, /^95%/);
  assert.equal(context.progress, '5');
});

test('SessionRuntimeController renders recovery_pending status with a session id fallback', async () => {
  const event = stateEvent(WS);
  event.status = 'recovery_pending';
  // The backend keeps the recovery session id in the state field so the row
  // does not blank out to "no session" while a reconnect is pending.
  event.sessionId = 'fake-session-new';
  const panel = await drive([event]);
  const snapshot = sessionRuntimeSnapshot(panel);

  assert.equal(snapshot.status.state, 'recovery_pending');
  assert.equal(snapshot.status.text, 'Recovery Pending');
  assert.notEqual(snapshot.session, 'no session');
  assert.match(snapshot.session, /^session /);
});

test('SessionRuntimeController renders the transient stopping status while a run is being cancelled', async () => {
  const event = stateEvent(WS);
  event.status = 'stopping';
  const panel = await drive([event]);
  const snapshot = sessionRuntimeSnapshot(panel);

  // stopping is a new transient busy status; the generic formatStatus split
  // renders it with zero production changes.
  assert.equal(snapshot.status.state, 'stopping');
  assert.equal(snapshot.status.text, 'Stopping');
});
