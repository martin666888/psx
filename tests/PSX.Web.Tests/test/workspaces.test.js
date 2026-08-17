import { test } from 'vitest';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace, composerReady } from './agentHarness.js';

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
  await composerReady(firstPanel);
  await composerReady(secondPanel);

  // Workspace-local draft text survives on each panel independently.
  role(firstPanel, 'input').value = 'first draft';
  role(firstPanel, 'input').dispatchEvent(new Event('input', { bubbles: true }));
  role(secondPanel, 'input').value = 'second draft';
  role(secondPanel, 'input').dispatchEvent(new Event('input', { bubbles: true }));

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
  assert.doesNotMatch(role(secondPanel, 'toolbar-host').textContent, /D:\/first/);
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

test('layout-driven app keeps agent panels hidden until the first projection', async () => {
  const dockInsets = [];
  const { app, panelFor, terminal } = await mountAgentApp({
    paneLayout: {
      setDockInset(px) {
        dockInsets.push(px);
      }
    }
  });
  const container = document.getElementById('agents');
  assert.ok(container.classList.contains('agent-layout-driven'),
    'markLayoutDriven stamps the container class at app creation');
  assert.ok(dockInsets.at(-1) > 40,
    'the paneLayout seam is wired (default-open dock reports the wider inset)');

  createAgentWorkspace(app, FIRST, { ready: false });
  const panel = panelFor(FIRST);
  assert.equal(panel.hidden, true, 'a fresh panel starts hidden');

  // With a paneLayout the host is layout-driven from creation: activation
  // must NOT fall back to the legacy fullscreen single-panel path.
  app.handle({ type: 'workspace_activated', workspaceId: FIRST, kind: 'agent' });
  assert.equal(panel.hidden, true, 'activation never directly shows the panel');
  assert.equal(terminal.visible, true, 'the terminal view stays untouched');

  // The first column projection (main.js paneLayout.recompute()) reveals it.
  app.setPaneLayout(
    { focusedColumnId: 'column-1', columns: [{ columnId: 'column-1', tabs: [{ workspaceId: FIRST, kind: 'agent' }], activeTabId: FIRST, ratio: 1 }] },
    new Map([['column-1', { left: 40, top: 40, width: 800, height: 860 }]])
  );
  assert.equal(panel.hidden, false, 'the projection shows the panel');
  assert.equal(panel.dataset.columnId, 'column-1');
});

test('dock-adjacent first column drops the panel left gutter', async () => {
  const { app, panelFor } = await mountAgentApp({ paneLayout: { setDockInset() {} } });
  createAgentWorkspace(app, FIRST, { ready: false });
  const panel = panelFor(FIRST);

  // Dock open: the inset already carries the left gutter + panel gap, so the
  // flagged first column sits at the rect edge — exactly one panel gap (12px)
  // from the dock edge, the same spacing a terminal column gets.
  app.setPaneLayout(
    { focusedColumnId: 'column-1', columns: [{ columnId: 'column-1', tabs: [{ workspaceId: FIRST, kind: 'agent' }], activeTabId: FIRST, ratio: 1 }] },
    new Map([['column-1', { left: 344, top: 40, width: 800, height: 860, dockAdjacent: true }]])
  );
  assert.equal(panel.style.left, '344px', 'dock-adjacent panel adds no second left gutter');
  assert.equal(panel.style.width, '788px', 'width loses only the right gutter');

  // Dock closed (no flag): the panel floats with the full gutter on every side.
  app.setPaneLayout(
    { focusedColumnId: 'column-1', columns: [{ columnId: 'column-1', tabs: [{ workspaceId: FIRST, kind: 'agent' }], activeTabId: FIRST, ratio: 1 }] },
    new Map([['column-1', { left: 40, top: 40, width: 800, height: 860 }]])
  );
  assert.equal(panel.style.left, '52px', 'undocked panel keeps the workbench gutter');
  assert.equal(panel.style.width, '776px', 'width loses both gutters');
});

test('mixed terminal/agent layout keeps the Agent layer click-through for terminal columns', async () => {
  const { app } = await mountAgentApp({ paneLayout: { setDockInset() {} } });
  const container = document.getElementById('agents');
  createAgentWorkspace(app, FIRST, { ready: false });

  // A visible terminal column beside the agent: the blanket container must
  // NOT take pointer events, or every click aimed at xterm is swallowed and
  // the terminal can neither focus nor receive input.
  app.setPaneLayout(
    {
      focusedColumnId: 'column-2',
      columns: [
        { columnId: 'column-1', tabs: [{ workspaceId: 'term-1', kind: 'terminal' }], activeTabId: 'term-1', ratio: 0.5 },
        { columnId: 'column-2', tabs: [{ workspaceId: FIRST, kind: 'agent' }], activeTabId: FIRST, ratio: 0.5 }
      ]
    },
    new Map([
      ['column-1', { left: 40, top: 40, width: 400, height: 860 }],
      ['column-2', { left: 440, top: 40, width: 400, height: 860 }]
    ])
  );
  assert.equal(container.classList.contains('agent-workspace-active'), false,
    'mixed layout leaves the container click-through (panels self-enable)');
  assert.equal(container.classList.contains('agent-split-active'), true);

  // Pure agent layout: the container still takes events and paints the
  // workbench backdrop as before.
  app.setPaneLayout(
    { focusedColumnId: 'column-1', columns: [{ columnId: 'column-1', tabs: [{ workspaceId: FIRST, kind: 'agent' }], activeTabId: FIRST, ratio: 1 }] },
    new Map([['column-1', { left: 40, top: 40, width: 800, height: 860 }]])
  );
  assert.equal(container.classList.contains('agent-workspace-active'), true,
    'pure agent layout keeps the active workbench layer');
});

