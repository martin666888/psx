import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace, modeTransitionEvent } from './agentHarness.js';

const WS = '22222222-2222-4222-8222-222222222222';

function stateEvent(workspaceId) {
  return {
    type: 'agent_state',
    workspaceId,
    status: 'ready',
    providerKey: 'claude-code',
    assistantName: 'Claude',
    agentName: 'Claude Code',
    cwd: '/tmp/project',
    sessionId: 'abcdef123456',
    threadId: 'thread-9',
    busy: false,
    isDraft: false,
    supportsImage: true,
    contextUsedTokens: 100
  };
}

function permissionEvent(workspaceId) {
  return {
    type: 'permission_request',
    workspaceId,
    requestId: 'permission-1',
    title: 'Run command?',
    text: '{"command":"build"}',
    options: [
      { optionId: 'once', name: 'Allow once', kind: 'allow_once' },
      { optionId: 'never', name: 'Reject', kind: 'reject_once' }
    ]
  };
}

function questionEvent(workspaceId) {
  return {
    type: 'question_request',
    workspaceId,
    requestId: 'question-1',
    title: 'Which branch?',
    text: '{"choices":["main","dev"]}',
    options: [
      { optionId: 'main', name: 'main', kind: 'allow_once' },
      { optionId: 'dev', name: 'dev', kind: 'allow_once' }
    ]
  };
}

function elicitationEvent(workspaceId) {
  return {
    type: 'elicitation_request',
    workspaceId,
    requestId: 'elicit-1',
    mode: 'url',
    url: 'javascript:alert(1)',
    schema: {}
  };
}

// Snapshot every decision node the controller owns: the timeline thread markup
// (permission/question/elicitation/mode-transition cards) plus the composer
// region toggles the mode-transition prompt drives through the composer seam.
function decisionSnapshot(panel) {
  const role = (name) => panel.querySelector('[data-role="' + name + '"]');
  const prompt = role('mode-transition-prompt');
  const inputRow = role('input-row');
  return {
    threadHtml: role('thread').innerHTML,
    promptHtml: prompt ? prompt.innerHTML : null,
    promptHidden: prompt ? prompt.hidden : null,
    inputRowHidden: inputRow ? inputRow.hidden : null
  };
}

// Drive the compiled Agent app the way production main.js does: create the
// workspace, mark the runtime ready, apply agent state, then replay the decision
// events and read the controller-owned nodes off the live panel.
async function drive(events) {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  app.handle(stateEvent(WS));
  for (const make of events) {
    app.handle(make(WS));
  }
  return panelFor(WS);
}

test('DecisionController renders a permission card', async () => {
  const panel = await drive([permissionEvent]);
  const controlled = decisionSnapshot(panel);
  assert.match(controlled.threadHtml, /agent-decision-permission/);
});

test('DecisionController renders a question card', async () => {
  const panel = await drive([questionEvent]);
  const controlled = decisionSnapshot(panel);
  assert.match(controlled.threadHtml, /agent-decision-question/);
});

test('DecisionController renders an elicitation form and keeps an unsafe URL plain text', async () => {
  const panel = await drive([elicitationEvent]);
  const controlled = decisionSnapshot(panel);
  assert.match(controlled.threadHtml, /agent-elicitation-url/);
  // Unsafe URL stays plain text with no anchor.
  assert.doesNotMatch(controlled.threadHtml, /<a[^>]*javascript:/);
});

test('DecisionController renders a mode-transition card and takes over the composer region', async () => {
  const panel = await drive([(ws) => modeTransitionEvent({ workspaceId: ws })]);
  const controlled = decisionSnapshot(panel);
  assert.match(controlled.threadHtml, /agent-mode-transition/);
  // The prompt takes over the composer region while it is shown.
  assert.equal(controlled.promptHidden, false);
  assert.equal(controlled.inputRowHidden, true);
});

test('DecisionController restores the composer once a mode transition resolves', async () => {
  const panel = await drive([
    (ws) => modeTransitionEvent({ workspaceId: ws }),
    (ws) => ({
      type: 'permission_resolved',
      workspaceId: ws,
      requestId: 'request-1',
      optionId: 'reject',
      optionName: 'Keep planning'
    })
  ]);

  const controlled = decisionSnapshot(panel);
  assert.equal(controlled.promptHidden, true);
  assert.equal(controlled.inputRowHidden, false);
});
