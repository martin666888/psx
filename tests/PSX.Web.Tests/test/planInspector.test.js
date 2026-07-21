import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// Create a ready workspace and expose the inspector-owned nodes plus the
// captured Bridge messages, then drive the peer Plan/History tabs the way a user
// does (tab clicks) and read the live panel.
async function mount() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  return { app, panel, posted: runtime.postedMessages };
}

test('keeps Plan and History as peer tabs and refreshes history only when requested', async () => {
  const { panel, posted } = await mount();

  role(panel, 'history-tab').click();
  assert.equal(role(panel, 'plan-panel').hidden, true);
  assert.equal(role(panel, 'history-panel').hidden, false);
  assert.equal(posted.at(-1).command, 'history');

  role(panel, 'plan-tab').click();
  assert.equal(role(panel, 'plan-panel').hidden, false);
  assert.equal(role(panel, 'history-panel').hidden, true);
});

test('renders normalized plan states and marks updates unread outside the Plan tab', async () => {
  const { app, panel } = await mount();
  role(panel, 'history-tab').click();
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
  assert.deepEqual(
    [...planPanel.querySelectorAll('.agent-plan-content')].map((node) => node.textContent),
    ['Inspect', 'Implement', 'Verify']
  );
  assert.notEqual(planPanel.querySelector('.agent-plan-item-completed'), null);
  assert.notEqual(planPanel.querySelector('.agent-plan-item-in-progress'), null);
  assert.equal(role(panel, 'plan-unread').hidden, false);
});

test('restores History scroll position after rendering a refreshed list', async () => {
  const { app, panel } = await mount();
  const historyPanel = role(panel, 'history-panel');
  historyPanel.scrollTop = 144;
  role(panel, 'history-tab').click();
  app.handle({
    type: 'agent_threads',
    workspaceId: WS,
    threads: [
      { threadId: 'one', title: 'One', cwd: 'D:/one' },
      { threadId: 'two', title: 'Two', cwd: 'D:/two' }
    ]
  });

  assert.equal(historyPanel.scrollTop, 144);
  assert.equal(historyPanel.querySelectorAll('.agent-history-item').length, 2);
});
