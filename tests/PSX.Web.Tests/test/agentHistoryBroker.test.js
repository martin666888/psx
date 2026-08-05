import { test } from 'vitest';
import assert from 'node:assert/strict';
import { appModule } from './agentHarness.js';

// Global History: the AgentHistoryStore owns the normalized list + provider
// catalog, the AgentHistoryRequestBroker owns channel selection, the request
// status machine, timeout/retry, invalidation coalescing and requestId guards.
// Every history command carries a requestId; only the matching reply resolves.

const {
  AgentHistoryStore,
  normalizeHistoryThreads,
  normalizeProviderCatalog
} = await appModule('history/AgentHistoryStore.js');
const { AgentHistoryRequestBroker } = await appModule('history/AgentHistoryRequestBroker.js');

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// Non-timeout tests get a generous timeout so a slow (coverage-instrumented)
// run never trips the in-flight retry mid-test; the timeout/retry tests pass
// their own tight timeoutMs explicitly.
function makeRig({ timeoutMs = 5000 } = {}) {
  const store = new AgentHistoryStore();
  const commands = [];
  const host = {
    alive: new Set(),
    active: '',
    isAlive(id) {
      return this.alive.has(id);
    },
    activeAgentWorkspace() {
      return this.active;
    },
    bridgeFor(id) {
      return {
        sendAgentMessage() {},
        uploadAgentAttachment() {},
        sendAgentCommand(command, value, requestId) {
          commands.push({ workspaceId: id, command, value, requestId });
        },
        sendAgentPermissionResponse() {},
        sendAgentQuestionResponse() {},
        sendAgentElicitationResponse() {}
      };
    }
  };
  const broker = new AgentHistoryRequestBroker(host, store, { timeoutMs });
  const notices = [];
  store.subscribe((state, notice) => notices.push(notice));
  return { store, broker, host, commands, notices };
}

// Mirror what the registry does on agent_workspace_created.
function addWorkspace(rig, id) {
  rig.host.alive.add(id);
  rig.broker.registerWorkspace(id);
}

// Mirror agent_workspace_closed.
function removeWorkspace(rig, id) {
  rig.host.alive.delete(id);
  rig.broker.unregisterWorkspace(id);
}

function historyCommands(commands) {
  return commands.filter((entry) => entry.command === 'history');
}

function latestHistoryRequest(commands) {
  return historyCommands(commands).at(-1);
}

function assertHistoryRequestId(requestId) {
  assert.match(requestId, /^h-[0-9a-f-]{36}-\d+$/);
}

function threadsReply(rig, workspaceId, payload) {
  const requestId = latestHistoryRequest(rig.commands)?.requestId ?? '';
  rig.broker.handleThreads(workspaceId, { ...payload, requestId });
}

function historyErrorReply(rig, workspaceId, payload) {
  const requestId = latestHistoryRequest(rig.commands)?.requestId ?? '';
  rig.broker.handleHistoryError(workspaceId, { ...payload, requestId });
}

// --- store normalization -----------------------------------------------------

test('history store: agent_threads normalization keeps the payload provider as providerKey', () => {
  const threads = normalizeHistoryThreads([
    { threadId: 't1', title: 'Chat', cwd: '/a', updatedAt: 'yesterday', sessionId: 's1', provider: 'claude-code' }
  ]);
  assert.deepEqual(threads, [
    { threadId: 't1', title: 'Chat', cwd: '/a', updatedAt: 'yesterday', sessionId: 's1', providerKey: 'claude-code' }
  ]);
});

test('history store: agent_threads normalization falls back to unknown when provider is missing, blank or not a string', () => {
  const threads = normalizeHistoryThreads([
    { threadId: 't1' },
    { threadId: 't2', provider: '' },
    { threadId: 't3', provider: '   ' },
    { threadId: 't4', provider: 42 }
  ]);
  assert.deepEqual(
    threads.map((thread) => thread.providerKey),
    ['unknown', 'unknown', 'unknown', 'unknown']
  );
});

test('history store: agent_providers payload folds into the global provider catalog', () => {
  const { store } = makeRig();
  store.applyProviders(normalizeProviderCatalog([
    { key: 'claude-code', displayName: 'Claude Code', assistantName: 'Claude', isDefault: true, iconKey: 'claude' },
    { key: 'kimi-code', displayName: 'Kimi Code', assistantName: 'Kimi', isDefault: false, iconKey: '  ' },
    { displayName: 'keyless entry is dropped' }
  ]));
  assert.deepEqual(store.getState().providers, [
    { key: 'claude-code', displayName: 'Claude Code', assistantName: 'Claude', isDefault: true, iconKey: 'claude' },
    // Blank/missing icon keys normalize to the generic 'agent' mark.
    { key: 'kimi-code', displayName: 'Kimi Code', assistantName: 'Kimi', isDefault: false, iconKey: 'agent' }
  ]);
});

// --- channel selection ---------------------------------------------------------

