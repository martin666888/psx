// workspaceChrome.test.js — the WebView shell chrome: per-column tab strips
// in the 40px workspace row, the create menu's column-cap gates and the
// column menu. Wire shape: workspace_layout carries focusedColumnId +
// columns[] (tabs/activeTabId/ratio); the catalog carries per-workspace
// columnId/isActiveTab and maxColumns.

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
      <button data-role="history-toggle">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"></path>
          <path d="M3 3v5h5"></path>
          <path d="M12 7v5l4 2"></path>
        </svg>
      </button>
      <button data-role="workspace-create-toggle">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <path d="M5 12h14"></path>
          <path d="M12 5v14"></path>
        </svg>
      </button>
      <button data-role="theme-toggle">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <path d="M12 22a1 1 0 0 1 0-20 10 9 0 0 1 10 9 5 5 0 0 1-5 5h-2.25a1.75 1.75 0 0 0-1.4 2.8l.3.4a1.75 1.75 0 0 1-1.4 2.8z"></path>
        </svg>
      </button>
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

test('tab icons render provider brand SVGs; terminal keeps its text glyph', async () => {
  installAgentRuntime();
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
        { workspaceId: 'term', kind: 'terminal' },
        { workspaceId: 'qwen', kind: 'agent' },
        { workspaceId: 'qoder', kind: 'agent' },
        { workspaceId: 'cline', kind: 'agent' },
        { workspaceId: 'weird', kind: 'agent' }
      ],
      activeTabId: 'qwen',
      ratio: 1
    }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [
      { workspaceId: 'term', kind: 'terminal', title: 'Term', iconKey: 'terminal', columnId: 'column-1', isActiveTab: false },
      { workspaceId: 'qwen', kind: 'agent', title: 'Qwen', iconKey: 'qwen', columnId: 'column-1', isActiveTab: true },
      { workspaceId: 'qoder', kind: 'agent', title: 'Qoder', iconKey: 'qoder', columnId: 'column-1', isActiveTab: false },
      { workspaceId: 'cline', kind: 'agent', title: 'Cline', iconKey: 'cline', columnId: 'column-1', isActiveTab: false },
      { workspaceId: 'weird', kind: 'agent', title: 'Weird', iconKey: 'not-a-brand', columnId: 'column-1', isActiveTab: false }
    ]
  });
  const tabIcon = (id) => document.querySelector(`.workspace-tab[data-workspace-id="${id}"] .workspace-tab-icon`);
  assert.equal(tabIcon('term').textContent, '>_', 'terminal tab keeps the text glyph');
  assert.equal(tabIcon('term').querySelector('svg'), null, 'terminal tab carries no svg');
  assert.ok(tabIcon('qwen').querySelector('svg[data-icon="qwen"]'), 'qwen tab renders the qwen brand mark');
  assert.ok(tabIcon('qoder').querySelector('svg[data-icon="qoder"]'), 'qoder tab renders the qoder brand mark, not a shared letter');
  assert.ok(tabIcon('cline').querySelector('svg[data-icon="cline"]'), 'cline tab renders the cline brand mark');
  assert.ok(tabIcon('weird').querySelector('svg[data-icon="agent"]'), 'unknown iconKey falls back to the generic sparkle');
  assert.equal(tabIcon('qwen').querySelector('svg').getAttribute('aria-hidden'), 'true');
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

test('global and pane popovers dismiss outside while internal interaction stays open', async () => {
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
      canCollapse: false
    }]
  });

  const create = document.querySelector('[data-role="workspace-create-toggle"]');
  create.click();
  const createMenu = document.querySelector('.workspace-popover-create');
  createMenu.dispatchEvent(new window.MouseEvent('pointerdown', { bubbles: true }));
  assert.ok(document.querySelector('.workspace-popover-create'), 'inside press keeps create open');
  document.getElementById('workspace-chrome').dispatchEvent(
    new window.MouseEvent('pointerdown', { bubbles: true })
  );
  assert.equal(document.querySelector('.workspace-popover'), null, 'chrome outside press closes create');
  assert.equal(create.getAttribute('aria-expanded'), 'false');

  const theme = document.querySelector('[data-role="theme-toggle"]');
  theme.click();
  document.querySelector('[data-role="history-toggle"]').dispatchEvent(
    new window.MouseEvent('pointerdown', { bubbles: true })
  );
  assert.equal(document.querySelector('.workspace-popover'), null, 'rail outside press closes theme');
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'theme_action',
    action: 'cancel'
  });

  const tab = document.querySelector('.workspace-tab');
  tab.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  assert.ok(document.querySelector('.workspace-popover-pane'));
  tab.dispatchEvent(new window.MouseEvent('pointerdown', { bubbles: true, button: 0 }));
  assert.equal(document.querySelector('.workspace-popover-pane'), null, 'left press on pane trigger closes its context menu');
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
  // A multi-tab source column: the split adds a column, so the pixel
  // capacity gate applies (a sole-tab column collapses instead — its gate is
  // covered by the collapse-split test below).
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

