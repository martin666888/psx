import { test } from 'vitest';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace, composerReady } from './agentHarness.js';

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function setDraft(panel, text) {
  const input = role(panel, 'input');
  input.value = text;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

async function waitFor(predicate, message = 'condition did not settle') {
  for (let attempt = 0; attempt < 100; attempt++) {
    const value = predicate();
    if (value) return value;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.fail(message);
}

// Drive the compiled composer the way production does: create a ready workspace,
// then observe the scoped Bridge messages (agent_submit / agent_command) and the
// composer-owned nodes on the live panel. The ComposerView island renders the
// composer asynchronously, so wait for its first commit before touching nodes.
async function mount() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  await composerReady(panel);
  return { app, panel, posted: runtime.postedMessages };
}

test('canonicalizes built-in and advertised ACP commands on submit', async () => {
  const { app, panel, posted } = await mount();
  app.handle({
    type: 'agent_commands',
    workspaceId: WS,
    ready: true,
    commands: [{ name: '/review', description: 'Review' }]
  });

  setDraft(panel, '/CLEAR');
  let before = posted.length;
  role(panel, 'send').click();
  await waitFor(() => posted.length > before);
  assert.equal(posted.at(-1).type, 'agent_submit');
  assert.equal(posted.at(-1).text, '/clear');

  setDraft(panel, '/REVIEW src');
  before = posted.length;
  role(panel, 'send').click();
  await waitFor(() => posted.length > before);
  assert.equal(posted.at(-1).text, '/review src');
});

test('renders slash suggestions with cmdk and selects advertised commands from the textarea', async () => {
  const { app, panel, posted } = await mount();
  app.handle({
    type: 'agent_commands',
    workspaceId: WS,
    ready: true,
    commands: [
      { name: '/review', description: 'Review changes' },
      { name: '/run', description: 'Run checks' }
    ]
  });
  const input = role(panel, 'input');
  setDraft(panel, '/r');

  const menu = role(panel, 'command-menu');
  for (let attempt = 0; attempt < 100 && menu.hidden; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  const items = [...menu.querySelectorAll('[data-slot="command-item"]')];
  assert.equal(menu.hidden, false);
  assert.deepEqual(items.map((item) => item.dataset.commandName), ['/review', '/run']);
  assert.equal(menu.querySelector('[data-selected="true"]').dataset.commandName, '/review');

  input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
  for (let attempt = 0; attempt < 100; attempt++) {
    if (menu.querySelector('[data-selected="true"]')?.dataset.commandName === '/run') break;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(menu.querySelector('[data-selected="true"]').dataset.commandName, '/run');

  input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
  for (let attempt = 0; attempt < 100 && !menu.hidden; attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(menu.hidden, true);
  assert.deepEqual(posted.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'agent_command',
    value: '/run',
    requestId: ''
  });
});

test('rejects unknown leading commands but sends inline slash-like text', async () => {
  const { app, panel, posted } = await mount();
  app.handle({ type: 'agent_commands', workspaceId: WS, ready: true, commands: [] });

  setDraft(panel, '/unknown');
  role(panel, 'send').click();
  await waitFor(() => role(panel, 'command-hint').hidden === false);
  // A rejected command posts no agent_submit; the composer surfaces a hint.
  assert.notEqual(posted.at(-1)?.type, 'agent_submit');
  assert.equal(role(panel, 'command-hint').hidden, false);

  setDraft(panel, 'Explain /unknown here');
  const before = posted.length;
  role(panel, 'send').click();
  await waitFor(() => posted.length > before);
  assert.equal(posted.at(-1).type, 'agent_submit');
  assert.equal(posted.at(-1).text, 'Explain /unknown here');
});

test('submits normal text and clears the draft only after validation succeeds', async () => {
  const { panel, posted } = await mount();
  setDraft(panel, '  hello agent  ');
  const before = posted.length;
  role(panel, 'send').click();
  await waitFor(() => posted.length > before);

  assert.deepEqual(posted.at(-1), {
    type: 'agent_submit',
    workspaceId: WS,
    text: 'hello agent',
    attachments: []
  });
  await waitFor(() => role(panel, 'input').value === '');
});

test('turns submission into Stop while a run is busy', async () => {
  const { app, panel, posted } = await mount();
  app.handle({ type: 'agent_state', workspaceId: WS, status: 'running', busy: true, cwd: '/tmp' });
  await waitFor(() => role(panel, 'send').getAttribute('aria-label') === 'Stop');

  setDraft(panel, 'must not send');
  const before = posted.length;
  role(panel, 'send').click();
  await waitFor(() => posted.length > before);

  assert.deepEqual(posted.at(-1), {
    type: 'agent_command',
    workspaceId: WS,
    command: 'stop',
    value: '',
    requestId: ''
  });
  assert.equal(role(panel, 'input').value, 'must not send');
});
