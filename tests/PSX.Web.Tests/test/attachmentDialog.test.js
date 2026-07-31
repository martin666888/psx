import { afterEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace, composerReady } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = false;
const WS = '89898989-8989-4989-8989-898989898989';
let currentApp = null;

async function waitFor(predicate, message = 'condition did not settle') {
  for (let attempt = 0; attempt < 100; attempt++) {
    const value = predicate();
    if (value) return value;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.fail(message);
}

afterEach(() => {
  if (!currentApp) return;
  const app = currentApp;
  currentApp = null;
  app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
});

test('attachment preview traps focus in Dialog and restores it on close', async () => {
  const { app, panelFor } = await mountAgentApp();
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

  const input = panel.querySelector('[data-role="attachment-input"]');
  // This test owns Dialog focus semantics, not browser File byte handling
  // (AttachmentBridge has dedicated real-File tests). A metadata-only file
  // avoids jsdom retaining an ArrayBuffer while V8 precise coverage is active.
  const file = {
    name: 'preview.png',
    type: 'image/png',
    size: 7,
    lastModified: 0
  };
  Object.defineProperty(input, 'files', { configurable: true, value: [file] });
  input.dispatchEvent(new Event('change', { bubbles: true }));

  const tile = await waitFor(() =>
    panel.querySelector('.agent-attachments-strip [role="button"]')
  );
  tile.focus();
  tile.click();
  const dialog = await waitFor(() =>
    document.querySelector('[data-role="image-preview"]')
  );

  assert.equal(dialog.getAttribute('role'), 'dialog');
  assert.match(dialog.querySelector('img').src, /^blob:psx\//);
  document.querySelector('[data-slot="dialog-close"]').click();
  await waitFor(() => document.querySelector('[data-role="image-preview"]') === null);
  assert.equal(document.activeElement, tile);
});
