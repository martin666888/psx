import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace, modeTransitionEvent } from './agentHarness.js';

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// Create a ready workspace and expose the decision-owned nodes: the timeline
// thread (permission/question/elicitation/mode-transition cards), the composer
// mode-transition prompt, the input row, and the captured Bridge messages.
async function mount() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  return {
    app,
    panel,
    posted: runtime.postedMessages,
    thread: role(panel, 'thread'),
    prompt: role(panel, 'mode-transition-prompt'),
    inputRow: role(panel, 'input-row')
  };
}

test('renders ordinary ACP options in their original order and submits the original optionId', async () => {
  const { app, thread, posted } = await mount();
  app.handle({
    type: 'permission_request',
    workspaceId: WS,
    requestId: 'permission-1',
    title: 'Run command?',
    text: '{"command":"build"}',
    options: [
      { optionId: 'once', name: 'Allow once', kind: 'allow_once' },
      { optionId: 'never', name: 'Reject', kind: 'reject_once' }
    ]
  });

  const buttons = [...thread.querySelectorAll('.agent-decision-actions button')];
  assert.deepEqual(buttons.map((button) => button.textContent), ['Allow once', 'Reject']);
  buttons[0].click();
  assert.deepEqual(posted.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: 'permission-1',
    value: 'once'
  });
  assert.equal(buttons.every((button) => button.disabled), true);
});

test('does not invent Allow or Reject when ACP supplies an empty options array', async () => {
  const { app, thread } = await mount();
  app.handle({ type: 'permission_request', workspaceId: WS, requestId: 'empty', options: [] });

  assert.equal(thread.querySelectorAll('.agent-decision-actions button').length, 0);
  assert.match(thread.querySelector('.agent-decision-options-error').textContent, /did not provide/);
});

test('moves an active mode transition into the composer and submits the exact selection', async () => {
  const { app, thread, prompt, inputRow, posted } = await mount();
  app.handle(modeTransitionEvent({ workspaceId: WS }));

  const card = thread.querySelector('.agent-mode-transition');
  assert.equal(card.querySelector('h1').textContent, 'Plan');
  assert.equal(prompt.hidden, false);
  assert.equal(inputRow.hidden, true);
  const buttons = [...prompt.querySelectorAll('.agent-composer-decision-option')];
  assert.deepEqual(buttons.map((button) => button.dataset.optionId), ['approve', 'reject']);

  buttons[0].click();

  assert.deepEqual(posted.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: 'request-1',
    value: 'approve'
  });
  assert.equal(card.dataset.decisionState, 'sending');
  assert.equal(buttons.every((button) => button.disabled), true);
});

test('resolves a selection into a read-only card and restores the composer', async () => {
  const { app, thread, prompt, inputRow } = await mount();
  app.handle(modeTransitionEvent({ workspaceId: WS }));
  app.handle({
    type: 'permission_resolved',
    workspaceId: WS,
    requestId: 'request-1',
    optionId: 'reject',
    optionName: 'Keep planning'
  });

  const card = thread.querySelector('.agent-mode-transition');
  assert.equal(card.dataset.decisionState, 'disabled');
  assert.equal(card.querySelector('.agent-mode-transition-header-state').textContent, 'Selected');
  assert.equal(
    card.querySelector('[data-option-id="reject"]').classList.contains('agent-mode-transition-option-selected'),
    true
  );
  assert.equal(card.querySelector('.agent-mode-transition-status').textContent, 'Selected: Keep planning');
  assert.equal(prompt.hidden, true);
  assert.equal(inputRow.hidden, false);
});

test('interrupts the previous active proposal when a newer request arrives', async () => {
  const { app, thread } = await mount();
  app.handle(modeTransitionEvent({ workspaceId: WS }));
  app.handle(modeTransitionEvent({ workspaceId: WS, requestId: 'request-2', toolCallId: 'tool-2', title: 'New proposal' }));

  const cards = [...thread.querySelectorAll('.agent-mode-transition')];
  assert.equal(cards.length, 2);
  assert.equal(cards[0].querySelector('.agent-mode-transition-header-state').textContent, 'Interrupted');
});

test('cancels an active proposal without leaving an unusable composer overlay', async () => {
  const { app, thread, prompt } = await mount();
  app.handle(modeTransitionEvent({ workspaceId: WS }));
  app.handle({ type: 'permission_cancelled', workspaceId: WS, requestId: 'request-1', text: 'Stopped.' });

  const card = thread.querySelector('.agent-mode-transition');
  assert.equal(card.querySelector('.agent-mode-transition-header-state').textContent, 'Cancelled');
  assert.equal(card.querySelector('.agent-mode-transition-status').textContent, 'Stopped.');
  assert.equal(prompt.hidden, true);
});

test('replays historical proposals as collapsed, read-only snapshots', async () => {
  const { app, thread, prompt } = await mount();
  app.handle({
    type: 'agent_thread_loaded',
    workspaceId: WS,
    clear: true,
    cwd: 'D:/workspace',
    messages: [{
      role: 'mode_transition',
      requestId: 'history-1',
      toolCallId: 'tool-history',
      name: 'Ready?',
      text: '# Historical plan',
      decisionState: 'selected',
      selectedOptionId: 'approve',
      decisionOptions: [{ optionId: 'approve', name: 'Approved', kind: 'allow_once' }]
    }]
  });

  const card = thread.querySelector('.agent-mode-transition');
  assert.equal(card.querySelector('details').open, false);
  assert.equal(card.querySelector('button').disabled, false);
  assert.equal(card.querySelector('.agent-mode-transition-option').disabled, true);
  assert.equal(prompt.hidden, true);
});

test('copies the complete proposal source through the existing message copy helper', async () => {
  const { app, thread } = await mount();
  const copied = [];
  const writeText = async (text) => copied.push(text);
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
  const event = modeTransitionEvent({ workspaceId: WS, documentText: '# Full plan\n\n- one\n- two' });
  app.handle(event);

  thread.querySelector('.agent-mode-transition-document-actions button').click();
  await Promise.resolve();

  assert.deepEqual(copied, [event.documentText]);
});

test('renders unsafe elicitation URLs as plain text', async () => {
  const { app, thread } = await mount();
  app.handle({
    type: 'elicitation_request',
    workspaceId: WS,
    requestId: 'url-1',
    mode: 'url',
    url: 'javascript:alert(1)',
    schema: {}
  });

  const row = thread.querySelector('.agent-elicitation-url');
  assert.equal(row.textContent, 'javascript:alert(1)');
  assert.equal(row.querySelector('a'), null);
});
