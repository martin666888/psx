// reactPlanCard.test.js — React-only plan behavior.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { act } from 'react';
import { mountAgentApp, createAgentWorkspace, appModule } from './agentHarness.js';

globalThis.IS_REACT_ACT_ENVIRONMENT = true;
const WS = '66666666-6666-4666-8666-666666666666';
await appModule('plan/planIsland.js');

const ENTRIES = [
  { content: 'Survey the code', status: 'completed', priority: 'high' },
  { content: 'Build the feature', status: 'in_progress' },
  { content: 'Write the tests', status: 'pending' }
];

const event = (entries, overrides = {}) => ({
  type: 'plan_update',
  workspaceId: WS,
  runId: 'run-1',
  entries,
  ...overrides
});

const role = (panel, name) => panel.querySelector('[data-role="' + name + '"]');

async function fixture() {
  const { app, panelFor } = await mountAgentApp({ wide: true });
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS) };
}

async function settle(app, predicate, run) {
  await act(async () => {
    run();
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
  for (let i = 0; i < 50 && !predicate(); i++) {
    await act(async () => new Promise((resolve) => setTimeout(resolve, 5)));
  }
  assert.ok(predicate(), 'plan island did not settle');
}

test('plan entries, document mode and empty state render in place', async () => {
  const { app, panel } = await fixture();
  const host = role(panel, 'plan-card');
  await settle(app, () => host.querySelectorAll('.agent-plan-item').length === 3, () => app.handle(event(ENTRIES)));
  assert.deepEqual(
    [...host.querySelectorAll('.agent-plan-content')].map((node) => node.textContent),
    ENTRIES.map((entry) => entry.content)
  );
  assert.equal(host.querySelector('.agent-plan-item').dataset.priority, 'high');
  assert.equal(host.dataset.islandState, 'mounted');

  // A plan update without entries is a full plan document: it renders
  // through the safe Markdown pipeline instead of a raw <pre> dump.
  await settle(
    app,
    () => !!host.querySelector('.agent-plan-document'),
    () => app.handle(event([], { text: '## Approach\n\nRefactor first, then **test**.' }))
  );
  const document_ = host.querySelector('.agent-plan-document');
  assert.ok(document_.querySelector('h2'), 'markdown headings render in document mode');
  assert.ok(document_.querySelector('strong'), 'inline markdown renders in document mode');
  assert.match(document_.textContent, /Refactor first, then test\./);
  await settle(
    app,
    () => !!host.querySelector('.agent-plan-empty'),
    () => app.handle({ type: 'agent_cleared', workspaceId: WS })
  );
  assert.match(host.querySelector('.agent-plan-empty').textContent, /No active plan/);
});

test('updates preserve existing row nodes', async () => {
  const { app, panel } = await fixture();
  const host = role(panel, 'plan-card');
  await settle(app, () => host.querySelectorAll('.agent-plan-item').length === 3, () => app.handle(event(ENTRIES)));
  const before = [...host.querySelectorAll('.agent-plan-item')];
  const updated = [
    ENTRIES[0],
    { ...ENTRIES[1], status: 'completed' },
    { ...ENTRIES[2], status: 'in_progress' },
    { content: 'Ship it', status: 'pending' }
  ];
  await settle(app, () => host.querySelectorAll('.agent-plan-item').length === 4, () => app.handle(event(updated)));
  const after = [...host.querySelectorAll('.agent-plan-item')];
  assert.equal(after[0], before[0]);
  assert.equal(after[1], before[1]);
  assert.equal(after[2], before[2]);
  assert.match(after[1].className, /completed/);
  assert.match(after[2].className, /in-progress/);
});

test('a hidden update raises unread without forcing the card open', async () => {
  const { app, panel } = await fixture();
  // The plan toggle renders through the workspace toolbar island.
  await settle(app, () => !!role(panel, 'plan-toggle'), () => {});
  await act(async () => role(panel, 'plan-toggle').click());
  assert.equal(role(panel, 'plan-card').hidden, true);
  await settle(
    app,
    () => !!role(panel, 'plan-card').querySelector('.agent-plan-list'),
    () => app.handle(event([{ content: 'Hidden update', status: 'pending' }]))
  );
  await settle(app, () => role(panel, 'plan-toggle-unread')?.hidden === false, () => {});
  assert.equal(role(panel, 'plan-card').hidden, true);
  await act(async () => role(panel, 'plan-toggle').click());
  assert.equal(role(panel, 'plan-card').hidden, false);
  await settle(app, () => role(panel, 'plan-toggle-unread')?.hidden === true, () => {});
});

test('closing the workspace unmounts plan content', async () => {
  const { app, panel } = await fixture();
  await settle(
    app,
    () => !!role(panel, 'plan-card').querySelector('.agent-plan-list'),
    () => app.handle(event(ENTRIES))
  );
  const host = role(panel, 'plan-card');
  await act(async () => app.handle({ type: 'agent_workspace_closed', workspaceId: WS }));
  assert.equal(host.childElementCount, 0);
  assert.equal(host.dataset.islandState, undefined);
});
