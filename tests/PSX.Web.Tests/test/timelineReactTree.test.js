// timelineReactTree.test.js — Station 3 React Timeline tree equivalence
// (Group B, unwired). The tree is not routed yet: these tests drive the
// TimelineProjection + TimelineView directly and assert structural DOM
// equivalence against the legacy TimelineController/DecisionController
// output for the same event stream.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, appModule } from './agentHarness.js';
import { threadSnapshot } from './domSnapshot.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;

const WS = '66666666-6666-4666-8666-666666666666';
const NAME = 'Agent'; // createInitialWorkspaceState default identity

const { TimelineProjection } = await appModule('timeline/timelineViewModel.js');
const { mountTimelineIsland } = await appModule('timeline/timelineIsland.js');

async function legacyThread(events) {
  const { app, panelFor } = await mountAgentApp(); // legacy pinned
  createAgentWorkspace(app, WS);
  for (const [type, raw] of events) {
    app.handle({ type, workspaceId: WS, ...raw });
  }
  return threadSnapshot(panelFor(WS).querySelector('[data-role="thread"]'));
}

const CALLBACKS = {
  copyText: async () => true,
  onOpenTerminal: () => {},
  onDecisionOption: () => {},
  createAttachmentTile: () => document.createElement('div')
};

async function reactThread(events) {
  const projection = new TimelineProjection();
  for (const [type, raw] of events) {
    projection.apply(type, raw ?? {}, NAME);
  }
  const host = document.createElement('div');
  document.body.appendChild(host);
  const island = mountTimelineIsland(host);
  await act(async () => {
    island.render({ rows: projection.snapshot().rows, assistantName: NAME, callbacks: CALLBACKS });
  });
  const snapshot = threadSnapshot(host);
  await act(async () => {
    island.dispose();
  });
  host.remove();
  return snapshot;
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

test('React timeline renders a full streaming turn structurally equivalent to legacy', async () => {
  const legacy = await legacyThread(STREAMING);
  const react = await reactThread(STREAMING);
  assert.deepEqual(react, legacy);
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

test('React timeline renders a history replay structurally equivalent to legacy', async () => {
  const legacy = await legacyThread(REPLAY);
  const react = await reactThread(REPLAY);
  assert.deepEqual(react, legacy);
});

test('React timeline renders the empty-thread ready row and agent_cleared like legacy', async () => {
  const events = [
    ['agent_thread_loaded', { clear: true, messages: [] }],
    ['agent_cleared', {}]
  ];
  const legacy = await legacyThread(events);
  const react = await reactThread(events);
  assert.deepEqual(react, legacy);
});

test('React timeline renders run_failed error surfaces like legacy', async () => {
  const events = [
    ['user_message', { text: 'go' }],
    ['tool_started', { runId: 'r1', toolCallId: 't1', name: 'Bash', input: 'x' }],
    ['run_failed', { text: 'boom' }]
  ];
  const legacy = await legacyThread(events);
  const react = await reactThread(events);
  assert.deepEqual(react, legacy);
});

test('React permission/question cards match legacy for active, resolved and cancelled states', async () => {
  const events = [
    ['user_message', { text: 'q' }],
    ['permission_request', { requestId: 'p1', title: 'Run tool?', text: '{"cmd":"ls"}', options: [
      { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
      { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
    ] }],
    ['question_request', { requestId: 'q1' }],
    ['permission_resolved', { requestId: 'p1', optionId: 'allow', optionName: 'Allow' }],
    ['permission_cancelled', { requestId: 'q1', text: 'Too late.' }]
  ];
  const legacy = await legacyThread(events);
  const react = await reactThread(events);
  assert.deepEqual(react, legacy);
});

const ELICITATION = [
  ['user_message', { text: 'q' }],
  [
    'elicitation_request',
    {
      requestId: 'e1',
      message: 'Pick your options.',
      schema: {
        properties: {
          choice: { type: 'string', title: 'Choice', enum: ['a', 'b'] },
          count: { type: 'integer', title: 'Count', default: 3 },
          flags: { type: 'array', title: 'Flags', items: { enum: ['x', 'y'] }, default: ['x'] },
          ok: { type: 'boolean', title: 'OK', default: true },
          reason: { type: 'string', title: 'Reason' }
        },
        required: ['choice', 'reason']
      }
    }
  ],
  ['elicitation_request', { requestId: 'e2', mode: 'url', url: 'https://example.com/verify' }]
];

test('React elicitation forms render structurally equivalent to legacy', async () => {
  const legacy = await legacyThread(ELICITATION);
  const react = await reactThread(ELICITATION);
  assert.deepEqual(react, legacy);
});

test('React elicitation validation errors match legacy on an empty required submit', async () => {
  const events = ELICITATION.slice(0, 2);
  let legacy;
  {
    const { app, panelFor } = await mountAgentApp();
    createAgentWorkspace(app, WS);
    for (const [type, raw] of events) app.handle({ type, workspaceId: WS, ...raw });
    const thread = panelFor(WS).querySelector('[data-role="thread"]');
    // Continue with the required 'reason' textarea empty: the error renders,
    // nothing is posted (validation fails before the bridge call).
    [...thread.querySelectorAll('button')].find((b) => b.textContent === 'Continue').click();
    legacy = threadSnapshot(thread);
  }

  const projection = new TimelineProjection();
  for (const [type, raw] of events) projection.apply(type, raw ?? {}, NAME);
  const host = document.createElement('div');
  document.body.appendChild(host);
  const island = mountTimelineIsland(host);
  await act(async () => {
    island.render({ rows: projection.snapshot().rows, assistantName: NAME, callbacks: CALLBACKS });
  });
  await act(async () => {
    [...host.querySelectorAll('button')].find((b) => b.textContent === 'Continue').click();
  });
  const react = threadSnapshot(host);
  await act(async () => {
    island.dispose();
  });
  host.remove();
  assert.deepEqual(react, legacy);
});

test('assistant delta re-renders keep node identity (no remount churn)', async () => {
  const projection = new TimelineProjection();
  projection.apply('user_message', { text: 'q' }, NAME);
  projection.apply('assistant_delta', { text: 'a' }, NAME);
  const host = document.createElement('div');
  document.body.appendChild(host);
  const island = mountTimelineIsland(host);
  const render = async () => {
    await act(async () => {
      island.render({ rows: projection.snapshot().rows, assistantName: NAME, callbacks: CALLBACKS });
    });
  };
  await render();
  const body = host.querySelector('.agent-message-assistant .agent-message-body');
  assert.ok(body);
  projection.apply('assistant_delta', { text: 'b' }, NAME);
  await render();
  assert.equal(host.querySelector('.agent-message-assistant .agent-message-body'), body);
  assert.equal(body.dataset.raw, 'ab');
  await act(async () => {
    island.dispose();
  });
  host.remove();
});
