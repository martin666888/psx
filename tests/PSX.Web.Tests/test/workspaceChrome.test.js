// workspaceChrome.test.js — the WebView shell chrome: per-column tab strips
// in the 40px workspace row, the open-tab workspace list, the create menu's
// column-cap gates and the column menu. Wire shape: workspace_layout carries
// focusedColumnId + columns[] (tabs/activeTabId/ratio); the catalog carries
// per-workspace columnId/isActiveTab and maxColumns.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
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

function columnSnapshot(columns, focusedColumnId = columns[0]?.columnId ?? 'column-1') {
  return { revision: 1, focusedColumnId, columns };
}

test('chrome merges layout and catalog in either arrival order without replacing tab strips', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'workspace-1', kind: 'terminal' }], activeTabId: 'workspace-1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  const strip = document.querySelector('.workspace-tab-strip');
  assert.equal(strip.querySelector('.workspace-tab-title').textContent, 'Workspace');

  chrome.applyCatalog({
    revision: 7,
    providers: [],
    maxColumns: 3,
    workspaces: [{ workspaceId: 'workspace-1', kind: 'terminal', title: 'PowerShell', iconKey: 'terminal', columnId: 'column-1', isActiveTab: true }]
  });
  assert.equal(document.querySelector('.workspace-tab-strip'), strip, 'catalog fills the stable strip in place');
  assert.equal(document.querySelector('.workspace-tab-title').textContent, 'PowerShell');
  assert.equal(strip.style.left, '40px');
  assert.equal(strip.style.width, '960px');
  chrome.dispose();
});

test('workspace menu is the open-tab overview: column labels and a pure-jump activate', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  const docsId = '0f75ac98-09e7-4d43-bac0-255512fcb777';
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'w1', kind: 'agent' }], activeTabId: 'w1', ratio: 0.5 },
    { columnId: 'column-2', tabs: [{ workspaceId: docsId, kind: 'agent' }], activeTabId: docsId, ratio: 0.5 }
  ], 'column-2'), new Map());
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: docsId, kind: 'agent', title: 'docs',
      providerName: 'Claude', attentionKind: 'question', columnId: 'column-2', isActiveTab: true
    }]
  });
  document.querySelector('[data-role="workspace-menu-toggle"]').click();
  const row = document.querySelector('.workspace-menu-row');
  assert.match(row.textContent, /第 2 列\(激活\) · 待回复/);
  row.click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'workspace_layout_intent',
    action: 'activate',
    workspaceId: docsId
  });
  chrome.dispose();
});

test('workspace list marks an inactive tab of a visible column without an (激活) label', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  const docsId = '0f75ac98-09e7-4d43-bac0-255512fcb777';
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [
      { workspaceId: 'w1', kind: 'agent' },
      { workspaceId: docsId, kind: 'agent' }
    ], activeTabId: 'w1', ratio: 1 }
  ], 'column-1'), new Map());
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: docsId, kind: 'agent', title: 'docs',
      providerName: 'Claude', columnId: 'column-1', isActiveTab: false
    }]
  });
  document.querySelector('[data-role="workspace-menu-toggle"]').click();
  const row = document.querySelector('.workspace-menu-row');
  assert.match(row.textContent, /第 1 列/);
  assert.doesNotMatch(row.textContent, /激活/);
  assert.doesNotMatch(row.textContent, /后台|临时收编/);
  chrome.dispose();
});

test('create menu disables new-right on pixel capacity and on the 3-column cap', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'w1', kind: 'terminal' }], activeTabId: 'w1', ratio: 1 }
  ]), new Map());
  chrome.applyCatalog({ revision: 1, providers: [], workspaces: [], maxColumns: 3 });
  const segment = (text) =>
    [...document.querySelectorAll('.workspace-segments button')].find((b) => b.textContent === text);
  const row = (text) =>
    [...document.querySelectorAll('.workspace-menu-row')].find(
      (r) => r.querySelector('.workspace-menu-primary').textContent === text
    );

  // Agent capacity exhausted: new-right is disabled with the explanatory
  // title; the Terminal row keeps its own (passing) capacity.
  chrome.setCapacityChecker(() => ({ fitsAgent: false, fitsTerminal: true, requestedColumnCount: 1 }));
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  assert.equal(segment('右侧新列').disabled, true);
  assert.equal(segment('右侧新列').title, '窗口宽度不足以容纳新列');
  assert.equal(segment('当前列').disabled, false);
  assert.equal(row('Terminal').disabled, false, 'terminal row unaffected at focused placement');
  assert.equal(chrome.createPlacement, 'focused');
  document.querySelector('[data-role="workspace-create-toggle"]').click();

  // Terminal capacity exhausted with new_right placement: only the Terminal
  // row is blocked, again with the capacity title.
  chrome.setCapacityChecker(() => ({ fitsAgent: true, fitsTerminal: false, requestedColumnCount: 1 }));
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  segment('右侧新列').click();
  assert.equal(chrome.createPlacement, 'new_right');
  assert.equal(segment('右侧新列').disabled, false, 'agent capacity still fits');
  assert.equal(row('Terminal').disabled, true);
  assert.equal(row('Terminal').title, '窗口宽度不足以容纳新列');
  document.querySelector('[data-role="workspace-create-toggle"]').click();

  // Column cap reached (3 requested columns): the C# count blocks new-right
  // even when every kind fits the width.
  chrome.setCapacityChecker(() => ({ fitsAgent: true, fitsTerminal: true, requestedColumnCount: 3 }));
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  assert.equal(segment('右侧新列').disabled, true);
  assert.equal(segment('右侧新列').title, '最多支持 3 列');
  assert.equal(segment('当前列').disabled, false);
  chrome.dispose();
});

