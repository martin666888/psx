// reactComposer.test.js — React composer islands plus imperative textarea seam.

import { afterEach, beforeEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import {
  mountAgentApp,
  createAgentWorkspace,
  appModule,
  flushAgentAnimationFrames
} from './agentHarness.js';

beforeEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = false;
});
const WS = '99999999-9999-4999-8999-999999999999';
await appModule('composer/composerIsland.js');

const role = (panel, name) => panel.querySelector('[data-role="' + name + '"]');
const tick = () => new Promise((resolve) => setTimeout(resolve, 5));

async function fixture() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS), runtime };
}

async function settle(run, predicate) {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 50 && !predicate(); i++) {
    await act(async () => tick());
  }
  assert.ok(predicate(), 'composer island did not settle');
}

function attach(panel, ...files) {
  const input = role(panel, 'attachment-input');
  Object.defineProperty(input, 'files', { configurable: true, value: files });
  input.dispatchEvent(new Event('change', { bubbles: true }));
}

test('attachment upload, confirmation and removal update the React strip', async () => {
  const { app, panel, runtime } = await fixture();
  await settle(
    () => app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready', supportsImage: true }),
    () => !!role(panel, 'attachment-input')
  );
  const file = new File([new Uint8Array([1, 2, 3])], 'shot.png', { type: 'image/png' });
  const before = runtime.postedMessages.length;
  await settle(
    () => attach(panel, file),
    () => !!panel.querySelector('.agent-attachment-uploading')
  );
  let upload;
  for (let i = 0; i < 50 && !upload; i++) {
    upload = runtime.postedMessages.slice(before).find((message) => message.clientId);
    if (!upload) await tick();
  }
  assert.ok(upload);
  assert.match(panel.querySelector('[data-status="uploading"]').textContent, /shot\.png/);
  await settle(
    () => app.handle({
      type: 'agent_attachment_uploaded',
      workspaceId: WS,
      clientId: upload.clientId,
      attachment: {
        id: 'srv-1',
        url: 'https://host/srv-1.png',
        fileName: 'shot.png',
        mimeType: 'image/png'
      }
    }),
    () => !!panel.querySelector('.agent-attachment-uploaded')
  );
  await settle(
    () => panel.querySelector('[aria-label="移除附件"]').click(),
    () => panel.querySelector('.agent-attachments-strip [data-status]') === null
  );
  assert.equal(role(panel, 'attachments-strip').hidden, true);
});

test('attach action reflects image support', async () => {
  const { app, panel } = await fixture();
  await settle(
    () => app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready', supportsImage: true }),
    () => !!role(panel, 'attach')
  );
  assert.equal(role(panel, 'attach').disabled, false);
  assert.equal(role(panel, 'attach').title, '附带图片');
  await act(async () => {
    app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready', supportsImage: false });
  });
  assert.equal(role(panel, 'attach').disabled, true);
  assert.match(role(panel, 'attach').title, /不支持图片输入/);
});

test('command rejection hint appears and clears on input', async () => {
  const { app, panel } = await fixture();
  await settle(
    () => app.handle({
      type: 'agent_command_rejected',
      workspaceId: WS,
      command: '/nope',
      reason: 'unsupported'
    }),
    () => role(panel, 'command-hint')?.textContent.includes('/nope')
  );
  assert.equal(role(panel, 'command-hint').hidden, false);
  await act(async () => {
    const input = role(panel, 'input');
    input.value = 'hello';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
  assert.equal(role(panel, 'command-hint').hidden, true);
});

test('textarea submit and stop preserve exact bridge payloads', async () => {
  const { app, panel, runtime } = await fixture();
  await settle(
    () => app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready', busy: false }),
    () => !!role(panel, 'send')
  );
  await settle(
    () => {
      const input = role(panel, 'input');
      input.value = 'ship it';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      role(panel, 'send').click();
    },
    () => runtime.postedMessages.at(-1)?.type === 'agent_submit'
  );
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_submit',
    workspaceId: WS,
    text: 'ship it',
    attachments: []
  });
  await act(async () => {
    app.handle({ type: 'agent_state', workspaceId: WS, status: 'busy', busy: true });
  });
  await settle(
    () => role(panel, 'send').click(),
    () => runtime.postedMessages.at(-1)?.command === 'stop'
  );
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'stop',
    value: '',
    requestId: ''
  });
});

test('config toggle is an icon button with no text', async () => {
  const { app, panel } = await fixture();
  await settle(
    () => app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready' }),
    () => !!panel.querySelector('.agent-config-toggle')
  );
  const toggle = panel.querySelector('.agent-config-toggle');
  assert.equal(toggle.getAttribute('aria-label'), 'Agent 配置');
  assert.ok(toggle.querySelector('svg'), 'config toggle renders an icon');
  assert.equal(toggle.textContent.trim(), '', 'config toggle carries no text');
});

test('compact config stays open for internal controls and closes outside or on Escape', async () => {
  const { app, panel } = await fixture();
  await settle(
    () => app.handle({ type: 'agent_state', workspaceId: WS, status: 'ready' }),
    () => !!panel.querySelector('.agent-config-toggle')
  );
  const toggle = panel.querySelector('.agent-config-toggle');
  const popover = panel.querySelector('.agent-config-popover');

  await act(async () => toggle.click());
  assert.equal(popover.dataset.open, 'true');
  await act(async () => {
    popover.dispatchEvent(new window.MouseEvent('pointerdown', { bubbles: true }));
  });
  assert.equal(popover.dataset.open, 'true', 'interacting inside keeps config open');

  await act(async () => {
    document.body.dispatchEvent(new window.MouseEvent('pointerdown', { bubbles: true }));
  });
  assert.equal(popover.dataset.open, 'false', 'outside pointer press closes config');

  await act(async () => toggle.click());
  await act(async () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    flushAgentAnimationFrames();
  });
  assert.equal(popover.dataset.open, 'false', 'Escape closes config');
  assert.equal(document.activeElement, toggle, 'Escape restores focus to the trigger');
});
