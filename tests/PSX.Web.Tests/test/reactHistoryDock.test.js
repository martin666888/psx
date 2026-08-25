// reactHistoryDock.test.js — React-only history content behavior.

import { afterEach, beforeEach, test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import {
  installAgentRuntime,
  installBreakpoint,
  agentTemplateMarkup,
  createAgentWorkspace,
  flushAgentAnimationFrames,
  registerAgentCleanup,
  appModule
} from './agentHarness.js';

beforeEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
});
afterEach(() => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = false;
});
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

function historyRequest(posted) {
  return [...posted].reverse().find(
    (message) => (message.type === 'agent_command' || message.type === 'agent_global_command')
      && message.command === 'history'
  );
}

function agentThreads(posted, threads) {
  const request = historyRequest(posted);
  return {
    type: 'agent_threads',
    ...(request?.type === 'agent_command' ? { workspaceId: WS } : {}),
    requestId: request?.requestId ?? '',
    threads
  };
}

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
  registerAgentCleanup(() => app.dispose());
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

function dispatchPointer(target, type, { clientX, pointerId = 1, button = 0 }) {
  const event = new window.MouseEvent(type, { bubbles: true, cancelable: true, clientX, button });
  Object.defineProperty(event, 'pointerId', { value: pointerId });
  target.dispatchEvent(event);
}

test('loading, grouped list and empty states render semantically', async () => {
  const { app, content, posted } = await fixture();
  await settle(() => {}, () => !!content().querySelector('.agent-history-state'));
  assert.match(content().textContent, /加载中/);
  await settle(
    () => app.handle(agentThreads(posted, THREADS)),
    () => !!content().querySelector('.agent-history-group')
  );
  assert.equal(content().querySelectorAll('.agent-history-group').length, 2);
  assert.equal(content().querySelectorAll('.agent-history-item').length, 3);
  const firstRow = content().querySelector('[data-thread-id="t1"]');
  assert.ok(firstRow.querySelector('.agent-history-title'), 'thread title owns the edge-fade treatment');
  assert.match(firstRow.title, /^Fix the build — /, 'the unclipped title remains available on hover');
  // The island host is the dock host element the portal renders into.
  assert.equal(document.querySelector('[data-role="history-dock-host"]').dataset.islandState, 'mounted');
  assert.ok(
    document.querySelector('[data-role="history-refresh"]').classList.contains('border-input'),
    'refresh button must use the soft border-input token like the search/select inputs'
  );

  await settle(
    () => {
      document.querySelector('[data-role="history-refresh"]').click();
      app.handle(agentThreads(posted, []));
    },
    () => !!content().querySelector('.agent-history-empty')
  );
  assert.match(content().textContent, /暂无已保存的 Agent 线程/);
});

