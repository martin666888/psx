import { test } from 'vitest';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// Drive the compiled composer the way production does: create a ready workspace,
// then observe the scoped Bridge messages (agent_submit / agent_command) and the
// composer-owned nodes on the live panel.
async function mount() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS), posted: runtime.postedMessages };
}

test('canonicalizes built-in and advertised ACP commands on submit', async () => {
  const { app, panel, posted } = await mount();
  app.handle({
    type: 'agent_commands',
    workspaceId: WS,
    ready: true,
    commands: [{ name: '/review', description: 'Review' }]
  });

  role(panel, 'input').value = '/CLEAR';
  role(panel, 'send').click();
  assert.equal(posted.at(-1).type, 'agent_submit');
  assert.equal(posted.at(-1).text, '/clear');

  role(panel, 'input').value = '/REVIEW src';
  role(panel, 'send').click();
  assert.equal(posted.at(-1).text, '/review src');
});

test('rejects unknown leading commands but sends inline slash-like text', async () => {
  const { app, panel, posted } = await mount();
  app.handle({ type: 'agent_commands', workspaceId: WS, ready: true, commands: [] });

  role(panel, 'input').value = '/unknown';
  role(panel, 'send').click();
  // A rejected command posts no agent_submit; the composer surfaces a hint.
  assert.notEqual(posted.at(-1)?.type, 'agent_submit');
  assert.equal(role(panel, 'command-hint').hidden, false);

  role(panel, 'input').value = 'Explain /unknown here';
  role(panel, 'send').click();
  assert.equal(posted.at(-1).type, 'agent_submit');
  assert.equal(posted.at(-1).text, 'Explain /unknown here');
});

test('submits normal text and clears the draft only after validation succeeds', async () => {
  const { panel, posted } = await mount();
  role(panel, 'input').value = '  hello agent  ';
  role(panel, 'send').click();

  assert.deepEqual(posted.at(-1), {
    type: 'agent_submit',
    workspaceId: WS,
    text: 'hello agent',
    attachments: []
  });
  assert.equal(role(panel, 'input').value, '');
});

test('turns submission into Stop while a run is busy', async () => {
  const { app, panel, posted } = await mount();
  app.handle({ type: 'agent_state', workspaceId: WS, status: 'running', busy: true, cwd: '/tmp' });

  role(panel, 'input').value = 'must not send';
  role(panel, 'send').click();

  assert.deepEqual(posted.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'stop',
    value: '',
    requestId: ''
  });
  assert.equal(role(panel, 'input').value, 'must not send');
});
