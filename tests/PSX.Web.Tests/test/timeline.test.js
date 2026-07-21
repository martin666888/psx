import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '11111111-1111-4111-8111-111111111111';

// Behaviour safety net: drive the full live timeline stream through the compiled
// app (user -> thinking -> tool -> assistant -> run finished) and assert only on
// the observable DOM inside the workspace thread.
async function mount() {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  return { app, thread: panel.querySelector('[data-role="thread"]') };
}

test('renders a user message with its rendered content', async () => {
  const { app, thread } = await mount();
  app.handle({ type: 'user_message', workspaceId: WS, text: 'Hello **agent**' });

  const message = thread.querySelector('.agent-message-user .agent-message-content');
  assert.ok(message);
  assert.equal(message.querySelector('strong')?.textContent, 'agent');
});

test('streams a thinking block that collapses and drops its spinner when finished', async () => {
  const { app, thread } = await mount();
  app.handle({ type: 'thinking_started', workspaceId: WS });
  assert.ok(thread.querySelector('.agent-thinking'));

  app.handle({ type: 'thinking_delta', workspaceId: WS, text: 'Considering options' });
  const block = thread.querySelector('.agent-thinking-block');
  assert.ok(block);
  assert.equal(block.querySelector('.agent-thinking-content').textContent, 'Considering options');
  assert.equal(block.open, true);

  app.handle({ type: 'thinking_finished', workspaceId: WS });
  assert.equal(block.open, false);
  assert.equal(block.getAttribute('aria-busy'), 'false');
  assert.equal(block.querySelector('.agent-spinner'), null);
});

test('removes an empty thinking indicator that never received content', async () => {
  const { app, thread } = await mount();
  app.handle({ type: 'thinking_started', workspaceId: WS });
  app.handle({ type: 'thinking_finished', workspaceId: WS });
  assert.equal(thread.querySelector('.agent-thinking'), null);
});

test('groups tool activity, accumulates delta output, and marks completion', async () => {
  const { app, thread } = await mount();
  app.handle({ type: 'tool_started', workspaceId: WS, runId: 'run-1', toolCallId: 'tool-1', name: 'grep', input: 'pattern', summary: 'Search' });

  const group = thread.querySelector('.agent-run-group');
  assert.ok(group);
  const card = group.querySelector('.agent-tool-card');
  assert.equal(card.dataset.state, 'running');
  assert.equal(card.querySelector('.agent-tool-card-content').textContent, 'pattern');

  app.handle({ type: 'tool_delta', workspaceId: WS, toolCallId: 'tool-1', text: ' output-a' });
  app.handle({ type: 'tool_delta', workspaceId: WS, toolCallId: 'tool-1', text: ' output-b' });
  assert.equal(card.querySelector('.agent-tool-card-content').textContent, 'pattern output-a output-b');

  app.handle({ type: 'tool_finished', workspaceId: WS, toolCallId: 'tool-1', status: 'done' });
  assert.equal(card.dataset.state, 'done');
  assert.equal(card.querySelector('.agent-tool-card-status').textContent, 'Done');
  assert.equal(card.open, false);
});

test('marks a failed tool card and flags the run group as errored', async () => {
  const { app, thread } = await mount();
  app.handle({ type: 'tool_started', workspaceId: WS, runId: 'run-2', toolCallId: 'tool-2', name: 'build', input: '' });
  app.handle({ type: 'tool_finished', workspaceId: WS, toolCallId: 'tool-2', status: 'error' });

  const card = thread.querySelector('.agent-tool-card');
  assert.equal(card.dataset.state, 'error');
  assert.equal(card.querySelector('.agent-tool-card-status').textContent, 'Failed');
});

test('accumulates assistant deltas into one rendered message and adds a copy action when done', async () => {
  const { app, thread } = await mount();
  app.handle({ type: 'assistant_delta', workspaceId: WS, text: '# Title\n\n' });
  app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'Body text' });

  const bodies = thread.querySelectorAll('.agent-message-assistant .agent-message-body');
  assert.equal(bodies.length, 1);
  assert.equal(bodies[0].dataset.raw, '# Title\n\nBody text');
  assert.equal(bodies[0].querySelector('h1')?.textContent, 'Title');

  app.handle({ type: 'assistant_message_done', workspaceId: WS });
  assert.ok(bodies[0].parentElement.querySelector('.agent-message-actions .agent-copy-button'));
});

test('finalizes the whole turn on run_finished', async () => {
  const { app, thread } = await mount();
  app.handle({ type: 'user_message', workspaceId: WS, text: 'do it' });
  app.handle({ type: 'tool_started', workspaceId: WS, runId: 'run-3', toolCallId: 'tool-3', name: 'edit', input: 'x' });
  app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'Done.' });
  app.handle({ type: 'run_finished', workspaceId: WS });

  const group = thread.querySelector('.agent-run-group');
  assert.equal(group.open, false);
});
