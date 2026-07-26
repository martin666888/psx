// reactUiMode.test.js — the React-by-default UI mode switch and the island
// fallback path (test matrix Group C).
//
// On this branch React rendering is the default; the localStorage key
// psx.agent.experimental.react is an emergency fallback ('0'/'false'/'off').
// Group C proves that a failing island import permanently falls back to the
// legacy renderer without breaking the app, via the interceptor seam in
// core/islandHost.js.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace, appModule } from './agentHarness.js';

const WS = '55555555-5555-4555-8555-555555555555';

function runtimeEvent(overrides = {}) {
  return {
    type: 'runtime_status',
    workspaceId: WS,
    state: 'missing',
    message: 'Agent runtime is not installed.',
    canInstall: true,
    canCancel: false,
    ...overrides
  };
}

test('isReactUiEnabled defaults to React and honors the emergency fallback values', async () => {
  const { isReactUiEnabled, reactUiMode } = await appModule('core/flags.js');
  const withStorage = (getItem, run) => {
    Object.defineProperty(globalThis, 'localStorage', { configurable: true, value: { getItem } });
    try {
      return run();
    } finally {
      delete globalThis.localStorage;
    }
  };

  withStorage(() => null, () => assert.equal(isReactUiEnabled(), true));
  withStorage(() => '1', () => assert.equal(isReactUiEnabled(), true));
  withStorage(() => '0', () => assert.equal(isReactUiEnabled(), false));
  withStorage(() => 'false', () => assert.equal(isReactUiEnabled(), false));
  withStorage(() => 'off', () => assert.equal(reactUiMode(), 'legacy'));
  withStorage(() => {
    throw new Error('blocked storage');
  }, () => assert.equal(reactUiMode(), 'react'));
  // No localStorage at all (removed by withStorage cleanup): default React.
  assert.equal(isReactUiEnabled(), true);
});

test('startup surfaces the UI mode on the console and the body dataset', async () => {
  const infos = [];
  const originalInfo = console.info;
  console.info = (...args) => {
    infos.push(args.join(' '));
  };
  try {
    await mountAgentApp({ uiMode: 'react' });
    assert.equal(document.body.dataset.agentUiMode, 'react');
    assert.ok(infos.some((line) => line === '[agent] UI mode: React'));

    await mountAgentApp({ uiMode: 'legacy' });
    assert.equal(document.body.dataset.agentUiMode, 'legacy');
    assert.ok(infos.some((line) => line === '[agent] UI mode: Legacy'));
  } finally {
    console.info = originalInfo;
  }
});

test('a failing island import falls back to the legacy card permanently', async () => {
  const { app, panelFor } = await mountAgentApp({ uiMode: 'react' });
  const { setIslandLoadInterceptorForTests } = await appModule('core/islandHost.js');
  const attempts = [];
  const warnings = [];
  const originalWarn = console.warn;
  console.warn = (...args) => {
    warnings.push(args[0]);
  };
  try {
    setIslandLoadInterceptorForTests((name) => {
      attempts.push(name);
      return Promise.reject(new Error('simulated island load failure'));
    });
    createAgentWorkspace(app, WS, { ready: false });
    app.handle(runtimeEvent());
    await new Promise((resolve) => setTimeout(resolve, 25));

    const panel = panelFor(WS);
    const card = panel.querySelector('[data-role="runtime-card"]');
    assert.equal(card.classList.contains('agent-runtime-card'), true, 'legacy card must own the role');
    assert.equal(card.hidden, false);
    assert.equal(card.dataset.state, 'missing');
    assert.equal(panel.querySelector('.agent-runtime-react-host'), null, 'no island host after failure');
    // Other islands (composer actions, …) load at mount and fail here too;
    // this test pins the runtime-card path specifically.
    assert.deepEqual(attempts.filter((name) => name === 'runtime-card'), ['runtime-card']);
    const runtimeWarnings = () => warnings.filter((w) => /island "runtime-card"/.test(String(w)));
    assert.equal(runtimeWarnings().length, 1);
    assert.match(String(runtimeWarnings()[0]), /island "runtime-card" failed to load/);

    // Later events render legacy without retrying the import, and the app
    // stays fully usable.
    app.handle(runtimeEvent({ state: 'installing', message: 'Downloading', canInstall: false, canCancel: true }));
    assert.equal(card.dataset.state, 'installing');
    assert.equal(card.getAttribute('aria-busy'), 'true');
    assert.deepEqual(attempts.filter((name) => name === 'runtime-card'), ['runtime-card']);
    assert.equal(runtimeWarnings().length, 1);
  } finally {
    console.warn = originalWarn;
    setIslandLoadInterceptorForTests(null);
  }
});

test('a slow failing import falls back with the latest buffered props', async () => {
  const { app, panelFor } = await mountAgentApp({ uiMode: 'react' });
  const { setIslandLoadInterceptorForTests } = await appModule('core/islandHost.js');
  const originalWarn = console.warn;
  console.warn = () => {};
  try {
    setIslandLoadInterceptorForTests(
      () =>
        new Promise((unusedResolve, reject) => {
          setTimeout(() => reject(new Error('slow failure')), 20);
        })
    );
    createAgentWorkspace(app, WS, { ready: false });
    app.handle(runtimeEvent()); // missing — starts the load
    app.handle(runtimeEvent({ state: 'failed', message: 'npm exited with code 1' })); // buffered
    await new Promise((resolve) => setTimeout(resolve, 60));

    const card = panelFor(WS).querySelector('[data-role="runtime-card"]');
    assert.equal(card.dataset.state, 'failed', 'fallback must render the latest buffered runtime state');
    assert.equal(card.querySelector('[data-role="runtime-message"]').textContent, 'npm exited with code 1');
  } finally {
    console.warn = originalWarn;
    setIslandLoadInterceptorForTests(null);
  }
});

test('closing the workspace during a failing import leaves the app clean', async () => {
  const { app } = await mountAgentApp({ uiMode: 'react' });
  const { setIslandLoadInterceptorForTests } = await appModule('core/islandHost.js');
  const warnings = [];
  const originalWarn = console.warn;
  console.warn = (...args) => {
    warnings.push(args[0]);
  };
  try {
    setIslandLoadInterceptorForTests(
      () =>
        new Promise((unusedResolve, reject) => {
          setTimeout(() => reject(new Error('slow failure')), 20);
        })
    );
    createAgentWorkspace(app, WS, { ready: false });
    app.handle(runtimeEvent());
    app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
    await new Promise((resolve) => setTimeout(resolve, 60));
    assert.equal(document.querySelector('.agent-runtime-react-host'), null);
    // The disposed loader must not invoke the legacy fallback of a dead panel.
    assert.equal(document.querySelector(`.agent-panel[data-workspace-id="${WS}"]`), null);
    assert.equal(
      warnings.filter((w) => /island "runtime-card"/.test(String(w))).length,
      1,
      'the load failure is still reported once'
    );
  } finally {
    console.warn = originalWarn;
    setIslandLoadInterceptorForTests(null);
  }
});
