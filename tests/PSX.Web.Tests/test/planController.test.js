import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

// PlanController: the workspace-local Plan card in the context-card column.
// Two-state visibility (visible/hidden) as per-workspace runtime state. The
// preferred state defaults to visible; narrow mode temporarily starts hidden.
// The toolbar toggle switches visibility and carries the unread dot
// while the card is hidden. The width is a fixed CSS token — no resizer.

const WS = '22222222-2222-4222-8222-222222222222';
const OTHER = '33333333-3333-4333-8333-333333333333';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function planEvent(workspaceId, entries = [{ content: 'Build feature', status: 'in_progress' }]) {
  return { type: 'plan_update', workspaceId, runId: 'run-1', entries };
}

async function mountWide() {
  const { app, panelFor, runtime } = await mountAgentApp({ wide: true });
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS), panelFor, posted: runtime.postedMessages };
}

function cardState(panel) {
  return {
    card: !role(panel, 'plan-card').hidden,
    toggleExpanded: role(panel, 'plan-toggle').getAttribute('aria-expanded'),
    unread: !role(panel, 'plan-toggle-unread').hidden
  };
}

test('PlanController: wide mode shows the empty card by default', async () => {
  const { panel } = await mountWide();
  assert.deepEqual(cardState(panel), { card: true, toggleExpanded: 'true', unread: false });
  assert.match(role(panel, 'plan-panel').textContent, /No active plan/);
});

test('PlanController: plan updates render in place without changing visibility', async () => {
  const { app, panel } = await mountWide();
  app.handle(planEvent(WS));
  assert.equal(panel.querySelectorAll('[data-role="plan-panel"] .agent-plan-item').length, 1);
  assert.deepEqual(cardState(panel), { card: true, toggleExpanded: 'true', unread: false });

  app.handle({ type: 'agent_cleared', workspaceId: WS });
  assert.match(role(panel, 'plan-panel').textContent, /No active plan/);
  assert.equal(role(panel, 'plan-card').hidden, false, 'clearing the plan never hides the card');
});

test('PlanController: the toolbar toggle hides and shows the card', async () => {
  const { panel } = await mountWide();
  role(panel, 'plan-toggle').click();
  assert.deepEqual(cardState(panel), { card: false, toggleExpanded: 'false', unread: false });

  role(panel, 'plan-toggle').click();
  assert.deepEqual(cardState(panel), { card: true, toggleExpanded: 'true', unread: false });
});

test('PlanController: a plan arriving while hidden raises the unread dot and showing clears it', async () => {
  const { app, panel } = await mountWide();
  role(panel, 'plan-toggle').click();
  assert.equal(role(panel, 'plan-card').hidden, true);

  app.handle(planEvent(WS, [{ content: 'New plan', status: 'pending' }]));
  assert.deepEqual(cardState(panel), { card: false, toggleExpanded: 'false', unread: true },
    'hidden card never auto-opens; the dot signals instead');

  role(panel, 'plan-toggle').click();
  assert.deepEqual(cardState(panel), { card: true, toggleExpanded: 'true', unread: false });
  assert.match(role(panel, 'plan-panel').textContent, /New plan/);
});

test('PlanController: the toggle aria-label reflects the unread state', async () => {
  const { app, panel } = await mountWide();
  const toggle = role(panel, 'plan-toggle');
  assert.equal(toggle.getAttribute('aria-label'), 'Toggle Plan card');

  role(panel, 'plan-toggle').click(); // hide
  app.handle(planEvent(WS, [{ content: 'New plan', status: 'pending' }]));
  assert.equal(toggle.getAttribute('aria-label'), 'Toggle Plan card, plan updated',
    'unread is part of the accessible name');

  role(panel, 'plan-toggle').click(); // show again
  assert.equal(toggle.getAttribute('aria-label'), 'Toggle Plan card',
    'label resets once the card is visible');
});

test('PlanController: narrow mode starts hidden so the card never appears unprompted', async () => {
  // The harness matchMedia stub reports narrow (matches: false).
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  assert.deepEqual(cardState(panel), { card: false, toggleExpanded: 'false', unread: false });
});

test('PlanController: visibility is independent across workspaces', async () => {
  const { app, panelFor, panel } = await mountWide();
  createAgentWorkspace(app, OTHER);
  const other = panelFor(OTHER);

  role(panel, 'plan-toggle').click();
  app.handle(planEvent(OTHER));

  assert.equal(role(panel, 'plan-card').hidden, true, 'WS stays hidden');
  assert.equal(role(other, 'plan-card').hidden, false, 'OTHER keeps its own default');
});

test('PlanController: a workspace created while narrow starts hidden', async () => {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  role(panelFor(WS), 'plan-toggle').click();
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false, 'first workspace has a temporary open');

  createAgentWorkspace(app, OTHER);
  assert.equal(role(panelFor(OTHER), 'plan-card').hidden, true,
    'new workspace receives the existing narrow mode immediately');
});

test('PlanController: a hidden narrow card raises unread and restores without persisting state', async () => {
  const { app, panelFor, breakpoint } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);

  app.handle(planEvent(WS, [{ content: 'Narrow update', status: 'pending' }]));
  assert.deepEqual(cardState(panel), { card: false, toggleExpanded: 'false', unread: true });

  breakpoint.setWide(true);
  assert.deepEqual(cardState(panel), { card: true, toggleExpanded: 'true', unread: false });
});

test('PlanController: the workspace template carries no inspector, history or resizer residue', async () => {
  const { panel } = await mountWide();
  for (const selector of [
    '[data-role="inspector"]',
    '[data-role="inspector-resizer"]',
    '[data-role="plan-tab"]',
    '[data-role="history-tab"]',
    '[data-role="history-panel"]',
    '[data-role="plan-resizer"]',
    '[data-role="plan-entry"]',
    '[data-role="plan-pin"]',
    '[data-role="plan-close"]',
    '.agent-inspector-tabs'
  ]) {
    assert.equal(panel.querySelector(selector), null, selector);
  }
  assert.ok(role(panel, 'context-cards'));
  assert.ok(role(panel, 'plan-card'));
  assert.ok(role(panel, 'plan-toggle'));
});
