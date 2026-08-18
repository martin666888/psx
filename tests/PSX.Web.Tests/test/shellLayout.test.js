import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { mountAgentApp, createAgentWorkspace, composerReady, repositoryRoot } from './agentHarness.js';

// AgentShell layered layout (step 5): History dock at the bottom layer, the
// conversation canvas above it, the Plan card as a right-edge overlay. The
// reading column is centred in the remaining conversation panel by explicit
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
  for (const name of ['plan-toggle', 'update']) {
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

// The resident product version label sits beside the Update button; ACP
// details stay tooltip-only, and no version text renders when unknown.
test('shell: the toolbar shows the product version label and tooltip details', async () => {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const panel = panelFor(WS);
  await toolbarReady(panel);

  const update = () => role(panel, 'update');
  const version = () => role(panel, 'update-version');
  async function until(predicate, message) {
    for (let attempt = 0; attempt < 200 && !predicate(); attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.ok(predicate(), message);
  }

  // Product label renders; ACP adapter detail is tooltip-only, never in the
  // resident label text.
  app.handle({
    type: 'runtime_update_status',
    workspaceId: WS,
    state: 'up_to_date',
    message: '',
    currentVersion: '2.1.0',
    pendingVersion: '',
    versionLabel: 'Claude Code v2.1.0',
    versionDetail: 'ACP adapter 1.0.0'
  });
  await until(() => version() && version().textContent === 'Claude Code v2.1.0', 'product version renders');
  assert.match(update().title, /Current: 2\.1\.0/);
  assert.match(update().title, /ACP adapter 1\.0\.0/);
  assert.ok(!version().textContent.includes('ACP'), 'ACP detail stays out of the resident label');

  // Staged: still shows the current version, folds the pending version into
  // the tooltip and keeps the Restart label.
  app.handle({
    type: 'runtime_update_status',
    workspaceId: WS,
    state: 'staged_restart_required',
    message: '',
    currentVersion: '2.1.0',
    pendingVersion: '2.2.0',
    versionLabel: 'Claude Code v2.1.0',
    versionDetail: 'ACP adapter 1.0.0 \u2192 1.1.0'
  });
  await until(() => update().dataset.updateState === 'staged_restart_required', 'staged renders');
  assert.equal(version().textContent, 'Claude Code v2.1.0', 'staged keeps the current version label');
  assert.match(update().title, /Update ready: 2\.2\.0/);

  // Failed keeps the current version label and adds the reason to the tooltip.
  app.handle({
    type: 'runtime_update_status',
    workspaceId: WS,
    state: 'failed',
    message: 'network unreachable',
    currentVersion: '2.1.0',
    pendingVersion: '',
    versionLabel: 'Claude Code v2.1.0',
    versionDetail: 'ACP adapter 1.0.0'
  });
  await until(() => update().dataset.updateState === 'failed', 'failed renders');
  assert.equal(version().textContent, 'Claude Code v2.1.0');
  assert.match(update().title, /network unreachable/);

  // Unknown / uninstalled: no version label element at all (never v0/Unknown).
  app.handle({
    type: 'runtime_update_status',
    workspaceId: WS,
    state: 'install_required',
    message: '',
    currentVersion: '',
    pendingVersion: '',
    versionLabel: '',
    versionDetail: ''
  });
  await until(() => update().dataset.updateState === 'install_required', 'install_required renders');
  assert.equal(version(), null, 'no version text when the version is unknown');

  // Narrow shells hide the version text but keep the Update button.
  const shell = readCss('shell.css');
  assert.match(
    shell,
    /#agent-workspace-container\.agent-shell-narrow \.agent-update-version\s*\{\s*display: none/,
    'narrow shell hides the resident version label'
  );
});

test('shell: dock open toggles the canvas dock-open state class', async () => {
  const { app, panelFor } = await mountAgentApp();
  createAgentWorkspace(app, WS);
  const container = document.getElementById('agents');
  document.querySelector('[data-role="global-history-toggle"]').click();
  assert.equal(container.classList.contains('agent-history-dock-open'), false, 'global trigger closes the default-open dock');

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
  document.querySelector('[data-role="global-history-toggle"]').click();
  assert.equal(container.classList.contains('agent-history-dock-open'), false);
});

test('shell: structural tokens and the centerline mechanism exist in shell.css', () => {
  const shell = readCss('shell.css');
  for (const token of [
    '--agent-history-width: 280px',
    '--agent-reading-max-width: 920px',
    '--agent-workbench-gutter: 12px',
    '--agent-panel-gap: 12px',
    '--agent-radius-structure: 24px',
    '--agent-workspace-radius: var(--agent-radius-structure)',
    '--agent-radius-composer: var(--agent-radius-structure)',
    '--agent-radius-context-card: var(--agent-radius-card)',
    '--agent-shadow-canvas',
    '--agent-shadow-context-card',
    '--agent-reading-column-max',
    '--agent-reading-column-start'
  ]) {
    assert.ok(shell.includes(token), `shell.css missing ${token}`);
  }
  // The retired 16px dock gap must not come back; the workbench uses one
  // panel-gap token between the two panels.
  assert.ok(!shell.includes('--agent-dock-gap'));
  assert.ok(!shell.includes('--agent-radius-workspace-canvas'));
  // Dock-open centres the reading column inside the remaining main panel
  // (pre-layout fallback path; the JS dock inset supersedes it once the pane
  // geometry engine drives rects).
  assert.match(shell, /#agent-workspace-container\.agent-history-dock-open:not\(\.agent-layout-driven\) \.agent-panel/);
  assert.match(shell, /--agent-main-panel-inline-size: calc\(/);
  assert.match(shell, /--agent-reading-column-start: max\(\s*var\(--agent-panel-gap\),/);
  assert.match(
    shell,
    /calc\(\(var\(--agent-main-panel-inline-size\) - var\(--agent-reading-column-max\)\) \/ 2\)/
  );
  assert.ok(!shell.includes('--agent-canvas-offset'));
  assert.ok(!shell.includes('--agent-side-clearance'));
  // Shadows derive from the theme-driven --agent-shadow, never hardcoded colors.
  assert.match(shell, /--agent-shadow-canvas:.*color-mix\(in srgb, var\(--agent-shadow\)/);
  // Animations die under reduced motion.
  assert.match(shell, /@media \(prefers-reduced-motion: reduce\)/);
  // The luminance ladder: canvas surface derives from theme variables; the
  // panel is a free-standing rounded workbench block with a full border and
  // radius on every side (no hardcoded colors).
  assert.match(shell, /--agent-canvas-surface: color-mix\(in srgb, var\(--agent-surface\)/);
  assert.match(
    shell,
    /\.agent-panel\s*\{[\s\S]*?inset: var\(--agent-workbench-gutter\)[\s\S]*?border: 1px solid var\(--agent-border\);\s*border-radius: var\(--agent-workspace-radius\)/,
    'main panel is a rounded workbench block with gutter, full border and radius'
  );
  // The workbench backdrop only appears while an Agent workspace is active:
  // an optional theme tint wash (left-to-right fade) over the flat --agent-bg
  // base. The tint token defaults to transparent so themes opt in via
  // [agentTheme] workbenchTint.
  assert.ok(shell.includes('--agent-workbench-tint: transparent'));
  assert.match(
    shell,
    /#agent-workspace-container\.agent-workspace-active\s*\{[\s\S]*?linear-gradient\(90deg,\s*var\(--agent-workbench-tint\),\s*transparent 62%\),\s*var\(--agent-bg\)/,
    'active container paints the tint wash over the workbench backdrop'
  );
  // The toolbar is an in-panel top bar, not a full-window strip.
  assert.match(
    shell,
    /\.agent-toolbar\s*\{[\s\S]*?background: transparent/,
    'toolbar sits transparently on the panel surface'
  );
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
  assert.match(
    plan,
    /\.agent-plan-card\s*\{[\s\S]*?border:\s*1px solid var\(--agent-border-strong\)/,
    'Plan card uses the strong theme border so its outline survives dark themes'
  );
  // The Plan card's lift matches the composer exactly — no heavier halo.
  const shell = readCss('shell.css');
  assert.match(
    shell,
    /--agent-shadow-context-card: 0 6px 18px color-mix\(in srgb, var\(--agent-shadow\) 12%, transparent\)/,
    'context-card shadow shares the composer strength'
  );

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
    // Standard scrollbar properties suppress the ::-webkit-scrollbar skin in
    // Chromium and bring back native arrow-button scrollbars.
    assert.ok(!/scrollbar-color\s*:/.test(css), `${file} sets scrollbar-color`);
    assert.ok(!/scrollbar-width\s*:/.test(css), `${file} sets scrollbar-width`);
  }
  // The shared skin hides the arrow endpoints explicitly.
  const scrollbars = readCss('scrollbars.css');
  assert.match(
    scrollbars,
    /::-webkit-scrollbar-button[\s\S]*?\{\s*display: none/,
    'scrollbar arrow buttons are removed'
  );
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
    composer,
    /\.agent-composer-card\s*\{[\s\S]*?border:\s*1px solid var\(--agent-border-strong\)/,
    'Composer uses the strong theme border instead of relying on a dark-theme shadow'
  );

  const usage = readCss('usage.css');
  assert.match(
    usage,
    /\.agent-usage-panel\s*\{[\s\S]*?border:\s*1px solid var\(--agent-border-strong\)/,
    'Usage dialog shell uses the strong theme border so it separates from the dark overlay'
  );
  assert.match(
    usage,
    /\.agent-usage-avatar-button:focus-visible[\s\S]*?outline:\s*2px solid var\(--agent-focus-ring\)(?!\s*!important)/,
    'Usage native controls show the uniform keyboard focus ring without needing !important'
  );
  // Every pane uses the same 24px bottom anchor. Responsive bands may change
  // controls inside Composer, but content growth must proceed upward instead
  // of shifting the whole card vertically relative to a neighbour.
  assert.match(shell, /--agent-composer-bottom-space: 24px/);
  assert.match(
    composer,
    /\.agent-composer\s*\{[\s\S]*?padding: 0 0 var\(--agent-composer-bottom-space\)/,
    'Composer bottom padding uses the workbench breathing-room token'
  );
  assert.ok(
    !/@container agent-shell[\s\S]*?\.agent-composer\s*\{[\s\S]*?padding-bottom:/.test(composer),
    'responsive bands never override the cross-pane Composer bottom baseline'
  );
  // The lift stays restrained: no heavy dark halo on the composer card.
  assert.match(shell, /--agent-shadow-composer: 0 6px 18px color-mix\(in srgb, var\(--agent-shadow\) 12%, transparent\)/);
  assert.match(
    shell,
    /\.agent-thread-scroll-button\s*\{[\s\S]*?left:\s*calc\(\s*max\(0px, var\(--agent-reading-column-start\)\)\s*\+\s*var\(--agent-reading-column-max\) \/ 2\s*\)/,
    'return-to-bottom button is centred on the collision-aware reading column'
  );
  // The old three-column composer grid is gone. Footer geometry moved with
  // the React-owned Composer subtree and no longer has a parallel CSS owner.
  assert.ok(!composer.includes('grid-template-columns: auto 1fr'), 'old footer grid retired');
  assert.ok(!composer.includes('agent-plan-column'), 'plan-column separator retired');
  assert.match(composer, /\.agent-composer-footer[\s\S]*?min-width: 0/);
  assert.ok(!view.includes('max-[760px]'), 'viewport-responsive Tailwind variants are retired');
  assert.match(view, /rounded-b-\[var\(--agent-radius-composer\)\]/);
});

// The composer responsive bands: at ≤719px the configuration controls fold
// into the popover anchored to the composer controls row (no wrap band); at
// ≤519px the context usage hint hides but the footer stays on one row.
function containerBlock(css, width) {
  const header = `@container agent-shell (max-width: ${width}px) {`;
  const start = css.indexOf(header);
  assert.ok(start !== -1, `missing @container (max-width: ${width}px) block`);
  const bodyStart = start + header.length;
  const nextContainer = css.indexOf('@container', bodyStart);
  const nextMedia = css.indexOf('@media', bodyStart);
  const ends = [nextContainer, nextMedia].filter((index) => index !== -1);
  const end = ends.length ? Math.min(...ends) : css.length;
  return css.slice(bodyStart, end);
}

test('shell: composer bands fold config controls into the overlay and drop the wrap rules', () => {
  const composer = readCss('composer.css');
  const band719 = containerBlock(composer, 719);
  assert.match(band719, /\.agent-config-toggle/);
  assert.match(
    band719,
    /\.agent-config-popover\[data-open="true"\]\s*\{\s*display: block/,
    '719px block opens the popover from the toggle'
  );
  assert.match(band719, /\.agent-config-popover/);
  assert.match(band719, /\.agent-config-options/);
  assert.match(
    band719,
    /\.agent-config-popover \.agent-config-controls\s*\{[\s\S]*?display: grid;[\s\S]*?gap: var\(--agent-space-1\)/,
    'all compact configuration rows share one 4px grid rhythm'
  );
  assert.match(
    band719,
    /\.agent-config-popover \.agent-mode-control,[\s\S]*?\.agent-config-popover \.agent-config-control\s*\{[\s\S]*?min-height: 36px/,
    'select and switch configuration rows share one height'
  );
  assert.match(
    band719,
    /\.agent-composer-controls\s*\{\s*position: relative/,
    '719px block anchors the popover to the composer controls'
  );
  assert.ok(!band719.includes('flex-wrap: wrap'), 'no wrap band remains in the 719px block');
  assert.ok(!band719.includes('order: 5'), 'no wrap order remains in the 719px block');
  assert.ok(!band719.includes('flex: 1 0 100%'), 'no full-row flex remains in the 719px block');
  assert.ok(!band719.includes('margin-left: auto'), 'no send auto-margin remains in the 719px block');
  const band519 = containerBlock(composer, 519);
  assert.match(band519, /\.agent-hints\s*\{\s*display: none/);
  assert.match(band519, /flex-wrap: nowrap/);
  assert.ok(!band519.includes('order: initial'), 'no order reset once the 719px block sets no order');
});

test('shell: compact toolbar metadata and its text action share one baseline', () => {
  const shell = readCss('shell.css');
  assert.match(
    shell,
    /\.agent-toolbar-more-session\s*\{[\s\S]*?display: grid;[\s\S]*?grid-template-columns: minmax\(0, 1fr\) auto;[\s\S]*?align-items: baseline;/,
    'path metadata and Change align by their text baseline instead of independent box tops'
  );
});

test('shell: dock width stays persisted at every pane width and narrow never overrides it', () => {
  const shell = readCss('shell.css');
  // The narrow fixed-width mechanism is gone: no narrow-width token and no
  // effective-width indirection anywhere in the stylesheet.
  assert.ok(!shell.includes('--agent-history-width-effective'), 'the effective-width indirection is retired');
  assert.ok(!shell.includes('--agent-history-narrow-width'), 'the narrow fixed width is retired');
  // Canvas and the collision-aware reading rules consume the persisted width
  // directly — with the workbench gutter and panel gap folded into every
  // offset (the pre-layout fallback block keeps the 100vw math).
  assert.match(
    shell,
    /left: calc\(\s*var\(--agent-workbench-gutter\) \+ var\(--agent-history-width\) \+ var\(--agent-panel-gap\)\s*\)/
  );
  assert.match(
    shell,
    /calc\(var\(--agent-main-panel-inline-size\) - 2 \* var\(--agent-panel-gap\)\)/
  );
  assert.match(
    shell,
    /--agent-main-panel-inline-size: calc\(\s*100vw - 2 \* var\(--agent-workbench-gutter\) - var\(--agent-history-width\) - var\(--agent-panel-gap\)\s*\)/
  );
  assert.match(
    shell,
    /calc\(\(var\(--agent-main-panel-inline-size\) - var\(--agent-reading-column-max\)\) \/ 2\)/
  );
  const history = readCss('history.css');
  assert.match(history, /width: var\(--agent-history-width, 280px\)/);
  // The resizer stays usable in narrow shells; agent-shell-narrow survives
  // only for the Plan/toolbar/Composer responsive rules.
  assert.ok(
    !/\.agent-shell-narrow \.agent-history-dock-resizer/.test(history),
    'narrow shells must keep the History resizer available'
  );
});

test('shell: the History dock is a free-standing rounded workbench panel', () => {
  const history = readCss('history.css');
  // Gutter on top/bottom/left, full border and the shared workspace radius.
  assert.match(
    history,
    /\.agent-history-dock\s*\{[\s\S]*?top: calc\(40px \+ var\(--agent-workbench-gutter\)\);\s*bottom: var\(--agent-workbench-gutter\);\s*left: calc\(40px \+ var\(--agent-workbench-gutter\)\)/
  );
  assert.match(
    history,
    /\.agent-history-dock\s*\{[\s\S]*?border: 1px solid var\(--agent-border\);\s*border-radius: var\(--agent-workspace-radius\)/
  );
  assert.match(history, /\.agent-history-dock\s*\{[\s\S]*?background: var\(--agent-canvas-surface\)/);
  // The resize hit zone still straddles the right edge and the dock never
  // clips it away with overflow.
  assert.match(history, /\.agent-history-dock-resizer\s*\{[\s\S]*?right: -4px/);
  assert.ok(
    !/\.agent-history-dock\s*\{[^}]*overflow\s*:\s*hidden/.test(history),
    'dock must not clip the resizer with overflow: hidden'
  );
});

test('shell: long History titles fade before the dock edge without painting a theme color', () => {
  const history = readCss('history.css');
  assert.match(
    history,
    /\.agent-history-dock-content > div\s*\{[^}]*display:\s*block\s*!important;[^}]*width:\s*100%;[^}]*min-width:\s*0\s*!important;/,
    'the Radix measurement wrapper cannot expand past the History viewport'
  );
  assert.match(
    history,
    /\.agent-history-list\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/,
    'the History grid track is allowed to shrink below a long title\'s min-content width'
  );
  assert.match(
    history,
    /\.agent-history-title\s*\{[^}]*text-overflow:\s*clip;[^}]*-webkit-mask-image:\s*linear-gradient\([^}]*mask-image:\s*linear-gradient\(/,
    'thread titles use the Chromium-prefixed and standard alpha masks'
  );
  assert.match(
    history,
    /transparent calc\(100% - var\(--agent-space-1\)\)/,
    'the fade leaves an optical gap before the row edge'
  );
});
