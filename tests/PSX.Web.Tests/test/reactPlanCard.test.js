// reactPlanCard.test.js — the React plan-panel island (test matrix Group B).
//
// React owns the content of data-role="plan-panel" in react mode; the
// controller keeps visibility, the unread dot and narrow-mode rules. DOM
// equivalence is asserted against the legacy renderer for all three content
// forms, and the node-identity test proves React reuses unchanged entry rows
// (the legacy renderer rebuilds the whole list on every update).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, appModule } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;

const WS = '66666666-6666-4666-8666-666666666666';

// Warm the module cache so the island's dynamic import resolves within the
// act scope deterministically (same absolute URL, same ESM cache entry).
await appModule('plan/planIsland.js');

const ENTRIES = [
  { content: 'Survey the code', status: 'completed', priority: 'high' },
  { content: 'Build the feature', status: 'in_progress' },
  { content: 'Write the tests', status: 'pending' }
];

function planEvent(entries, overrides = {}) {
  return { type: 'plan_update', workspaceId: WS, runId: 'run-1', entries, ...overrides };
}

function panelRole(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// Snapshot every DOM fact the legacy renderPlan writes into the plan panel.
function planSnapshot(panel) {
  const pane = panelRole(panel, 'plan-panel');
  return {
    childCount: pane.children.length,
    empty: pane.querySelector('.agent-plan-empty')?.textContent ?? null,
    fallback: pane.querySelector('.agent-plan-fallback')?.textContent ?? null,
    items: [...pane.querySelectorAll('.agent-plan-item')].map((item) => ({
      className: item.className,
      ariaLabel: item.getAttribute('aria-label'),
      priority: item.dataset.priority ?? null,
      marker: item.querySelector('.agent-plan-marker').textContent,
      content: item.querySelector('.agent-plan-content').textContent
    }))
  };
}

async function actAndSettle(app, run, predicate) {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 100 && !predicate(); i++) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 5));
    });
  }
  assert.ok(predicate(), 'react plan content did not settle in time');
}

async function mountPlanApp(uiMode) {
  const { app, panelFor } = await mountAgentApp({ wide: true, uiMode });
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS) };
}

test('React plan panel renders DOM equivalent to legacy for all three content forms', async () => {
  // Legacy snapshots: entries list, plain-text fallback, cleared empty state.
  const legacy = [];
  {
    const { app, panel } = await mountPlanApp('legacy');
    app.handle(planEvent(ENTRIES));
    legacy.push(planSnapshot(panel));
    app.handle(planEvent([], { runId: 'run-2', text: 'Refactor first, then test.' }));
    legacy.push(planSnapshot(panel));
    app.handle({ type: 'agent_cleared', workspaceId: WS });
    legacy.push(planSnapshot(panel));
  }

  const reactSnapshots = [];
  {
    const { app, panel } = await mountPlanApp('react');
    await actAndSettle(
      app,
      () => app.handle(planEvent(ENTRIES)),
      () => panelRole(panel, 'plan-panel').querySelector('.agent-plan-list')
    );
    reactSnapshots.push(planSnapshot(panel));
    await actAndSettle(
      app,
      () => app.handle(planEvent([], { runId: 'run-2', text: 'Refactor first, then test.' })),
      () => panelRole(panel, 'plan-panel').querySelector('.agent-plan-fallback')
    );
    reactSnapshots.push(planSnapshot(panel));
    await actAndSettle(
      app,
      () => app.handle({ type: 'agent_cleared', workspaceId: WS }),
      () => panelRole(panel, 'plan-panel').querySelector('.agent-plan-empty')
    );
    reactSnapshots.push(planSnapshot(panel));
    await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
  }

  assert.deepEqual(reactSnapshots, legacy);
});

test('React reuses the DOM nodes of unchanged plan entries across updates', async () => {
  const { app, panel } = await mountPlanApp('react');
  await actAndSettle(
    app,
    () => app.handle(planEvent(ENTRIES)),
    () => panelRole(panel, 'plan-panel').querySelectorAll('.agent-plan-item').length === 3
  );
  const before = [...panelRole(panel, 'plan-panel').querySelectorAll('.agent-plan-item')];

  // Entry 1 completes, entry 2 starts, and a new entry is appended.
  const updated = [
    ENTRIES[0],
    { ...ENTRIES[1], status: 'completed' },
    { ...ENTRIES[2], status: 'in_progress' },
    { content: 'Ship it', status: 'pending' }
  ];
  await actAndSettle(
    app,
    () => app.handle(planEvent(updated, { runId: 'run-1' })),
    () => panelRole(panel, 'plan-panel').querySelectorAll('.agent-plan-item').length === 4
  );
  const after = [...panelRole(panel, 'plan-panel').querySelectorAll('.agent-plan-item')];

  // Positional keys: every pre-existing row keeps its DOM node (the legacy
  // renderer rebuilds all of them on every update).
  assert.equal(after[0], before[0]);
  assert.equal(after[1], before[1]);
  assert.equal(after[2], before[2]);
  assert.equal(after[1].className, 'agent-plan-item agent-plan-item-completed');
  assert.equal(after[2].className, 'agent-plan-item agent-plan-item-in-progress');
  assert.equal(after[3].querySelector('.agent-plan-content').textContent, 'Ship it');
  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
});

test('react mode keeps the hidden-card unread dot and renders the latest plan on show', async () => {
  const { app, panel } = await mountPlanApp('react');
  panelRole(panel, 'plan-toggle').click();
  assert.equal(panelRole(panel, 'plan-card').hidden, true);

  await actAndSettle(
    app,
    () => app.handle(planEvent([{ content: 'Hidden update', status: 'pending' }])),
    () => panelRole(panel, 'plan-panel').querySelector('.agent-plan-list')
  );
  assert.equal(panelRole(panel, 'plan-toggle-unread').hidden, false, 'unread dot raised while hidden');
  assert.equal(panelRole(panel, 'plan-card').hidden, true, 'card never auto-opens');

  panelRole(panel, 'plan-toggle').click();
  assert.equal(panelRole(panel, 'plan-card').hidden, false);
  assert.equal(panelRole(panel, 'plan-toggle-unread').hidden, true, 'showing clears the dot');
  assert.match(panelRole(panel, 'plan-panel').textContent, /Hidden update/);
  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
});

test('closing the workspace unmounts the plan island without residue', async () => {
  const { app, panel } = await mountPlanApp('react');
  await actAndSettle(
    app,
    () => app.handle(planEvent(ENTRIES)),
    () => panelRole(panel, 'plan-panel').querySelector('.agent-plan-list')
  );
  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
  assert.equal(document.querySelector(`.agent-panel[data-workspace-id="${WS}"]`), null);
  assert.equal(document.querySelector('.agent-plan-list'), null, 'React content unmounted with the panel');
});