test('create menu refreshes in place without replaying the entry animation or stealing focus', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.setCapacityChecker(() => ({ fitsAgent: true, fitsTerminal: true, requestedColumnCount: 1 }));
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'w1', kind: 'terminal' }], activeTabId: 'w1', ratio: 1 }
  ]), new Map());
  chrome.applyCatalog({ revision: 1, providers: [], workspaces: [], maxColumns: 3 });
  const segment = (text) =>
    [...document.querySelectorAll('.workspace-segments button')].find((b) => b.textContent === text);

  // Fresh open: the CSS entry animation applies (no inline override) and the
  // microtask moves focus to the first enabled control.
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  const firstPopover = document.querySelector('.workspace-popover');
  assert.equal(firstPopover.style.animation, '');
  await Promise.resolve();
  assert.equal(firstPopover.contains(document.activeElement), true, 'fresh open focuses a menu control');

  // Segment toggle = in-place refresh: the node is still swapped, but the
  // animation is suppressed and focus is not yanked back to the first button.
  segment('右侧新列').click();
  const refreshed = document.querySelector('.workspace-popover');
  assert.notEqual(refreshed, firstPopover, 'refresh still swaps in a fresh node');
  assert.equal(refreshed.style.animation, 'none', 'refresh suppresses the entry animation');
  await Promise.resolve();
  assert.equal(refreshed.contains(document.activeElement), false, 'refresh does not steal focus');

  // Close and reopen: a fresh open animates and focuses again.
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  const reopened = document.querySelector('.workspace-popover');
  assert.equal(reopened.style.animation, '');
  await Promise.resolve();
  assert.equal(reopened.contains(document.activeElement), true);
  chrome.dispose();
});

test('column menu blocks split-right on pixel capacity and prefers the C# reason', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'w1', kind: 'agent' }], activeTabId: 'w1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 'w1', kind: 'agent', title: 'docs', iconKey: 'claude',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, splitBlockedReason: '',
      canCollapse: true
    }]
  });
  const openMenu = () => {
    const tab = document.querySelector('.workspace-tab');
    tab.dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  };
  const splitRow = () =>
    [...document.querySelectorAll('.workspace-menu-row')].find(
      (r) => r.querySelector('.workspace-menu-primary').textContent === '移到右侧新列'
    );

  // Capacity exhausted: split-right is disabled with the capacity text.
  chrome.setCapacityChecker(() => ({ fitsAgent: false, fitsTerminal: true, requestedColumnCount: 1 }));
  openMenu();
  assert.equal(splitRow().disabled, true);
  assert.equal(splitRow().querySelector('.workspace-menu-secondary').textContent, '窗口宽度不足以容纳新列');
  assert.ok(
    [...document.querySelectorAll('.workspace-menu-row')].some(
      (r) => r.querySelector('.workspace-menu-primary').textContent === '合并为单列'
    ),
    'the merge-into-single-column entry renders'
  );
  assert.equal(
    [...document.querySelectorAll('.workspace-menu-row')].some(
      (r) => r.querySelector('.workspace-menu-primary').textContent.startsWith('移到第')
    ),
    false,
    'no per-column move entries'
  );
  assert.equal(
    [...document.querySelectorAll('.workspace-menu-row')].some(
      (r) => r.querySelector('.workspace-menu-primary').textContent.includes('交换')
    ),
    false,
    'no swap entry'
  );
  document.querySelector('.workspace-popover').dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

  // The C# reason wins the secondary line when both layers block.
  chrome.applyCatalog({
    revision: 2,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 'w1', kind: 'agent', title: 'docs', iconKey: 'claude',
      columnId: 'column-1', isActiveTab: true, canSplitRight: false, splitBlockedReason: '最多支持 3 列',
      canCollapse: true
    }]
  });
  openMenu();
  assert.equal(splitRow().disabled, true);
  assert.equal(splitRow().querySelector('.workspace-menu-secondary').textContent, '最多支持 3 列');
  chrome.dispose();
});

test('column menu merge entry sends collapse_single', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'w1', kind: 'agent' }], activeTabId: 'w1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 'w1', kind: 'agent', title: 'docs', iconKey: 'claude',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, splitBlockedReason: '',
      canCollapse: true
    }]
  });
  const tab = document.querySelector('.workspace-tab');
  tab.dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  const collapseRow = [...document.querySelectorAll('.workspace-menu-row')].find(
    (r) => r.querySelector('.workspace-menu-primary').textContent === '合并为单列'
  );
  assert.ok(collapseRow);
  collapseRow.click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'workspace_layout_intent',
    action: 'collapse_single'
  });
  chrome.dispose();
});