test('mixed kimi_web/agent layout keeps the Agent layer click-through for kimi columns', async () => {
  const { app } = await mountAgentApp({ paneLayout: { setDockInset() {} } });
  const container = document.getElementById('agents');
  createAgentWorkspace(app, FIRST, { ready: false });

  // A visible kimi_web column beside the agent: the shell-owned kimi surface
  // must not be covered by the Agent blanket, exactly like DSH.
  app.setPaneLayout(
    {
      focusedColumnId: 'column-2',
      columns: [
        { columnId: 'column-1', tabs: [{ workspaceId: 'kimi-1', kind: 'kimi_web' }], activeTabId: 'kimi-1', ratio: 0.5 },
        { columnId: 'column-2', tabs: [{ workspaceId: FIRST, kind: 'agent' }], activeTabId: FIRST, ratio: 0.5 }
      ]
    },
    new Map([
      ['column-1', { left: 40, top: 40, width: 400, height: 860 }],
      ['column-2', { left: 440, top: 40, width: 400, height: 860 }]
    ])
  );
  assert.equal(container.classList.contains('agent-workspace-active'), false,
    'a kimi_web column leaves the container click-through (the kimi panel self-enables)');
  assert.equal(container.classList.contains('agent-split-active'), true);

  // A kimi_web-only layout also keeps the blanket inactive.
  app.setPaneLayout(
    { focusedColumnId: 'column-1', columns: [{ columnId: 'column-1', tabs: [{ workspaceId: 'kimi-1', kind: 'kimi_web' }], activeTabId: 'kimi-1', ratio: 1 }] },
    new Map([['column-1', { left: 40, top: 40, width: 800, height: 860 }]])
  );
  assert.equal(container.classList.contains('agent-workspace-active'), false,
    'a pure kimi_web layout never activates the Agent blanket');

  // Pure agent layout: the container takes events and paints the workbench.
  app.setPaneLayout(
    { focusedColumnId: 'column-1', columns: [{ columnId: 'column-1', tabs: [{ workspaceId: FIRST, kind: 'agent' }], activeTabId: FIRST, ratio: 1 }] },
    new Map([['column-1', { left: 40, top: 40, width: 800, height: 860 }]])
  );
  assert.equal(container.classList.contains('agent-workspace-active'), true,
    'pure agent layout keeps the active workbench layer');
});

test('legacy harness without paneLayout keeps the activation fallback', async () => {
  const { app, panelFor, terminal } = await mountAgentApp();
  const container = document.getElementById('agents');
  assert.equal(container.classList.contains('agent-layout-driven'), false,
    'no paneLayout means no layout-driven stamp');

  createAgentWorkspace(app, FIRST, { ready: false });
  const panel = panelFor(FIRST);
  app.handle({ type: 'workspace_activated', workspaceId: FIRST, kind: 'agent' });
  assert.equal(panel.hidden, false, 'legacy activation still shows the activated panel');
  assert.equal(terminal.visible, false);
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
  await composerReady(panel);
  const input = role(panel, 'input');
  const send = role(panel, 'send');
  assert.equal(input.disabled, true);
  assert.equal(send.disabled, true);
  assert.equal(role(panel, 'send').getAttribute('aria-label'), 'Read only');
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

  await composerReady(panelFor(FIRST));
  await composerReady(panelFor(SECOND));
  await composerReady(panelFor(THIRD));
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

test('inactive tab in a visible column stays hidden keep-alive', async () => {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, FIRST);
  createAgentWorkspace(app, SECOND);
  const firstPanel = panelFor(FIRST);
  const secondPanel = panelFor(SECOND);

  app.setPaneLayout(
    {
      focusedColumnId: 'column-1',
      columns: [{
        columnId: 'column-1',
        tabs: [{ workspaceId: FIRST, kind: 'agent' }, { workspaceId: SECOND, kind: 'agent' }],
        activeTabId: FIRST,
        ratio: 1
      }]
    },
    new Map([['column-1', { left: 40, top: 40, width: 800, height: 860 }]])
  );
  assert.equal(firstPanel.hidden, false, 'the active tab of the column is visible');
  assert.equal(secondPanel.hidden, true, 'an inactive tab in the same column stays hidden keep-alive');
  assert.equal(firstPanel.dataset.paneFocused, 'true', 'the visible panel marks the focused column');
});
