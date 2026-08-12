import { test } from 'vitest';
import assert from 'node:assert/strict';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import {
  appModule,
  flushAgentAnimationFrames,
  installAgentRuntime
} from './agentHarness.js';

async function waitFor(predicate, message = 'condition did not settle') {
  for (let attempt = 0; attempt < 100; attempt++) {
    const value = predicate();
    if (value) return value;
    await act(async () => {
      flushAgentAnimationFrames();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
  assert.fail(message);
}

test('attachment preview traps focus in Dialog and restores it on close', async () => {
  installAgentRuntime();
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  const { ComposerImagePreview } = await appModule('composer/ComposerImagePreview.js');
  const { setPortalContainer } = await appModule('ui/portalContainer.js');
  const trigger = document.createElement('button');
  trigger.type = 'button';
  trigger.textContent = 'Preview image';
  const portal = document.createElement('div');
  portal.className = 'agent-ui';
  const host = document.createElement('div');
  document.body.append(trigger, portal, host);
  setPortalContainer(portal);
  const root = createRoot(host);

  try {
    trigger.focus();
    await act(async () => {
      root.render(createElement(ComposerImagePreview, {
        preview: {
          requestToken: 1,
          closeToken: 0,
          src: 'blob:psx/1'
        }
      }));
      await Promise.resolve();
    });

    const dialog = await waitFor(() => document.querySelector('[data-role="image-preview"]'));
    assert.equal(dialog.getAttribute('role'), 'dialog');
    assert.equal(dialog.querySelector('img').src, 'blob:psx/1');
    assert.ok(dialog.contains(document.activeElement), 'focus moves inside the open Dialog');

    await act(async () => {
      document.querySelector('[data-slot="dialog-close"]').click();
      await Promise.resolve();
    });
    await waitFor(() => document.querySelector('[data-role="image-preview"]') === null);
    await waitFor(() => document.activeElement === trigger);
  } finally {
    await act(async () => root.unmount());
    setPortalContainer(null);
    globalThis.IS_REACT_ACT_ENVIRONMENT = false;
  }
});
