import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import {
  mountAgentApp,
  createAgentWorkspace,
  flushAgentAnimationFrames,
  repositoryRoot
} from './agentHarness.js';

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function dock() {
  return document.querySelector('[data-role="history-dock"]');
}

function historyTrigger() {
  return document.querySelector('[data-role="global-history-toggle"]');
}

async function toolbarReady(panel) {
  for (let i = 0; i < 100 && !role(panel, 'plan-toggle'); i++) {
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  assert.ok(role(panel, 'plan-toggle'), 'workspace toolbar island did not mount');
}

test('responsive: only panes below 520px enter narrow Plan mode', async () => {
  const { app, breakpoint, panelFor } = await mountAgentApp({ wide: false });
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  await toolbarReady(panel);
  assert.equal(document.getElementById('agents').classList.contains('agent-shell-narrow'), true);
  assert.equal(role(panel, 'plan-card').hidden, true);

  breakpoint.setViewportWidth(600);
  flushAgentAnimationFrames();
  assert.equal(document.getElementById('agents').classList.contains('agent-shell-narrow'), false);
  assert.equal(role(panel, 'plan-card').hidden, false, 'the workspace preference restores above 519px');
});

test('responsive: global History remains a dock and can coexist with narrow Plan overlay', async () => {
  const { app, panelFor } = await mountAgentApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  const panel = panelFor(WS);
  await toolbarReady(panel);

  if (dock().hidden) historyTrigger().click();
  assert.equal(dock().hidden, false);
  role(panel, 'plan-toggle').click();
  assert.equal(role(panel, 'plan-card').hidden, false);
  assert.equal(dock().hidden, false, 'History is not auto-collapsed by a narrow Agent pane');
});

test('responsive: Escape closes a narrow Plan overlay and restores toggle focus', async () => {
  const { app, panelFor } = await mountAgentApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  const panel = panelFor(WS);
  await toolbarReady(panel);
  role(panel, 'plan-toggle').click();
  assert.equal(role(panel, 'plan-card').hidden, false);

  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  assert.equal(role(panel, 'plan-card').hidden, true);
  assert.equal(document.activeElement, role(panel, 'plan-toggle'));
});

test('responsive: Escape is inert above the Plan overlay breakpoint', async () => {
  const { app, panelFor } = await mountAgentApp({ wide: true });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  const panel = panelFor(WS);
  await toolbarReady(panel);
  assert.equal(role(panel, 'plan-card').hidden, false);
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  assert.equal(role(panel, 'plan-card').hidden, false);
});

test('responsive: a prevented Escape is not double-handled', async () => {
  const { app, panelFor } = await mountAgentApp({ wide: false });
  createAgentWorkspace(app, WS);
  app.handle({ type: 'workspace_activated', workspaceId: WS, kind: 'agent' });
  const panel = panelFor(WS);
  await toolbarReady(panel);
  role(panel, 'plan-toggle').click();
  const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
  event.preventDefault();
  document.dispatchEvent(event);
  assert.equal(role(panel, 'plan-card').hidden, false);
});

test('responsive CSS preserves dock/card shapes and reduced-motion discipline', () => {
  const history = fs.readFileSync(path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent', 'history.css'), 'utf8');
  const plan = fs.readFileSync(path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent', 'plan.css'), 'utf8');
  const shell = fs.readFileSync(path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent', 'shell.css'), 'utf8');
  // Narrow shells keep the History resizer available (no dock width fallback).
  assert.ok(
    !/\.agent-shell-narrow \.agent-history-dock-resizer/.test(history),
    'narrow shells must keep the History resizer available'
  );
  assert.match(plan, /@container agent-shell \(max-width: 519px\)/);
  assert.match(plan, /@keyframes agent-plan-card-in/);
  assert.match(history, /@keyframes agent-history-in/);
  assert.match(shell, /@media \(prefers-reduced-motion: reduce\)/);
});
