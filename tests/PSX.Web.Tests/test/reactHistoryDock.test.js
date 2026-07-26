// reactHistoryDock.test.js — the React history-dock content island (Group B).
//
// React owns the children of data-role="history-content" in react mode; the
// controller keeps the dock frame, top bar, resizer, persistence and the
// fold/expand interaction state. DOM equivalence is asserted against the
// legacy renderer for the states and the grouped list, plus the full
// open-thread flow in react mode.

import { test } from 'node:test';
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

// Warm the module cache so the island's dynamic import resolves quickly and
// deterministically inside act scopes.
await appModule('history/historyIsland.js');

function thread(overrides) {
  const provider = overrides?.provider ?? overrides?.providerKey ?? 'claude-code';
  return {
    threadId: 't',
    title: 'Chat',
    cwd: 'D:/proj',
    updatedAt: '2026-07-20 10:00:00Z',
    sessionId: '',
    provider,
    providerKey: provider,
    ...overrides
  };
}

const THREADS = [
  thread({ threadId: 't1', title: 'Fix the build', updatedAt: '2026-07-20 10:00:00Z' }),
  thread({ threadId: 't2', title: 'Refactor storage', updatedAt: '2026-07-19 10:00:00Z' }),
  thread({ threadId: 't3', title: 'Write docs', cwd: 'D:/other', updatedAt: '2026-07-18 10:00:00Z' })
];

async function mountDock(uiMode) {
  const runtime = installAgentRuntime();
  installBreakpoint(true);
  document.body.innerHTML = `<div id="agents"></div>${agentTemplateMarkup()}`;
  window.localStorage.setItem('psx.agent.historyDockOpen', '1');
  if (uiMode === 'react') window.localStorage.removeItem('psx.agent.experimental.react');
  const { createAgentApp } = await appModule('entry.js');
  const app = createAgentApp({
    terminalManager: { setViewVisible() {} },
    container: document.getElementById('agents'),
    template: document.getElementById('agent-workspace-template')
  });
  createAgentWorkspace(app, WS);
  return {
    app,
    posted: runtime.postedMessages,
    content: () => document.querySelector('[data-role="history-content"]')
  };
}

async function actAndSettle(run, predicate) {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 100 && !predicate(); i++) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 5));
    });
  }
  assert.ok(predicate(), 'react history content did not settle in time');
}

// Snapshot of every DOM fact the legacy content renderer writes.
function contentSnapshot(content) {
  const stateBox = content.querySelector('.agent-history-state');
  return {
    state: stateBox
      ? { className: stateBox.className, text: stateBox.textContent, role: stateBox.getAttribute('role') }
      : null,
    openError: content.querySelector('.agent-history-open-error')?.querySelector('p')?.textContent ?? null,
    groups: [...content.querySelectorAll('.agent-history-group')].map((group) => ({
      folded: group.dataset.folded,
      headerExpanded: group.querySelector('.agent-history-group-header').getAttribute('aria-expanded'),
      name: group.querySelector('.agent-history-group-label strong').textContent,
      path: group.querySelector('.agent-history-group-label small')?.textContent ?? null,
      count: group.querySelector('.agent-history-group-count').textContent,
      hasActiveDot: !!group.querySelector('.agent-history-group-active'),
      threadsHidden: group.querySelector('.agent-history-group-threads').hidden,
      folderSvgs: group.querySelectorAll('.agent-history-group-folder svg').length,
      rows: [...group.querySelectorAll('.agent-history-item')].map((row) => ({
        threadId: row.dataset.threadId,
        ariaCurrent: row.getAttribute('aria-current'),
        ariaLabel: row.getAttribute('aria-label'),
        title: row.getAttribute('title'),
        heading: row.querySelector('strong').textContent,
        badge:
          row.querySelector('.agent-history-current')?.textContent ??
          row.querySelector('.agent-history-open')?.textContent ??
          null,
        meta: row.querySelector('small').textContent
      })),
      showMore: group.querySelector('.agent-history-show-more')?.textContent ?? null
    }))
  };
}

