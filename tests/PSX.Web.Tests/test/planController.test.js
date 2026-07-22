import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mountAgentApp, createAgentWorkspace } from './agentHarness.js';

// PlanController: the workspace-local Plan card in the context-card column.
// Card mode (auto/pinned/closed) is per-workspace runtime state; the width is
// a global preference with a one-time legacy migration.

const WS = '22222222-2222-4222-8222-222222222222';
const OTHER = '33333333-3333-4333-8333-333333333333';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function planEvent(workspaceId, entries = [{ content: 'Build feature', status: 'in_progress' }]) {
  return { type: 'plan_update', workspaceId, runId: 'run-1', entries };
}

async function mount() {
  const { app, panelFor, runtime } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  return { app, panel: panelFor(WS), panelFor, posted: runtime.postedMessages };
}

function container() {
  return document.getElementById('agents');
}

function expanded(panel) {
  return {
    card: !role(panel, 'plan-card').hidden,
    entry: !role(panel, 'plan-entry').hidden,
    unread: !role(panel, 'plan-unread').hidden
  };
}

// --- three-state card mode ---------------------------------------------------

test('PlanController: auto mode collapses to the entry without a plan and expands on plan_update', async () => {
  const { app, panel } = await mount();
  assert.deepEqual(expanded(panel), { card: false, entry: true, unread: false });

  app.handle(planEvent(WS));
  assert.deepEqual(expanded(panel), { card: true, entry: false, unread: false });
  assert.equal(panel.querySelectorAll('[data-role="plan-panel"] .agent-plan-item').length, 1);

  app.handle({ type: 'agent_cleared', workspaceId: WS });
  assert.deepEqual(expanded(panel), { card: false, entry: true, unread: false }, 'auto collapses again when the plan clears');
});

test('PlanController: pinned mode keeps the empty plan card expanded', async () => {
  const { app, panel } = await mount();
  // The idle auto entry pins the card open (empty state stays reachable).
  role(panel, 'plan-entry').click();
  assert.deepEqual(expanded(panel), { card: true, entry: false, unread: false });
  assert.match(role(panel, 'plan-panel').textContent, /No active plan/);
  assert.equal(role(panel, 'plan-pin').getAttribute('aria-pressed'), 'true');

  app.handle({ type: 'agent_cleared', workspaceId: WS });
  assert.deepEqual(expanded(panel), { card: true, entry: false, unread: false }, 'pinned never collapses');

  role(panel, 'plan-pin').click();
  assert.equal(role(panel, 'plan-pin').getAttribute('aria-pressed'), 'false');
  assert.deepEqual(expanded(panel), { card: false, entry: true, unread: false }, 'unpin returns to auto');
});

test('PlanController: closed mode only raises the unread badge and expanding clears it', async () => {
  const { app, panel } = await mount();
  app.handle(planEvent(WS));
  role(panel, 'plan-close').click();
  assert.deepEqual(expanded(panel), { card: false, entry: true, unread: false });

  app.handle(planEvent(WS, [{ content: 'New plan', status: 'pending' }]));
  assert.deepEqual(expanded(panel), { card: false, entry: true, unread: true }, 'no auto-expand while closed');

  role(panel, 'plan-entry').click();
  assert.deepEqual(expanded(panel), { card: true, entry: false, unread: false }, 'opening clears the badge');
  assert.match(role(panel, 'plan-panel').textContent, /New plan/);
});

test('PlanController: card modes are independent across workspaces', async () => {
  const { app, panelFor } = await mount();
  createAgentWorkspace(app, OTHER);
  const other = panelFor(OTHER);

  app.handle(planEvent(WS));
  role(panelFor(WS), 'plan-close').click();
  app.handle(planEvent(OTHER));
  app.handle(planEvent(WS, [{ content: 'Updated', status: 'pending' }]));

  assert.deepEqual(expanded(panelFor(WS)), { card: false, entry: true, unread: true });
  assert.deepEqual(expanded(other), { card: true, entry: false, unread: false });
});

// --- width preference + migration -----------------------------------------------

test('PlanController: defaults to 320px and clamps the stored width into 280-400', async () => {
  const fresh = await mount();
  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '320px');
  assert.equal(fresh.panel.style.getPropertyValue('--agent-plan-width'), '', 'panels never carry their own width');
});

