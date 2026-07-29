import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { mountAgentApp, createAgentWorkspace, composerReady, repositoryRoot } from './agentHarness.js';

// AgentShell layered layout (step 5): History dock at the bottom layer, the
// conversation canvas above it, the Plan card as a right-edge overlay. The
// reading column center is pinned to viewportWidth / 2 by explicit vw-based
// offsets in shell.css — pixel verification is manual (docs/testing.md); here
// we pin the DOM structure and the CSS contract the mechanism relies on.

const WS = '11111111-1111-4111-8111-111111111111';

function role(panel, name) {
  return panel.querySelector('[data-role="' + name + '"]');
}

// The workspace toolbar island renders the toggles asynchronously after
// workspace creation; poll until its first commit lands.
async function toolbarReady(panel) {
  for (let attempt = 0; attempt < 200 && !role(panel, 'plan-toggle'); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.ok(role(panel, 'plan-toggle'), 'workspace toolbar island did not mount');
}

function readCss(name) {
  return fs.readFileSync(path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent', name), 'utf8');
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
  assert.equal(workspace.children[0].dataset.role, 'conversation-host');

  assert.equal(cards.querySelector('[data-role="plan-resizer"]'), null, 'fixed-width card: no resizer');
  await toolbarReady(panel);
  assert.ok(panel.querySelector('.agent-toolbar [data-role="plan-toggle"]'), 'visibility toggle lives in the toolbar');

  // Agent chrome buttons share the soft border-input token; the raw `border`
  // color reads darker than inputs/selects and must not come back.
  for (const name of ['history-toggle', 'plan-toggle', 'update']) {
    assert.ok(
      role(panel, name).classList.contains('border-input'),
      name + ' must use the soft border-input token'
    );
  }
});

// The runtime Update button (former Clear slot) renders every
// runtime_update_status state and posts check_runtime_update on click.
test('shell: the toolbar Update button follows runtime_update_status states', async () => {
  const { app, runtime, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  await toolbarReady(panel);

  const update = () => role(panel, 'update');
  async function until(predicate, message) {
    for (let attempt = 0; attempt < 200 && !predicate(); attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.ok(predicate(), message);
  }

  // Default (idle): actionable before any status event arrives.
  assert.equal(update().textContent, 'Update');
  assert.equal(update().disabled, false);

  update().click();
  const request = runtime.postedMessages.at(-1);
  assert.equal(request.command, 'check_runtime_update');

  app.handle({ type: 'runtime_update_status', workspaceId: WS, state: 'checking', message: '', currentVersion: '1.2.3', pendingVersion: '' });
  await until(() => update().dataset.updateState === 'checking', 'checking state renders');
  assert.equal(update().disabled, true);

  app.handle({ type: 'runtime_update_status', workspaceId: WS, state: 'staged_restart_required', message: '', currentVersion: '1.2.3', pendingVersion: '2.0.0' });
  await until(() => update().dataset.updateState === 'staged_restart_required', 'staged state renders');
  assert.equal(update().textContent, 'Restart to update');
  assert.equal(update().disabled, true);
  assert.match(update().title, /2\.0\.0/);

  app.handle({ type: 'runtime_update_status', workspaceId: WS, state: 'unsupported', message: '', currentVersion: '0.29.1', pendingVersion: '' });
  await until(() => update().dataset.updateState === 'unsupported', 'unsupported state renders');
  assert.equal(update().disabled, true);
  assert.equal(update().title, 'Updates ship with PSX releases');

  app.handle({ type: 'runtime_update_status', workspaceId: WS, state: 'install_required', message: '', currentVersion: '', pendingVersion: '' });
  await until(() => update().dataset.updateState === 'install_required', 'install_required state renders');
  assert.equal(update().disabled, true);
  assert.equal(update().title, 'Install the Agent runtime first');

  app.handle({ type: 'runtime_update_status', workspaceId: WS, state: 'unavailable', message: '', currentVersion: '', pendingVersion: '' });
  await until(() => update().dataset.updateState === 'unavailable', 'unavailable state renders');
  assert.equal(update().disabled, true);
  assert.equal(update().title, 'Updates are not available for this workspace');

  app.handle({ type: 'runtime_update_status', workspaceId: WS, state: 'failed', message: 'npm exit code 1: network unreachable', currentVersion: '1.2.3', pendingVersion: '' });
  await until(() => update().dataset.updateState === 'failed', 'failed state renders');
  assert.equal(update().textContent, 'Retry update');
  assert.equal(update().disabled, false);
  // The backend failure reason surfaces as the tooltip so the user sees why.
  assert.match(update().title, /network unreachable/);

  app.handle({ type: 'runtime_update_status', workspaceId: WS, state: 'up_to_date', message: '', currentVersion: '1.2.3', pendingVersion: '' });
  await until(() => update().dataset.updateState === 'up_to_date', 'up_to_date state renders');
  assert.equal(update().textContent, 'Up to date');
  assert.equal(update().disabled, false);
});

test('shell: dock open toggles the canvas dock-open state class', async () => {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const container = document.getElementById('agents');
  assert.equal(container.classList.contains('agent-history-dock-open'), false);

  const panel = panelFor(WS);
  await composerReady(panel);
  role(panel, 'input').value = '/history';
  role(panel, 'input').dispatchEvent(new Event('input', { bubbles: true }));
  role(panel, 'input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
  for (let attempt = 0; attempt < 100 && !container.classList.contains('agent-history-dock-open'); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  assert.equal(container.classList.contains('agent-history-dock-open'), true);

  await toolbarReady(panel);
  role(panel, 'history-toggle').click();
  assert.equal(container.classList.contains('agent-history-dock-open'), false);
});

test('shell: structural tokens and the centerline mechanism exist in shell.css', () => {
  const shell = readCss('shell.css');
  for (const token of [
    '--agent-history-width: 280px',
    '--agent-history-narrow-width: 220px',
    '--agent-reading-max-width: 920px',
    '--agent-dock-gap: 16px',
    '--agent-radius-context-card: var(--agent-radius-card)',
    '--agent-radius-workspace-canvas: 18px',
    '--agent-shadow-canvas',
    '--agent-shadow-context-card',
    '--agent-reading-column-max',
    '--agent-reading-column-start'
  ]) {
    assert.ok(shell.includes(token), `shell.css missing ${token}`);
  }
  // Dock-open preserves the reading width and only moves its start enough to
  // clear History. There is no mirrored dock-width clearance on the right.
  assert.match(shell, /#agent-workspace-container\.agent-history-dock-open \.agent-panel/);
  assert.match(shell, /--agent-reading-column-start: max\(\s*var\(--agent-dock-gap\),/);
  assert.ok(!shell.includes('--agent-canvas-offset'));
  assert.ok(!shell.includes('--agent-side-clearance'));
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
  // Content-height card anchored below the toolbar at every viewport width.
  assert.match(plan, /top: calc\(var\(--agent-toolbar-height\) \+ var\(--agent-space-2\)\)/);
  assert.match(plan, /max-height: min\(60vh, 560px\)/);
  assert.ok(!plan.includes('agent-shell-compact'));
  assert.ok(!plan.includes('max-height: none'));
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
  const dir = path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent');
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
  const shell = readCss('shell.css');
  const view = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'agent', 'src', 'composer', 'ComposerView.tsx'),
    'utf8'
  );
  assert.match(composer, /width: var\(--agent-reading-column-max\)/);
  assert.match(composer, /margin-left: max\(0px, var\(--agent-reading-column-start\)\)/);
  assert.match(
    shell,
    /\.agent-thread-scroll-button\s*\{[\s\S]*?left:\s*calc\(\s*max\(0px, var\(--agent-reading-column-start\)\)\s*\+\s*var\(--agent-reading-column-max\) \/ 2\s*\)/,
    'return-to-bottom button is centred on the collision-aware reading column'
  );
  // The old three-column composer grid is gone. Footer geometry moved with
  // the React-owned Composer subtree and no longer has a parallel CSS owner.
  assert.ok(!composer.includes('grid-template-columns'), 'composer grid retired');
  assert.ok(!composer.includes('agent-plan-column'), 'plan-column separator retired');
  assert.ok(!composer.includes('.agent-composer-footer'));
  assert.match(view, /rounded-b-\[var\(--agent-radius-composer\)\]/);
});

test('shell: dock width stays persisted in wide mode and becomes fixed only while narrow', () => {
  const shell = readCss('shell.css');
  assert.match(shell, /--agent-history-width-effective: var\(--agent-history-width\);/);
  assert.match(shell, /#agent-workspace-container\.agent-shell-narrow \{\s*--agent-history-width-effective: var\(--agent-history-narrow-width\);?\s*\}/);
  // Canvas and the collision-aware reading rules consume the effective width,
  // never the raw one.
  assert.match(shell, /left: var\(--agent-history-width-effective\)/);
  assert.match(shell, /calc\(100vw - var\(--agent-history-width-effective\) - var\(--agent-dock-gap\) - var\(--agent-viewport-padding\)\)/);
  assert.match(shell, /calc\(\(100vw - var\(--agent-reading-column-max\)\) \/ 2 - var\(--agent-history-width-effective\)\)/);
  const history = readCss('history.css');
  assert.match(history, /width: var\(--agent-history-width-effective, 280px\)/);
  assert.match(history, /\.agent-shell-narrow \.agent-history-dock-resizer/);
});
