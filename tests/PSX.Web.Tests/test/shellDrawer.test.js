import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {
  installAgentRuntime,
  installBreakpoint,
  agentTemplateMarkup,
  createAgentWorkspace,
  appModule,
  repositoryRoot
} from './agentHarness.js';

// Narrow mode (<1000px): History remains a dock and Plan remains a content
// card, but both auto-collapse and reopen one at a time. matchMedia is
// controllable so tests can flip the breakpoint live.

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function dock() {
  return document.querySelector('[data-role="history-dock"]');
}

// The dock's open/close control lives in every workspace toolbar.
function trigger(panel) {
  return panel.querySelector('[data-role="history-toggle"]');
}

async function mountApp({ wide = false } = {}) {
  const runtime = installAgentRuntime();
  document.body.innerHTML = `<div id="agents"></div>${agentTemplateMarkup()}`;
  const breakpoint = installBreakpoint(wide);
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

function escape() {
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
}

test('responsive: narrow mode marks the container without using drawer state', async () => {
  const narrow = await mountApp({ wide: false });
  createAgentWorkspace(narrow.app, WS);
  assert.equal(narrow.container.classList.contains('agent-shell-narrow'), true);
  assert.equal(narrow.container.classList.contains('agent-shell-compact'), false);

  const wide = await mountApp({ wide: true });
  createAgentWorkspace(wide.app, WS);
  assert.equal(wide.container.classList.contains('agent-shell-narrow'), false);
});

test('responsive: opening History closes the active Plan card in narrow mode', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  const panel = panelFor(WS);
  // Narrow starts hidden; the user temporarily opens the Plan card.
  role(panel, 'plan-toggle').click();
  assert.equal(role(panel, 'plan-card').hidden, false);

  trigger(panel).click();
  assert.equal(dock().hidden, false);
  assert.equal(role(panel, 'plan-card').hidden, true, 'one narrow panel at a time');
  assert.equal(role(panel, 'plan-toggle').getAttribute('aria-expanded'), 'false');
});

test('responsive: showing the Plan card closes History in narrow mode', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  trigger(panelFor(WS)).click();
  assert.equal(dock().hidden, false);

  role(panelFor(WS), 'plan-toggle').click();
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false);
  assert.equal(dock().hidden, true, 'Plan wins the narrow slot');
});

test('responsive: wide mode lets the dock and the Plan card coexist', async () => {
  const { app, panelFor } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  // Wide mode shows the Plan card by default; both panels can coexist.
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false);
  trigger(panelFor(WS)).click();

  assert.equal(dock().hidden, false);
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false, 'no exclusivity above the breakpoint');
});

test('responsive: entering narrow auto-collapses both and widening restores preferences', async () => {
  const { app, breakpoint, panelFor } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  trigger(panelFor(WS)).click();
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false, 'wide default: plan visible');

  breakpoint.setWide(false);
  assert.equal(dock().hidden, true, 'History auto-collapses');
  assert.equal(role(panelFor(WS), 'plan-card').hidden, true, 'Plan auto-collapses');

  breakpoint.setWide(true);
  assert.equal(dock().hidden, false, 'History preference restores on wide return');
  assert.equal(role(panelFor(WS), 'plan-card').hidden, false, 'Plan preference restores on wide return');
});

test('responsive: History yields before it would squeeze the full reading column', async () => {
  const { app, breakpoint, container, panelFor } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  const panel = panelFor(WS);
  trigger(panel).click();
  assert.equal(dock().hidden, false, 'History is visible while the full column fits');

  // Default 280px History: 280 + 16px gap + 920px reading + 24px edge = 1240px.
  breakpoint.setViewportWidth(1239);
  assert.equal(dock().hidden, true, 'the dock yields instead of shrinking the reading column');
  assert.equal(container.classList.contains('agent-shell-history-collapsed-for-reading'), true);
  assert.equal(role(panel, 'plan-card').hidden, false, 'Plan stays an independent overlay');
  assert.equal(window.localStorage.getItem('psx.agent.historyDockOpen'), '1', 'responsive collapse is not persisted');

  breakpoint.setViewportWidth(1240);
  assert.equal(dock().hidden, false, 'History restores once the full column fits again');
  assert.equal(container.classList.contains('agent-shell-history-collapsed-for-reading'), false);
});

