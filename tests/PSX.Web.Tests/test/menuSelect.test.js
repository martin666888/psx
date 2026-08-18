import { afterEach, beforeEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, composerReady } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = false;
const WS = '77777777-7777-4777-8777-777777777777';
let fixture = null;

async function flushReact(callback) {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  try {
    await act(callback);
  } finally {
    globalThis.IS_REACT_ACT_ENVIRONMENT = false;
  }
}

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
  await flushReact(async () => {
    createAgentWorkspace(app, WS);
    app.handle(stateEvent(WS));
    app.handle(modesEvent(WS));
  });
  const panel = panelFor(WS);
  // The ComposerView island renders the composer and the controller mounts
  // the Radix Select triggers inside its first commit; wait for that signal.
  await composerReady(panel);
  return { app, panel, posted: runtime.postedMessages };
}

beforeEach(async () => {
  fixture = await mount();
});

afterEach(async () => {
  if (!fixture) return;
  const { app } = fixture;
  fixture = null;
  await flushReact(async () => {
    app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
});

const popup = () => document.querySelector('[data-slot="select-content"]');
const options = () => [...document.querySelectorAll('[data-slot="select-item"]')];

async function waitForElement(root, selector) {
  for (let attempt = 0; attempt < 100; attempt++) {
    const element = root.querySelector(selector);
    if (element) return element;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error(`element did not render: ${selector}`);
}

async function openSelect(trigger) {
  await flushReact(async () => {
    trigger.focus();
    trigger.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

async function openSelectRaw(trigger) {
  trigger.focus();
  trigger.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
  for (let attempt = 0; attempt < 100 && !popup(); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

async function press(target, key) {
  let event;
  await flushReact(async () => {
    event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    target.dispatchEvent(event);
  });
  return event;
}

async function pressRaw(target, key) {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
  target.dispatchEvent(event);
  await new Promise((resolve) => setTimeout(resolve, 0));
  return event;
}

test('Radix Select opens above the trigger with the current value checked', async () => {
  const { panel } = fixture;
  const trigger = panel.querySelector('[data-role="mode"]');

  assert.equal(popup(), null);
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');

  await openSelect(trigger);

  assert.ok(popup());
  assert.equal(trigger.getAttribute('aria-expanded'), 'true');
  const current = options().find((option) => option.getAttribute('aria-selected') === 'true');
  assert.equal(current.textContent.trim(), 'Default');
  assert.equal(current.getAttribute('aria-selected'), 'true');
  assert.ok(current.querySelector('svg'),
    'the current option carries the check mark');
  await press(popup(), 'Escape');
});

test('Radix Select closes on Escape and returns focus to the trigger', async () => {
  const { panel } = fixture;
  const trigger = panel.querySelector('[data-role="mode"]');

  await openSelect(trigger);
  await press(popup(), 'Escape');

  assert.equal(popup(), null);
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  assert.equal(document.activeElement, trigger);
});

test('Radix Select closes on outside pointerdown without selecting', async () => {
  const { panel, posted } = fixture;
  const trigger = panel.querySelector('[data-role="mode"]');
  const before = posted.length;

  await openSelect(trigger);
  await flushReact(async () => {
    document.body.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, button: 0 }));
  });

  assert.equal(popup(), null);
  assert.equal(posted.length, before, 'dismissing the popup must not post a command');
});

test('Radix Select picks via keyboard and only opens one popup at a time', async () => {
  const { app, panel, posted } = fixture;
  app.handle({
    type: 'agent_config_options',
    workspaceId: WS,
    options: [{ id: 'model', name: 'Model', type: 'select', options: [{ value: 'a', name: 'A' }, { value: 'b', name: 'B' }], currentValue: 'a' }]
  });
  const modeTrigger = panel.querySelector('[data-role="mode"]');
  const modelTrigger = await waitForElement(panel, '[data-config-id="model"]');

  await openSelect(modeTrigger);
  const firstPopup = popup();
  await press(firstPopup, 'Escape');
  await openSelectRaw(modelTrigger);
  assert.notEqual(popup(), null);
  assert.ok(!firstPopup.isConnected, 'the first popup closes when another menu opens');

  await pressRaw(document.activeElement, 'ArrowDown');
  await pressRaw(document.activeElement, 'Enter');

  assert.equal(popup(), null);
  assert.equal(posted.at(-1).command, 'set_config_option');
  assert.equal(posted.at(-1).value, 'b');
});

test('Radix Select stays closed while disabled', async () => {
  const { app, panel } = fixture;
  await flushReact(async () => {
    app.handle({ ...stateEvent(WS), busy: true });
  });
  const trigger = panel.querySelector('[data-role="mode"]');
  for (let attempt = 0; attempt < 100 && !trigger.disabled; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }

  await openSelect(trigger);

  assert.equal(popup(), null);
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  await flushReact(async () => {
    app.handle(stateEvent(WS));
  });
});

test('Radix Select announces the current value in its accessible name', async () => {
  const { app, panel } = fixture;
  app.handle({
    type: 'agent_config_options',
    workspaceId: WS,
    options: [{ id: 'model', name: 'Model', type: 'select', options: [{ value: 'a', name: 'A' }, { value: 'b', name: 'B' }], currentValue: 'b' }]
  });
  const trigger = await waitForElement(panel, '[data-config-id="model"]');

  assert.equal(trigger.getAttribute('aria-label'), 'Model: B');

  await openSelectRaw(trigger);
  await pressRaw(popup(), 'Home');
  await pressRaw(document.activeElement, 'Enter');
  app.handle({
    type: 'agent_config_options',
    workspaceId: WS,
    options: [{ id: 'model', name: 'Model', type: 'select', options: [{ value: 'a', name: 'A' }, { value: 'b', name: 'B' }], currentValue: 'a' }]
  });
  for (let attempt = 0; attempt < 100; attempt++) {
    if (panel.querySelector('button[data-config-id="model"]')?.getAttribute('aria-label') === 'Model: A') break;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(panel.querySelector('button[data-config-id="model"]').getAttribute('aria-label'), 'Model: A');
});

test('Radix Select traps Tab inside its open listbox', async () => {
  const { panel } = fixture;
  const trigger = panel.querySelector('[data-role="mode"]');

  await openSelect(trigger);
  const listbox = popup();
  const event = await press(listbox, 'Tab');

  assert.equal(event.defaultPrevented, true);
  assert.ok(popup(), 'Radix keeps its modal listbox open while Tab is suppressed');
  assert.ok(listbox.contains(document.activeElement), 'focus remains inside the listbox');
  await press(listbox, 'Escape');
});

test('Radix Select popup is destroyed with its workspace', async () => {
  const { app, panel } = fixture;
  await openSelect(panel.querySelector('[data-role="mode"]'));
  assert.ok(popup());

  app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
  fixture = null;

  assert.equal(popup(), null, 'closing the workspace must remove the portal popup');
});
