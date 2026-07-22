import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// Create a ready workspace and expose the inspector-owned nodes, then drive
// plan events the way the host sends them and read the live panel.
async function mount() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  return { app, panel, posted: runtime.postedMessages };
}

test('renders normalized plan states from the reduced state', async () => {
  const { app, panel } = await mount();
  app.handle({
    type: 'plan_update',
    workspaceId: WS,
    runId: 'run-1',
    entries: [
      { content: 'Inspect', status: 'completed' },
      { content: 'Implement', status: 'in_progress' },
      { content: 'Verify', status: 'pending' }
    ]
  });

  const planPanel = role(panel, 'plan-panel');
  assert.equal(planPanel.hidden, false);
  assert.deepEqual(
    [...planPanel.querySelectorAll('.agent-plan-content')].map((node) => node.textContent),
    ['Inspect', 'Implement', 'Verify']
  );
  assert.notEqual(planPanel.querySelector('.agent-plan-item-completed'), null);
  assert.notEqual(planPanel.querySelector('.agent-plan-item-in-progress'), null);
});

test('keeps the plan panel stable while other workspaces change', async () => {
  const OTHER = '22222222-2222-4222-8222-222222222222';
  const { app, panel } = await mount();
  app.handle({
    type: 'plan_update',
    workspaceId: WS,
    entries: [{ content: 'Keep me', status: 'pending' }]
  });
  createAgentWorkspace(app, OTHER);
  app.handle({
    type: 'plan_update',
    workspaceId: OTHER,
    entries: [{ content: 'Other plan', status: 'pending' }]
  });

  assert.deepEqual(
    [...role(panel, 'plan-panel').querySelectorAll('.agent-plan-content')].map((node) => node.textContent),
    ['Keep me']
  );
});