test('broker: prefers the originating workspace as the request channel', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');
  addWorkspace(rig, 'b');
  rig.host.active = 'a';

  rig.broker.requestRefresh('b');
  const sent = latestHistoryRequest(rig.commands);
  assert.equal(sent.command, 'history');
  assertHistoryRequestId(sent.requestId);

  threadsReply(rig, 'b', { threads: [] });
  assert.equal(rig.store.getState().status, 'idle');
  assert.equal(rig.notices.at(-1).kind, 'threads');
});

test('broker: falls back to the active agent workspace, then the most recently activated one', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');
  addWorkspace(rig, 'b');
  rig.host.active = 'a';

  rig.broker.requestRefresh('');
  assert.equal(historyCommands(rig.commands).at(-1).workspaceId, 'a');

  // Without an active agent workspace the most recently activated wins.
  threadsReply(rig, 'a', { threads: [] });
  rig.host.active = '';
  rig.broker.activateWorkspace('a');
  rig.broker.activateWorkspace('b');
  rig.broker.requestRefresh('');
  assert.equal(historyCommands(rig.commands).at(-1).workspaceId, 'b');
});

test('broker: any live workspace can carry the request when none is active', () => {
  const rig = makeRig();
  // Channel selection never consults session state: busy and transcript-only
  // workspaces stay valid channels, so the broker only tracks liveness.
  addWorkspace(rig, 'only');
  rig.broker.requestRefresh('');
  const [sent] = historyCommands(rig.commands);
  assert.deepEqual(
    { workspaceId: sent.workspaceId, command: sent.command, value: sent.value },
    { workspaceId: 'only', command: 'history', value: undefined }
  );
  assertHistoryRequestId(sent.requestId);
});

test('broker: without a live workspace the request is unavailable and keeps the cached list', () => {
  const rig = makeRig();
  rig.store.applyThreads(
    [{ threadId: 'cached', title: 'Cached', cwd: '', updatedAt: '', sessionId: '', providerKey: 'unknown' }]
  );

  rig.broker.requestRefresh('');
  assert.equal(rig.commands.length, 0);
  const state = rig.store.getState();
  assert.equal(state.status, 'unavailable');
  assert.equal(state.dirty, true);
  assert.equal(state.threads.length, 1);
});

// --- carrier loss + dirty retry -------------------------------------------------

test('broker: retries on another channel when the carrier closes mid-request', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');
  addWorkspace(rig, 'b');

  rig.broker.requestRefresh('a');
  assert.equal(historyCommands(rig.commands).at(-1).workspaceId, 'a');

  removeWorkspace(rig, 'a');
  const retried = historyCommands(rig.commands);
  assert.equal(retried.length, 2);
  assert.equal(retried[1].workspaceId, 'b');

  // A late response from the closed carrier is dropped; the new channel lands.
  rig.broker.handleThreads('a', { threads: [{ threadId: 'stale' }], requestId: 'h0' });
  assert.equal(rig.store.getState().threads.length, 0);
  threadsReply(rig, 'b', { threads: [{ threadId: 'fresh' }] });
  const state = rig.store.getState();
  assert.equal(state.status, 'idle');
  assert.equal(state.dirty, false);
  assert.equal(state.threads[0].threadId, 'fresh');
});

test('broker: goes unavailable when the last carrier closes and retries dirty on workspace creation', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');
  rig.broker.requestRefresh('a');
  removeWorkspace(rig, 'a');

  const down = rig.store.getState();
  assert.equal(down.status, 'unavailable');
  assert.equal(down.dirty, true);
  assert.equal(historyCommands(rig.commands).length, 1);

  addWorkspace(rig, 'b');
  const retried = historyCommands(rig.commands);
  assert.equal(retried.length, 2);
  assert.equal(retried[1].workspaceId, 'b');
});

// --- invalidation coalescing ------------------------------------------------------

test('broker: coalesces a multi-workspace invalidation broadcast into one refresh', async () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');
  addWorkspace(rig, 'b');

  // C# broadcasts one agent_history_invalidated per workspace.
  rig.broker.handleInvalidated('a');
  rig.broker.handleInvalidated('b');
  await sleep(5);
  assert.equal(historyCommands(rig.commands).length, 1);
});

test('broker: ignores invalidation from a closing or closed workspace', async () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');
  addWorkspace(rig, 'b');

  removeWorkspace(rig, 'a');
  rig.broker.handleInvalidated('a');
  await sleep(5);
  assert.equal(historyCommands(rig.commands).length, 0, 'a dead sender never triggers a refresh');

  rig.broker.handleInvalidated('b');
  await sleep(5);
  assert.equal(historyCommands(rig.commands).length, 1, 'a live sender refreshes normally');
});

test('broker: queues at most one follow-up when invalidated mid-request', async () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');

  rig.broker.requestRefresh('a');
  assert.equal(historyCommands(rig.commands).length, 1);

  rig.broker.handleInvalidated('a');
  rig.broker.handleInvalidated('a');
  await sleep(5);
  assert.equal(historyCommands(rig.commands).length, 1, 'no parallel request while in flight');

  threadsReply(rig, 'a', { threads: [] });
  assert.equal(historyCommands(rig.commands).length, 2, 'one follow-up after the landing');

  rig.broker.handleInvalidated('a');
  await sleep(5);
  threadsReply(rig, 'a', { threads: [] });
  assert.equal(historyCommands(rig.commands).length, 3, 'still one follow-up per landing');
});

