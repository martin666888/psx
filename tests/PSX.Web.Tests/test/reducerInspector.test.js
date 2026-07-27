import { test } from 'vitest';
import assert from 'node:assert/strict';
import { appModule } from './agentHarness.js';

// Phase 4 checkpoint 2: the inspector plan slice is folded by the pure reducer
// so PlanController can render straight from state. These tests pin that
// folding independently of the DOM. History rows no longer live in workspace
// state — they are global (AgentHistoryStore); see agentHistoryBroker.test.js.

const { reduceWorkspaceState } = await appModule('core/reducer.js');
const { createInitialWorkspaceState } = await appModule('contracts/workspace-state.js');

const WS = 'ws-inspector';

function workspaceEvent(raw) {
  return { scope: 'agent-workspace', type: raw.type, workspaceId: WS, raw };
}

test('reducer: plan_update with entries activates the plan and normalizes rows', () => {
  let state = createInitialWorkspaceState(WS);
  assert.equal(state.inspector.plan.active, false);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'plan_update',
    runId: 'run-7',
    entries: [
      { content: '  Build feature  ', status: 'in_progress', priority: 'high' },
      { content: '', status: 'pending' },
      { content: 'Write tests', status: 'completed' }
    ]
  }));
  assert.equal(state.inspector.plan.active, true);
  assert.equal(state.inspector.plan.runId, 'run-7');
  assert.deepEqual(state.inspector.plan.entries, [
    { content: 'Build feature', status: 'in_progress', priority: 'high' },
    { content: 'Write tests', status: 'completed', priority: '' }
  ]);
});

test('reducer: plan_update with empty entries and a raw plan payload keeps the current plan', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'plan_update',
    runId: 'run-1',
    entries: [{ content: 'Keep me', status: 'pending' }]
  }));
  const active = state.inspector;

  // An empty raw plan JSON payload must NOT wipe the active plan (legacy guard).
  const rawPayload = JSON.stringify({ sessionUpdate: 'plan', entries: [] });
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'plan_update',
    runId: 'run-1',
    entries: [],
    text: rawPayload
  }));
  assert.equal(state.inspector, active, 'unchanged plan returns the same slice ref');
});

test('reducer: plan_update with plain text falls back to a text-only active plan', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'plan_update',
    runId: 'run-2',
    entries: [],
    text: 'Freeform plan text'
  }));
  assert.equal(state.inspector.plan.active, true);
  assert.deepEqual(state.inspector.plan.entries, []);
  assert.equal(state.inspector.plan.fallbackText, 'Freeform plan text');
});

test('reducer: agent_cleared resets an active plan and is a no-op when already empty', () => {
  let state = createInitialWorkspaceState(WS);
  const empty = state.inspector.plan;
  // No-op path returns the identical state ref.
  const same = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_cleared' }));
  assert.equal(same, state);

  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'plan_update',
    entries: [{ content: 'Active', status: 'pending' }]
  }));
  assert.equal(state.inspector.plan.active, true);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_cleared' }));
  assert.equal(state.inspector.plan.active, false);
  assert.deepEqual(state.inspector.plan, empty);
});

test('reducer: agent_thread_loaded folds plan-role messages, last one wins', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_thread_loaded',
    clear: true,
    threadId: 'thread-42',
    cwd: '/proj',
    sessionId: 'sess-42',
    messages: [
      { role: 'user', text: 'hello' },
      { role: 'plan', runId: 'r1', planEntries: [{ content: 'First', status: 'pending' }] },
      { role: 'assistant', text: 'working' },
      { role: 'plan', runId: 'r2', planEntries: [{ content: 'Final', status: 'in_progress' }] }
    ]
  }));
  assert.equal(state.session.currentThreadId, 'thread-42');
  assert.equal(state.inspector.plan.active, true);
  assert.equal(state.inspector.plan.runId, 'r2');
  assert.deepEqual(state.inspector.plan.entries, [
    { content: 'Final', status: 'in_progress', priority: '' }
  ]);
});

test('reducer: agent_thread_loaded without plan messages leaves the plan slice untouched', () => {
  let state = createInitialWorkspaceState(WS);
  const before = state.inspector;
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_thread_loaded',
    threadId: 'thread-9',
    messages: [{ role: 'user', text: 'hi' }]
  }));
  assert.equal(state.inspector, before, 'plan slice ref is preserved when no plan messages');
});
