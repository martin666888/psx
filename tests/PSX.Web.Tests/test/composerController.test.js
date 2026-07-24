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
      },
      {
        id: 'fast_mode',
        type: 'boolean',
        name: 'Fast mode',
        description: 'Use fast mode',
        currentValue: false
      },
      {
        id: 'legacy_fast',
        type: 'select',
        name: 'Legacy fast',
        currentValue: 'off',
        options: [
          { value: 'on', name: 'On' },
          { value: 'off', name: 'Off' }
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
      text: role('send-label').textContent,
      title: send.title,
      ariaLabel: send.getAttribute('aria-label'),
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
      value: mode.querySelector('.agent-menu-select-value')?.textContent ?? '',
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

  panel.querySelector('[data-role="mode"]').click();
  const modeOptions = [...document.querySelectorAll('.agent-menu-select-option')];
  assert.deepEqual(modeOptions.map((option) => option.dataset.value), ['default', 'plan']);
  panel.querySelector('[data-role="mode"]').click();
  assert.equal(controlled.mode.value, 'Plan');
  assert.match(controlled.configOptionsHtml, /Verbosity/);
  assert.equal(panel.querySelectorAll('.agent-config-switch').length, 2);
});

test('ComposerController submits native booleans and legacy binary selects without changing their values', async () => {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  app.handle(stateEvent(WS, false));
  app.handle(configEvent(WS));
  const panel = panelFor(WS);

  const nativeBoolean = panel.querySelector('button[data-config-id="fast_mode"]');
  nativeBoolean.click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_command', workspaceId: WS, command: 'set_config_option', value: true, requestId: 'fast_mode'
  });

  const legacySelect = panel.querySelector('button[data-config-id="legacy_fast"]');
  legacySelect.click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_command', workspaceId: WS, command: 'set_config_option', value: 'on', requestId: 'legacy_fast'
  });
});

test('ComposerController flips Send into Stop while a run is busy', async () => {
  const panel = await drive([stateEvent(WS, true)]);
  const controlled = composerSnapshot(panel);

  assert.equal(controlled.send.text, 'Stop');
  assert.equal(controlled.send.ariaLabel, 'Stop');
  assert.equal(controlled.send.stop, true);
});

test('ComposerController keeps the Stop affordance live and the composer interactive while stopping', async () => {
  const event = stateEvent(WS, true);
  event.status = 'stopping';
  const panel = await drive([event]);
  const controlled = composerSnapshot(panel);

  // stopping is a busy state: the button stays a live Stop and neither it nor
  // the input is disabled, so the user can press Stop again / keep typing while
  // the forced-reset watchdog runs.
  assert.equal(controlled.send.text, 'Stop');
  assert.equal(controlled.send.stop, true);
  assert.equal(controlled.send.disabled, false);
  assert.equal(controlled.input.disabled, false);
});

test('Composer template keeps input, controls, and actions in one reading-column card', async () => {
  const panel = await drive([stateEvent(WS, false)]);
  const card = panel.querySelector('.agent-composer-card');
  const input = panel.querySelector('[data-role="input"]');
  const attach = panel.querySelector('[data-role="attach"]');
  const send = panel.querySelector('[data-role="send"]');
  const controls = panel.querySelector('.agent-composer-controls');

  assert.ok(card);
  assert.equal(input.closest('.agent-composer-card'), card);
  assert.equal(attach.closest('.agent-composer-footer'), send.closest('.agent-composer-footer'));
  assert.equal(controls.contains(panel.querySelector('[data-role="mode"]')), true);
  assert.equal(input.style.height, '52px');
  assert.ok(send.querySelector('.agent-send-icon-submit'));
  assert.ok(send.querySelector('.agent-send-icon-stop'));
});

test('ComposerController renders the command-rejected hint', async () => {
  const panel = await drive([stateEvent(WS, false), rejectEvent(WS)]);
  const controlled = composerSnapshot(panel);

  assert.equal(controlled.commandHint.hidden, false);
  assert.ok(controlled.commandHint.text.length > 0);
});
