import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const FIRST = '11111111-1111-4111-8111-111111111111';
const SECOND = '22222222-2222-4222-8222-222222222222';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// Hide/restore is a Tab switch: activating another workspace hides this panel,
// activating it again restores it. Workspace-local composer draft, inspector
// tab, and width live in the persistent panel DOM and must survive the cycle.
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

  it('keeps the active inspector tab across a hide/restore cycle', async () => {
    const { app, first } = await mountPair();
    role(first, 'history-tab').click();
    assert.equal(role(first, 'history-tab').getAttribute('aria-selected'), 'true');

    hideThenRestore(app);

    assert.equal(role(first, 'history-tab').getAttribute('aria-selected'), 'true');
    assert.equal(role(first, 'history-panel').hidden, false);
  });

  it('keeps the inspector width across a hide/restore cycle and persists it', async () => {
    const { app, first } = await mountPair();
    const resizer = role(first, 'inspector-resizer');
    resizer.focus?.();
    resizer.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft' }));

    assert.equal(first.style.getPropertyValue('--agent-inspector-width'), '276px');
    assert.equal(window.localStorage.getItem('psx.agent.inspectorWidth'), '276');

    hideThenRestore(app);
    assert.equal(first.style.getPropertyValue('--agent-inspector-width'), '276px');
  });

  it('restores the persisted inspector width when a workspace is recreated', async () => {
    const { app, panelFor } = await mountAgentApp();
    window.localStorage.setItem('psx.agent.inspectorWidth', '352');
    createAgentWorkspace(app, SECOND);
    assert.equal(panelFor(SECOND).style.getPropertyValue('--agent-inspector-width'), '352px');
  });
});

describe('Inspector resize lifecycle', () => {
  async function mountResizer() {
    const { app, panelFor } = await mountAgentApp();
    createAgentWorkspace(app, FIRST);
    const resizer = role(panelFor(FIRST), 'inspector-resizer');
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
    assert.equal(document.body.classList.contains('agent-inspector-resizing'), true);

    resizer.dispatchEvent(pointer('pointerup'));
    assert.equal(document.body.classList.contains('agent-inspector-resizing'), false);
  });

  it('clears the resizing body class when the gesture is cancelled', async () => {
    const resizer = await mountResizer();
    resizer.dispatchEvent(pointer('pointerdown'));
    assert.equal(document.body.classList.contains('agent-inspector-resizing'), true);

    resizer.dispatchEvent(pointer('pointercancel'));
    assert.equal(document.body.classList.contains('agent-inspector-resizing'), false);
  });
});
