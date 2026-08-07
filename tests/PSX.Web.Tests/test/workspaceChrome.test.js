import { test } from 'vitest';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { installAgentRuntime, repositoryRoot } from './agentHarness.js';

const controllerUrl = pathToFileURL(
  path.join(repositoryRoot, 'frontend', 'webview', 'src', 'WorkspaceChromeController.js')
).href;

function mountChrome() {
  document.body.innerHTML = `
    <nav id="activity-rail">
      <button data-role="history-toggle"></button>
      <button data-role="workspace-menu-toggle"></button>
      <button data-role="workspace-create-toggle"></button>
      <button data-role="theme-toggle"></button>
    </nav>
    <header id="workspace-chrome">
      <div data-role="pane-nameplates"></div>
    </header>
    <div id="workspace-popover-root"></div>
    <div id="workspace-notices"></div>`;
}

test('workspace chrome merges layout and catalog in either arrival order without replacing nameplates', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  const snapshot = {
    revision: 3,
    focusedPaneId: 'pane-1',
    panes: [{ paneId: 'pane-1', workspaceId: 'workspace-1', kind: 'terminal' }]
  };
  chrome.applyLayout(snapshot, new Map([['pane-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  const plate = document.querySelector('.workspace-nameplate');
  assert.equal(plate.querySelector('.workspace-nameplate-title').textContent, 'Workspace');

  chrome.applyCatalog({
    revision: 7,
    providers: [],
    workspaces: [{ workspaceId: 'workspace-1', kind: 'terminal', title: 'PowerShell', iconKey: 'terminal', paneId: 'pane-1' }]
  });
  assert.equal(document.querySelector('.workspace-nameplate'), plate, 'catalog fills the stable nameplate in place');
  assert.equal(plate.querySelector('.workspace-nameplate-title').textContent, 'PowerShell');
  assert.equal(plate.style.left, '40px');
  assert.equal(plate.style.width, '960px');
  chrome.dispose();
});

test('workspace menu exposes typed attention text and activates background workspaces', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout({ focusedPaneId: 'pane-1', panes: [] }, new Map());
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    workspaces: [{
      workspaceId: '0f75ac98-09e7-4d43-bac0-255512fcb777', kind: 'agent', title: 'docs',
      providerName: 'Claude', attentionKind: 'question', paneId: null
    }]
  });
  document.querySelector('[data-role="workspace-menu-toggle"]').click();
  const row = document.querySelector('.workspace-menu-row');
  assert.match(row.textContent, /后台 · 待回复/);
  row.click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'workspace_layout_intent',
    action: 'activate',
    workspaceId: '0f75ac98-09e7-4d43-bac0-255512fcb777'
  });
  chrome.dispose();
});

test('create menu disables new-right entries the pixel capacity cannot fit', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout({ focusedPaneId: 'pane-1', panes: [{ paneId: 'pane-1', workspaceId: 'w1', kind: 'terminal' }] }, new Map());
  chrome.applyCatalog({ revision: 1, providers: [], workspaces: [], maxPanes: 4 });
  const segment = (text) =>
    [...document.querySelectorAll('.workspace-segments button')].find((b) => b.textContent === text);
  const row = (text) =>
    [...document.querySelectorAll('.workspace-menu-row')].find(
      (r) => r.querySelector('.workspace-menu-primary').textContent === text
    );

  // Agent capacity exhausted: new-right is disabled with the explanatory
  // title; the Terminal row keeps its own (passing) capacity.
  chrome.setCapacityChecker(() => ({ fitsAgent: false, fitsTerminal: true }));
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  assert.equal(segment('右侧新列').disabled, true);
  assert.equal(segment('右侧新列').title, '窗口宽度不足以容纳新列');
  assert.equal(segment('当前列').disabled, false);
  assert.equal(row('Terminal').disabled, false, 'terminal row unaffected at focused placement');
  assert.equal(chrome.createPlacement, 'focused');
  document.querySelector('[data-role="workspace-create-toggle"]').click();

  // Terminal capacity exhausted with new_right placement: only the Terminal
  // row is blocked, again with the capacity title.
  chrome.setCapacityChecker(() => ({ fitsAgent: true, fitsTerminal: false }));
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  segment('右侧新列').click();
  assert.equal(chrome.createPlacement, 'new_right');
  assert.equal(segment('右侧新列').disabled, false, 'agent capacity still fits');
  assert.equal(row('Terminal').disabled, true);
  assert.equal(row('Terminal').title, '窗口宽度不足以容纳新列');
  chrome.dispose();
});