test('column menu split-right gate skips pixel capacity when the source column collapses', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
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

  // Sole tab of its column: the split collapses the source column and adds
  // no column, so even a checker reporting the phantom new-column width
  // would not fit must NOT disable the entry.
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'w1', kind: 'agent' }], activeTabId: 'w1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.setCapacityChecker(() => ({ fitsAgent: false, fitsTerminal: true, requestedColumnCount: 1 }));
  openMenu();
  assert.equal(splitRow().disabled, false, 'a collapse split adds no column: the pixel gate does not apply');
  document.querySelector('.workspace-popover').dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

  // The column holds multiple tabs: the split adds a column, so the pixel
  // gate applies and a width shortage disables the entry.
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
  openMenu();
  assert.equal(splitRow().disabled, true, 'a multi-tab split adds a column: the pixel gate applies');
  assert.equal(splitRow().querySelector('.workspace-menu-secondary').textContent, '窗口宽度不足以容纳新列');
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

test('activity rail owns the three global buttons and the chrome row keeps only tab strips', async () => {
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
    'workspace-create-toggle',
    'theme-toggle'
  ], 'the three global buttons live in the activity rail in order');
  const header = document.getElementById('workspace-chrome');
  assert.equal(header.querySelectorAll('button').length, 0, 'chrome row owns no global buttons');
  assert.ok(header.querySelector('[data-role="pane-nameplates"]'), 'chrome row keeps the tab-strip host');
  assert.equal(chrome.historyButton, document.querySelector('[data-role="history-toggle"]'));
  assert.equal(chrome.historyButton.disabled, false, 'History is available before any Agent workspace exists');
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{ workspaceId: 'terminal-1', kind: 'terminal', title: 'Terminal' }]
  });
  assert.equal(chrome.historyButton.disabled, false, 'a terminal-only catalog does not disable global History');
  chrome.dispose();
});

test('rail buttons render inline SVG icons and expose no workspace-list entry', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  const buttons = [...document.querySelectorAll('#activity-rail button')];
  assert.equal(buttons.length, 3);
  for (const button of buttons) {
    const svg = button.querySelector('svg');
    assert.ok(svg, 'each rail button renders an inline SVG icon');
    assert.equal(svg.getAttribute('aria-hidden'), 'true', 'rail icons are decorative');
  }
  assert.equal(
    document.querySelector('[data-role="workspace-menu-toggle"]'),
    null,
    'the workspace-list entry is gone from the rail'
  );
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
  const trigger = document.querySelector('[data-role="workspace-create-toggle"]');
  trigger.getBoundingClientRect = () => ({ top: 123, left: 0, width: 40, height: 40 });
  trigger.click();
  const menu = document.querySelector('.workspace-popover');
  assert.ok(menu, 'create menu renders');
  assert.equal(menu.style.top, '123px', 'top follows the trigger button rect');
  assert.ok(
    menu.style.maxHeight.includes('100vh') && menu.style.maxHeight.includes('123px'),
    'max-height keeps the menu inside the viewport (CSSOM may reorder calc terms)'
  );
  assert.equal(menu.style.left, '', 'left stays CSS-owned (rail edge + gap), not inline');
  chrome.dispose();
});

test('column menu anchors under its tab trigger, left-aligned to it', async () => {
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
  tab.querySelector('.workspace-tab-target').getBoundingClientRect = () => ({
    top: 10, bottom: 34, left: 700, right: 732, width: 32, height: 24
  });
  tab.dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  const menu = document.querySelector('.workspace-popover-pane');
  assert.ok(menu, 'column menu renders');
  assert.equal(menu.style.top, '38px', 'top sits directly under the trigger');
  assert.equal(menu.style.left, '700px', 'menu left edge follows the trigger left edge');
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

  // The workspace target and close action are sibling buttons in a labelled
  // control group, so both remain independently keyboard reachable.
  assert.equal(first.getAttribute('role'), 'presentation');
  assert.equal(first.querySelector('.workspace-tab-target').getAttribute('role'), null);
  assert.equal(first.querySelector('.workspace-tab-target').getAttribute('aria-pressed'), 'true');
  assert.equal(
    first.querySelector('.workspace-tab-target').contains(first.querySelector('.workspace-tab-close')),
    false
  );
  first.querySelector('.workspace-tab-close').click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'workspace_layout_intent',
    action: 'close',
    workspaceId: 'w1'
  });
  second.querySelector('.workspace-tab-target').click();
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