test('React history content renders DOM equivalent to legacy across states and the list', async () => {
  const legacySnapshots = [];
  {
    const { app, content } = await mountDock('legacy');
    legacySnapshots.push(contentSnapshot(content())); // auto-load pending: loading state
    app.handle({ type: 'agent_threads', workspaceId: WS, threads: THREADS });
    legacySnapshots.push(contentSnapshot(content())); // grouped list
    // A response is only accepted while a request is in flight: refresh first.
    document.querySelector('[data-role="history-refresh"]').click();
    app.handle({ type: 'agent_threads', workspaceId: WS, threads: [] });
    legacySnapshots.push(contentSnapshot(content())); // empty after load
  }

  const reactSnapshots = [];
  {
    const { app, content } = await mountDock('react');
    await actAndSettle(() => {}, () => content().querySelector('.agent-history-state'));
    reactSnapshots.push(contentSnapshot(content()));
    await actAndSettle(
      () => app.handle({ type: 'agent_threads', workspaceId: WS, threads: THREADS }),
      () => content().querySelector('.agent-history-group')
    );
    reactSnapshots.push(contentSnapshot(content()));
    await actAndSettle(
      () => {
        document.querySelector('[data-role="history-refresh"]').click();
        app.handle({ type: 'agent_threads', workspaceId: WS, threads: [] });
      },
      () => content().querySelector('.agent-history-empty')
    );
    reactSnapshots.push(contentSnapshot(content()));
  }

  assert.deepEqual(reactSnapshots, legacySnapshots);
});

test('react mode: clicking a thread row posts the same load_thread command as legacy', async () => {
  let legacyCommand;
  {
    const { app, content, posted } = await mountDock('legacy');
    app.handle({ type: 'agent_threads', workspaceId: WS, threads: THREADS });
    content().querySelector('.agent-history-item[data-thread-id="t2"]').click();
    legacyCommand = posted.at(-1);
    assert.equal(legacyCommand.command, 'load_thread');
  }

  const { app, content, posted } = await mountDock('react');
  await actAndSettle(
    () => app.handle({ type: 'agent_threads', workspaceId: WS, threads: THREADS }),
    () => content().querySelector('.agent-history-group')
  );
  await act(async () => {
    content().querySelector('.agent-history-item[data-thread-id="t2"]').click();
  });
  assert.deepEqual(posted.at(-1), legacyCommand, 'react posts the byte-identical load_thread command');
});

test('react mode: fold, show-more and search filtering drive the React list', async () => {
  const manyThreads = Array.from({ length: 7 }, (_, i) =>
    thread({ threadId: 'm' + i, title: 'Task ' + i, updatedAt: '2026-07-1' + ((9 - i) % 10) + ' 10:00:00Z' })
  );
  const { app, content } = await mountDock('react');
  await actAndSettle(
    () => app.handle({ type: 'agent_threads', workspaceId: WS, threads: manyThreads }),
    () => content().querySelector('.agent-history-group')
  );

  // Preview limit: 5 rows + a show-more button.
  assert.equal(content().querySelectorAll('.agent-history-item').length, 5);
  await act(async () => {
    content().querySelector('.agent-history-show-more').click();
  });
  assert.equal(content().querySelectorAll('.agent-history-item').length, 7);
  assert.equal(content().querySelector('.agent-history-show-more'), null);

  // Folding hides the group's thread container.
  await act(async () => {
    content().querySelector('.agent-history-group-header').click();
  });
  const group = content().querySelector('.agent-history-group');
  assert.equal(group.dataset.folded, 'true');
  assert.equal(group.querySelector('.agent-history-group-threads').hidden, true);

  // Searching forces the group open and filters rows.
  const search = document.querySelector('[data-role="history-search"]');
  search.value = 'Task 3';
  await act(async () => {
    search.dispatchEvent(new window.Event('input', { bubbles: true }));
  });
  const openGroup = content().querySelector('.agent-history-group');
  assert.equal(openGroup.dataset.folded, 'false', 'searching forces groups open');
  assert.equal(content().querySelectorAll('.agent-history-item').length, 1);
  assert.equal(content().querySelector('.agent-history-item strong').textContent, 'Task 3');

  // No match: the empty state replaces the list.
  search.value = 'zzz';
  await act(async () => {
    search.dispatchEvent(new window.Event('input', { bubbles: true }));
  });
  assert.match(content().querySelector('.agent-history-state').textContent, /No threads match/);
});

test('react mode: the open-thread error notice retries and dismisses', async () => {
  const { app, content } = await mountDock('react');
  await actAndSettle(
    () => app.handle({ type: 'agent_threads', workspaceId: WS, threads: THREADS }),
    () => content().querySelector('.agent-history-group')
  );
  await act(async () => {
    app.handle({ type: 'agent_thread_open_error', workspaceId: WS, threadId: 't1', text: 'Disk error' });
  });
  const notice = content().querySelector('.agent-history-open-error');
  assert.ok(notice, 'open error notice rendered');
  assert.equal(notice.querySelector('p').textContent, 'Disk error');
  await act(async () => {
    notice.querySelector('.agent-history-dismiss').click();
  });
  assert.equal(content().querySelector('.agent-history-open-error'), null);
});
