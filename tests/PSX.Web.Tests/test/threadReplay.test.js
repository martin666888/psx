import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const workspaceId = '11111111-1111-4111-8111-111111111111';

// Projects the observable results of a rendered thread + inspector into a
// structure that is stable across the live-stream and history-replay code
// paths, reading only the observable DOM inside the workspace panel.
function observableProjection(panel) {
  const thread = panel.querySelector('[data-role="thread"]');
  const planPanel = panel.querySelector('[data-role="plan-panel"]');
  const messages = [...thread.querySelectorAll('.agent-message')].map((row) => {
    const role = row.classList.contains('agent-message-user') ? 'user' : 'assistant';
    const content = row.querySelector('.agent-message-content') || row.querySelector('.agent-message-body');
    return { role, text: (content?.textContent || '').trim() };
  });
  const tools = [...thread.querySelectorAll('.agent-tool-card')].map((card) => ({
    summary: card.querySelector('.agent-tool-card-summary')?.textContent || '',
    state: card.dataset.state || '',
    output: card.querySelector('.agent-tool-card-content')?.textContent || ''
  }));
  const plan = [...planPanel.querySelectorAll('.agent-plan-item')].map((item) => ({
    content: item.querySelector('.agent-plan-content')?.textContent || '',
    status: item.className.replace('agent-plan-item ', '').trim()
  }));
  return { messages, tools, plan };
}

async function mount() {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, workspaceId);
  return { app, panel: panelFor(workspaceId) };
}

describe('Thread replay', () => {
  it('clears and rebuilds the thread from a historical payload', async () => {
    const { app, panel } = await mount();
    app.handle({ type: 'assistant_delta', workspaceId, text: 'stale live output' });
    app.handle({
      type: 'agent_thread_loaded',
      workspaceId,
      clear: true,
      cwd: 'D:/replay',
      sessionId: 'sess-1234abcd',
      messages: [
        { role: 'user', text: 'Please refactor' },
        { role: 'assistant', text: 'Refactored.' }
      ]
    });

    const messages = observableProjection(panel).messages;
    assert.deepEqual(messages, [
      { role: 'user', text: 'Please refactor' },
      { role: 'assistant', text: 'Refactored.' }
    ]);
    assert.equal(panel.querySelector('[data-role="cwd"]').textContent, 'D:/replay');
  });

  it('shows a ready hint when replaying an empty thread', async () => {
    const { app, panel } = await mount();
    app.handle({ type: 'agent_thread_loaded', workspaceId, clear: true, messages: [] });
    const thread = panel.querySelector('[data-role="thread"]');
    assert.match(thread.querySelector('.agent-system').textContent, /will start on the first message/);
  });
});

describe('Live and history observable-DOM invariant', () => {
  function buildLive(app) {
    app.handle({ type: 'user_message', workspaceId, text: 'Add a feature' });
    app.handle({ type: 'tool_started', workspaceId, runId: 'run-1', toolCallId: 'tool-1', name: 'edit', input: '', summary: 'Edit file' });
    app.handle({ type: 'tool_delta', workspaceId, toolCallId: 'tool-1', text: 'patched 3 lines' });
    app.handle({ type: 'tool_finished', workspaceId, toolCallId: 'tool-1', status: 'done' });
    app.handle({ type: 'plan_update', workspaceId, runId: 'run-1', entries: [
      { content: 'Inspect', status: 'completed' },
      { content: 'Implement', status: 'in_progress' },
      { content: 'Verify', status: 'pending' }
    ] });
    app.handle({ type: 'assistant_delta', workspaceId, text: 'All done.' });
    app.handle({ type: 'assistant_message_done', workspaceId });
    app.handle({ type: 'run_finished', workspaceId });
  }

  function buildHistory(app) {
    app.handle({
      type: 'agent_thread_loaded',
      workspaceId,
      clear: true,
      cwd: 'D:/replay',
      messages: [
        { role: 'user', text: 'Add a feature' },
        { role: 'tool', runId: 'run-1', toolCallId: 'tool-1', name: 'edit', summary: 'Edit file', toolOutput: 'patched 3 lines', toolStatus: 'done' },
        { role: 'plan', runId: 'run-1', planEntries: [
          { content: 'Inspect', status: 'completed' },
          { content: 'Implement', status: 'in_progress' },
          { content: 'Verify', status: 'pending' }
        ] },
        { role: 'assistant', text: 'All done.' }
      ]
    });
  }

  it('produces the same observable messages, tools, and plan for live vs replayed history', async () => {
    const live = await mount();
    buildLive(live.app);
    const liveProjection = observableProjection(live.panel);

    const history = await mount();
    buildHistory(history.app);
    const historyProjection = observableProjection(history.panel);

    assert.deepEqual(historyProjection, liveProjection);
    assert.deepEqual(liveProjection.messages, [
      { role: 'user', text: 'Add a feature' },
      { role: 'assistant', text: 'All done.' }
    ]);
    assert.deepEqual(liveProjection.tools, [
      { summary: 'Edit file', state: 'done', output: 'patched 3 lines' }
    ]);
    assert.deepEqual(liveProjection.plan.map((entry) => entry.content), ['Inspect', 'Implement', 'Verify']);
  });
});