test('activity rail owns the four global buttons and the chrome row keeps only tab strips', async () => {
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
  assert.ok(header.querySelector('[data-role="pane-nameplates"]'), 'chrome row keeps the tab-strip host');
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
  chrome.applyLayout(columnSnapshot([]), new Map());
  chrome.applyCatalog({ revision: 1, providers: [], workspaces: [], maxColumns: 3 });
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

test('column menu anchors under its tab trigger, right-aligned to it', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'w1', kind: 'agent' }], activeTabId: 'w1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 'w1', kind: 'agent', title: 'docs', iconKey: 'claude',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, splitBlockedReason: '',
      canCollapse: false
    }]
  });
  const tab = document.querySelector('.workspace-tab');
  tab.getBoundingClientRect = () => ({ top: 10, bottom: 34, left: 700, right: 732, width: 32, height: 24 });
  tab.dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  const menu = document.querySelector('.workspace-popover-pane');
  assert.ok(menu, 'column menu renders');
  assert.equal(menu.style.top, '38px', 'top sits directly under the trigger');
  // jsdom reports offsetWidth 0: the right-align measurement (trigger right
  // 732 - width 0) lands exactly on the trigger's right edge; with a real
  // width the menu's right edge aligns to the trigger, clamped at 44px.
  assert.equal(menu.style.left, '732px', 'left comes from the post-mount right-align measurement');
  assert.equal(menu.style.transform, '', 'no transform — it would fight the popover-in animation');
  chrome.dispose();
});

test('tab strips render attention badges and single-tab columns keep the nameplate look', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    {
      columnId: 'column-1',
      tabs: [
        { workspaceId: 'w1', kind: 'agent' },
        { workspaceId: 'w2', kind: 'agent' }
      ],
      activeTabId: 'w1',
      ratio: 1
    }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [
      { workspaceId: 'w1', kind: 'agent', title: 'First', iconKey: 'claude', columnId: 'column-1', isActiveTab: true, attentionKind: 'permission' },
      { workspaceId: 'w2', kind: 'agent', title: 'Second', iconKey: 'kimi', columnId: 'column-1', isActiveTab: false, attentionKind: 'error' }
    ]
  });
  const tabs = [...document.querySelectorAll('.workspace-tab')];
  assert.equal(tabs.length, 2, 'both tabs of the column render');
  const first = tabs.find((t) => t.dataset.workspaceId === 'w1');
  const second = tabs.find((t) => t.dataset.workspaceId === 'w2');
  assert.equal(first.dataset.active, 'true');
  assert.equal(second.dataset.active, 'false');
  assert.equal(first.querySelector('.workspace-tab-attention').textContent, '需确认');
  assert.equal(second.querySelector('.workspace-tab-attention').textContent, '出错');
  assert.equal(first.querySelector('.workspace-tab-title').textContent, 'First');

  // The close button closes; clicking the tab activates it in place.
  first.querySelector('.workspace-tab-close').click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'workspace_layout_intent',
    action: 'close',
    workspaceId: 'w1'
  });
  second.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'workspace_layout_intent',
    action: 'activate',
    workspaceId: 'w2'
  });

  // A single-tab column keeps the legacy nameplate look: the lone tab fills
  // the strip (CSS :only-child rule).
  const css = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'workspace-chrome.css'),
    'utf8'
  );
  assert.match(css, /:has\(\.workspace-tab:only-child\)/, 'single-tab columns fill the strip');
  chrome.dispose();
});

test('tab strips overflow horizontally on wheel', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  const tabs = Array.from({ length: 6 }, (_, index) => ({
    workspaceId: `w${index + 1}`,
    kind: 'agent'
  }));
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs, activeTabId: 'w1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 200, height: 40 }]]));
  const strip = document.querySelector('.workspace-tab-strip');
  // jsdom reports zero scroll geometry; stub the overflowed shape.
  Object.defineProperty(strip, 'scrollWidth', { configurable: true, value: 600 });
  Object.defineProperty(strip, 'clientWidth', { configurable: true, value: 200 });

  const wheel = new window.WheelEvent('wheel', { deltaY: 120, bubbles: true, cancelable: true });
  strip.dispatchEvent(wheel);
  assert.equal(strip.scrollLeft, 120, 'wheel translates to horizontal scroll');
  assert.equal(wheel.defaultPrevented, true);

  // The strip clamps at the end and consumes the wheel only while overflowed.
  const clamp = new window.WheelEvent('wheel', { deltaY: 9999, bubbles: true, cancelable: true });
  strip.dispatchEvent(clamp);
  assert.equal(strip.scrollLeft, 400, 'scroll clamps at maxScroll');
  assert.equal(clamp.defaultPrevented, true);

  const css = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'workspace-chrome.css'),
    'utf8'
  );
  assert.match(css, /\.workspace-tab-strip\s*\{[\s\S]*?overflow-x:\s*auto/, 'the strip scrolls horizontally');
  chrome.dispose();
});
