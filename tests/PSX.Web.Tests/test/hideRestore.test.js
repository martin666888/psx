import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const FIRST = '11111111-1111-4111-8111-111111111111';
const SECOND = '22222222-2222-4222-8222-222222222222';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// Hide/restore is a Tab switch: activating another workspace hides this panel,
// activating it again restores it. Workspace-local composer draft and the shared
// plan width live in the persistent panel DOM and must survive the cycle.
async function mountPair() {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, FIRST);
  createAgentWorkspace(app, SECOND);
  return { app, first: panelFor(FIRST), second: panelFor(SECOND) };
}

function hideThenRestore(app) {
  app.handle({ type: 'workspace_activated', workspaceId: SECOND, kind: 'agent' });
  app.handle({ type: 'workspace_activated', workspaceId: FIRST, kind: 'agent' });
}

describe('Hide and restore preservation', () => {
  it('keeps the composer draft across a hide/restore cycle', async () => {
    const { app, first } = await mountPair();
    role(first, 'input').value = 'draft in progress';

    app.handle({ type: 'workspace_activated', workspaceId: SECOND, kind: 'agent' });
    assert.equal(first.hidden, true);
    app.handle({ type: 'workspace_activated', workspaceId: FIRST, kind: 'agent' });

    assert.equal(first.hidden, false);
    assert.equal(role(first, 'input').value, 'draft in progress');
  });

  it('keeps the plan width across a hide/restore cycle and persists it', async () => {
    const { app, first } = await mountPair();
    const resizer = role(first, 'plan-resizer');
    resizer.focus?.();
    resizer.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft' }));

    const container = document.getElementById('agents');
    assert.equal(container.style.getPropertyValue('--agent-plan-width'), '336px');
    assert.equal(window.localStorage.getItem('psx.agent.planWidth'), '336');

    hideThenRestore(app);
    assert.equal(container.style.getPropertyValue('--agent-plan-width'), '336px');
  });

  it('restores the persisted plan width when a workspace is recreated', async () => {
    const { app, panelFor } = await mountAgentApp();
    window.localStorage.setItem('psx.agent.planWidth', '352');
    createAgentWorkspace(app, SECOND);
    assert.equal(document.getElementById('agents').style.getPropertyValue('--agent-plan-width'), '352px');
  });
});

describe('Plan resize lifecycle', () => {
  async function mountResizer() {
    const { app, panelFor } = await mountAgentApp();
    createAgentWorkspace(app, FIRST);
    const resizer = role(panelFor(FIRST), 'plan-resizer');
    resizer.setPointerCapture = () => {};
    resizer.releasePointerCapture = () => {};
    return resizer;
  }

  function pointer(type) {
    const event = new Event(type);
    event.button = 0;
    event.pointerId = 1;
    return event;
  }

  it('toggles the resizing body class between pointerdown and pointerup', async () => {
    const resizer = await mountResizer();
    resizer.dispatchEvent(pointer('pointerdown'));
    assert.equal(document.body.classList.contains('agent-plan-resizing'), true);

    resizer.dispatchEvent(pointer('pointerup'));
    assert.equal(document.body.classList.contains('agent-plan-resizing'), false);
  });

  it('clears the resizing body class when the gesture is cancelled', async () => {
    const resizer = await mountResizer();
    resizer.dispatchEvent(pointer('pointerdown'));
    assert.equal(document.body.classList.contains('agent-plan-resizing'), true);

    resizer.dispatchEvent(pointer('pointercancel'));
    assert.equal(document.body.classList.contains('agent-plan-resizing'), false);
  });
});