test('responsive: an explicit History open remains available in the reading-constrained range', async () => {
  const { app, breakpoint, panelFor } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  breakpoint.setViewportWidth(1239);
  const panel = panelFor(WS);

  trigger(panel).click();
  assert.equal(dock().hidden, false, 'the user can temporarily prioritize History');
  assert.equal(window.localStorage.getItem('psx.agent.historyDockOpen'), '0', 'temporary open leaves the preference unchanged');
});

test('responsive: aria-expanded tracks both narrow-panel triggers', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });

  const historyToggle = trigger(panelFor(WS));
  assert.equal(historyToggle.getAttribute('aria-expanded'), 'false');
  historyToggle.click();
  assert.equal(historyToggle.getAttribute('aria-expanded'), 'true');
  assert.equal(document.activeElement, document.querySelector('[data-role="history-search"]'),
    'focus moves into History search on deliberate open');

  const toggle = role(panelFor(WS), 'plan-toggle');
  assert.equal(toggle.getAttribute('aria-expanded'), 'false', 'narrow starts hidden');
  toggle.click();
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
});

test('responsive: Escape closes narrow History and returns focus to the toolbar toggle', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  trigger(panelFor(WS)).click();
  assert.equal(dock().hidden, false);

  escape();
  assert.equal(dock().hidden, true);
  assert.equal(document.activeElement, trigger(panelFor(WS)), 'focus is not lost to the body');
});

test('responsive: Escape closes the narrow Plan card and returns focus to its toggle', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  const panel = panelFor(WS);
  role(panel, 'plan-toggle').click();
  assert.equal(role(panel, 'plan-card').hidden, false);

  escape();
  assert.equal(role(panel, 'plan-card').hidden, true);
  assert.equal(document.activeElement, role(panel, 'plan-toggle'));
});

test('responsive: Escape is inert in wide mode', async () => {
  const { app, panelFor } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  trigger(panelFor(WS)).click();
  escape();
  assert.equal(dock().hidden, false, 'wide dock stays open');
});

test('responsive: narrow styles preserve panel shapes and motion rules', () => {
  const history = fs.readFileSync(path.join(repositoryRoot, 'wwwroot', 'css', 'agent', 'history.css'), 'utf8');
  const plan = fs.readFileSync(path.join(repositoryRoot, 'wwwroot', 'css', 'agent', 'plan.css'), 'utf8');
  assert.match(history, /\.agent-shell-narrow \.agent-history-dock-resizer/);
  assert.ok(!history.includes('agent-shell-compact'));
  assert.ok(!plan.includes('agent-shell-compact'));
  assert.ok(!plan.includes('max-height: none'));
  assert.match(history, /@keyframes agent-history-in/);
  assert.match(plan, /@keyframes agent-plan-card-in/);
  const shell = fs.readFileSync(path.join(repositoryRoot, 'wwwroot', 'css', 'agent', 'shell.css'), 'utf8');
  assert.match(shell, /@media \(prefers-reduced-motion: reduce\)/);
});

test('responsive: Escape respects an already-handled (defaultPrevented) event', async () => {
  const { app, panelFor } = await mountApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  trigger(panelFor(WS)).click();
  assert.equal(dock().hidden, false);

  const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
  event.preventDefault();
  document.dispatchEvent(event);
  assert.equal(dock().hidden, false, 'a prevented Escape is not double-handled');
});

test('responsive: widening restores History without stealing Composer focus', async () => {
  const { app, breakpoint, panelFor } = await mountApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  const panel = panelFor(WS);

  trigger(panel).click();
  role(panel, 'input').focus();
  breakpoint.setWide(false);
  assert.equal(dock().hidden, true);

  breakpoint.setWide(true);
  assert.equal(dock().hidden, false);
  assert.equal(document.activeElement, role(panel, 'input'));
});