// --- timeout + retry ----------------------------------------------------------------

test('broker: times out, retries once, then enters the error state', async () => {
  const rig = makeRig({ timeoutMs: 30 });
  addWorkspace(rig, 'a');

  rig.broker.requestRefresh('a');
  await sleep(50);
  assert.equal(historyCommands(rig.commands).length, 2, 'one retry after the first timeout');

  await sleep(50);
  const failed = rig.store.getState();
  assert.equal(failed.status, 'error');
  assert.match(failed.errorText, /timed out/);
  assert.equal(failed.dirty, false);

  // Manual retry is still possible from the error state.
  rig.broker.requestRefresh('a');
  assert.equal(historyCommands(rig.commands).length, 3);
  threadsReply(rig, 'a', { threads: [] });
  assert.equal(rig.store.getState().status, 'idle');
});

test('broker: prefers a different channel for the timeout retry', async () => {
  const rig = makeRig({ timeoutMs: 30 });
  addWorkspace(rig, 'a');
  addWorkspace(rig, 'b');

  rig.broker.requestRefresh('a');
  await sleep(50);
  const retried = historyCommands(rig.commands);
  assert.equal(retried.length, 2);
  assert.equal(retried[1].workspaceId, 'b');
});

// --- list preservation + response guards --------------------------------------------

test('broker: refresh keeps the cached list while refreshing', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');

  rig.store.applyThreads([{ threadId: 't1', title: 'One', cwd: '', updatedAt: '', sessionId: '', providerKey: 'unknown' }]);
  assert.equal(rig.store.getState().threads.length, 1);

  rig.broker.requestRefresh('a');
  const refreshing = rig.store.getState();
  assert.equal(refreshing.status, 'refreshing');
  assert.equal(refreshing.threads[0].threadId, 't1');

  threadsReply(rig, 'a', { threads: [{ threadId: 't2', title: 'Two' }] });
  assert.equal(rig.store.getState().threads[0].threadId, 't2');
});

test('broker: history command includes requestId and matching reply applies threads', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');

  rig.broker.requestRefresh('a');
  const sent = latestHistoryRequest(rig.commands);
  assertHistoryRequestId(sent.requestId);

  threadsReply(rig, 'a', { threads: [{ threadId: 't1', title: 'One' }] });
  assert.equal(rig.store.getState().threads[0].threadId, 't1');
  assert.equal(rig.store.getState().status, 'idle');
});

test('broker: request IDs stay unique when the broker is recreated', () => {
  const first = makeRig();
  const second = makeRig();
  addWorkspace(first, 'a');
  addWorkspace(second, 'a');

  first.broker.requestRefresh('a');
  second.broker.requestRefresh('a');

  const firstId = latestHistoryRequest(first.commands).requestId;
  const secondId = latestHistoryRequest(second.commands).requestId;
  assertHistoryRequestId(firstId);
  assertHistoryRequestId(secondId);
  assert.notEqual(firstId, secondId);
});

test('broker: drops a stale requestId even when workspaceId matches', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');

  rig.broker.requestRefresh('a');
  const current = latestHistoryRequest(rig.commands).requestId;

  rig.broker.handleThreads('a', {
    threads: [{ threadId: 'stale' }],
    requestId: 'h0'
  });
  assert.equal(rig.store.getState().status, 'initial-loading');
  assert.equal(rig.store.getState().threads.length, 0);

  rig.broker.handleThreads('a', {
    threads: [{ threadId: 'wrong' }],
    requestId: current + '-superseded'
  });
  assert.equal(rig.store.getState().threads.length, 0);

  rig.broker.handleThreads('a', {
    threads: [{ threadId: 'good' }],
    requestId: current
  });
  assert.equal(rig.store.getState().threads[0].threadId, 'good');
});

test('broker: drops late responses from a stale channel while a request is in flight', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');
  addWorkspace(rig, 'b');

  rig.broker.requestRefresh('a');
  rig.broker.handleThreads('b', { threads: [{ threadId: 'stale' }], requestId: 'h0' });
  assert.equal(rig.store.getState().status, 'initial-loading');
  assert.equal(rig.store.getState().threads.length, 0);

  threadsReply(rig, 'a', { threads: [{ threadId: 'good' }] });
  assert.equal(rig.store.getState().threads[0].threadId, 'good');
});

test('broker: drops a response that arrives without an in-flight request', () => {
  const rig = makeRig();
  addWorkspace(rig, 'a');

  // Every `history` command is broker-initiated, so a spontaneous response
  // (stale, duplicated or misrouted) must not clobber the current state.
  rig.broker.handleThreads('a', { threads: [{ threadId: 't1' }] });
  const state = rig.store.getState();
  assert.equal(state.status, 'idle');
  assert.equal(state.loaded, false);
  assert.equal(state.threads.length, 0);

  rig.broker.handleHistoryError('a', { text: 'boom' });
  assert.equal(rig.store.getState().status, 'idle');
});
