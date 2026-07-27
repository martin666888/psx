// reactTimelineApp.test.js — wired React timeline and workspace integration.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { act } from 'react';
import {
  mountAgentApp,
  createAgentWorkspace,
  modeTransitionEvent,
  appModule
} from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;
const WS = '44444444-4444-4444-8444-444444444444';
await appModule('timeline/timelineIsland.js');

async function settle(run, predicate) {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 60 && !predicate(); i++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(predicate(), 'timeline island did not settle');
}

async function fixture(workspaceId = WS) {
  const mounted = await mountAgentApp();
  createAgentWorkspace(mounted.app, workspaceId);
  return { ...mounted, panel: mounted.panelFor(workspaceId) };
}

test('a streaming turn reaches the wired React timeline', async () => {
  const { app, panel } = await fixture();
  const thread = panel.querySelector('[data-role="thread"]');
  await settle(
    () => {
      app.handle({ type: 'user_message', workspaceId: WS, text: 'question' });
      app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'answer' });
      app.handle({ type: 'run_finished', workspaceId: WS });
    },
    () => !!thread.querySelector('.agent-message-assistant')
  );
  assert.match(thread.querySelector('.agent-message-user').textContent, /question/);
  assert.equal(thread.querySelector('.agent-message-assistant .agent-message-body').dataset.raw, 'answer');
  assert.equal(thread.dataset.islandState, 'mounted');
});

test('permission response uses the unchanged bridge payload', async () => {
  const { app, panel, runtime } = await fixture();
  const thread = panel.querySelector('[data-role="thread"]');
  await settle(
    () => app.handle({
      type: 'permission_request',
      workspaceId: WS,
      requestId: 'p1',
      title: 'Run tool?',
      options: [
        { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
        { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
      ]
    }),
    () => !!thread.querySelector('[data-option-id="allow"]')
  );
  await act(async () => thread.querySelector('[data-option-id="allow"]').click());
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: 'p1',
    value: 'allow'
  });
  assert.equal(thread.querySelector('[data-request-id="p1"]').dataset.decisionState, 'disabled');
});

test('mode transition owns the composer prompt and restores it after resolution', async () => {
  const { app, panel, runtime } = await fixture();
  await settle(
    () => app.handle(modeTransitionEvent({ workspaceId: WS })),
    () => !panel.querySelector('[data-role="mode-transition-prompt"]').hidden
  );
  const prompt = panel.querySelector('[data-role="mode-transition-prompt"]');
  assert.equal(panel.querySelector('[data-role="input-row"]').hidden, true);
  prompt.querySelector('[data-option-id="approve"]').click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: 'request-1',
    value: 'approve'
  });
  await act(async () => {
    app.handle({
      type: 'permission_resolved',
      workspaceId: WS,
      requestId: 'request-1',
      optionId: 'approve',
      optionName: 'Approve'
    });
  });
  assert.equal(prompt.hidden, true);
  assert.equal(panel.querySelector('[data-role="input-row"]').hidden, false);
});

test('workspace switching preserves independent React roots and state', async () => {
  const OTHER = '99999999-9999-4999-8999-999999999999';
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  createAgentWorkspace(app, OTHER);
  const first = panelFor(WS);
  const second = panelFor(OTHER);
  await settle(
    () => {
      app.handle({ type: 'user_message', workspaceId: WS, text: 'first thread' });
      app.handle({ type: 'user_message', workspaceId: OTHER, text: 'second thread' });
    },
    () => !!first.querySelector('.agent-message-user') && !!second.querySelector('.agent-message-user')
  );
  const firstNode = first.querySelector('.agent-message-user');
  await act(async () => app.handle({ type: 'workspace_activated', workspaceId: OTHER, kind: 'agent' }));
  assert.equal(first.hidden, true);
  await act(async () => app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'hidden update' }));
  await act(async () => app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' }));
  assert.equal(first.querySelector('.agent-message-user'), firstNode);
  assert.match(first.textContent, /hidden update/);
  assert.match(second.textContent, /second thread/);
});

test('closing mid-stream unmounts cleanly and ignores late events', async () => {
  const { app, panel } = await fixture();
  await settle(
    () => {
      app.handle({ type: 'user_message', workspaceId: WS, text: 'question' });
      app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'partial' });
    },
    () => !!panel.querySelector('.agent-message-assistant')
  );
  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
  assert.equal(document.querySelector(`[data-workspace-id="${WS}"]`), null);
  await act(async () => app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'late' }));
});