test('PlanController: migrates the legacy inspector width once and never reads it again', async () => {
  const cases = [
    ['260', '280px'],
    ['340', '340px'],
    ['480', '400px'],
    ['garbage', '320px']
  ];
  for (const [legacy, expected] of cases) {
    const { app, panelFor } = await mountAgentApp();
    window.localStorage.setItem('psx.agent.inspectorWidth', legacy);
    createAgentWorkspace(app, WS);
    assert.equal(container().style.getPropertyValue('--agent-plan-width'), expected, `legacy ${legacy}`);
  }

  const { app, panelFor } = await mountAgentApp();
  window.localStorage.setItem('psx.agent.inspectorWidth', '300');
  createAgentWorkspace(app, WS);
  assert.equal(window.localStorage.getItem('psx.agent.planWidth'), '300', 'migration writes the new key');

  // After the migration the legacy key is ignored, even if it changes.
  window.localStorage.setItem('psx.agent.inspectorWidth', '380');
  createAgentWorkspace(app, OTHER);
  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '300px');
});

test('PlanController: keyboard resizing adjusts and persists the shared width', async () => {
  const { panel } = await mount();
  const resizer = role(panel, 'plan-resizer');
  resizer.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft' }));
  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '336px');
  assert.equal(window.localStorage.getItem('psx.agent.planWidth'), '336');

  resizer.dispatchEvent(new KeyboardEvent('keydown', { key: 'End' }));
  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '280px');
  resizer.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home' }));
  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '400px');
});

test('PlanController: pointer resizing measures against the inset overlay column', async () => {
  const { panel } = await mount();
  const cards = role(panel, 'context-cards');
  const resizer = role(panel, 'plan-resizer');
  // jsdom has no layout: stub the overlay geometry (right inset included) and
  // drag the left-edge resizer.
  cards.getBoundingClientRect = () => ({
    right: 500, left: 180, top: 0, bottom: 600, width: 320, height: 600, x: 180, y: 0, toJSON() { return {}; }
  });
  resizer.setPointerCapture = () => {};
  resizer.releasePointerCapture = () => {};
  const pointer = (type, clientX) => {
    const event = new Event(type);
    event.button = 0;
    event.pointerId = 1;
    event.clientX = clientX;
    return event;
  };

  resizer.dispatchEvent(pointer('pointerdown', 180));
  resizer.dispatchEvent(pointer('pointermove', 140));
  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '360px', 'width = overlay right edge - pointer x');

  resizer.dispatchEvent(pointer('pointermove', 400));
  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '280px', 'clamped at the minimum');
  resizer.dispatchEvent(pointer('pointerup', 400));
  assert.equal(window.localStorage.getItem('psx.agent.planWidth'), '280');
});

test('PlanController: width writes go to the shared container for every workspace', async () => {
  const { app, panelFor } = await mount();
  createAgentWorkspace(app, OTHER);
  const resizer = role(panelFor(WS), 'plan-resizer');
  resizer.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft' }));

  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '336px');
  assert.equal(panelFor(WS).style.getPropertyValue('--agent-plan-width'), '', 'no panel-level override on A');
  assert.equal(panelFor(OTHER).style.getPropertyValue('--agent-plan-width'), '', 'no panel-level override on B');

  app.handle({ type: 'workspace_activated', workspaceId: OTHER, kind: 'agent' });
  assert.equal(container().style.getPropertyValue('--agent-plan-width'), '336px', 'activation keeps the shared width');
});

// --- template contract -------------------------------------------------------------

test('PlanController: the workspace template carries no inspector or history residue', async () => {
  const { panel } = await mount();
  for (const selector of [
    '[data-role="inspector"]',
    '[data-role="inspector-resizer"]',
    '[data-role="plan-tab"]',
    '[data-role="history-tab"]',
    '[data-role="history-panel"]',
    '.agent-inspector-tabs'
  ]) {
    assert.equal(panel.querySelector(selector), null, selector);
  }
  assert.ok(role(panel, 'context-cards'));
  assert.ok(role(panel, 'plan-card'));
});
