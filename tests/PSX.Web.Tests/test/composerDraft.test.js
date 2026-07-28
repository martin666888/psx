import { afterEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, composerReady } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = false;
const WS = '78787878-7878-4878-8878-787878787878';
let currentApp = null;

const role = (panel, name) => panel.querySelector('[data-role="' + name + '"]');

async function flushReact(callback) {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  try {
    await act(callback);
  } finally {
    globalThis.IS_REACT_ACT_ENVIRONMENT = false;
  }
}

async function waitFor(predicate, message = 'condition did not settle') {
  for (let attempt = 0; attempt < 100; attempt++) {
    const value = predicate();
    if (value) return value;
    await flushReact(async () => new Promise((resolve) => setTimeout(resolve, 10)));
  }
  assert.fail(message);
}

async function fixture() {
  const { app, panelFor, runtime } = await mountAgentApp();
  currentApp = app;
  createAgentWorkspace(app, WS);
  app.handle({
    type: 'agent_state',
    workspaceId: WS,
    status: 'ready',
    busy: false
  });
  const panel = panelFor(WS);
  await composerReady(panel);
  return { app, panel, runtime };
}

async function setDraft(panel, text) {
  await flushReact(async () => {
    const input = role(panel, 'input');
    input.value = text;
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

afterEach(async () => {
  if (!currentApp) return;
  const app = currentApp;
  currentApp = null;
  await flushReact(async () => {
    app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
});

test('IME Enter does not submit until composition ends', async () => {
  const { panel, runtime } = await fixture();
  const input = role(panel, 'input');
  const before = runtime.postedMessages.length;

  await flushReact(async () => {
    input.dispatchEvent(new Event('compositionstart', { bubbles: true }));
    input.value = '中文';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'Enter',
      bubbles: true,
      isComposing: true
    }));
  });

  assert.equal(runtime.postedMessages.length, before);
  assert.equal(input.value, '中文');
  await flushReact(async () => input.dispatchEvent(new Event('compositionend', { bubbles: true })));
});

test('draftToken projection waits for compositionend and applies the newest restore', async () => {
  const { app, panel, runtime } = await fixture();
  await setDraft(panel, 'recover me');
  const before = runtime.postedMessages.length;
  await flushReact(async () => role(panel, 'send').click());
  await waitFor(() => runtime.postedMessages.length > before);
  await waitFor(() => role(panel, 'input').value === '');

  const input = role(panel, 'input');
  await flushReact(async () => {
    input.dispatchEvent(new Event('compositionstart', { bubbles: true }));
    input.value = '正在输入';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    app.handle({ type: 'run_failed', workspaceId: WS, text: 'provider failed' });
  });

  assert.equal(input.value, '正在输入');
  await flushReact(async () => input.dispatchEvent(new Event('compositionend', { bubbles: true })));
  await waitFor(() => input.value === 'recover me');
});

test('rejected slash validation restores the controlled draft after form reset', async () => {
  const { app, panel, runtime } = await fixture();
  app.handle({ type: 'agent_commands', workspaceId: WS, ready: true, commands: [] });
  await setDraft(panel, '/unknown');
  const before = runtime.postedMessages.length;
  await flushReact(async () => role(panel, 'send').click());

  await waitFor(() => role(panel, 'command-hint').hidden === false);
  assert.equal(runtime.postedMessages.length, before);
  assert.equal(role(panel, 'input').value, '/unknown');
});
