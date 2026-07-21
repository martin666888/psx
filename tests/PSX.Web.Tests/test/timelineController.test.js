import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '22222222-2222-4222-8222-222222222222';

function stateEvent(workspaceId) {
  return {
    type: 'agent_state',
    workspaceId,
    status: 'ready',
    providerKey: 'claude-code',
    assistantName: 'Claude',
    agentName: 'Claude Code',
    cwd: '/tmp/project',
    sessionId: 'abcdef123456',
    threadId: 'thread-9',
    busy: false,
    isDraft: false,
    supportsImage: true,
    contextUsedTokens: 100
  };
}

// The timeline owns the single [data-role="thread"] node. Attachments tiles
// (composer seam) and historical mode-transition cards (decision seam) still
// land inside the thread, so a thread snapshot covers those cross-domain seams.
function threadHtml(panel) {
  return panel.querySelector('[data-role="thread"]').innerHTML;
}

// Drive the compiled Agent app the way production main.js does, then read the
// controller-owned thread node off the live panel.
async function drive(events) {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  app.handle(stateEvent(WS));
  for (const make of events) {
    app.handle(make(WS));
  }
  return threadHtml(panelFor(WS));
}

// Live-stream event factories mirror the public event surface.
const userMessage = (ws) => ({ type: 'user_message', workspaceId: ws, text: 'Hello **agent**' });
const thinkingStarted = (ws) => ({ type: 'thinking_started', workspaceId: ws });
const thinkingDelta = (ws) => ({ type: 'thinking_delta', workspaceId: ws, text: 'Considering options' });
const thinkingFinished = (ws) => ({ type: 'thinking_finished', workspaceId: ws });
const toolStarted = (ws) => ({ type: 'tool_started', workspaceId: ws, runId: 'run-1', toolCallId: 'tool-1', name: 'grep', input: 'pattern', summary: 'Search' });
const toolDelta = (ws) => ({ type: 'tool_delta', workspaceId: ws, toolCallId: 'tool-1', text: ' output' });
const toolFinished = (ws) => ({ type: 'tool_finished', workspaceId: ws, toolCallId: 'tool-1', status: 'done' });
const assistantDelta = (ws) => ({ type: 'assistant_delta', workspaceId: ws, text: '# Title\n\nBody text' });
const assistantDone = (ws) => ({ type: 'assistant_message_done', workspaceId: ws });
const runFinished = (ws) => ({ type: 'run_finished', workspaceId: ws });

test('TimelineController streams a full turn', async () => {
  const controlled = await drive([
    userMessage, thinkingStarted, thinkingDelta, thinkingFinished,
    toolStarted, toolDelta, toolFinished, assistantDelta, assistantDone, runFinished
  ]);

  assert.match(controlled, /agent-message-user/);
  assert.match(controlled, /agent-run-group/);
  assert.match(controlled, /agent-message-assistant/);
});

test('TimelineController renders a command_result system row', async () => {
  const controlled = await drive([
    (ws) => ({ type: 'command_result', workspaceId: ws, text: 'Command finished' })
  ]);

  assert.match(controlled, /Command finished/);
});

test('TimelineController renders a user message with attachment tiles', async () => {
  const controlled = await drive([
    (ws) => ({
      type: 'user_message',
      workspaceId: ws,
      text: 'See attached',
      attachments: [
        { clientId: 'c1', id: 'a1', fileName: 'diagram.png', mimeType: 'image/png', size: 2048, status: 'uploaded', url: 'blob:psx/att-1' }
      ]
    })
  ]);

  assert.match(controlled, /agent-message-attachments/);
  assert.match(controlled, /diagram\.png/);
});

test('TimelineController renders a failed run + vision hint', async () => {
  const controlled = await drive([
    userMessage, toolStarted,
    (ws) => ({ type: 'run_failed', workspaceId: ws, text: 'Something broke', visionContextHint: 'Image context was dropped.' })
  ]);

  assert.match(controlled, /Claude error/);
  assert.match(controlled, /Image context was dropped\./);
});

test('TimelineController renders a resume_failed recovery card', async () => {
  const controlled = await drive([
    (ws) => ({ type: 'resume_failed', workspaceId: ws, message: 'Could not resume', detail: 'network error' })
  ]);

  assert.match(controlled, /agent-recovery/);
  assert.match(controlled, /Could not resume/);
});

test('TimelineController replays a loaded thread', async () => {
  const controlled = await drive([
    (ws) => ({
      type: 'agent_thread_loaded',
      workspaceId: ws,
      clear: true,
      threadId: 'thread-9',
      cwd: '/tmp/project',
      sessionId: 'abcdef123456',
      messages: [
        { role: 'user', text: 'Hi there' },
        { role: 'thinking', text: 'Let me think' },
        { role: 'plan', runId: 'run-1', planEntries: [{ content: 'do it', status: 'pending' }], text: 'plan text' },
        { role: 'tool', runId: 'run-1', name: 'grep', text: 'found', toolCallId: 't1' },
        { role: 'assistant', text: 'All done' },
        { role: 'system', text: 'session note' }
      ]
    })
  ]);

  assert.match(controlled, /Hi there/);
  assert.match(controlled, /All done/);
});

test('TimelineController replays a historical mode transition', async () => {
  const controlled = await drive([
    (ws) => ({
      type: 'agent_thread_loaded',
      workspaceId: ws,
      clear: true,
      threadId: 'thread-9',
      messages: [
        { role: 'user', text: 'plan it' },
        {
          role: 'mode_transition',
          requestId: 'req-1',
          toolCallId: 'tool-1',
          name: 'Ready to code?',
          text: '# Plan\n\n1. step',
          decisionOptions: [
            { optionId: 'approve', name: 'Approve', kind: 'allow_once' },
            { optionId: 'reject', name: 'Reject', kind: 'reject_once' }
          ],
          selectedOptionId: 'approve',
          decisionState: 'resolved'
        }
      ]
    })
  ]);

  // The historical card crosses into the decision seam
  // (renderHistoricalModeTransition) yet is emitted inside the timeline thread.
  assert.match(controlled, /agent-mode-transition/);
});

test('TimelineController clears the thread on agent_cleared', async () => {
  const controlled = await drive([
    userMessage, assistantDelta, assistantDone,
    (ws) => ({ type: 'agent_cleared', workspaceId: ws })
  ]);

  assert.match(controlled, /Thread UI cleared/);
  assert.doesNotMatch(controlled, /Hello/);
});
