import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { repositoryRoot } from './agentHarness.js';

// The compiled ES modules live under wwwroot/js/agent-app (verify:agent keeps
// them in lockstep with frontend/agent/src). The repo path contains '#', so we
// must import through a properly encoded file URL rather than a raw specifier.
function appModule(relative) {
  const abs = path.join(repositoryRoot, 'wwwroot/js/agent-app', relative);
  return import(pathToFileURL(abs).href);
}

const { reduceWorkspaceState, seedIdentityFromCreation } = await appModule('core/reducer.js');
const { createInitialWorkspaceState } = await appModule('contracts/workspace-state.js');
const { HostEventDecoder } = await appModule('core/HostEventDecoder.js');
const { scopeOfType } = await appModule('contracts/host-events.js');
const { AgentWorkspaceStore } = await appModule('workspace/AgentWorkspaceStore.js');

const WS = 'ws-1';

function workspaceEvent(raw) {
  return { scope: 'agent-workspace', type: raw.type, workspaceId: WS, raw };
}

// --- reducer: provider identity merge priority -----------------------------

test('reducer: neutral Agent fallback when no identity is ever supplied', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_state', status: 'ready' }));
  assert.equal(state.identity.providerKey, '');
  assert.equal(state.identity.agentName, 'Agent');
  assert.equal(state.identity.assistantName, 'Agent');
});

test('reducer: agent_state resolves identity WITHOUT any agent_providers', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_state',
    status: 'ready',
    providerKey: 'acp-foo',
    agentName: 'Foo Code',
    assistantName: 'Foo'
  }));
  assert.equal(state.identity.providerKey, 'acp-foo');
  assert.equal(state.identity.agentName, 'Foo Code');
  assert.equal(state.identity.assistantName, 'Foo');
});

test('reducer: agent_state overrides the agent_workspace_created seed', () => {
  let state = seedIdentityFromCreation(createInitialWorkspaceState(WS), {
    type: 'agent_workspace_created',
    providerKey: 'acp-seed',
    agentName: 'Seed Code',
    assistantName: 'Seed'
  });
  assert.equal(state.identity.providerKey, 'acp-seed');

  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_state',
    status: 'ready',
    providerKey: 'acp-bar',
    assistantName: 'Bar'
  }));
  // agent_state wins for the fields it supplies; created seed remains for the rest.
  assert.equal(state.identity.providerKey, 'acp-bar');
  assert.equal(state.identity.assistantName, 'Bar');
  assert.equal(state.identity.agentName, 'Seed Code');
});

test('reducer: blank/whitespace identity fields never overwrite existing values', () => {
  let state = seedIdentityFromCreation(createInitialWorkspaceState(WS), {
    providerKey: 'acp-seed',
    agentName: 'Seed Code',
    assistantName: 'Seed'
  });
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_state',
    status: 'ready',
    providerKey: '   ',
    agentName: '',
    assistantName: null
  }));
  assert.equal(state.identity.providerKey, 'acp-seed');
  assert.equal(state.identity.agentName, 'Seed Code');
  assert.equal(state.identity.assistantName, 'Seed');
});

// --- reducer: session slice ------------------------------------------------

test('reducer: agent_state folds the full session slice', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_state',
    status: 'restoring',
    cwd: '/tmp/project',
    sessionId: 'abcdef123456',
    threadId: 'thread-9',
    busy: true,
    isDraft: false,
    supportsImage: false,
    contextUsedTokens: 4200,
    contextWindowTokens: 100000,
    contextCostAmount: 0.23,
    contextCostCurrency: 'USD'
  }));
  assert.equal(state.session.status, 'restoring');
  assert.equal(state.session.cwd, '/tmp/project');
  assert.equal(state.session.sessionId, 'abcdef123456');
  assert.equal(state.session.currentThreadId, 'thread-9');
  assert.equal(state.session.busy, true);
  assert.equal(state.session.isDraft, false);
  assert.equal(state.session.isRestoring, true);
  assert.equal(state.session.isTranscriptOnly, false);
  assert.equal(state.identity.supportsImage, false);
  assert.equal(state.session.contextUsedTokens, 4200);
  assert.equal(state.session.contextWindowTokens, 100000);
  assert.equal(state.session.contextCostAmount, 0.23);
  assert.equal(state.session.contextCostCurrency, 'USD');
});

test('reducer: transcript_only status sets the transcript flag', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_state', status: 'transcript_only' }));
  assert.equal(state.session.isTranscriptOnly, true);
  assert.equal(state.session.isRestoring, false);
});

test('reducer: default status is ready and supportsImage defaults true', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_state' }));
  assert.equal(state.session.status, 'ready');
  assert.equal(state.identity.supportsImage, true);
});

test('reducer: empty threadId keeps the previous currentThreadId', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_state', threadId: 'keep-me' }));
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_state' }));
  assert.equal(state.session.currentThreadId, 'keep-me');
});