test('pane menu blocks split-right on pixel capacity and prefers the C# reason', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(
    { focusedPaneId: 'pane-1', panes: [{ paneId: 'pane-1', workspaceId: 'w1', kind: 'agent' }] },
    new Map([['pane-1', { left: 40, top: 40, width: 960, height: 700 }]])
  );
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxPanes: 4,
    workspaces: [{
      workspaceId: 'w1', kind: 'agent', title: 'docs', iconKey: 'claude',
      paneId: 'pane-1', canSplitRight: true, splitBlockedReason: '',
      availableTargetPanes: [], canSwap: false, canCollapse: false
    }]
  });
  const splitRow = () =>
    [...document.querySelectorAll('.workspace-menu-row')].find(
      (r) => r.querySelector('.workspace-menu-primary').textContent === '移到右侧新列'
    );

  // Capacity exhausted: split-right is disabled with the capacity text.
  chrome.setCapacityChecker(() => ({ fitsAgent: false, fitsTerminal: true }));
  document.querySelector('.workspace-nameplate-more').click();
  assert.equal(splitRow().disabled, true);
  assert.equal(splitRow().querySelector('.workspace-menu-secondary').textContent, '窗口宽度不足以容纳新列');

  // The C# reason wins the secondary line when both layers block.
  chrome.applyCatalog({
    revision: 2,
    providers: [],
    maxPanes: 4,
    workspaces: [{
      workspaceId: 'w1', kind: 'agent', title: 'docs', iconKey: 'claude',
      paneId: 'pane-1', canSplitRight: false, splitBlockedReason: '最多四列',
      availableTargetPanes: [], canSwap: false, canCollapse: false
    }]
  });
  assert.equal(splitRow().disabled, true);
  assert.equal(splitRow().querySelector('.workspace-menu-secondary').textContent, '最多四列');
  chrome.dispose();
});

test('activity rail owns the four global buttons and the chrome row keeps only nameplates', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  const roles = [...document.querySelectorAll('#activity-rail button')].map((button) => button.dataset.role);
  assert.deepEqual(roles, [
    'history-toggle',
    'workspace-menu-toggle',
    'workspace-create-toggle',
    'theme-toggle'
  ], 'the four global buttons live in the activity rail in order');
  const header = document.getElementById('workspace-chrome');
  assert.equal(header.querySelectorAll('button').length, 0, 'chrome row owns no global buttons');
  assert.ok(header.querySelector('[data-role="pane-nameplates"]'), 'chrome row keeps the nameplate host');
  assert.equal(chrome.historyButton, document.querySelector('[data-role="history-toggle"]'));
  chrome.dispose();
});

test('global menus anchor to the trigger button top beside the activity rail', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout({ focusedPaneId: 'pane-1', panes: [] }, new Map());
  chrome.applyCatalog({ revision: 1, providers: [], workspaces: [] });
  const trigger = document.querySelector('[data-role="workspace-menu-toggle"]');
  trigger.getBoundingClientRect = () => ({ top: 123, left: 0, width: 40, height: 40 });
  trigger.click();
  const menu = document.querySelector('.workspace-popover');
  assert.ok(menu, 'workspace menu renders');
  assert.equal(menu.style.top, '123px', 'top follows the trigger button rect');
  assert.ok(
    menu.style.maxHeight.includes('100vh') && menu.style.maxHeight.includes('123px'),
    'max-height keeps the menu inside the viewport (CSSOM may reorder calc terms)'
  );
  assert.equal(menu.style.left, '', 'left stays CSS-owned (rail edge + gap), not inline');
  chrome.dispose();
});

test('pane menu anchors under its nameplate trigger, right-aligned to it', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(
    { focusedPaneId: 'pane-1', panes: [{ paneId: 'pane-1', workspaceId: 'w1', kind: 'agent' }] },
    new Map([['pane-1', { left: 40, top: 40, width: 960, height: 700 }]])
  );
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxPanes: 4,
    workspaces: [{
      workspaceId: 'w1', kind: 'agent', title: 'docs', iconKey: 'claude',
      paneId: 'pane-1', canSplitRight: true, splitBlockedReason: '',
      availableTargetPanes: [], canSwap: false, canCollapse: false
    }]
  });
  const trigger = document.querySelector('.workspace-nameplate-more');
  trigger.getBoundingClientRect = () => ({ top: 10, bottom: 34, left: 700, right: 732, width: 32, height: 24 });
  trigger.click();
  const menu = document.querySelector('.workspace-popover-pane');
  assert.ok(menu, 'pane menu renders');
  assert.equal(menu.style.top, '38px', 'top sits directly under the trigger');
  // jsdom reports offsetWidth 0: the right-align measurement (trigger right
  // 732 - width 0) lands exactly on the trigger's right edge; with a real
  // width the menu's right edge aligns to the trigger, clamped at 44px.
  assert.equal(menu.style.left, '732px', 'left comes from the post-mount right-align measurement');
  assert.equal(menu.style.transform, '', 'no transform — it would fight the popover-in animation');
  chrome.dispose();
});
