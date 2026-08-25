import { test } from 'vitest';
import assert from 'node:assert/strict';
import { appModule } from './agentHarness.js';

// Phase 4 checkpoint 3: the composer slice (agent commands / modes / config
// options / current mode) is folded by the pure reducer so ComposerController
// can render straight from state. These tests pin that folding independently of
// the DOM.

const { reduceWorkspaceState } = await appModule('core/reducer.js');
const { createInitialWorkspaceState } = await appModule('contracts/workspace-state.js');

const WS = 'ws-composer';

function workspaceEvent(raw) {
  return { scope: 'agent-workspace', type: raw.type, workspaceId: WS, raw };
}

function withAssistant(name) {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_state', status: 'ready', assistantName: name }));
  return state;
}

// --- agent_commands --------------------------------------------------------

test('reducer: agent_commands normalizes string and object commands', () => {
  let state = withAssistant('Foo');
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_commands',
    ready: true,
    commands: ['compact', { name: '/review', description: 'Review the diff' }, { name: 'plan' }]
  }));

  assert.equal(state.composer.agentCommandsReady, true);
  // source is the assistant name; a missing provider description stays ''
  // and the composer menu localizes the fallback label at render.
  assert.deepEqual(state.composer.agentCommands, [
    { source: 'Foo', name: '/compact', label: '', agent: true },
    { source: 'Foo', name: '/review', label: 'Review the diff', agent: true },
    { source: 'Foo', name: '/plan', label: '', agent: true }
  ]);
});

test('reducer: agent_commands drops blank names and defaults ready to false', () => {
  let state = withAssistant('Bar');
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_commands',
    commands: ['   ', { name: '' }, { description: 'no name' }, { name: 'ok' }]
  }));

  assert.equal(state.composer.agentCommandsReady, false);
  assert.equal(state.composer.agentCommands.length, 1);
  assert.equal(state.composer.agentCommands[0].name, '/ok');
});

// --- agent_modes -----------------------------------------------------------

test('reducer: agent_modes filters entries without an id and folds currentModeId', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_modes',
    modes: [{ id: 'code', name: 'Code' }, { name: 'no-id' }, null, { id: 'plan' }],
    currentModeId: 'plan'
  }));

  assert.deepEqual(state.composer.modes, [
    { id: 'code', name: 'Code', description: '' },
    { id: 'plan', name: '', description: '' }
  ]);
  assert.equal(state.composer.currentModeId, 'plan');
});

test('reducer: agent_modes without currentModeId keeps the existing mode', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_mode_current', currentModeId: 'code' }));
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_modes', modes: [{ id: 'code' }] }));
  assert.equal(state.composer.currentModeId, 'code');
});

// --- agent_config_options --------------------------------------------------

test('reducer: agent_config_options preserves boolean controls and filters invalid values', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({
    type: 'agent_config_options',
    options: [
      { id: 'effort', type: 'select', options: [{ value: 'high' }] },
      { id: 'text', type: 'text', options: [{ value: 'x' }] },
      { id: 'empty', type: 'select', options: [] },
      { id: 'fast_mode', type: 'boolean', currentValue: true },
      { id: 'bad_bool', type: 'boolean', currentValue: 'true' },
      { id: 'model', type: 'select', options: [{ value: 'sonnet', name: 'Sonnet' }] },
      { id: 'mode', type: 'select', currentValue: 'plan', options: [{ value: 'plan' }] }
    ]
  }));

  assert.deepEqual(state.composer.configOptions.map((o) => o.id), ['mode', 'model', 'effort', 'fast_mode']);
  assert.equal(state.composer.currentModeId, 'plan');
  assert.deepEqual(state.composer.configOptions[1].options, [
    { value: 'sonnet', name: 'Sonnet', description: '' }
  ]);
  assert.equal(state.composer.configOptions[3].type, 'boolean');
  assert.equal(state.composer.configOptions[3].currentValue, true);
});

// --- agent_mode_current ----------------------------------------------------

test('reducer: agent_mode_current updates the mode and is a no-op when unchanged', () => {
  let state = createInitialWorkspaceState(WS);
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_mode_current', currentModeId: 'code' }));
  assert.equal(state.composer.currentModeId, 'code');

  const before = state;
  state = reduceWorkspaceState(state, workspaceEvent({ type: 'agent_mode_current', currentModeId: 'code' }));
  assert.equal(state, before, 'unchanged current mode must be a no-op');
});
