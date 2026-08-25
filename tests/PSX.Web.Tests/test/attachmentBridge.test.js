import { afterEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, composerReady } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = false;
const WS = '88888888-8888-4888-8888-888888888888';
let currentApp = null;

async function flushReact(callback) {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  try {
    await act(callback);
  } finally {
    globalThis.IS_REACT_ACT_ENVIRONMENT = false;
  }
}

async function fixture() {
  const { app, panelFor, runtime } = await mountAgentApp();
  currentApp = app;
  createAgentWorkspace(app, WS);
  app.handle({
    type: 'agent_state',
    workspaceId: WS,
    status: 'ready',
    supportsImage: true,
    busy: false
  });
  const panel = panelFor(WS);
  await composerReady(panel);
  return { app, panel, runtime };
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

function attach(panel, ...files) {
  const input = panel.querySelector('[data-role="attachment-input"]');
  Object.defineProperty(input, 'files', { configurable: true, value: files });
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

function uploads(runtime) {
  return runtime.postedMessages.filter((message) => message.type === 'agent_upload_attachment');
}

async function waitFor(predicate, message = 'condition did not settle') {
  for (let attempt = 0; attempt < 100; attempt++) {
    const value = predicate();
    if (value) return value;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.fail(message);
}

test('fast removal aborts blob reconstruction before upload starts', async () => {
  const { panel, runtime } = await fixture();
  const file = new File(['fast'], 'fast.png', { type: 'image/png' });
  let releaseFetch;
  const deferredFetch = (_input, _init) => new Promise((resolve) => {
    releaseFetch = () => resolve({ blob: async () => file });
  });
  globalThis.fetch = deferredFetch;
  window.fetch = deferredFetch;

  attach(panel, file);
  await waitFor(() => releaseFetch);
  const remove = await waitFor(() => panel.querySelector('[aria-label="移除附件"]'));
  await flushReact(async () => remove.click());
  releaseFetch();
  await new Promise((resolve) => setTimeout(resolve, 20));

  assert.equal(uploads(runtime).length, 0);
  assert.equal(panel.querySelector('.agent-attachments-strip [data-status]'), null);
});

test('blob fetch failure removes the local attachment without uploading', async () => {
  const { panel, runtime } = await fixture();
  let fetchCalled = false;
  const failedFetch = async () => {
    fetchCalled = true;
    throw new Error('blob disappeared');
  };
  globalThis.fetch = failedFetch;
  window.fetch = failedFetch;

  attach(panel, new File(['gone'], 'gone.png', { type: 'image/png' }));
  await waitFor(() => fetchCalled);
  await waitFor(() => panel.querySelector('.agent-attachments-strip [data-status]') === null);

  assert.equal(uploads(runtime).length, 0);
});

test('workspace disposal aborts in-flight preparation and suppresses late uploads', async () => {
  const { app, panel, runtime } = await fixture();
  const file = new File(['late'], 'late.png', { type: 'image/png' });
  let releaseFetch;
  const deferredFetch = () => new Promise((resolve) => {
    releaseFetch = () => resolve({ blob: async () => file });
  });
  globalThis.fetch = deferredFetch;
  window.fetch = deferredFetch;

  attach(panel, file);
  await waitFor(() => releaseFetch);
  app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
  currentApp = null;
  releaseFetch();
  await new Promise((resolve) => setTimeout(resolve, 20));

  assert.equal(uploads(runtime).length, 0);
});

test('run failure restores files by retryKey and reuses completed server IDs', async () => {
  const { app, panel, runtime } = await fixture();
  attach(panel, new File(['retry'], 'retry.png', { type: 'image/png' }));
  const upload = await waitFor(() => uploads(runtime)[0]);
  app.handle({
    type: 'agent_attachment_uploaded',
    workspaceId: WS,
    clientId: upload.clientId,
    attachment: {
      id: 'srv-retry',
      url: 'https://host/srv-retry.png',
      fileName: 'retry.png',
      mimeType: 'image/png'
    }
  });
  await waitFor(() => panel.querySelector('.agent-attachment-uploaded'));

  const input = panel.querySelector('[data-role="input"]');
  await flushReact(async () => {
    input.value = 'retry this';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    panel.querySelector('[data-role="send"]').click();
  });
  await waitFor(() => runtime.postedMessages.at(-1)?.attachments?.[0] === 'srv-retry');
  assert.deepEqual(runtime.postedMessages.at(-1).attachments, ['srv-retry']);
  await waitFor(() => panel.querySelector('.agent-attachments-strip [data-status]') === null);
  const uploadCount = uploads(runtime).length;

  app.handle({ type: 'run_failed', workspaceId: WS, text: 'provider failed' });
  await waitFor(() => panel.querySelector('.agent-attachment-uploaded'));

  assert.equal(input.value, 'retry this');
  assert.equal(uploads(runtime).length, uploadCount, 'an uploaded retry reuses its server ID');
});

test('submit pairs server IDs in PromptInput snapshot order despite out-of-order preparation', async () => {
  const { app, panel, runtime } = await fixture();
  const first = new File(['first'], 'first.png', { type: 'image/png' });
  const second = new File(['second'], 'second.png', { type: 'image/png' });
  const releases = [];
  const deferredFetch = () => new Promise((resolve) => {
    releases.push((file) => resolve({ blob: async () => file }));
  });
  globalThis.fetch = deferredFetch;
  window.fetch = deferredFetch;

  attach(panel, first, second);
  await waitFor(() => releases.length === 2);
  releases[1](second);
  releases[0](first);
  await waitFor(() => uploads(runtime).length === 2);
  const uploadByName = new Map(uploads(runtime).map((upload) => [upload.fileName, upload]));

  for (const [fileName, serverId] of [['first.png', 'srv-first'], ['second.png', 'srv-second']]) {
    const upload = uploadByName.get(fileName);
    app.handle({
      type: 'agent_attachment_uploaded',
      workspaceId: WS,
      clientId: upload.clientId,
      attachment: { id: serverId, fileName, mimeType: 'image/png', url: 'https://host/' + fileName }
    });
  }
  await waitFor(() => panel.querySelectorAll('.agent-attachment-uploaded').length === 2);

  const input = panel.querySelector('[data-role="input"]');
  await flushReact(async () => {
    input.value = 'ordered';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    panel.querySelector('[data-role="send"]').click();
  });
  await waitFor(() => runtime.postedMessages.at(-1)?.attachments?.length === 2);
  assert.deepEqual(runtime.postedMessages.at(-1).attachments, ['srv-first', 'srv-second']);
});
