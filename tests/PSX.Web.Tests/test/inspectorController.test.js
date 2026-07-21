import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '22222222-2222-4222-8222-222222222222';

function planEvent(workspaceId) {
  return {
    type: 'plan_update',
    workspaceId,
    runId: 'run-1',
    entries: [
      { content: 'Build feature', status: 'in_progress', priority: 'high' },
      { content: 'Write tests', status: 'pending' },
      { content: 'Ship it', status: 'completed' }
    ]
  };
}

function threadsEvent(workspaceId) {
  return {
    type: 'agent_threads',
    workspaceId,
    threads: [
      { threadId: 't1', title: 'First chat', cwd: '/proj/a', updatedAt: 'today', sessionId: 'sess-abcdefgh' },
      { threadId: 't2', title: 'Second chat', cwd: '/proj/b', updatedAt: 'yesterday', sessionId: '' }
    ]
  };
}

function errorEvent(workspaceId) {
  return { type: 'agent_history_error', workspaceId, text: 'History backend unavailable.' };
}

// Snapshot every inspector DOM node the controller owns.
function inspectorSnapshot(panel) {
  const role = (name) => panel.querySelector('[data-role="' + name + '"]');
  const planTab = role('plan-tab');
  const historyTab = role('history-tab');
  const planPanel = role('plan-panel');
  const historyPanel = role('history-panel');
  const planUnread = role('plan-unread');
  return {
    planTab: { selected: planTab.getAttribute('aria-selected'), tabIndex: planTab.tabIndex, hidden: planPanel.hidden },
    historyTab: { selected: historyTab.getAttribute('aria-selected'), tabIndex: historyTab.tabIndex, hidden: historyPanel.hidden },
    planPanelHtml: planPanel.innerHTML,
    historyPanelHtml: historyPanel.innerHTML,
    planUnread: planUnread.hidden,
    width: panel.style.getPropertyValue('--agent-inspector-width')
  };
}

// Drive the compiled Agent app the way production main.js does, then read the
// controller-owned inspector nodes off the live panel.
async function drive(events) {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  for (const event of events) {
    app.handle(event);
  }
  return panelFor(WS);
}

test('InspectorController renders the plan panel and the history list', async () => {
  const panel = await drive([planEvent(WS), threadsEvent(WS)]);
  const controlled = inspectorSnapshot(panel);

  assert.equal(panel.querySelectorAll('[data-role="plan-panel"] .agent-plan-item').length, 3);
  assert.equal(panel.querySelectorAll('[data-role="history-panel"] .agent-history-item').length, 2);
  assert.match(controlled.planPanelHtml, /Build feature/);
});

test('InspectorController renders the history error state', async () => {
  const panel = await drive([errorEvent(WS)]);
  const controlled = inspectorSnapshot(panel);

  assert.match(controlled.historyPanelHtml, /agent-history-error/);
  assert.match(controlled.historyPanelHtml, /History backend unavailable/);
});
