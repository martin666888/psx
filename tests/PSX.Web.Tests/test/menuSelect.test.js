import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '77777777-7777-4777-8777-777777777777';

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

function modesEvent(workspaceId) {
  return {
    type: 'agent_modes',
    workspaceId,
    modes: [
      { id: 'default', name: 'Default', description: 'Default mode' },
      { id: 'plan', name: 'Plan', description: 'Planning mode' }
    ],
    currentModeId: 'default'
  };
}

async function mount() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  app.handle(stateEvent(WS));
  app.handle(modesEvent(WS));
  return { app, panel: panelFor(WS), posted: runtime.postedMessages };
}

const popup = () => document.querySelector('.agent-menu-select-popup');
const options = () => [...document.querySelectorAll('.agent-menu-select-option')];

test('MenuSelect opens above the trigger with the current value checked', async () => {
  const { panel } = await mount();
  const trigger = panel.querySelector('[data-role="mode"]');

  assert.equal(popup(), null);
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');

  trigger.click();

  assert.ok(popup());
  assert.equal(trigger.getAttribute('aria-expanded'), 'true');
  const current = options().find((option) => option.dataset.value === 'default');
  assert.equal(current.getAttribute('aria-selected'), 'true');
  assert.ok(current.querySelector('svg'), 'the current option carries the check mark');
});

test('MenuSelect closes on Escape and returns focus to the trigger', async () => {
  const { panel } = await mount();
  const trigger = panel.querySelector('[data-role="mode"]');

  trigger.click();
  popup().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

  assert.equal(popup(), null);
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  assert.equal(document.activeElement, trigger);
});

test('MenuSelect closes on outside pointerdown without selecting', async () => {
  const { panel, posted } = await mount();
  const trigger = panel.querySelector('[data-role="mode"]');
  const before = posted.length;

  trigger.click();
  document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }));

  assert.equal(popup(), null);
  assert.equal(posted.length, before, 'dismissing the popup must not post a command');
});

test('MenuSelect picks via keyboard and only opens one popup at a time', async () => {
  const { app, panel, posted } = await mount();
  app.handle({
    type: 'agent_config_options',
    workspaceId: WS,
    options: [{ id: 'model', name: 'Model', type: 'select', options: [{ value: 'a', name: 'A' }, { value: 'b', name: 'B' }], currentValue: 'a' }]
  });
  const modeTrigger = panel.querySelector('[data-role="mode"]');
  const modelTrigger = panel.querySelector('button[data-config-id="model"]');

  modeTrigger.click();
  const firstPopup = popup();
  modelTrigger.click();
  assert.notEqual(popup(), null);
  assert.ok(!firstPopup.isConnected, 'the first popup closes when another menu opens');

  popup().dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
  popup().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

  assert.equal(popup(), null);
  assert.equal(posted.at(-1).command, 'set_config_option');
  assert.equal(posted.at(-1).value, 'b');
});

test('MenuSelect stays closed while disabled', async () => {
  const { panel } = await mount();
  const trigger = panel.querySelector('[data-role="mode"]');
  trigger.disabled = true;

  trigger.click();

  assert.equal(popup(), null);
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
});

test('MenuSelect ignores its own popup scrolling but closes on outside scroll', async () => {
  const { panel } = await mount();
  const trigger = panel.querySelector('[data-role="mode"]');

  trigger.click();
  popup().dispatchEvent(new Event('scroll'));
  assert.ok(popup(), 'scrolling the option list itself must not close it');

  window.dispatchEvent(new Event('scroll'));
  assert.equal(popup(), null, 'scrolling outside the popup still closes it');
});

test('MenuSelect announces the current value in its accessible name', async () => {
  const { app, panel } = await mount();
  app.handle({
    type: 'agent_config_options',
    workspaceId: WS,
    options: [{ id: 'model', name: 'Model', type: 'select', options: [{ value: 'a', name: 'A' }, { value: 'b', name: 'B' }], currentValue: 'b' }]
  });
  const trigger = panel.querySelector('button[data-config-id="model"]');

  assert.equal(trigger.getAttribute('aria-label'), 'Model: B');

  trigger.click();
  options().find((option) => option.dataset.value === 'a').click();
  app.handle({
    type: 'agent_config_options',
    workspaceId: WS,
    options: [{ id: 'model', name: 'Model', type: 'select', options: [{ value: 'a', name: 'A' }, { value: 'b', name: 'B' }], currentValue: 'a' }]
  });
  assert.equal(panel.querySelector('button[data-config-id="model"]').getAttribute('aria-label'), 'Model: A');
});

test('MenuSelect Tab closes the popup and keeps navigation on the trigger', async () => {
  const { panel } = await mount();
  const trigger = panel.querySelector('[data-role="mode"]');

  trigger.click();
  popup().dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));

  assert.equal(popup(), null);
  assert.equal(document.activeElement, trigger, 'Tab must not strand focus on body');
});

test('MenuSelect popup is destroyed with its workspace', async () => {
  const { app, panel } = await mount();
  panel.querySelector('[data-role="mode"]').click();
  assert.ok(popup());

  app.handle({ type: 'agent_workspace_closed', workspaceId: WS });

  assert.equal(popup(), null, 'closing the workspace must remove the portal popup');
});
