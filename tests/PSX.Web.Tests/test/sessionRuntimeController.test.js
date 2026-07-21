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
    contextUsedTokens: 4200
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
    contextUsed: role('context-used').textContent,
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
});
