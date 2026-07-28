// reactHistoryDock.test.js — React-only history content behavior.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import {
  installAgentRuntime,
  installBreakpoint,
  agentTemplateMarkup,
  createAgentWorkspace,
  appModule
} from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;
const WS = '77777777-7777-4777-8777-777777777777';

const thread = (overrides) => ({
  threadId: 't',
  title: 'Chat',
  cwd: 'D:/proj',
  updatedAt: '2026-07-20 10:00:00Z',
  sessionId: '',
  provider: 'claude-code',
  providerKey: 'claude-code',
  ...overrides
});

const THREADS = [
  thread({ threadId: 't1', title: 'Fix the build', updatedAt: '2026-07-20 10:00:00Z' }),
  thread({ threadId: 't2', title: 'Refactor storage', updatedAt: '2026-07-19 10:00:00Z' }),
  thread({ threadId: 't3', title: 'Write docs', cwd: 'D:/other', updatedAt: '2026-07-18 10:00:00Z' })
];

async function fixture() {
  const runtime = installAgentRuntime();
  // Warm the island module only after the jsdom globals exist: react-dom
  // probes input-event support at first evaluation, and the controlled
  // search box needs the real input-event path (not the no-op polyfill).
  await appModule('history/historyIsland.js');
  installBreakpoint(true);
  document.body.innerHTML = `<div id="agents"></div>${agentTemplateMarkup()}`;
  window.localStorage.setItem('psx.agent.historyDockOpen', '1');
  const { createAgentApp } = await appModule('entry.js');
  const app = createAgentApp({
    terminalManager: { setViewVisible() {} },
    container: document.getElementById('agents'),
    template: document.getElementById('agent-workspace-template')
  });
  createAgentWorkspace(app, WS);
  const content = () => document.querySelector('[data-role="history-content"]');
  // The dock chrome renders through the history-dock React island; wait for
  // its first commit before tests start querying inside it.
  for (let i = 0; i < 100 && !content(); i++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(content(), 'history dock island did not mount');
  return {
    app,
    posted: runtime.postedMessages,
    content
  };
}

async function settle(run, predicate) {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 50 && !predicate(); i++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(predicate(), 'history island did not settle');
}

test('loading, grouped list and empty states render semantically', async () => {
  const { app, content } = await fixture();
  await settle(() => {}, () => !!content().querySelector('.agent-history-state'));
  assert.match(content().textContent, /Loading|No Agent workspace/);
  await settle(
    () => app.handle({ type: 'agent_threads', workspaceId: WS, threads: THREADS }),
    () => !!content().querySelector('.agent-history-group')
  );
  assert.equal(content().querySelectorAll('.agent-history-group').length, 2);
  assert.equal(content().querySelectorAll('.agent-history-item').length, 3);
  // The island host is the dock host element the portal renders into.
  assert.equal(document.querySelector('[data-role="history-dock-host"]').dataset.islandState, 'mounted');

  await settle(
    () => {
      document.querySelector('[data-role="history-refresh"]').click();
      app.handle({ type: 'agent_threads', workspaceId: WS, threads: [] });
    },
    () => !!content().querySelector('.agent-history-empty')
  );
  assert.match(content().textContent, /No saved Agent threads/);
});

test('clicking a row emits the exact load_thread bridge command', async () => {
  const { app, content, posted } = await fixture();
  await settle(
    () => app.handle({ type: 'agent_threads', workspaceId: WS, threads: THREADS }),
    () => !!content().querySelector('[data-thread-id="t2"]')
  );
  await act(async () => content().querySelector('[data-thread-id="t2"]').click());
  assert.deepEqual(posted.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'load_thread',
    value: 't2',
    requestId: ''
  });
});

test('show-more, folding and search update the React list', async () => {
  const many = Array.from({ length: 7 }, (_, i) =>
    thread({ threadId: 'm' + i, title: 'Task ' + i, updatedAt: `2026-07-${19 - i} 10:00:00Z` })
  );
  const { app, content } = await fixture();
  await settle(
    () => app.handle({ type: 'agent_threads', workspaceId: WS, threads: many }),
    () => !!content().querySelector('.agent-history-group')
  );
  assert.equal(content().querySelectorAll('.agent-history-item').length, 5);
  await act(async () => content().querySelector('.agent-history-show-more').click());
  assert.equal(content().querySelectorAll('.agent-history-item').length, 7);
  await act(async () => content().querySelector('.agent-history-group-header').click());
  assert.equal(content().querySelector('.agent-history-group').dataset.folded, 'true');

  // Drive the controlled search box the way a browser keystroke would: the
  // native prototype setter bypasses React's instance value tracker, so the
  // following input event registers as a real change (same pattern as
  // tools/screenshot-baseline.mjs).
  const search = document.querySelector('[data-role="history-search"]');
  const setValue = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
  await act(async () => {
    setValue.call(search, 'Task 3');
    search.dispatchEvent(new Event('input', { bubbles: true }));
  });
  assert.equal(content().querySelector('.agent-history-group').dataset.folded, 'false');
  assert.equal(content().querySelectorAll('.agent-history-item').length, 1);
  assert.match(content().textContent, /Task 3/);
});

test('thread-open error remains local and supports dismiss', async () => {
  const { app, content } = await fixture();
  await settle(
    () => app.handle({ type: 'agent_threads', workspaceId: WS, threads: THREADS }),
    () => !!content().querySelector('.agent-history-group')
  );
  await act(async () => {
    app.handle({
      type: 'agent_thread_open_error',
      workspaceId: WS,
      threadId: 't1',
      text: 'Disk error'
    });
  });
  const notice = content().querySelector('.agent-history-open-error');
  assert.equal(notice.getAttribute('role'), 'alert');
  assert.match(notice.textContent, /Disk error/);
  await act(async () => notice.querySelector('.agent-history-dismiss').click());
  assert.equal(content().querySelector('.agent-history-open-error'), null);
});
