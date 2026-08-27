// reactTimelineApp.test.js — wired React timeline and workspace integration.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import {
  mountAgentApp,
  createAgentWorkspace,
  composerReady,
  modeTransitionEvent,
  appModule,
  flushAgentAnimationFrames
} from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = false;
const WS = '44444444-4444-4444-8444-444444444444';
await appModule('timeline/timelineIsland.js');

async function flushReact(callback) {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;
  try {
    await act(callback);
  } finally {
    globalThis.IS_REACT_ACT_ENVIRONMENT = false;
  }
}

async function settle(run, predicate) {
  await flushReact(async () => {
    run();
    flushAgentAnimationFrames();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 60 && !predicate(); i++) {
    await flushReact(async () => {
      flushAgentAnimationFrames();
      await new Promise((resolve) => setTimeout(resolve, 5));
    });
  }
  assert.ok(predicate(), 'timeline island did not settle');
}

async function fixture(workspaceId = WS) {
  const mounted = await mountAgentApp();
  createAgentWorkspace(mounted.app, workspaceId);
  return { ...mounted, panel: mounted.panelFor(workspaceId) };
}

test('empty conversation waits for runtime status and then follows its lifecycle', async () => {
  const mounted = await mountAgentApp();
  mounted.app.handle({
    type: 'agent_workspace_created',
    workspaceId: WS,
    agentName: 'Claude Code',
    assistantName: 'Claude'
  });
  const panel = mounted.panelFor(WS);
  const thread = () => panel.querySelector('[data-role="thread"]');
  const empty = () => thread()?.querySelector('.agent-conversation-empty');

  await settle(
    () => mounted.app.handle({
      type: 'agent_thread_loaded',
      workspaceId: WS,
      clear: true,
      messages: []
    }),
    () => !!thread()
  );
  assert.equal(empty(), null, 'the reducer default must not flash as authoritative missing state');

  await settle(
    () => mounted.app.handle({ type: 'runtime_status', workspaceId: WS, state: 'missing', agentName: 'Claude' }),
    () => empty()?.textContent.includes('需要 Agent 运行时') === true
  );
  assert.match(empty().textContent, /安装运行时后开始对话/);

  await settle(
    () => mounted.app.handle({ type: 'runtime_status', workspaceId: WS, state: 'installing', agentName: 'Claude' }),
    () => empty()?.textContent.includes('正在准备 Agent 运行时') === true
  );
  assert.match(empty().textContent, /安装完成后即可发送消息/);

  await settle(
    () => mounted.app.handle({ type: 'runtime_status', workspaceId: WS, state: 'failed', agentName: 'Claude' }),
    () => empty()?.textContent.includes('尚未就绪') === true
  );
  assert.match(empty().textContent, /上方运行时卡片重试安装/);

  await settle(
    () => mounted.app.handle({ type: 'runtime_status', workspaceId: WS, state: 'cancelled', agentName: 'Claude' }),
    () => empty()?.textContent.includes('尚未就绪') === true
  );
  assert.match(empty().textContent, /上方运行时卡片重试安装/);

  await settle(
    () => mounted.app.handle({ type: 'runtime_status', workspaceId: WS, state: 'ready', agentName: 'Claude' }),
    () => empty()?.textContent.includes('Claude 将从第一条消息开始处理') === true
  );
  assert.doesNotMatch(empty().textContent, /还没有消息/);
  assert.doesNotMatch(empty().textContent, /Start a conversation/);
});

test('a streaming turn reaches the wired React timeline', async () => {
  const { app, panel } = await fixture();
  // [data-role="thread"] is rendered by the React Conversation inside the
  // conversation-host island; look it up lazily until the island mounts.
  const thread = () => panel.querySelector('[data-role="thread"]');
  await settle(
    () => {
      app.handle({ type: 'user_message', workspaceId: WS, text: 'question' });
      app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'answer' });
      app.handle({ type: 'run_finished', workspaceId: WS });
    },
    () => !!thread()?.querySelector('.agent-message-assistant')
  );
  assert.match(thread().querySelector('.agent-message-user').textContent, /question/);
  assert.equal(thread().querySelector('.agent-message-assistant .agent-message-body').dataset.raw, 'answer');
  assert.equal(panel.querySelector('[data-role="conversation-host"]').dataset.islandState, 'mounted');
});