test('reducer: agent_usage_update normalizes Context usage, limit, and optional cost', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_usage_update', contextUsedTokens: 1000, contextWindowTokens: 2000,
    contextCostAmount: 0.1, contextCostCurrency: 'USD'
  }));
  assert.equal(state.session.contextUsedTokens, 1000);
  assert.equal(state.session.contextWindowTokens, 2000);
  assert.equal(state.session.contextCostAmount, 0.1);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_usage_update', contextUsedTokens: 'nope' }));
  assert.equal(state.session.contextUsedTokens, null);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_usage_update', contextUsedTokens: -5 }));
  assert.equal(state.session.contextUsedTokens, null);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_usage_update', contextUsedTokens: 12, contextWindowTokens: 0,
    contextCostAmount: 0.2, contextCostCurrency: ''
  }));
  assert.equal(state.session.contextWindowTokens, null);
  assert.equal(state.session.contextCostAmount, null);
});

// --- reducer: runtime slice ------------------------------------------------

test('reducer: runtime_status accepts allowed states and rejects others', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'runtime_status', state: 'installing', message: 'Downloading', canInstall: false, canCancel: true
  }));
  assert.equal(state.runtime.state, 'installing');
  assert.equal(state.runtime.message, 'Downloading');
  assert.equal(state.runtime.canCancel, true);

  state = reduceWorkspaceState(state, workspaceEvent({ type: 'runtime_status', state: 'bogus' }));
  assert.equal(state.runtime.state, 'missing');
  assert.equal(state.runtime.message, 'Agent runtime is not installed.');
});

test('reducer: runtime_status also merges identity fields', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'runtime_status', state: 'ready', assistantName: 'Foo'
  }));
  assert.equal(state.identity.assistantName, 'Foo');
});

// --- reducer: agent_ready --------------------------------------------------

test('reducer: agent_ready records the ready session once, idempotently', () => {
  let state = createInitialWorkspaceState(WS);
  const before = state;
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_ready', sessionId: '' }));
  assert.equal(state, before, 'empty sessionId must not change state');

  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_ready', sessionId: 'sess-1' }));
  assert.equal(state.session.readySessionId, 'sess-1');
  const afterFirst = state;
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_ready', sessionId: 'sess-1' }));
  assert.equal(state, afterFirst, 'repeat ready must be a no-op');
});

test('reducer: unrelated events do not change the identity/session/runtime slices', () => {
  let state = createInitialWorkspaceState(WS);
  const before = state;
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'assistant_delta', text: 'hi' }));
  assert.equal(state, before);
});

// --- decoder ---------------------------------------------------------------

function makeDecoder(controllers = new Set()) {
  const ignored = [];
  const decoder = new HostEventDecoder(
    (id) => controllers.has(id),
    { onIgnored: (reason, message) => ignored.push({ reason, message }) }
  );
  return { decoder, ignored };
}

test('decoder: unknown wire type returns null without diagnostics', () => {
  const { decoder, ignored } = makeDecoder();
  assert.equal(decoder.decode({ type: 'terminal_output', workspaceId: 'x' }), null);
  assert.equal(scopeOfType('terminal_output'), null);
  assert.equal(ignored.length, 0);
});

test('decoder: missing type is ignored with a diagnostic', () => {
  const { decoder, ignored } = makeDecoder();
  assert.equal(decoder.decode({ workspaceId: 'x' }), null);
  assert.equal(ignored.length, 1);
});

test('decoder: app events survive without a workspaceId', () => {
  const { decoder } = makeDecoder();
  const event = decoder.decode({ type: 'settings', settings: { agentFontSize: 14 } });
  assert.equal(event.scope, 'app');
  assert.deepEqual(event.settings, { agentFontSize: 14 });
});

test('decoder: agent-global events survive without a workspaceId', () => {
  const { decoder } = makeDecoder();
  const event = decoder.decode({ type: 'agent_providers', providers: [{ key: 'a' }] });
  assert.equal(event.scope, 'agent-global');
  assert.equal(event.providers.length, 1);
});

test('decoder: lifecycle event without workspaceId is ignored', () => {
  const { decoder, ignored } = makeDecoder();
  assert.equal(decoder.decode({ type: 'agent_workspace_created' }), null);
  assert.equal(ignored.length, 1);
});

test('decoder: workspace content event needs a live controller', () => {
  const controllers = new Set();
  const { decoder, ignored } = makeDecoder(controllers);
  assert.equal(decoder.decode({ type: 'agent_state', workspaceId: WS }), null);
  assert.equal(ignored.length, 1);

  controllers.add(WS);
  const event = decoder.decode({ type: 'agent_state', workspaceId: WS });
  assert.equal(event.scope, 'agent-workspace');
  assert.equal(event.workspaceId, WS);
});

// --- store integration -----------------------------------------------------

test('store: create seeds identity and reduce folds events', () => {
  const store = new AgentWorkspaceStore();
  store.create(WS, { providerKey: 'acp-seed', assistantName: 'Seed' });
  assert.equal(store.get(WS).identity.assistantName, 'Seed');

  const next = store.reduce(WS, workspaceEvent({ type: 'agent_state', status: 'ready', assistantName: 'Final' }));
  assert.equal(next.identity.assistantName, 'Final');
  assert.equal(store.get(WS).identity.assistantName, 'Final');
});

test('store: reduce on an unknown workspace returns undefined', () => {
  const store = new AgentWorkspaceStore();
  assert.equal(store.reduce('missing', workspaceEvent({ type: 'agent_state' })), undefined);
});