test('clicking a row emits the exact load_thread bridge command', async () => {
  const { app, content, posted } = await fixture();
  await settle(
    () => app.handle(agentThreads(posted, THREADS)),
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

test('rows carry catalog-driven provider brand icons with the agent fallback', async () => {
  const { app, content, posted } = await fixture();
  await settle(
    () => {
      app.handle({
        type: 'agent_providers',
        providers: [
          { key: 'claude-code', displayName: 'Claude Code', assistantName: 'Claude', isDefault: true, iconKey: 'claude' },
          { key: 'kimi-code', displayName: 'Kimi Code', assistantName: 'Kimi', isDefault: false, iconKey: 'kimi' }
        ]
      });
      app.handle(agentThreads(posted, [
          thread({ threadId: 'c1', provider: 'claude-code', updatedAt: '2026-07-20 10:00:00Z' }),
          thread({ threadId: 'k1', provider: 'kimi-code', updatedAt: '2026-07-19 10:00:00Z' }),
          thread({ threadId: 'u1', provider: 'legacy-provider', updatedAt: '2026-07-18 10:00:00Z' })
        ]));
    },
    () => !!content().querySelector('[data-thread-id="u1"]')
  );

  const iconFor = (threadId) =>
    content().querySelector(`[data-thread-id="${threadId}"] .agent-history-provider-icon svg`);
  assert.equal(iconFor('c1').dataset.icon, 'claude', 'Claude thread renders the Claude mark');
  assert.equal(iconFor('k1').dataset.icon, 'kimi', 'Kimi thread renders the Kimi mark');
  assert.equal(iconFor('u1').dataset.icon, 'agent', 'unknown provider falls back to the generic mark');
  // Decorative slot: fixed size, hidden from the accessibility tree (the row
  // label already names the provider).
  for (const threadId of ['c1', 'k1', 'u1']) {
    const slot = content().querySelector(`[data-thread-id="${threadId}"] .agent-history-provider-icon`);
    assert.equal(slot.getAttribute('aria-hidden'), 'true');
    assert.ok(slot.classList.contains('shrink-0'), 'icon slot must not collapse under long titles');
  }
  // The icon column must not break row activation.
  await act(async () => content().querySelector('[data-thread-id="k1"]').click());
  assert.equal(posted.at(-1).command, 'load_thread');
  assert.equal(posted.at(-1).value, 'k1');
});

test('show-more, folding and search update the React list', async () => {
  const many = Array.from({ length: 7 }, (_, i) =>
    thread({ threadId: 'm' + i, title: 'Task ' + i, updatedAt: `2026-07-${19 - i} 10:00:00Z` })
  );
  const { app, content, posted } = await fixture();
  await settle(
    () => app.handle(agentThreads(posted, many)),
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
  const { app, content, posted } = await fixture();
  await settle(
    () => app.handle(agentThreads(posted, THREADS)),
    () => !!content().querySelector('.agent-history-group')
  );
  await act(async () => {
    app.handle({
      type: 'agent_thread_open_error',
      workspaceId: WS,
      threadId: 't1',
      code: 'history.open_failed',
      detail: 'Disk error'
    });
  });
  const notice = content().querySelector('.agent-history-open-error');
  assert.equal(notice.getAttribute('role'), 'alert');
  assert.match(notice.textContent, /Disk error/);
  assert.match(notice.textContent, /无法打开选定的 Agent 线程/);
  await act(async () => notice.querySelector('.agent-history-dismiss').click());
  assert.equal(content().querySelector('.agent-history-open-error'), null);
});

test('dock list sits above the resizer without a profile footer', async () => {
  await fixture();
  const dock = document.querySelector('[data-role="history-dock"]');
  assert.equal(dock.querySelector('[data-role="history-profile"]'), null);
  const content = dock.querySelector('[data-role="history-content"]');
  const resizer = dock.querySelector('[data-role="history-dock-resizer"]');
  assert.ok(content, 'history list renders');
  assert.ok(
    content.compareDocumentPosition(resizer) & Node.DOCUMENT_POSITION_FOLLOWING,
    'list precedes the resizer'
  );
});

test('dock resize batches live layout writes and derives width from the drag start', async () => {
  await fixture();
  const container = document.getElementById('agents');
  const dock = document.querySelector('[data-role="history-dock"]');
  const resizer = dock.querySelector('[data-role="history-dock-resizer"]');
  assert.equal(container.style.getPropertyValue('--agent-history-width'), '280px');

  // A drag must never measure geometry after live width writes begin. The
  // start coordinate + start width are sufficient and remain stable.
  dock.getBoundingClientRect = () => {
    throw new Error('resize hot path must not read dock layout');
  };

  await act(async () => {
    dispatchPointer(resizer, 'pointerdown', { clientX: 300, pointerId: 7 });
    dispatchPointer(resizer, 'pointermove', { clientX: 320, pointerId: 7 });
    dispatchPointer(resizer, 'pointermove', { clientX: 350, pointerId: 7 });
    dispatchPointer(resizer, 'pointermove', { clientX: 370, pointerId: 7 });
  });

  assert.equal(
    container.style.getPropertyValue('--agent-history-width'),
    '280px',
    'pointer events only queue the newest preview until the next frame'
  );

  await act(async () => flushAgentAnimationFrames());
  assert.equal(container.style.getPropertyValue('--agent-history-width'), '350px');
  assert.equal(resizer.getAttribute('aria-valuenow'), '350');

  await act(async () => {
    dispatchPointer(resizer, 'pointerup', { clientX: 370, pointerId: 7 });
  });
  assert.equal(window.localStorage.getItem('psx.agent.historyDockWidth'), '350');
  assert.equal(document.body.classList.contains('agent-history-dock-resizing'), false);
});

test('dock resize settles the latest preview when capture is lost or the window blurs', async () => {
  await fixture();
  const container = document.getElementById('agents');
  const resizer = document.querySelector('[data-role="history-dock-resizer"]');

  await act(async () => {
    dispatchPointer(resizer, 'pointerdown', { clientX: 300, pointerId: 7 });
    dispatchPointer(resizer, 'pointermove', { clientX: 360, pointerId: 7 });
    flushAgentAnimationFrames();
  });
  assert.equal(container.style.getPropertyValue('--agent-history-width'), '340px');
  assert.equal(document.body.classList.contains('agent-history-dock-resizing'), true);

  await act(async () => {
    dispatchPointer(resizer, 'lostpointercapture', { clientX: 360, pointerId: 7 });
  });
  assert.equal(window.localStorage.getItem('psx.agent.historyDockWidth'), '340');
  assert.equal(document.body.classList.contains('agent-history-dock-resizing'), false);

  // A second drag that loses the host window follows the same idempotent
  // settle path. A late pointerup from the interrupted stream is ignored.
  await act(async () => {
    dispatchPointer(resizer, 'pointerdown', { clientX: 340, pointerId: 8 });
    dispatchPointer(resizer, 'pointermove', { clientX: 380, pointerId: 8 });
    flushAgentAnimationFrames();
    window.dispatchEvent(new Event('blur'));
    dispatchPointer(resizer, 'pointerup', { clientX: 420, pointerId: 8 });
  });
  assert.equal(window.localStorage.getItem('psx.agent.historyDockWidth'), '380');
  assert.equal(resizer.getAttribute('aria-valuenow'), '380');
  assert.equal(document.body.classList.contains('agent-history-dock-resizing'), false);
});