test('permission response uses the unchanged bridge payload', async () => {
  const { app, panel, runtime } = await fixture();
  const thread = () => panel.querySelector('[data-role="thread"]');
  await settle(
    () => app.handle({
      type: 'permission_request',
      workspaceId: WS,
      requestId: 'p1',
      title: 'Run tool?',
      description: 'Run the listed Bash command once.',
      options: [
        { optionId: 'allow', name: 'Allow', kind: 'allow_once' },
        { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
      ]
    }),
    () => !!thread()?.querySelector('[data-option-id="allow"]')
  );
  const permissionGroup = thread().querySelector('.agent-decision-option-pills');
  assert.ok(permissionGroup.classList.contains('flex-wrap'));
  assert.ok(permissionGroup.querySelector('[data-option-id="allow"]').classList.contains('rounded-full'));
  assert.match(
    thread().querySelector('[data-request-id="p1"]').textContent,
    /Run the listed Bash command once/
  );
  assert.equal(
    thread().querySelector('[data-request-id="p1"] .agent-decision-raw-input'),
    null,
    'ordinary permission without explicit rawInput hides Raw Input'
  );
  await flushReact(async () => thread().querySelector('[data-option-id="allow"]').click());
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: 'p1',
    value: 'allow'
  });
  const permissionCard = thread().querySelector('[data-request-id="p1"]');
  assert.equal(permissionCard.dataset.decisionState, 'disabled');
  assert.equal(permissionCard.querySelectorAll('[data-option-id]').length, 1);
});

test('reused ACP requestId only folds the live permission card', async () => {
  const { app, panel, runtime } = await fixture();
  const thread = () => panel.querySelector('[data-role="thread"]');
  await settle(
    () => {
      app.handle({
        type: 'agent_thread_loaded',
        workspaceId: WS,
        clear: true,
        messages: [
          {
            role: 'document_permission',
            requestId: '2',
            name: 'Old approval',
            text: '# historical plan',
            decisionOptions: [
              { optionId: 'approve_always', name: 'Approve for this session', kind: 'allow_always' }
            ],
            selectedOptionId: 'approve_always',
            decisionState: 'selected'
          }
        ]
      });
      app.handle({
        type: 'permission_request',
        workspaceId: WS,
        requestId: '2',
        title: 'Bash',
        description: 'ls -la "$HOME/.arkcli/"',
        options: [
          { optionId: 'approve_once', name: 'Approve once', kind: 'allow_once' },
          { optionId: 'approve_always', name: 'Approve for this session', kind: 'allow_always' },
          { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
        ]
      });
    },
    () => thread()?.querySelectorAll('[data-request-id="2"]').length === 2
      && !!thread()?.querySelector('.agent-decision-permission [data-option-id="approve_always"]')
  );

  const liveCard = thread().querySelector('.agent-decision-permission[data-request-id="2"]');
  const historicalCard = thread().querySelector('.agent-document-permission[data-request-id="2"]');
  assert.ok(liveCard);
  assert.ok(historicalCard);
  assert.equal(liveCard.dataset.decisionState, 'active');
  assert.equal(historicalCard.dataset.decisionState, 'disabled');

  await flushReact(async () =>
    liveCard.querySelector('[data-option-id="approve_always"]').click()
  );
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: '2',
    value: 'approve_always'
  });
  const foldedLive = thread().querySelector('.agent-decision-permission[data-request-id="2"]');
  const untouchedHistorical = thread().querySelector(
    '.agent-document-permission[data-request-id="2"]'
  );
  assert.equal(foldedLive.dataset.decisionState, 'disabled');
  assert.equal(foldedLive.dataset.state, 'closed', 'live card collapses after local select');
  assert.match(foldedLive.textContent, /已记录所选选项/);
  assert.equal(untouchedHistorical.dataset.decisionState, 'disabled');
});

test('mode transition card options approve directly from the timeline', async () => {
  const { app, panel, runtime } = await fixture();
  const thread = () => panel.querySelector('[data-role="thread"]');
  await settle(
    () => app.handle(modeTransitionEvent({ workspaceId: WS })),
    () => !!thread()?.querySelector('.agent-mode-transition [data-option-id="approve"]')
  );
  const card = thread().querySelector('.agent-mode-transition');
  const option = card.querySelector('[data-option-id="approve"]');
  assert.ok(card.querySelector('.agent-mode-transition-options').classList.contains('flex-wrap'));
  assert.ok(option.classList.contains('rounded-full'));
  assert.equal(option.classList.contains('agent-btn-allow'), false);
  // Live approvals are clickable in the card itself, mirroring the composer
  // prompt (second entry point, same agent_permission_response payload).
  assert.equal(option.disabled, false);
  await flushReact(async () => option.click());
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: 'request-1',
    value: 'approve'
  });
  assert.equal(card.dataset.decisionState, 'disabled');
  assert.equal(card.querySelector('[data-option-id="approve"]').disabled, true);
});

