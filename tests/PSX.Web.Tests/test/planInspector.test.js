import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';
import { createAgentManager, installAgentRuntime } from './agentHarness.js';

describe('Plan and History inspector', () => {
  let manager;
  let postedMessages;

  beforeEach(() => {
    const runtime = installAgentRuntime();
    postedMessages = runtime.postedMessages;
    manager = createAgentManager(runtime.AgentThreadManager);
  });

  it('keeps Plan and History as peer tabs and refreshes history only when requested', () => {
    manager.selectInspectorTab('history', true);
    assert.equal(manager.planPanel.hidden, true);
    assert.equal(manager.historyPanel.hidden, false);
    assert.equal(postedMessages.at(-1).command, 'history');

    manager.selectInspectorTab('plan', false);
    assert.equal(manager.planPanel.hidden, false);
    assert.equal(manager.historyPanel.hidden, true);
  });

  it('renders normalized plan states and marks updates unread outside the Plan tab', () => {
    manager.selectInspectorTab('history', false);
    manager.handleEvent({
      type: 'plan_update',
      runId: 'run-1',
      entries: [
        { content: 'Inspect', status: 'completed' },
        { content: 'Implement', status: 'in_progress' },
        { content: 'Verify', status: 'pending' }
      ]
    });

    assert.deepEqual([...manager.planPanel.querySelectorAll('.agent-plan-content')].map((node) => node.textContent),
      ['Inspect', 'Implement', 'Verify']);
    assert.notEqual(manager.planPanel.querySelector('.agent-plan-item-completed'), null);
    assert.notEqual(manager.planPanel.querySelector('.agent-plan-item-in-progress'), null);
    assert.equal(manager.planUnread.hidden, false);
  });

  it('restores History scroll position after rendering a refreshed list', () => {
    manager.historyPanel.scrollTop = 144;
    manager.selectInspectorTab('history', true);
    manager._renderHistory([
      { threadId: 'one', title: 'One', cwd: 'D:/one' },
      { threadId: 'two', title: 'Two', cwd: 'D:/two' }
    ]);

    assert.equal(manager.historyPanel.scrollTop, 144);
    assert.equal(manager.historyPanel.querySelectorAll('.agent-history-item').length, 2);
  });
});
