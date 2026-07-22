import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {
  installAgentRuntime,
  agentTemplateMarkup,
  createAgentWorkspace,
  appModule,
  repositoryRoot
} from './agentHarness.js';

// Compact drawer mode (<1080px): History dock and Plan overlay become drawers,
// one at a time, with Esc/focus accessibility. matchMedia is stubbed per mount
// so tests can flip the breakpoint live.

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function dock() {
  return document.querySelector('[data-role="history-dock"]');
}

function trigger() {
  return document.querySelector('[data-role="history-trigger"]');
}

// Install a controllable matchMedia BEFORE createAgentApp reads it.
function stubBreakpoint(wide) {
  const listeners = new Set();
  const mql = {
    matches: wide,
    media: '(min-width: 1080px)',
    addEventListener: (type, fn) => listeners.add(fn),
    removeEventListener: (type, fn) => listeners.delete(fn)
  };
  window.matchMedia = () => mql;
  return {
    setWide(value) {
      mql.matches = value;
      for (const fn of listeners) fn();
    }
  };
}

async function mountApp({ wide = false } = {}) {
  const runtime = installAgentRuntime();
  document.body.innerHTML = `<div id="agents"></div>${agentTemplateMarkup()}`;
  const breakpoint = stubBreakpoint(wide);
  window.localStorage.setItem('psx.agent.historyDockOpen', '0');
  const { createAgentApp } = await appModule('entry.js');
  const terminal = { visible: true, setViewVisible(value) { this.visible = value; } };
  const app = createAgentApp({
    terminalManager: terminal,
    container: document.getElementById('agents'),
    template: document.getElementById('agent-workspace-template')
  });
  return {
    app,
    breakpoint,
    posted: runtime.postedMessages,
    container: document.getElementById('agents'),
    panelFor: (id) => document.querySelector(`.agent-panel[data-workspace-id="${id}"]`)
  };
}

function planEvent(workspaceId) {
  return { type: 'plan_update', workspaceId, runId: 'run-1', entries: [{ content: 'Build', status: 'in_progress' }] };
}

function escape() {
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
}

test('drawer: compact mode marks the container, wide mode does not', async () => {
  const compact = await mountApp({ wide: false });
  createAgentWorkspace(compact.app, WS);
  assert.equal(compact.container.classList.contains('agent-shell-compact'), true);

  const wide = await mountApp({ wide: true });
  createAgentWorkspace(wide.app, WS);
  assert.equal(wide.container.classList.contains('agent-shell-compact'), false);
});

test('drawer: opening History closes the active Plan card in compact mode', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  app.handle(planEvent(WS));
  const panel = panelFor(WS);
  assert.equal(role(panel, 'plan-card').hidden, false, 'auto mode expanded on the new plan');

  trigger().click();
  assert.equal(dock().hidden, false);
  assert.equal(role(panel, 'plan-card').hidden, true, 'one drawer at a time');
  assert.equal(role(panel, 'plan-entry').hidden, false);
});

test('drawer: expanding the Plan card closes the History drawer in compact mode', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  app.handle(planEvent(WS));
  trigger().click();
  assert.equal(dock().hidden, false);

  role(panelFor(WS), 'plan-entry').click();
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false);
  assert.equal(dock().hidden, true, 'Plan wins the drawer slot');
});

test('drawer: wide mode lets the dock and the Plan card coexist', async () => {
  const { app, panelFor } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  app.handle(planEvent(WS));
  trigger().click();

  assert.equal(dock().hidden, false);
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false, 'no exclusivity above the breakpoint');
});

test('drawer: crossing into compact enforces exclusivity, crossing back keeps state', async () => {
  const { app, breakpoint, panelFor } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  app.handle(planEvent(WS));
  trigger().click();
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false);

  breakpoint.setWide(false);
  assert.equal(dock().hidden, false, 'dock open maps to drawer open');
  assert.equal(role(panelFor(WS), 'plan-card').hidden, true, 'History wins the tie');

  breakpoint.setWide(true);
  assert.equal(dock().hidden, false, 'open state survives the round trip');
  assert.equal(role(panelFor(WS), 'plan-card').hidden, true, 'closed plan stays closed (runtime state)');
});

test('drawer: aria-expanded tracks both triggers', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });

  assert.equal(trigger().getAttribute('aria-expanded'), 'false');
  trigger().click();
  assert.equal(trigger().getAttribute('aria-expanded'), 'true');
  assert.equal(document.activeElement, document.querySelector('[data-role="history-search"]'),
    'focus moves into the drawer on open');

  app.handle(planEvent(WS));
  assert.equal(role(panelFor(WS), 'plan-entry').getAttribute('aria-expanded'), 'true');
});

test('drawer: Escape closes the History drawer and returns focus to the trigger', async () => {
  const { app } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  trigger().click();
  assert.equal(dock().hidden, false);

  escape();
  assert.equal(dock().hidden, true);
  assert.equal(document.activeElement, trigger(), 'focus is not lost to the body');
});

test('drawer: Escape closes the Plan drawer and returns focus to its entry', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  app.handle(planEvent(WS));
  const panel = panelFor(WS);
  assert.equal(role(panel, 'plan-card').hidden, false);

  escape();
  assert.equal(role(panel, 'plan-card').hidden, true);
  assert.equal(document.activeElement, role(panel, 'plan-entry'));
});

test('drawer: Escape is inert in wide mode', async () => {
  const { app } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  trigger().click();
  escape();
  assert.equal(dock().hidden, false, 'wide dock is not a drawer');
});

test('drawer: compact drawer styles and motion rules exist in CSS', () => {
  const history = fs.readFileSync(path.join(repositoryRoot, 'wwwroot', 'css', 'agent', 'history.css'), 'utf8');
  const plan = fs.readFileSync(path.join(repositoryRoot, 'wwwroot', 'css', 'agent', 'plan.css'), 'utf8');
  assert.match(history, /\.agent-shell-compact \.agent-history-dock/);
  assert.match(plan, /\.agent-shell-compact \.agent-context-cards/);
  assert.match(history, /@keyframes agent-history-in/);
  assert.match(plan, /@keyframes agent-plan-card-in/);
  const shell = fs.readFileSync(path.join(repositoryRoot, 'wwwroot', 'css', 'agent', 'shell.css'), 'utf8');
  assert.match(shell, /@media \(prefers-reduced-motion: reduce\)/);
});

test('drawer: Escape respects an already-handled (defaultPrevented) event', async () => {
  const { app } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  trigger().click();
  assert.equal(dock().hidden, false);

  const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
  event.preventDefault();
  document.dispatchEvent(event);
  assert.equal(dock().hidden, false, 'a prevented Escape is not double-handled');
});