test('mode transition owns the composer prompt and restores it after resolution', async () => {
  const { app, panel, runtime } = await fixture();
  // The prompt controller is imperative and drops events whose composer shell
  // is not rendered yet; the ComposerView island (3-0) renders that shell
  // asynchronously, so wait for its first commit before raising the request.
  await composerReady(panel);
  await settle(
    () => app.handle(modeTransitionEvent({ workspaceId: WS })),
    () => panel.querySelector('[data-role="mode-transition-prompt"]')?.hidden === false
  );
  const prompt = panel.querySelector('[data-role="mode-transition-prompt"]');
  assert.equal(panel.querySelector('[data-role="input-row"]').hidden, true);
  const promptOptions = prompt.querySelector('.agent-composer-decision-options');
  assert.ok(promptOptions.classList.contains('flex-wrap'));
  assert.ok(promptOptions.querySelector('[data-option-id="approve"]').classList.contains('rounded-full'));
  const stopButton = prompt.querySelector('button[title^="停止 "]');
  assert.ok(stopButton.classList.contains('rounded-full'));
  assert.equal(stopButton.classList.contains('bg-destructive'), false);
  await flushReact(async () => prompt.querySelector('[data-option-id="approve"]').click());
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: 'request-1',
    value: 'approve'
  });
  await flushReact(async () => {
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

test('document permission renders Markdown in the timeline without opening the composer prompt', async () => {
  const { app, panel, runtime } = await fixture();
  await composerReady(panel);
  const thread = () => panel.querySelector('[data-role="thread"]');
  await settle(
    () => app.handle({
      type: 'permission_request',
      workspaceId: WS,
      presentation: 'document',
      requestId: 'kimi-plan-1',
      toolCallId: 'kimi-tool-1',
      title: 'ExitPlanMode',
      documentText: '# Kimi plan\n\n1. Review\n2. Implement',
      options: [{ optionId: 'approve', name: 'Approve', kind: 'allow_once' }]
    }),
    () => !!thread()?.querySelector('.agent-document-permission [data-option-id="approve"]')
  );
  const card = thread().querySelector('.agent-document-permission');
  assert.match(card.textContent, /Kimi plan/);
  assert.match(card.textContent, /Review/);
  assert.doesNotMatch(card.textContent, /"toolCallId"/);
  assert.equal(panel.querySelector('[data-role="mode-transition-prompt"]')?.hidden, true);
  assert.equal(panel.querySelector('[data-role="input-row"]').hidden, false);

  await flushReact(async () => card.querySelector('[data-option-id="approve"]').click());
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'agent_permission_response',
    workspaceId: WS,
    requestId: 'kimi-plan-1',
    value: 'approve'
  });
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
  await flushReact(async () =>
    app.handle({ type: 'workspace_activated', workspaceId: OTHER, kind: 'agent' })
  );
  assert.equal(first.hidden, true);
  await flushReact(async () => {
    app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'hidden update' });
    flushAgentAnimationFrames();
  });
  await flushReact(async () =>
    app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' })
  );
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
  await flushReact(async () =>
    app.handle({ type: 'agent_workspace_closed', workspaceId: WS })
  );
  assert.equal(document.querySelector(`[data-workspace-id="${WS}"]`), null);
  await flushReact(async () =>
    app.handle({ type: 'assistant_delta', workspaceId: WS, text: 'late' })
  );
});

test('hidden workspace defers timeline render until re-activated', async () => {
  const WS_B = '55555555-5555-4555-8555-555555555555';
  const { app, panelFor } = await fixture();
  createAgentWorkspace(app, WS_B);
  // Activating A leaves B hidden: its controller enters the throttled state.
  await flushReact(async () =>
    app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' })
  );
  const panelB = panelFor(WS_B);
  assert.equal(panelB.hidden, true);

  // Streaming keeps folding into the projection but must not reach the DOM.
  await flushReact(async () => {
    app.handle({ type: 'user_message', workspaceId: WS_B, text: 'hidden-question' });
    app.handle({ type: 'assistant_delta', workspaceId: WS_B, text: 'hidden-answer' });
    app.handle({ type: 'run_finished', workspaceId: WS_B });
    flushAgentAnimationFrames();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  assert.equal(
    panelB.querySelector('[data-role="conversation-host"]').textContent.includes('hidden-answer'),
    false,
    'a hidden workspace must defer its React render'
  );

  // Re-activating B projects the accumulated snapshot in one render.
  await settle(
    () => app.handle({ type: 'workspace_activated', workspaceId: WS_B, kind: 'agent' }),
    () =>
      panelB
        .querySelector('[data-role="thread"]')
        ?.textContent.includes('hidden-answer') ?? false
  );
  assert.match(panelB.querySelector('[data-role="thread"]').textContent, /hidden-question/);
});
