import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { mountAgentApp, createAgentWorkspace, repositoryRoot } from './agentHarness.js';

// AgentShell layered layout (step 5): History dock at the bottom layer, the
// conversation canvas above it, the Plan card as a right-edge overlay. The
// reading column center is pinned to viewportWidth / 2 by explicit vw-based
// offsets in shell.css — pixel verification is manual (docs/testing.md); here
// we pin the DOM structure and the CSS contract the mechanism relies on.

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

function readCss(name) {
  return fs.readFileSync(path.join(repositoryRoot, 'wwwroot', 'css', 'agent', name), 'utf8');
}

test('shell: the context-cards overlay sits outside the single-column workspace grid', async () => {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);

  const cards = role(panel, 'context-cards');
  assert.ok(cards);
  assert.equal(cards.parentElement, panel, 'overlay is a direct child of the canvas panel');
  assert.equal(panel.querySelector('.agent-workspace [data-role="context-cards"]'), null);

  const workspace = panel.querySelector('.agent-workspace');
  assert.equal(workspace.children.length, 1, 'workspace grid is back to a single column');
  assert.equal(workspace.children[0].dataset.role, 'thread');

  assert.equal(cards.querySelector('[data-role="plan-resizer"]'), null, 'fixed-width card: no resizer');
  assert.ok(panel.querySelector('.agent-toolbar [data-role="plan-toggle"]'), 'visibility toggle lives in the toolbar');
});

test('shell: dock open toggles the canvas dock-open state class', async () => {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const container = document.getElementById('agents');
  assert.equal(container.classList.contains('agent-history-dock-open'), false);

  const panel = panelFor(WS);
  role(panel, 'input').value = '/history';
  role(panel, 'input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
  assert.equal(container.classList.contains('agent-history-dock-open'), true);

  role(panel, 'history-toggle').click();
  assert.equal(container.classList.contains('agent-history-dock-open'), false);
});

test('shell: structural tokens and the centerline mechanism exist in shell.css', () => {
  const shell = readCss('shell.css');
  for (const token of [
    '--agent-history-width: 280px',
    '--agent-reading-max-width: 880px',
    '--agent-dock-gap: 16px',
    '--agent-radius-context-card: 12px',
    '--agent-radius-workspace-canvas: 16px',
    '--agent-shadow-canvas',
    '--agent-shadow-context-card',
    '--agent-reading-column-max',
    '--agent-reading-column-start',
    '--agent-canvas-offset'
  ]) {
    assert.ok(shell.includes(token), `shell.css missing ${token}`);
  }
  // dock-open: canvas offset + symmetric clearance are what keep the center fixed.
  assert.match(shell, /#agent-workspace-container\.agent-history-dock-open:not\(\.agent-shell-compact\) \.agent-panel/);
  assert.match(shell, /--agent-side-clearance: calc\(var\(--agent-history-width-effective\) \+ var\(--agent-dock-gap\)\)/);
  // Shadows derive from the theme-driven --agent-shadow, never hardcoded colors.
  assert.match(shell, /--agent-shadow-canvas:.*color-mix\(in srgb, var\(--agent-shadow\)/);
  // Animations die under reduced motion.
  assert.match(shell, /@media \(prefers-reduced-motion: reduce\)/);
  // The luminance ladder: canvas surface derives from theme variables and the
  // dock-open canvas edge gets a border + radius + shadow (no hardcoded colors).
  assert.match(shell, /--agent-canvas-surface: color-mix\(in srgb, var\(--agent-surface\)/);
  assert.match(shell, /border-left: 1px solid var\(--agent-border\)/);
  assert.match(shell, /--agent-toolbar-height: 44px/);
});

test('shell: the Plan card and the runtime card follow the new canvas rules', () => {
  const plan = readCss('plan.css');
  // Content-height card anchored below the toolbar; only compact mode fills height.
  assert.match(plan, /top: calc\(var\(--agent-toolbar-height\) \+ var\(--agent-space-2\)\)/);
  assert.match(plan, /max-height: min\(60vh, 560px\)/);
  assert.match(plan, /\.agent-shell-compact \.agent-plan-card/);
  assert.ok(!plan.includes('agent-plan-resizer'), 'resizer styles retired');
  assert.ok(!plan.includes('agent-plan-entry'), 'entry styles retired');

  const runtime = readCss('runtime.css');
  // The install card rides the same reading column as the conversation.
  assert.match(runtime, /width: var\(--agent-reading-column-max\)/);
  assert.match(runtime, /margin: 12px auto 0 max\(0px, var\(--agent-reading-column-start\)\)/);
  // Border-box keeps the outer width identical to the composer row despite
  // the card's own padding and 1px border (content-box would make it ~34px
  // wider than the input row above).
  assert.match(runtime, /box-sizing: border-box/);
});

test('shell: retired layout tokens are gone from every agent stylesheet', () => {
  const dir = path.join(repositoryRoot, 'wwwroot', 'css', 'agent');
  for (const file of fs.readdirSync(dir)) {
    if (!file.endsWith('.css')) continue;
    const css = fs.readFileSync(path.join(dir, file), 'utf8');
    assert.ok(!css.includes('--agent-conversation-max-width'), `${file} still uses --agent-conversation-max-width`);
    assert.ok(!css.includes('--agent-history-dock-width'), `${file} still uses --agent-history-dock-width`);
    assert.ok(!css.includes('--agent-content-inline-padding'), `${file} still uses --agent-content-inline-padding`);
  }
});

test('shell: composer and conversation share the same reading-column rules', () => {
  const composer = readCss('composer.css');
  assert.match(composer, /width: var\(--agent-reading-column-max\)/);
  assert.match(composer, /margin-left: max\(0px, var\(--agent-reading-column-start\)\)/);
  // The three-column composer grid and its plan-column separator are gone.
  assert.ok(!composer.includes('grid-template-columns'), 'composer grid retired');
  assert.ok(!composer.includes('::after'), 'plan-column separator retired');
});

test('shell: the effective dock width is capped only in wide mode', () => {
  const shell = readCss('shell.css');
  // Base (compact drawer) is uncapped: effective width follows the dock width.
  assert.match(shell, /--agent-history-width-effective: var\(--agent-history-width\)/);
  // Wide mode caps at the largest width that keeps a min-width column centered.
  assert.match(
    shell,
    /#agent-workspace-container:not\(\.agent-shell-compact\) \{\s*--agent-history-width-effective: min\(\s*var\(--agent-history-width\),\s*calc\(\(100vw - var\(--agent-reading-min-width\)\) \/ 2 - var\(--agent-dock-gap\)\)\s*\);?\s*\}/
  );
  // Canvas and reading rules consume the effective width, never the raw one.
  assert.match(shell, /left: var\(--agent-history-width-effective\)/);
  assert.match(shell, /--agent-canvas-offset: var\(--agent-history-width-effective\)/);
  assert.match(shell, /--agent-side-clearance: calc\(var\(--agent-history-width-effective\) \+ var\(--agent-dock-gap\)\)/);
  // 1080px cap = (1080-320)/2-16 = 364px; 1440px cap = 544 > 420 (uncapped).
  const history = readCss('history.css');
  assert.match(history, /width: var\(--agent-history-width-effective, 280px\)/);
});
