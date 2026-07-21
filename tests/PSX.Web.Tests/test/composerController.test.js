import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '22222222-2222-4222-8222-222222222222';

function stateEvent(workspaceId, busy) {
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
    busy: !!busy,
    isDraft: false,
    supportsImage: true,
    contextUsedTokens: 100
  };
}

function modesEvent(workspaceId) {
  return {
    type: 'agent_modes',
    workspaceId,
    modes: [
      { id: 'default', name: 'Default', description: 'Default mode' },
      { id: 'plan', name: 'Plan', description: 'Planning mode' }
    ],
    currentModeId: 'plan'
  };
}

function configEvent(workspaceId) {
  return {
    type: 'agent_config_options',
    workspaceId,
    options: [
      {
        id: 'verbosity',
        type: 'select',
        name: 'Verbosity',
        description: 'How verbose the agent is',
        currentValue: 'high',
        options: [
          { value: 'low', name: 'Low', description: 'Terse' },
          { value: 'high', name: 'High', description: 'Detailed' }
        ]
      }
    ]
  };
}

function rejectEvent(workspaceId) {
  return { type: 'agent_command_rejected', workspaceId, command: '/nope', reason: 'unsupported' };
}

// Snapshot every composer DOM node the controller owns.
function composerSnapshot(panel) {
  const role = (name) => panel.querySelector('[data-role="' + name + '"]');
  const send = role('send');
  const input = role('input');
  const mode = role('mode');
  const modeLabel = mode.closest('label');
  const hint = role('command-hint');
  const menu = role('command-menu');
  return {
    send: {
      text: send.textContent,
      title: send.title,
      disabled: send.disabled,
      stop: send.classList.contains('agent-send-stop')
    },
    input: {
      disabled: input.disabled,
      placeholder: input.placeholder,
      ariaExpanded: input.getAttribute('aria-expanded')
    },
    mode: {
      html: mode.innerHTML,
      value: mode.value,
      disabled: mode.disabled,
      labelHidden: modeLabel ? modeLabel.hidden : null
    },
    configOptionsHtml: role('config-options').innerHTML,
    commandHint: { text: hint.textContent, hidden: hint.hidden },
    commandMenuHidden: menu.hidden
  };
}

// Drive the compiled Agent app the way production main.js does, then read the
// controller-owned composer nodes off the live panel.
async function drive(events) {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  for (const event of events) {
    app.handle(event);
  }
  return panelFor(WS);
}

test('ComposerController renders composer controls from modes and config options', async () => {
  const panel = await drive([stateEvent(WS, false), modesEvent(WS), configEvent(WS)]);
  const controlled = composerSnapshot(panel);

  assert.deepEqual(
    [...panel.querySelectorAll('[data-role="mode"] option')].map((option) => option.value),
    ['default', 'plan']
  );
  assert.equal(controlled.mode.value, 'plan');
  assert.match(controlled.configOptionsHtml, /Verbosity/);
});

test('ComposerController flips Send into Stop while a run is busy', async () => {
  const panel = await drive([stateEvent(WS, true)]);
  const controlled = composerSnapshot(panel);

  assert.equal(controlled.send.text, 'Stop');
  assert.equal(controlled.send.stop, true);
});

test('ComposerController renders the command-rejected hint', async () => {
  const panel = await drive([stateEvent(WS, false), rejectEvent(WS)]);
  const controlled = composerSnapshot(panel);

  assert.equal(controlled.commandHint.hidden, false);
  assert.ok(controlled.commandHint.text.length > 0);
});
