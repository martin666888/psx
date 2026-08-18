// reactSessionToolbar.test.js — React-only session metadata and context usage.

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
const WS = '88888888-8888-4888-8888-888888888888';
await appModule('workspace/sessionIsland.js');

function stateEvent(overrides = {}) {
  return {
    type: 'agent_state',
    workspaceId: WS,
    status: 'ready',
    cwd: '/tmp/project',
    sessionId: 'abcdef123456',
    threadId: 'thread-9',
    busy: false,
    isDraft: false,
    supportsImage: false,
    contextUsedTokens: 4200,
    contextWindowTokens: 100000,
    contextCostAmount: 0.23,
    contextCostCurrency: 'USD',
    ...overrides
  };
}

async function fixture() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS), posted: runtime.postedMessages };
}

async function settle(app, panel, event) {
  await act(async () => {
    app.handle(event);
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  // The context-usage island mounts after the ComposerView island commits
  // (MutationObserver replay), so wait for both island outputs.
  const ready = () =>
    panel.querySelector('[data-role="status"]') && panel.querySelector('[data-role="context-used"]');
  for (let i = 0; i < 50 && !ready(); i++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(ready());
}

test('session status, cwd, session id and context bands update semantically', async () => {
  const { app, panel } = await fixture();
  await settle(app, panel, stateEvent());
  assert.equal(panel.querySelector('[data-role="status"]').dataset.status, 'ready');
  assert.equal(panel.querySelector('[data-role="cwd"]').textContent, '/tmp/project');
  assert.equal(panel.querySelector('[data-role="session"]').textContent, 'session: abcdef123456');
  assert.equal(panel.querySelector('[data-role="context-used"]').dataset.contextState, 'accent');
  assert.match(panel.querySelector('[data-role="context-used"]').getAttribute('aria-label'), /4\.2K/);

  await settle(app, panel, {
    type: 'agent_usage_update',
    workspaceId: WS,
    contextUsedTokens: 85,
    contextWindowTokens: 100
  });
  assert.equal(panel.querySelector('[data-role="context-used"]').dataset.contextState, 'warning');
  await settle(app, panel, {
    type: 'agent_usage_update',
    workspaceId: WS,
    contextUsedTokens: 95,
    contextWindowTokens: 100
  });
  assert.equal(panel.querySelector('[data-role="context-used"]').dataset.contextState, 'error');
});

test('provider-prefixed ACP session ids remain complete', async () => {
  const { app, panel } = await fixture();
  const kimiSessionId = 'session_238e83a7-7a98-4c6f-92c4-123456789abc';
  await settle(app, panel, stateEvent({ sessionId: kimiSessionId }));
  assert.equal(
    panel.querySelector('[data-role="session"]').textContent,
    'session: ' + kimiSessionId
  );
});

test('draft Change button emits pick_cwd and locks after session start', async () => {
  const { app, panel, posted } = await fixture();
  await settle(app, panel, stateEvent({ isDraft: true, sessionId: '', cwd: '' }));
  const change = panel.querySelector('[data-role="change-cwd"]');
  assert.equal(change.disabled, false);
  await act(async () => change.click());
  assert.deepEqual(posted.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'pick_cwd',
    value: '',
    requestId: ''
  });

  await settle(app, panel, stateEvent({ isDraft: false }));
  assert.equal(change.disabled, true);
  assert.match(change.title, /new Agent tab/);
});

test('repeated updates preserve the context ring DOM node', async () => {
  const { app, panel } = await fixture();
  await settle(app, panel, stateEvent());
  const ring = panel.querySelector('[data-role="context-ring-progress"]');
  await settle(app, panel, {
    type: 'agent_usage_update',
    workspaceId: WS,
    contextUsedTokens: 50,
    contextWindowTokens: 100
  });
  assert.equal(panel.querySelector('[data-role="context-ring-progress"]'), ring);
});

// The workspace toolbar island (workspace/toolbarIsland.js) renders the More
// responsive menu next to the session meta line; these cases pin the icon-only
// trigger, wide-mode action reachability and the session-label fallback contract.
async function toolbarMore(panel) {
  for (let attempt = 0; attempt < 200 && !panel.querySelector('.agent-toolbar-more'); attempt++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(panel.querySelector('.agent-toolbar-more'), 'toolbar More menu did not mount');
}

test('toolbar More trigger renders the Ellipsis icon with no visible text', async () => {
  const { app, panel } = await fixture();
  await settle(app, panel, stateEvent());
  await toolbarMore(panel);
  const trigger = panel.querySelector('.agent-toolbar-more-trigger');
  assert.equal(trigger.getAttribute('aria-label'), '更多 Agent 操作');
  assert.equal(trigger.getAttribute('aria-expanded'), 'false');
  assert.ok(trigger.querySelector('svg'), 'trigger renders the Ellipsis icon');
  assert.equal(trigger.textContent.trim(), '', 'trigger carries no visible text');
  assert.equal(panel.querySelector('.agent-toolbar-more').tagName, 'DIV');
  assert.ok(panel.querySelector('[data-role="plan-toggle"]'), 'wide toolbar keeps Plan reachable');
  assert.ok(panel.querySelector('[data-role="update"]'), 'wide toolbar keeps Update reachable');
});

test('toolbar More closes on an outside pointer press and Escape', async () => {
  const { app, panel } = await fixture();
  await settle(app, panel, stateEvent());
  await toolbarMore(panel);
  const menu = panel.querySelector('.agent-toolbar-more');
  const trigger = menu.querySelector('.agent-toolbar-more-trigger');

  await act(async () => trigger.click());
  assert.equal(menu.dataset.open, 'true');
  assert.equal(trigger.getAttribute('aria-expanded'), 'true');
  await act(async () => {
    document.body.dispatchEvent(new window.MouseEvent('pointerdown', { bubbles: true }));
  });
  assert.equal(menu.dataset.open, 'false', 'outside pointer press closes the menu');

  await act(async () => trigger.click());
  await act(async () => {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    flushAgentAnimationFrames();
  });
  assert.equal(menu.dataset.open, 'false', 'Escape closes the menu');
  assert.equal(document.activeElement, trigger, 'Escape restores focus to the trigger');
});

test('toolbar More session label hides without a session id and shows it once set', async () => {
  const { app, panel } = await fixture();
  await settle(app, panel, stateEvent({ sessionId: '' }));
  await toolbarMore(panel);
  assert.equal(panel.querySelector('.agent-toolbar-more-session small'), null, 'no session id renders no session label');

  await settle(app, panel, stateEvent());
  const small = panel.querySelector('.agent-toolbar-more-session small');
  assert.ok(small, 'session label renders once a session id exists');
  assert.equal(small.textContent, 'session: abcdef123456');
});
