import assert from 'node:assert/strict';
import { describe, it } from 'vitest';
import { mountAgentApp, createAgentWorkspace, composerReady } from './agentHarness.js';

const FIRST = '11111111-1111-4111-8111-111111111111';
const SECOND = '22222222-2222-4222-8222-222222222222';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// Hide/restore is a Tab switch: activating another workspace hides this panel,
// activating it again restores it. Workspace-local composer draft and Plan
// card visibility live in the persistent panel DOM and must survive the cycle.
async function mountPair() {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, FIRST);
  createAgentWorkspace(app, SECOND);
  return { app, first: panelFor(FIRST), second: panelFor(SECOND) };
}

// The workspace toolbar island renders the toggles asynchronously after
// workspace creation; poll until its first commit lands.
async function toolbarReady(panel) {
  for (let attempt = 0; attempt < 200 && !role(panel, 'plan-toggle'); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.ok(role(panel, 'plan-toggle'), 'workspace toolbar island did not mount');
}

describe('Hide and restore preservation', () => {
  it('keeps the composer draft across a hide/restore cycle', async () => {
    const { app, first } = await mountPair();
    await composerReady(first);
    role(first, 'input').value = 'draft in progress';

    app.handle({ type: 'workspace_activated', workspaceId: SECOND, kind: 'agent' });
    assert.equal(first.hidden, true);
    app.handle({ type: 'workspace_activated', workspaceId: FIRST, kind: 'agent' });

    assert.equal(first.hidden, false);
    assert.equal(role(first, 'input').value, 'draft in progress');
  });

  it('keeps the Plan card visibility across a hide/restore cycle', async () => {
    const { app, first } = await mountPair();
    await toolbarReady(first);
    // Compact harness default: the card starts hidden. Show it, then hide the
    // workspace and restore it — the visibility is workspace-local state.
    role(first, 'plan-toggle').click();
    assert.equal(role(first, 'plan-card').hidden, false);

    app.handle({ type: 'workspace_activated', workspaceId: SECOND, kind: 'agent' });
    app.handle({ type: 'workspace_activated', workspaceId: FIRST, kind: 'agent' });

    assert.equal(first.hidden, false);
    assert.equal(role(first, 'plan-card').hidden, false, 'visibility survives the tab switch');
    // aria-expanded commits with the toolbar island's next render.
    for (let attempt = 0; attempt < 200 && role(first, 'plan-toggle').getAttribute('aria-expanded') !== 'true'; attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.equal(role(first, 'plan-toggle').getAttribute('aria-expanded'), 'true');
  });
});
