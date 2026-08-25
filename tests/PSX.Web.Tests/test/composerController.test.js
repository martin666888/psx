import { test } from 'vitest';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace, composerReady } from './agentHarness.js';

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

function protocolConfigEvent(workspaceId) {
  return {
    type: 'agent_config_options',
    workspaceId,
    options: [
      {
        id: 'mode',
        type: 'select',
        name: '模式',
        currentValue: 'default',
        options: [{ value: 'default', name: 'Default' }]
      },
      {
        id: 'model',
        type: 'select',
        name: '模型',
        currentValue: 'k3',
        options: [{ value: 'k3', name: 'K3' }]
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
      text: send.getAttribute('aria-label'),
      title: send.title,
      ariaLabel: send.getAttribute('aria-label'),
      disabled: send.disabled,
      stop: send.dataset.status === 'streaming'
    },
    input: {
      disabled: input.disabled,
      placeholder: input.placeholder,
      ariaExpanded: input.getAttribute('aria-expanded')
    },
    mode: {
      html: mode.innerHTML,
      value: mode.querySelector('[data-slot="select-value"]')?.textContent ?? '',
      ariaLabel: mode.getAttribute('aria-label'),
      disabled: mode.disabled,
      labelHidden: modeLabel ? modeLabel.hidden : null
    },
    configOptionsHtml: role('config-options').innerHTML,
    commandHint: { text: hint.textContent, hidden: hint.hidden },
    commandMenuHidden: menu.hidden
  };
}

// Drive the compiled Agent app the way production main.js does, then read the
// controller-owned composer nodes off the live panel. The ComposerView island
// renders the composer asynchronously; [data-role="attach"] marks its first
// commit and [data-role="mode"] the controller's wiring (both land inside the
// same synchronous commit, so together they pin full readiness).
async function drive(events) {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  for (const event of events) app.handle(event);
  const panel = panelFor(WS);
  const expectsHint = events.some((event) => event.type === 'agent_command_rejected');
  for (let attempt = 0; attempt < 200; attempt++) {
    const attachReady = !!panel.querySelector('[data-role="attach"]');
    const wiringReady = !!panel.querySelector('[data-role="mode"]');
    const hintReady =
      !expectsHint || !!panel.querySelector('[data-role="command-hint"]')?.textContent;
    if (attachReady && wiringReady && hintReady) return panel;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.fail('composer islands did not reach the requested state');
}

test('ComposerController renders composer controls from modes and config options', async () => {
  const panel = await drive([stateEvent(WS, false), modesEvent(WS), configEvent(WS)]);
  const controlled = composerSnapshot(panel);

  assert.equal(controlled.mode.value, 'Plan');
  assert.equal(controlled.mode.ariaLabel, 'Mode: Plan');
  assert.match(controlled.configOptionsHtml, /Verbosity/);
  assert.equal(panel.querySelectorAll('[data-slot="select-trigger"]').length, 2);
  assert.equal(panel.querySelectorAll('.agent-config-switch').length, 2);
});

test('ComposerController keeps Mode and Model labels in English before and after provider config arrives', async () => {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  app.handle(stateEvent(WS, false));
  app.handle(modesEvent(WS));
  const panel = panelFor(WS);
  await composerReady(panel);

  assert.equal(panel.querySelector('[data-role="mode"]').getAttribute('aria-label'), 'Mode: Plan');

  app.handle(protocolConfigEvent(WS));
  for (let attempt = 0; attempt < 100; attempt++) {
    if (panel.querySelector('button[data-config-id="model"]')) break;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }

  assert.equal(panel.querySelector('button[data-config-id="mode"]').getAttribute('aria-label'), 'Mode: Default');
  assert.equal(panel.querySelector('button[data-config-id="model"]').getAttribute('aria-label'), 'Model: K3');
  assert.doesNotMatch(panel.querySelector('[data-role="config-options"]').textContent, /模式|模型/);
});

test('ComposerController preserves an agent-confirmed config selection when submitting', async () => {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  app.handle(stateEvent(WS, false));
  app.handle(configEvent(WS));
  const panel = panelFor(WS);
  await composerReady(panel);

  const updatedConfig = configEvent(WS);
  updatedConfig.options[0].currentValue = 'low';
  app.handle(updatedConfig);

  const configTrigger = panel.querySelector('button[data-config-id="verbosity"]');
  for (let attempt = 0; attempt < 100; attempt++) {
    if (configTrigger.getAttribute('aria-label') === 'Verbosity: Low') break;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(configTrigger.getAttribute('aria-label'), 'Verbosity: Low');

  const postedBeforeSubmit = runtime.postedMessages.length;
  const input = panel.querySelector('[data-role="input"]');
  input.value = 'keep the selected model';
  input.dispatchEvent(new Event('input', { bubbles: true }));
  panel.querySelector('[data-role="send"]').click();

  for (let attempt = 0; attempt < 100; attempt++) {
    if (runtime.postedMessages.slice(postedBeforeSubmit).some((message) => message.type === 'agent_submit')) break;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  const submittedMessages = runtime.postedMessages.slice(postedBeforeSubmit);
  assert.equal(submittedMessages.some((message) => message.type === 'agent_submit'), true);
  assert.equal(
    submittedMessages.some(
      (message) => message.type === 'agent_command' && message.command === 'set_config_option'
    ),
    false
  );
  assert.equal(configTrigger.getAttribute('aria-label'), 'Verbosity: Low');
});

test('ComposerController submits native booleans and legacy binary selects without changing their values', async () => {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  app.handle(stateEvent(WS, false));
  app.handle(configEvent(WS));
  const panel = panelFor(WS);
  await composerReady(panel);

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

  assert.equal(controlled.send.text, '停止');
  assert.equal(controlled.send.ariaLabel, '停止');
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
  assert.equal(controlled.send.text, '停止');
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
  assert.equal(input.getAttribute('data-slot'), 'input-group-control');
  assert.ok(input.classList.contains('field-sizing-content'));
  assert.ok(send.querySelector('.lucide-corner-down-left'));
});

test('ComposerController renders the command-rejected hint', async () => {
  const panel = await drive([stateEvent(WS, false), rejectEvent(WS)]);
  const controlled = composerSnapshot(panel);

  assert.equal(controlled.commandHint.hidden, false);
  assert.ok(controlled.commandHint.text.length > 0);
});
