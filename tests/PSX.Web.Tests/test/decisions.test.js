import assert from 'node:assert/strict';
import { beforeEach, describe, it } from 'node:test';
import { createAgentManager, installAgentRuntime, modeTransitionEvent } from './agentHarness.js';

describe('Permission and mode transition UI', () => {
  let manager;
  let postedMessages;

  beforeEach(() => {
    const runtime = installAgentRuntime();
    postedMessages = runtime.postedMessages;
    manager = createAgentManager(runtime.AgentThreadManager);
  });

  it('renders ordinary ACP options in their original order and submits the original optionId', () => {
    manager.handleEvent({
      type: 'permission_request',
      requestId: 'permission-1',
      title: 'Run command?',
      text: '{"command":"build"}',
      options: [
        { optionId: 'once', name: 'Allow once', kind: 'allow_once' },
        { optionId: 'never', name: 'Reject', kind: 'reject_once' }
      ]
    });

    const buttons = [...manager.thread.querySelectorAll('.agent-decision-actions button')];
    assert.deepEqual(buttons.map((button) => button.textContent), ['Allow once', 'Reject']);
    buttons[0].click();
    assert.deepEqual(postedMessages.at(-1), { type: 'agent_permission_response', workspaceId: '11111111-1111-4111-8111-111111111111', requestId: 'permission-1', value: 'once' });
    assert.equal(buttons.every((button) => button.disabled), true);
  });

  it('does not invent Allow or Reject when ACP supplies an empty options array', () => {
    manager.handleEvent({ type: 'permission_request', requestId: 'empty', options: [] });

    assert.equal(manager.thread.querySelectorAll('.agent-decision-actions button').length, 0);
    assert.match(manager.thread.querySelector('.agent-decision-options-error').textContent, /did not provide/);
  });

  it('moves an active mode transition into the composer and submits the exact selection', () => {
    manager.handleEvent(modeTransitionEvent());

    const card = manager.thread.querySelector('.agent-mode-transition');
    const prompt = manager.meta.modeTransitionPrompt;
    assert.equal(card.querySelector('h1').textContent, 'Plan');
    assert.equal(prompt.hidden, false);
    assert.equal(manager.meta.inputRow.hidden, true);
    const buttons = [...prompt.querySelectorAll('.agent-composer-decision-option')];
    assert.deepEqual(buttons.map((button) => button.dataset.optionId), ['approve', 'reject']);

    buttons[0].click();

    assert.deepEqual(postedMessages.at(-1), { type: 'agent_permission_response', workspaceId: '11111111-1111-4111-8111-111111111111', requestId: 'request-1', value: 'approve' });
    assert.equal(card.dataset.decisionState, 'sending');
    assert.equal(buttons.every((button) => button.disabled), true);
  });

  it('resolves a selection into a read-only card and restores the composer', () => {
    manager.handleEvent(modeTransitionEvent());
    manager.handleEvent({ type: 'permission_resolved', requestId: 'request-1', optionId: 'reject', optionName: 'Keep planning' });

    const card = manager.thread.querySelector('.agent-mode-transition');
    assert.equal(card.dataset.decisionState, 'disabled');
    assert.equal(card.querySelector('.agent-mode-transition-header-state').textContent, 'Selected');
    assert.equal(card.querySelector('[data-option-id="reject"]').classList.contains('agent-mode-transition-option-selected'), true);
    assert.equal(card.querySelector('.agent-mode-transition-status').textContent, 'Selected: Keep planning');
    assert.equal(manager.meta.modeTransitionPrompt.hidden, true);
    assert.equal(manager.meta.inputRow.hidden, false);
  });

  it('interrupts the previous active proposal when a newer request arrives', () => {
    manager.handleEvent(modeTransitionEvent());
    manager.handleEvent(modeTransitionEvent({ requestId: 'request-2', toolCallId: 'tool-2', title: 'New proposal' }));

    const cards = [...manager.thread.querySelectorAll('.agent-mode-transition')];
    assert.equal(cards.length, 2);
    assert.equal(cards[0].querySelector('.agent-mode-transition-header-state').textContent, 'Interrupted');
    assert.equal(manager.activeModeTransitionRequestId, 'request-2');
  });

  it('cancels an active proposal without leaving an unusable composer overlay', () => {
    manager.handleEvent(modeTransitionEvent());
    manager.handleEvent({ type: 'permission_cancelled', requestId: 'request-1', text: 'Stopped.' });

    const card = manager.thread.querySelector('.agent-mode-transition');
    assert.equal(card.querySelector('.agent-mode-transition-header-state').textContent, 'Cancelled');
    assert.equal(card.querySelector('.agent-mode-transition-status').textContent, 'Stopped.');
    assert.equal(manager.meta.modeTransitionPrompt.hidden, true);
  });

  it('replays historical proposals as collapsed, read-only snapshots', () => {
    manager.handleEvent({
      type: 'agent_thread_loaded',
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

    const card = manager.thread.querySelector('.agent-mode-transition');
    assert.equal(card.querySelector('details').open, false);
    assert.equal(card.querySelector('button').disabled, false);
    assert.equal(card.querySelector('.agent-mode-transition-option').disabled, true);
    assert.equal(manager.meta.modeTransitionPrompt.hidden, true);
  });

  it('copies the complete proposal source through the existing message copy helper', async () => {
    const copied = [];
    const writeText = async (text) => copied.push(text);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    const event = modeTransitionEvent({ documentText: '# Full plan\n\n- one\n- two' });
    manager.handleEvent(event);

    manager.thread.querySelector('.agent-mode-transition-document-actions button').click();
    await Promise.resolve();

    assert.deepEqual(copied, [event.documentText]);
  });

  it('renders unsafe elicitation URLs as plain text', () => {
    manager.handleEvent({
      type: 'elicitation_request',
      requestId: 'url-1',
      mode: 'url',
      url: 'javascript:alert(1)',
      schema: {}
    });

    const row = manager.thread.querySelector('.agent-elicitation-url');
    assert.equal(row.textContent, 'javascript:alert(1)');
    assert.equal(row.querySelector('a'), null);
  });
});
