import { test } from 'vitest';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

const FIRST = '11111111-1111-4111-8111-111111111111';
const SECOND = '22222222-2222-4222-8222-222222222222';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

test('keeps independent DOM and routes events only to the matching workspace', async () => {
  const { app, panelFor, terminal } = await mountAgentApp();
  createAgentWorkspace(app, FIRST, { ready: false });
  createAgentWorkspace(app, SECOND, { ready: false });

  const firstPanel = panelFor(FIRST);
  const secondPanel = panelFor(SECOND);
  assert.ok(firstPanel);
  assert.ok(secondPanel);
  assert.notEqual(firstPanel, secondPanel);

  // Workspace-local draft text survives on each panel independently.
  role(firstPanel, 'input').value = 'first draft';
  role(secondPanel, 'input').value = 'second draft';

  // A state event addressed to the first workspace only touches its panel.
  app.handle({
    type: 'agent_state',
    workspaceId: FIRST,
    status: 'running',
    busy: true,
    cwd: 'D:/first',
    title: 'First'
  });
  for (let attempt = 0; attempt < 200 && !role(firstPanel, 'cwd'); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  // Activating the second workspace hides the first and the terminal view.
  app.handle({ type: 'workspace_activated', workspaceId: SECOND, kind: 'agent' });

  assert.equal(role(firstPanel, 'cwd').textContent, 'D:/first');
  assert.doesNotMatch(role(secondPanel, 'session-meta-host').textContent, /D:\/first/);
  assert.equal(role(firstPanel, 'input').value, 'first draft');
  assert.equal(role(secondPanel, 'input').value, 'second draft');
  assert.equal(firstPanel.hidden, true);
  assert.equal(secondPanel.hidden, false);
  assert.equal(terminal.visible, false);
});

test('only lets the absolute Agent layer receive pointer events for an active Agent workspace', async () => {
  const { app, terminal } = await mountAgentApp();
  createAgentWorkspace(app, FIRST, { ready: false });
  const container = document.getElementById('agents');

  assert.equal(container.classList.contains('agent-workspace-active'), false,
    'the terminal-safe default does not cover terminal clicks');

  app.handle({ type: 'workspace_activated', workspaceId: FIRST, kind: 'agent' });
  assert.equal(container.classList.contains('agent-workspace-active'), true);
  assert.equal(terminal.visible, false);

  app.handle({ type: 'workspace_activated', workspaceId: 'terminal-1', kind: 'terminal' });
  assert.equal(container.classList.contains('agent-workspace-active'), false,
    'the transparent Agent root yields pointer hit-testing back to xterm');
  assert.equal(terminal.visible, true);
});

test('removes a closed workspace and ignores its later events', async () => {
  const { app, panelFor } = await mountAgentApp();
  const workspaceId = '33333333-3333-4333-8333-333333333333';

  createAgentWorkspace(app, workspaceId, { ready: false });
  assert.ok(panelFor(workspaceId));

  app.handle({ type: 'agent_workspace_closed', workspaceId });
  // A late event for the closed workspace must be a no-op (no re-created panel).
  app.handle({ type: 'assistant_delta', workspaceId, text: 'late' });

  assert.equal(panelFor(workspaceId), null);
  assert.equal(document.querySelector(`[data-workspace-id="${workspaceId}"]`), null);
});

test('keeps transcript-only workspaces read-only even when their runtime is ready', async () => {
  const { app, panelFor } = await mountAgentApp();
  const workspaceId = '44444444-4444-4444-8444-444444444444';

  createAgentWorkspace(app, workspaceId, { ready: true });
  app.handle({
    type: 'agent_state',
    workspaceId,
    status: 'transcript_only',
    cwd: 'C:/saved'
  });

  const panel = panelFor(workspaceId);
  const input = role(panel, 'input');
  const send = role(panel, 'send');
  assert.equal(input.disabled, true);
  assert.equal(send.disabled, true);
  assert.equal(role(panel, 'send-label').textContent, 'Read only');
});

test('renders each provider identity independently and stays brand-neutral for unknown providers', async () => {
  const THIRD = '33333333-3333-4333-8333-333333333333';
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, FIRST);
  createAgentWorkspace(app, SECOND);
  createAgentWorkspace(app, THIRD);

  // Two arbitrary provider keys carry distinct identities; the third keeps the
  // neutral default so an unknown provider never inherits a sibling's brand.
  app.handle({ type: 'agent_state', workspaceId: FIRST, status: 'ready', providerKey: 'alpha', assistantName: 'Alpha' });
  app.handle({ type: 'agent_state', workspaceId: SECOND, status: 'ready', providerKey: 'beta', assistantName: 'Beta' });

  const first = role(panelFor(FIRST), 'input').placeholder;
  const second = role(panelFor(SECOND), 'input').placeholder;
  const third = role(panelFor(THIRD), 'input').placeholder;

  assert.match(first, /Alpha/);
  assert.doesNotMatch(first, /Beta/);
  assert.match(second, /Beta/);
  assert.doesNotMatch(second, /Alpha/);
  // Unknown provider falls back to the neutral 'Agent', not a hardcoded brand.
  assert.match(third, /Message Agent Agent/);
  for (const text of [first, second, third]) assert.doesNotMatch(text, /Claude/);
});
