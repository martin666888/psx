// reactTimelineApp.test.js — Station 3 application-level React timeline
// (Group B, wired). mountAgentApp({ uiMode: 'react' }) routes the whole
// thread subtree through the React island; these tests drive the same
// bridge event streams through the full app in both modes and assert the
// thread DOM stays structurally equivalent, decision clicks post
// byte-identical commands, and mid-stream teardown stays clean.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, appModule, modeTransitionEvent } from './agentHarness.js';
import { threadSnapshot } from './domSnapshot.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;

const WS = '77777777-7777-4777-8777-777777777777';

// Warm the module cache so the island's dynamic import resolves quickly and
// deterministically inside act scopes.
await appModule('timeline/timelineIsland.js');

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
  assert.ok(predicate(), 'react timeline did not settle in time');
}

function legacyThread(events) {
  return mountAgentApp().then(({ app, panelFor, runtime }) => {
    createAgentWorkspace(app, WS);
    for (const [type, raw] of events) app.handle({ type, workspaceId: WS, ...raw });
    return {
      app,
      runtime,
      thread: panelFor(WS).querySelector('[data-role="thread"]'),
      snapshot: threadSnapshot(panelFor(WS).querySelector('[data-role="thread"]'))
    };
  });
}

async function reactApp(events, settlePredicate) {
  const { app, panelFor, runtime } = await mountAgentApp({ uiMode: 'react' });
  // mount() preheats the island load; keep the pending-render flush inside act.
  await act(async () => {
    createAgentWorkspace(app, WS);
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  const thread = () => panelFor(WS).querySelector('[data-role="thread"]');
  await actAndSettle(
    () => app.handle({ type: events[0][0], workspaceId: WS, ...events[0][1] }),
    () => settlePredicate(thread())
  );
  for (const [type, raw] of events.slice(1)) {
    await act(async () => {
      app.handle({ type, workspaceId: WS, ...raw });
    });
  }
  return { app, runtime, thread };
}

// Unmount the React root through the app's own teardown and let the
// scheduler drain inside act, so no async React work leaks into the next
// test's document.
async function drain(app) {
  await act(async () => {
    app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
    await new Promise((resolve) => setTimeout(resolve, 25));
  });
}

const STREAMING = [
  ['user_message', { text: 'hello **md**' }],
  ['thinking_started', {}],
  ['thinking_delta', { text: 'pondering' }],
  ['thinking_finished', {}],
  ['tool_started', { runId: 'r1', toolCallId: 't1', name: 'Bash', summary: 'run ls', input: 'ls' }],
  ['tool_delta', { toolCallId: 't1', text: '\nout' }],
  ['tool_finished', { toolCallId: 't1', status: 'completed' }],
  ['assistant_delta', { text: 'Hi ' }],
  ['assistant_delta', { text: '**there**' }],
  ['run_finished', {}],
  ['command_result', { text: 'done' }]
];

test('react mode app: a full streaming turn renders the thread equivalent to legacy', async () => {
  const { snapshot: legacy } = await legacyThread(STREAMING);
  const { app, thread } = await reactApp(STREAMING, (t) => t.querySelector('.agent-message-user'));
  assert.deepEqual(threadSnapshot(thread()), legacy);
  await drain(app);
});

const REPLAY = [
  [
    'agent_thread_loaded',
    {
      clear: true,
      messages: [
        { role: 'user', text: 'hi' },
        { role: 'thinking', text: 'hmm' },
        { role: 'tool', runId: 'r1', toolCallId: 't1', summary: 'call 1', toolOutput: 'out', toolStatus: 'completed' },
        { role: 'tool', runId: 'r1', toolCallId: 't2', summary: 'call 2', text: 'second', toolStatus: 'failed' },
        { role: 'assistant', text: 'answer **bold**' },
        { role: 'system', text: 'note' }
      ]
    }
  ]
];

test('react mode app: a history replay renders the thread equivalent to legacy', async () => {
  const { snapshot: legacy } = await legacyThread(REPLAY);
  const { app, thread } = await reactApp(REPLAY, (t) => t.querySelector('.agent-message-user'));
  assert.deepEqual(threadSnapshot(thread()), legacy);
  await drain(app);
});

test('react mode app: agent_cleared resets the thread like legacy', async () => {
  const events = [...STREAMING, ['agent_cleared', {}]];
  const { snapshot: legacy } = await legacyThread(events);
  const { app, thread } = await reactApp(events, (t) => t.children.length > 0);
  assert.deepEqual(threadSnapshot(thread()), legacy);
  await drain(app);
});

const DECISION = [
  ['user_message', { text: 'q' }],
  ['permission_request', { requestId: 'p1', title: 'Run tool?', text: '{"cmd":"ls"}', options: [
    { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
    { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
  ] }]
];

test('react mode app: a permission click posts the byte-identical response and resolves like legacy', async () => {
  let legacyCommand;
  let legacyResolved;
  {
    const { app, runtime, thread } = await legacyThread(DECISION);
    [...thread.querySelectorAll('button')].find((b) => b.textContent.includes('Allow')).click();
    legacyCommand = runtime.postedMessages.at(-1);
    assert.deepEqual(legacyCommand, {
      type: 'agent_permission_response',
      workspaceId: WS,
      requestId: 'p1',
      value: 'allow'
    });
    app.handle({ type: 'permission_resolved', workspaceId: WS, requestId: 'p1', optionId: 'allow', optionName: 'Allow' });
    legacyResolved = threadSnapshot(thread);
  }

  const { app, runtime, thread } = await reactApp([DECISION[0]], (t) => t.querySelector('.agent-message-user'));
  await act(async () => {
    app.handle({ type: DECISION[1][0], workspaceId: WS, ...DECISION[1][1] });
  });
  assert.ok(thread().querySelector('[data-option-id="allow"]'), 'the react permission card rendered its options');
  await act(async () => {
    thread().querySelector('[data-option-id="allow"]').click();
  });
  assert.deepEqual(runtime.postedMessages.at(-1), legacyCommand, 'react posts the byte-identical permission response');
  await act(async () => {
    app.handle({ type: 'permission_resolved', workspaceId: WS, requestId: 'p1', optionId: 'allow', optionName: 'Allow' });
  });
  assert.deepEqual(threadSnapshot(thread()), legacyResolved);
  await drain(app);
});

test('react mode app: an active mode transition occupies the composer and resolves like legacy', async () => {
  let legacyCommand;
  let legacyResolved;
  {
    const { app, panelFor, runtime } = await mountAgentApp();
    createAgentWorkspace(app, WS);
    const panel = panelFor(WS);
    app.handle(modeTransitionEvent({ workspaceId: WS }));
    const prompt = panel.querySelector('[data-role="mode-transition-prompt"]');
    assert.equal(prompt.hidden, false);
    assert.equal(panel.querySelector('[data-role="input-row"]').hidden, true);
    [...prompt.querySelectorAll('button')].find((b) => b.dataset.optionId === 'approve').click();
    legacyCommand = runtime.postedMessages.at(-1);
    app.handle({ type: 'permission_resolved', workspaceId: WS, requestId: 'request-1', optionId: 'approve', optionName: 'Approve' });
    assert.equal(prompt.hidden, true);
    legacyResolved = threadSnapshot(panel.querySelector('[data-role="thread"]'));
  }

  const { app, panelFor, runtime } = await mountAgentApp({ uiMode: 'react' });
  await act(async () => {
    createAgentWorkspace(app, WS);
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  const panel = panelFor(WS);
  await act(async () => {
    app.handle(modeTransitionEvent({ workspaceId: WS }));
  });
  const prompt = panel.querySelector('[data-role="mode-transition-prompt"]');
  assert.equal(prompt.hidden, false, 'the composer prompt takes over in react mode too');
  assert.equal(panel.querySelector('[data-role="input-row"]').hidden, true);
  await act(async () => {
    [...prompt.querySelectorAll('button')].find((b) => b.dataset.optionId === 'approve').click();
  });
  assert.deepEqual(runtime.postedMessages.at(-1), legacyCommand, 'react posts the byte-identical mode transition response');
  await act(async () => {
    app.handle({ type: 'permission_resolved', workspaceId: WS, requestId: 'request-1', optionId: 'approve', optionName: 'Approve' });
  });
  assert.equal(prompt.hidden, true, 'the prompt clears once the transition resolves');
  assert.deepEqual(threadSnapshot(panel.querySelector('[data-role="thread"]')), legacyResolved);
  await drain(app);
});

test('react mode app: switching workspaces hides and restores each React thread intact', async () => {
  const OTHER = '99999999-9999-4999-8999-999999999999';
  const { app, panelFor } = await mountAgentApp({ uiMode: 'react' });
  await act(async () => {
    createAgentWorkspace(app, WS);
    createAgentWorkspace(app, OTHER);
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  await act(async () => {
    app.handle({ type: 'user_message', workspaceId: WS, text: 'first thread' });
    app.handle({ type: 'user_message', workspaceId: OTHER, text: 'second thread' });
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  const first = panelFor(WS);
  const second = panelFor(OTHER);
  const firstBody = () => first.querySelector('[data-role="thread"] .agent-message-body');
  assert.match(firstBody().textContent, /first thread/);
  assert.match(second.querySelector('[data-role="thread"] .agent-message-body').textContent, /second thread/);
  const firstNode = firstBody();

  await act(async () => {
    app.handle({ type: 'workspace_activated', workspaceId: OTHER, kind: 'agent' });
  });
  assert.equal(first.hidden, true, 'the inactive workspace hides');
  // Streaming continues into the hidden workspace's React tree.
  await act(async () => {
    app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'still streaming' });
  });
  await act(async () => {
    app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  });
  assert.equal(first.hidden, false, 'restore shows the workspace again');
  assert.equal(firstBody(), firstNode, 'hide/restore keeps the React node identity');
  assert.match(first.querySelector('[data-role="thread"]').textContent, /still streaming/);
  await act(async () => {
    app.handle({ type: 'agent_workspace_closed', workspaceId: OTHER });
    await new Promise((resolve) => setTimeout(resolve, 25));
  });
  await drain(app);
});

test('react mode app: closing the workspace mid-stream tears down cleanly', async () => {
  const midStream = STREAMING.slice(0, 6); // tool still running
  const { app, thread } = await reactApp(midStream, (t) => t.querySelector('.agent-message-user'));
  assert.ok(thread().querySelector('.agent-tool-card'));
  await act(async () => {
    app.handle({ type: 'agent_workspace_closed', workspaceId: WS });
  });
  assert.equal(document.querySelector(`.agent-panel[data-workspace-id="${WS}"]`), null);
  // Late events for the closed workspace must be ignored without throwing.
  await act(async () => {
    app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'late' });
    await new Promise((resolve) => setTimeout(resolve, 25));
  });
});

test('react mode app: the harness lifts the legacy pin so the shipped default applies', async () => {
  // The global dock islands also mount in react mode; keep their async
  // mount inside act.
  await act(async () => {
    await mountAgentApp({ uiMode: 'react' });
    await new Promise((resolve) => setTimeout(resolve, 25));
  });
  const { isReactUiEnabled } = await appModule('core/flags.js');
  assert.equal(isReactUiEnabled(), true);
});
