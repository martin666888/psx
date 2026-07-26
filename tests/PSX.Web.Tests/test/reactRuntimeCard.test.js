// reactRuntimeCard.test.js — the flag-gated React runtime-card island.
//
// React 19 act contract: IS_REACT_ACT_ENVIRONMENT is enabled and every event
// that renders, updates or unmounts the island runs inside `await act(...)`.
// The tests import the same root-workspace `react` package the compiled
// agent-app modules resolve (development build, where act exists), so exactly
// one React instance is exercised.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;

const WS = '33333333-3333-4333-8333-333333333333';
const FLAG = 'psx.agent.experimental.react';

// One representative runtime_status payload per runtime state.
const RUNTIME_EVENTS = [
  { state: 'missing', message: 'Agent runtime is not installed.', canInstall: true, canCancel: false },
  { state: 'installing', message: 'Downloading', canInstall: false, canCancel: true },
  { state: 'failed', message: 'npm exited with code 1', canInstall: true, canCancel: false },
  { state: 'cancelled', message: 'Installation cancelled.', canInstall: true, canCancel: false },
  { state: 'ready', message: 'Ready', canInstall: false, canCancel: false }
].map((runtime) => ({ type: 'runtime_status', workspaceId: WS, ...runtime }));

function setFlag(enabled) {
  // The harness jsdom owns localStorage; the flag reader goes through
  // globalThis, mirroring how the compiled app sees the WebView2 global.
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: window.localStorage
  });
  if (enabled) window.localStorage.setItem(FLAG, '1');
  else window.localStorage.removeItem(FLAG);
}

// Snapshot of every DOM fact renderRuntime writes, taken from whichever
// element currently owns data-role="runtime-card". All child lookups are
// scoped to that card: the retired legacy section keeps its inner
// runtime-title/message/button nodes, which would win a panel-wide query.
function cardSnapshot(panel) {
  const card = panel.querySelector('[data-role="runtime-card"]');
  const role = (name) => card.querySelector('[data-role="' + name + '"]');
  const install = role('runtime-install');
  const cancel = role('runtime-cancel');
  return {
    hidden: card.hidden,
    state: card.dataset.state,
    ariaBusy: card.getAttribute('aria-busy'),
    ariaLive: card.getAttribute('aria-live'),
    className: card.className,
    title: role('runtime-title').textContent,
    message: role('runtime-message').textContent,
    note: card.querySelector('.agent-runtime-note').textContent.replace(/\s+/g, ' ').trim(),
    install: { hidden: install.hidden, disabled: install.disabled, text: install.textContent },
    cancel: { hidden: cancel.hidden, disabled: cancel.disabled, text: cancel.textContent }
  };
}

const reactHost = (panel) => panel.querySelector('.agent-runtime-react-host');

// Deliver the first runtime_status inside act: wait (still inside the act
// scope) until the dynamic island import resolved and called root.render —
// observable through the host node the controller creates synchronously
// before rendering. React then flushes the commit when the act scope exits,
// so the legacy swap is done right after this returns. Waiting for the commit
// itself inside act would deadlock: act only flushes its queue on exit.
async function actFirstRuntimeEvent(app, panel, event) {
  await act(async () => {
    app.handle(event);
    for (let i = 0; i < 400 && !reactHost(panel); i++) {
      await new Promise((resolve) => setTimeout(resolve, 5));
    }
  });
  assert.ok(reactHost(panel), 'island did not mount in time');
  assert.ok(panel.querySelector('[data-role="runtime-card-legacy"]'), 'island commit did not retire the legacy card');
}

async function mountWithFlag(enabled) {
  const { app, panelFor, runtime } = await mountAgentApp();
  setFlag(enabled);
  createAgentWorkspace(app, WS, { ready: false });
  return { app, panel: panelFor(WS), posted: runtime.postedMessages };
}

test('React runtime card renders DOM equivalent to the legacy card for all five states', async () => {
  const legacySnapshots = [];
  {
    const { app, panel } = await mountWithFlag(false);
    for (const event of RUNTIME_EVENTS) {
      app.handle(event);
      legacySnapshots.push(cardSnapshot(panel));
    }
    assert.equal(reactHost(panel), null, 'flag-off must never create the React host');
  }

  const reactSnapshots = [];
  {
    const { app, panel } = await mountWithFlag(true);
    await actFirstRuntimeEvent(app, panel, RUNTIME_EVENTS[0]);
    reactSnapshots.push(cardSnapshot(panel));
    for (const event of RUNTIME_EVENTS.slice(1)) {
      await act(async () => {
        app.handle(event);
      });
      reactSnapshots.push(cardSnapshot(panel));
    }
    await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
  }

  assert.deepEqual(reactSnapshots, legacySnapshots);
});

test('React install and cancel buttons post the same bridge commands as legacy', async () => {
  const { app, panel, posted } = await mountWithFlag(true);
  await actFirstRuntimeEvent(app, panel, RUNTIME_EVENTS[0]); // missing: install visible
  // Scope clicks to the React-owned card; the retired legacy card keeps its
  // (hidden) buttons in the document.
  const button = (name) => panel.querySelector('[data-role="runtime-card"] [data-role="' + name + '"]');

  await act(async () => {
    button('runtime-install').click();
  });
  assert.deepEqual(posted.at(-1), {
    type: 'agent_command', workspaceId: WS, command: 'install_runtime', value: '', requestId: ''
  });

  await act(async () => {
    app.handle(RUNTIME_EVENTS[1]); // installing: cancel visible
  });
  await act(async () => {
    button('runtime-cancel').click();
  });
  assert.deepEqual(posted.at(-1), {
    type: 'agent_command', workspaceId: WS, command: 'cancel_runtime_install', value: '', requestId: ''
  });

  // Guard parity: install is ignored while installing.
  const before = posted.length;
  await act(async () => {
    button('runtime-install').click();
  });
  assert.equal(posted.length, before);

  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
});

test('flag-on keeps the untouched legacy card until the first runtime_status', async () => {
  const { app, panel } = await mountWithFlag(true);
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 25));
  });
  assert.equal(reactHost(panel), null);
  const card = panel.querySelector('[data-role="runtime-card"]');
  assert.equal(card.classList.contains('agent-runtime-card'), true);
  assert.equal(card.hidden, true); // template default
  assert.equal(panel.querySelector('[data-role="runtime-card-legacy"]'), null);
  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
});

test('closing the workspace while the island import is in flight leaves no residue', async () => {
  const { app } = await mountWithFlag(true);
  await act(async () => {
    app.handle(RUNTIME_EVENTS[1]);
    // Close synchronously, before the dynamic import can resolve.
    app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
    await new Promise((resolve) => setTimeout(resolve, 50));
  });
  assert.equal(document.querySelector('.agent-runtime-react-host'), null);
});

test('the swap keeps data-role="runtime-card" unique and unmount removes the island', async () => {
  const { app, panel } = await mountWithFlag(true);
  await actFirstRuntimeEvent(app, panel, RUNTIME_EVENTS[1]);

  const cards = panel.querySelectorAll('[data-role="runtime-card"]');
  assert.equal(cards.length, 1);
  assert.equal(cards[0].parentElement.className, 'agent-runtime-react-host');
  const legacy = panel.querySelector('[data-role="runtime-card-legacy"]');
  assert.equal(legacy.hidden, true);

  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
  assert.equal(document.querySelector('.agent-runtime-react-host'), null);
  assert.equal(document.querySelector('[data-role="runtime-card"]'), null);
});
