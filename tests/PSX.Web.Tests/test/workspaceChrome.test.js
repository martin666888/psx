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

test('create popover keeps one language-independent width and single-line placement labels', () => {
  const css = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'workspace-chrome.css'),
    'utf8'
  );
  assert.match(css, /\.workspace-popover-create\s*\{[^}]*width:\s*360px;/s);
  assert.match(css, /\.workspace-segments button\s*\{[^}]*white-space:\s*nowrap;/s);
});

function mountChrome() {
  document.body.innerHTML = `
    <nav id="activity-rail">
      <button data-role="history-toggle">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" aria-hidden="true">
          <path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"></path>
          <path d="M3 3v5h5"></path>
          <path d="M12 7v5l4 2"></path>
        </svg>
      </button>
      <button data-role="workspace-create-toggle">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" aria-hidden="true">
          <path d="M5 12h14"></path>
          <path d="M12 5v14"></path>
        </svg>
      </button>
      <button data-role="theme-toggle">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" aria-hidden="true">
          <path d="M12 22a1 1 0 0 1 0-20 10 9 0 0 1 10 9 5 5 0 0 1-5 5h-2.25a1.75 1.75 0 0 0-1.4 2.8l.3.4a1.75 1.75 0 0 1-1.4 2.8z"></path>
        </svg>
      </button>
      <button data-role="app-settings-toggle">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" aria-hidden="true">
          <circle cx="12" cy="12" r="3"></circle>
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
  assert.equal(strip.querySelector('.workspace-tab-title').textContent, '工作区');

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
        { workspaceId: 'opencode', kind: 'agent' },
        { workspaceId: 'dsh', kind: 'dsh_web' },
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
      { workspaceId: 'opencode', kind: 'agent', title: 'OpenCode', iconKey: 'opencode', columnId: 'column-1', isActiveTab: false },
      { workspaceId: 'dsh', kind: 'dsh_web', title: 'DeepSeek Harness', iconKey: 'dsh', columnId: 'column-1', isActiveTab: false },
      { workspaceId: 'weird', kind: 'agent', title: 'Weird', iconKey: 'not-a-brand', columnId: 'column-1', isActiveTab: false }
    ]
  });
  const tabIcon = (id) => document.querySelector(`.workspace-tab[data-workspace-id="${id}"] .workspace-tab-icon`);
  assert.equal(tabIcon('term').textContent, '>_', 'terminal tab keeps the text glyph');
  assert.equal(tabIcon('term').querySelector('svg'), null, 'terminal tab carries no svg');
  assert.ok(tabIcon('qwen').querySelector('svg[data-icon="qwen"]'), 'qwen tab renders the qwen brand mark');
  assert.ok(tabIcon('opencode').querySelector('svg[data-icon="opencode"]'), 'opencode tab renders the opencode brand mark');
  assert.ok(tabIcon('dsh').querySelector('svg[data-icon="dsh"]'), 'dsh tab renders the DeepSeek brand mark');
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
  assert.equal(row('终端').disabled, false, 'terminal row unaffected at focused placement');
  assert.equal(chrome.createPlacement, 'focused');
  document.querySelector('[data-role="workspace-create-toggle"]').click();

  // Terminal capacity exhausted with new_right placement: only the Terminal
  // row is blocked, again with the capacity title.
  chrome.setCapacityChecker(() => ({ fitsAgent: true, fitsTerminal: false, requestedColumnCount: 1 }));
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  segment('右侧新列').click();
  assert.equal(chrome.createPlacement, 'new_right');
  assert.equal(segment('右侧新列').disabled, false, 'agent capacity still fits');
  assert.equal(row('终端').disabled, true);
  assert.equal(row('终端').title, '窗口宽度不足以容纳新列');
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


test('create menu gates the DeepSeek Harness row with its own fitsDsh capacity', async () => {
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
  const row = (text) =>
    [...document.querySelectorAll('.workspace-menu-row')].find(
      (r) => r.querySelector('.workspace-menu-primary').textContent === text
    );
  const segment = (text) =>
    [...document.querySelectorAll('.workspace-segments button')].find((b) => b.textContent === text);

  // DSH capacity exhausted at new_right: only the DSH row is blocked.
  chrome.setCapacityChecker(() => ({ fitsAgent: true, fitsTerminal: true, fitsDsh: false, requestedColumnCount: 1 }));
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  segment('右侧新列').click();
  assert.equal(row('DeepSeek Harness').disabled, true);
  assert.equal(row('DeepSeek Harness').title, '窗口宽度不足以容纳新列');
  assert.equal(row('终端').disabled, false, 'fitsDsh never gates the shared new-right segment or Terminal row');
  chrome.dispose();
});

test('create menu lists web apps under WEB APP and ACP providers under AGENT (ACP)', async () => {
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
  chrome.applyCatalog({
    revision: 1,
    providers: [{ key: 'acp-claude', displayName: 'Claude Code', iconKey: 'claude' }],
    workspaces: [],
    maxColumns: 3
  });
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  const labels = [...document.querySelectorAll('.workspace-popover .workspace-menu-row .workspace-menu-primary, .workspace-popover .workspace-popover-subheading')]
    .map((node) => node.textContent);
  const terminal = labels.indexOf('终端');
  const webApp = labels.indexOf('网页应用');
  const kimiWeb = labels.indexOf('Kimi Code Web');
  const dsh = labels.indexOf('DeepSeek Harness');
  const agent = labels.indexOf('Agent（ACP）');
  const claude = labels.indexOf('Claude Code');
  assert.ok(terminal >= 0 && webApp > terminal, 'WEB APP heading follows Terminal');
  assert.ok(kimiWeb > webApp, 'Kimi Code Web opens the WEB APP group');
  assert.ok(dsh > kimiWeb, 'DeepSeek Harness joins the WEB APP group below Kimi Code Web');
  assert.ok(agent > dsh, 'AGENT (ACP) heading follows the web-app rows');
  assert.ok(claude > agent, 'ACP providers follow the AGENT heading');
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
      columnId: 'column-1', isActiveTab: true, canSplitRight: false, splitBlockedReason: 'split.column_cap',
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

test('dsh_web tab menu exposes a stop-runtime entry; other kinds do not', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'dsh1', kind: 'dsh_web' }], activeTabId: 'dsh1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 'dsh1', kind: 'dsh_web', title: 'DeepSeek Harness', iconKey: 'dsh',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, splitBlockedReason: '',
      canCollapse: false
    }]
  });
  chrome.applyDshRuntimeStatus({
    state: 'ready',
    currentVersion: '0.1.0-rc.6',
    updateState: 'idle'
  });
  const stopRowText = (r) => r.querySelector('.workspace-menu-primary').textContent;
  document.querySelector('.workspace-tab')
    .dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  const stopRow = [...document.querySelectorAll('.workspace-menu-row')].find(
    (r) => stopRowText(r) === '停止运行时'
  );
  assert.ok(stopRow, 'the dsh_web tab menu offers the stop-runtime entry');
  stopRow.click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'dsh_command',
    name: 'stop'
  }, 'clicking the entry sends the dsh_command stop wire message');

  // A terminal tab menu never offers the runtime stop entry.
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 2,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 't1', kind: 'terminal', title: 'PowerShell', iconKey: 'terminal',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, splitBlockedReason: '',
      canCollapse: false
    }]
  });
  document.querySelector('.workspace-tab')
    .dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  assert.ok(
    ![...document.querySelectorAll('.workspace-menu-row')].some((r) => stopRowText(r) === '停止运行时'),
    'non-dsh tabs keep no stop-runtime entry'
  );
  chrome.dispose();
});

test('dsh_web tab menu matches chrome hierarchy and confirms an available update in place', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'dsh1', kind: 'dsh_web' }], activeTabId: 'dsh1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 'dsh1', kind: 'dsh_web', title: 'DeepSeek Harness', iconKey: 'dsh',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, splitBlockedReason: '',
      canCollapse: false
    }]
  });
  chrome.applyDshRuntimeStatus({
    state: 'ready',
    currentVersion: '0.1.0-rc.6',
    updateState: 'available',
    availableVersion: '0.1.0-rc.8',
    availableVersions: [
      { version: '0.1.0-rc.8', tags: ['next'] },
      { version: '0.1.0-rc.7', tags: ['latest'] }
    ]
  });

  document.querySelector('.workspace-tab')
    .dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  assert.equal(document.querySelector('.workspace-popover-heading-row > span').textContent, 'v0.1.0-rc.6');
  assert.deepEqual(
    [...document.querySelectorAll('.workspace-popover-subheading')].map((node) => node.textContent),
    ['布局', '运行时']
  );
  const updateRow = [...document.querySelectorAll('.workspace-menu-row')].find(
    (row) => row.querySelector('.workspace-menu-primary').textContent === '更新到 v0.1.0-rc.8'
  );
  assert.ok(updateRow);
  assert.equal(updateRow.querySelector('.workspace-menu-secondary').textContent, '后台更新');
  updateRow.click();

  assert.equal(
    document.querySelector('.workspace-update-versions').textContent,
    '0.1.0-rc.6 → 0.1.0-rc.8'
  );
  const options = [...document.querySelectorAll('.workspace-update-version-option')];
  assert.equal(options.length, 2);
  assert.equal(options[0].querySelector('.workspace-menu-primary').textContent, 'v0.1.0-rc.8');
  assert.equal(options[0].getAttribute('aria-selected'), 'true');
  options[1].click();
  assert.equal(
    document.querySelector('.workspace-update-versions').textContent,
    '0.1.0-rc.6 → 0.1.0-rc.7'
  );
  assert.equal(
    document.querySelector('.workspace-update-version-option[data-selected="true"] .workspace-menu-primary').textContent,
    'v0.1.0-rc.7'
  );

  // A status push that keeps the same catalog must not reset the selection.
  chrome.applyDshRuntimeStatus({
    state: 'ready',
    currentVersion: '0.1.0-rc.6',
    updateState: 'available',
    availableVersion: '0.1.0-rc.8',
    availableVersions: [
      { version: '0.1.0-rc.8', tags: ['next'] },
      { version: '0.1.0-rc.7', tags: ['latest'] }
    ]
  });
  assert.equal(
    document.querySelector('.workspace-update-versions').textContent,
    '0.1.0-rc.6 → 0.1.0-rc.7',
    'selected version survives status rebuild'
  );

  assert.match(document.querySelector('.workspace-update-confirmation p').textContent, /无需重启 PSX/);
  assert.match(document.querySelector('.workspace-update-confirmation p').textContent, /配置和会话不会被删除/);
  assert.match(document.querySelector('.workspace-update-confirmation p').textContent, /可能需要几分钟/);
  const confirm = [...document.querySelectorAll('.workspace-update-actions button')].find(
    (button) => button.textContent === '开始后台更新'
  );
  confirm.click();
  assert.deepEqual(runtime.postedMessages.at(-1), {
    type: 'dsh_command',
    name: 'update',
    version: '0.1.0-rc.7'
  });
  assert.equal(
    document.querySelector('.workspace-menu-row:disabled .workspace-menu-primary').textContent,
    '正在后台下载 v0.1.0-rc.7…'
  );
  assert.equal(
    document.querySelector('.workspace-menu-row:disabled .workspace-menu-secondary').textContent,
    '可能需要几分钟'
  );
  let cancelUpdate = [...document.querySelectorAll('.workspace-menu-row')].find(
    (row) => row.querySelector('.workspace-menu-primary').textContent === '取消更新'
  );
  assert.equal(cancelUpdate.querySelector('.workspace-menu-secondary').textContent, '保留当前版本');

  chrome.applyDshRuntimeStatus({
    state: 'ready', currentVersion: '0.1.0-rc.6', updateState: 'updating',
    updatePhase: 'validating', availableVersion: '0.1.0-rc.7', availableVersions: []
  });
  assert.equal(document.querySelector('.workspace-menu-row:disabled .workspace-menu-primary').textContent,
    '正在校验 v0.1.0-rc.7…');
  cancelUpdate = [...document.querySelectorAll('.workspace-menu-row')].find(
    (row) => row.querySelector('.workspace-menu-primary').textContent === '取消更新'
  );
  cancelUpdate.click();
  assert.deepEqual(runtime.postedMessages.at(-1), { type: 'dsh_command', name: 'cancel_update' });
  assert.equal(cancelUpdate.disabled, true);

  chrome.applyDshRuntimeStatus({
    state: 'ready', currentVersion: '0.1.0-rc.6', updateState: 'updating',
    updatePhase: 'restarting', availableVersion: '0.1.0-rc.7', availableVersions: []
  });
  assert.equal(document.querySelector('.workspace-menu-row:disabled .workspace-menu-primary').textContent,
    '正在切换到 v0.1.0-rc.7…');
  assert.ok(![...document.querySelectorAll('.workspace-menu-primary')]
    .some((node) => node.textContent === '取消更新'), 'switch phase is no longer cancellable');
  chrome.dispose();
});

test('dsh_web update menu reports checking, latest and safe errors without long row descriptions', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'dsh1', kind: 'dsh_web' }], activeTabId: 'dsh1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [], maxColumns: 3,
    workspaces: [{
      workspaceId: 'dsh1', kind: 'dsh_web', title: 'DeepSeek Harness', iconKey: 'dsh',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, canCollapse: false
    }]
  });
  chrome.applyDshRuntimeStatus({ state: 'ready', currentVersion: '0.1.0-rc.6', updateState: 'idle' });
  document.querySelector('.workspace-tab')
    .dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  const stableMenu = document.getElementById('workspace-popover-root').firstElementChild;
  const stableTop = stableMenu.style.top;
  const stableLeft = stableMenu.style.left;
  const check = [...document.querySelectorAll('.workspace-menu-row')].find(
    (row) => row.querySelector('.workspace-menu-primary').textContent === '检查更新'
  );
  check.click();
  assert.deepEqual(runtime.postedMessages.at(-1), { type: 'dsh_command', name: 'check_update' });
  assert.equal(document.getElementById('workspace-popover-root').firstElementChild, stableMenu,
    'checking refresh keeps the same anchored popover surface');
  assert.equal(stableMenu.style.top, stableTop);
  assert.equal(stableMenu.style.left, stableLeft);
  assert.equal(document.querySelector('.workspace-menu-row:disabled .workspace-menu-primary').textContent, '正在检查更新…');

  chrome.applyDshRuntimeStatus({ state: 'ready', currentVersion: '0.1.0-rc.6', updateState: 'up_to_date' });
  assert.equal(document.getElementById('workspace-popover-root').firstElementChild, stableMenu,
    'backend result refresh keeps the same popover surface');
  const latest = [...document.querySelectorAll('.workspace-menu-row')].find(
    (row) => row.querySelector('.workspace-menu-primary').textContent === '检查更新'
  );
  assert.equal(latest.querySelector('.workspace-menu-secondary').textContent, '已是最新');

  chrome.applyDshRuntimeStatus({
    state: 'ready', currentVersion: '0.1.0-rc.6', updateState: 'failed',
    updateErrorCode: 'registry_network'
  });
  assert.equal(document.querySelector('.workspace-popover-message[data-error="true"]').textContent,
    '无法连接 npm 仓库，请检查网络后重试。');
  const stop = [...document.querySelectorAll('.workspace-menu-row')].find(
    (row) => row.querySelector('.workspace-menu-primary').textContent === '停止运行时'
  );
  assert.equal(stop.querySelector('.workspace-menu-secondary').textContent, '',
    'long prose must not squeeze the stop action out of the compact row');
  chrome.dispose();
});

test('dsh_web menu separates deferred and blocked versions from installable ones', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'dsh1', kind: 'dsh_web' }], activeTabId: 'dsh1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [], maxColumns: 3,
    workspaces: [{
      workspaceId: 'dsh1', kind: 'dsh_web', title: 'DeepSeek Harness', iconKey: 'dsh',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, canCollapse: false
    }]
  });

  // Everything newer is deferred or blocked: the state must not pretend the
  // user is up to date, and no update entry may appear.
  chrome.applyDshRuntimeStatus({
    state: 'ready',
    currentVersion: '0.1.0-rc.6',
    updateState: 'requires_psx_update',
    availableVersions: [],
    deferredVersions: [
      { version: '0.1.1', tags: [] },
      { version: '../../evil', tags: [] },
      { version: 42, tags: [] }
    ],
    blockedVersions: [{ version: '0.1.2', tags: ['latest'] }],
    updateErrorCode: 'update_requires_psx'
  });
  document.querySelector('.workspace-tab')
    .dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  const primaries = () => [...document.querySelectorAll('.workspace-menu-row .workspace-menu-primary')]
    .map((node) => node.textContent);
  assert.ok(!primaries().some((text) => text.startsWith('更新到')),
    'requires_psx_update must not offer an update entry');
  assert.ok(primaries().includes('发现新版本'));
  const notices = [...document.querySelectorAll('.workspace-popover-message[data-muted="true"]')]
    .map((node) => node.textContent);
  assert.equal(notices.length, 2);
  assert.match(notices[0], /当前 PSX 不支持此版本：v0\.1\.2/);
  assert.match(notices[1], /已发布但暂不可安装：v0\.1\.1（将随 PSX 更新提供）/);
  assert.ok(!notices.some((text) => text.includes('evil')),
    'path-shaped or non-string versions are dropped by normalization');
  assert.ok(primaries().includes('重新检查'));

  // Mixed state: installable entries stay selectable while deferred and
  // blocked versions render as notices inside the update confirmation.
  chrome.applyDshRuntimeStatus({
    state: 'ready',
    currentVersion: '0.1.0-rc.6',
    updateState: 'available',
    availableVersion: '0.1.1',
    availableVersions: [{ version: '0.1.1', tags: [] }],
    deferredVersions: [{ version: '0.2.0', tags: [] }],
    blockedVersions: [{ version: '0.1.2', tags: [] }]
  });
  const updateRow = [...document.querySelectorAll('.workspace-menu-row')].find(
    (row) => row.querySelector('.workspace-menu-primary').textContent === '更新到 v0.1.1'
  );
  updateRow.click();
  const confirmationNotices = [...document.querySelectorAll('.workspace-update-confirmation .workspace-popover-message[data-muted="true"]')]
    .map((node) => node.textContent);
  assert.match(confirmationNotices[0], /当前 PSX 不支持此版本：v0\.1\.2/);
  assert.match(confirmationNotices[1], /已发布但暂不可安装：v0\.2\.0（将随 PSX 更新提供）/);
  chrome.dispose();
});

test('create menu gates the Kimi Code Web row with its own fitsKimiWeb capacity', async () => {
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
  const row = (text) =>
    [...document.querySelectorAll('.workspace-menu-row')].find(
      (r) => r.querySelector('.workspace-menu-primary').textContent === text
    );
  const segment = (text) =>
    [...document.querySelectorAll('.workspace-segments button')].find((b) => b.textContent === text);

  // Kimi Web capacity exhausted at new_right: only the Kimi Code Web row is
  // blocked; fitsKimiWeb never gates the shared segment or the DSH row.
  chrome.setCapacityChecker(() => ({ fitsAgent: true, fitsTerminal: true, fitsDsh: true, fitsKimiWeb: false, requestedColumnCount: 1 }));
  document.querySelector('[data-role="workspace-create-toggle"]').click();
  segment('右侧新列').click();
  assert.equal(row('Kimi Code Web').disabled, true);
  assert.equal(row('Kimi Code Web').title, '窗口宽度不足以容纳新列');
  assert.equal(row('DeepSeek Harness').disabled, false, 'fitsKimiWeb never gates the DSH row');
  assert.equal(row('终端').disabled, false, 'fitsKimiWeb never gates the Terminal row');
  chrome.dispose();
});

test('kimi_web tab menu switches the runtime action by status', async () => {
  const runtime = installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  chrome.applyLayout(columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'k1', kind: 'kimi_web' }], activeTabId: 'k1', ratio: 1 }
  ]), new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]));
  chrome.applyCatalog({
    revision: 1,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 'k1', kind: 'kimi_web', title: 'Kimi Code Web', iconKey: 'kimi',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, splitBlockedReason: '',
      canCollapse: false
    }]
  });
  const primaryText = (r) => r.querySelector('.workspace-menu-primary').textContent;
  const openMenu = () => document.querySelector('.workspace-tab')
    .dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
  const runtimeRow = () => [...document.querySelectorAll('.workspace-menu-row')].find(
    (r) => r.closest('[data-role="kimi-web-runtime-menu"]')
  );

  // ready -> the stop action.
  chrome.applyKimiWebRuntimeStatus({ state: 'ready' });
  openMenu();
  assert.equal(primaryText(runtimeRow()), '停止运行时');
  runtimeRow().click();
  assert.deepEqual(runtime.postedMessages.at(-1), { type: 'kimi_web_command', name: 'stop' });

  // unavailable -> retry that only re-validates (no process pull).
  chrome.applyKimiWebRuntimeStatus({ state: 'unavailable', reason: 'runtime_missing' });
  openMenu();
  assert.equal(primaryText(runtimeRow()), '重试');
  assert.equal(runtimeRow().querySelector('.workspace-menu-secondary').textContent, '仅重新校验运行时');
  runtimeRow().click();
  assert.deepEqual(runtime.postedMessages.at(-1), { type: 'kimi_web_command', name: 'retry' });

  // failed -> retry.
  chrome.applyKimiWebRuntimeStatus({ state: 'failed', errorClass: 'launch_failed' });
  openMenu();
  assert.equal(primaryText(runtimeRow()), '重试');

  // starting / stopping -> disabled busy labels.
  chrome.applyKimiWebRuntimeStatus({ state: 'starting' });
  openMenu();
  assert.equal(primaryText(runtimeRow()), '正在启动…');
  assert.equal(runtimeRow().disabled, true);
  chrome.applyKimiWebRuntimeStatus({ state: 'stopping' });
  openMenu();
  assert.equal(primaryText(runtimeRow()), '正在停止…');
  assert.equal(runtimeRow().disabled, true);
  chrome.dispose();
});

test('activity rail owns the global buttons and the chrome row keeps only tab strips', async () => {
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
    'theme-toggle',
    'app-settings-toggle'
  ], 'the global buttons live in the activity rail in order');
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
  assert.equal(buttons.length, 4);
  for (const button of buttons) {
    const svg = button.querySelector('svg');
    assert.ok(svg, 'each rail button renders an inline SVG icon');
    assert.equal(svg.getAttribute('aria-hidden'), 'true', 'rail icons are decorative');
    assert.equal(svg.getAttribute('stroke-width'), '1.75', 'rail Lucide strokes are 1.75');
  }
  assert.equal(
    document.querySelector('[data-role="workspace-menu-toggle"]'),
    null,
    'the workspace-list entry is gone from the rail'
  );
  const indexHtml = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'index.html'),
    'utf8'
  );
  assert.equal(
    (indexHtml.match(/stroke-width="1\.75"/g) || []).length,
    4,
    'shipped rail SVGs use stroke-width 1.75'
  );
  const css = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'workspace-chrome.css'),
    'utf8'
  );
  assert.match(css, /#activity-rail \.workspace-chrome-button::before\s*\{[^}]*inset:\s*4px;/s);
  assert.match(
    css,
    /#activity-rail \.workspace-chrome-button\[aria-expanded="true"\]::before\s*\{[^}]*--agent-shell-sidebar-selected/s
  );
  assert.doesNotMatch(
    css,
    /#activity-rail \.workspace-chrome-button\[aria-expanded="true"\]::before\s*\{[^}]*width:\s*3px;/s,
    'expanded rail state must not use a 3px accent bar'
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

test('PSX settings opens the Agent settings dialog instead of a rail popover', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  let opened = 0;
  document.addEventListener('psx-open-settings', () => {
    opened += 1;
  });
  const trigger = document.querySelector('[data-role="app-settings-toggle"]');
  trigger.click();
  assert.equal(opened, 1, 'settings button must dispatch the settings page event');
  assert.equal(document.querySelector('.workspace-popover'), null, 'settings must not use the rail popover');
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

test('column menu rebinds after tab replacement and closes on an embedded-frame pointer ping', async () => {
  installAgentRuntime();
  mountChrome();
  const { WorkspaceChromeController } = await import(controllerUrl);
  const chrome = new WorkspaceChromeController(
    document.getElementById('workspace-chrome'),
    document.getElementById('workspace-popover-root')
  );
  const layout = columnSnapshot([
    { columnId: 'column-1', tabs: [{ workspaceId: 'dsh1', kind: 'dsh_web' }], activeTabId: 'dsh1', ratio: 1 }
  ]);
  const rects = new Map([['column-1', { left: 40, top: 40, width: 960, height: 700 }]]);
  const catalog = (revision) => ({
    revision,
    providers: [],
    maxColumns: 3,
    workspaces: [{
      workspaceId: 'dsh1', kind: 'dsh_web', title: 'DeepSeek Harness', iconKey: 'dsh',
      columnId: 'column-1', isActiveTab: true, canSplitRight: true, canCollapse: false
    }]
  });
  const originalRect = window.HTMLButtonElement.prototype.getBoundingClientRect;
  window.HTMLButtonElement.prototype.getBoundingClientRect = function getBoundingClientRect() {
    if (this.classList.contains('workspace-tab-target')) {
      return { top: 10, bottom: 34, left: 700, right: 732, width: 32, height: 24 };
    }
    return originalRect.call(this);
  };
  try {
    chrome.applyLayout(layout, rects);
    chrome.applyCatalog(catalog(1));
    const oldTrigger = document.querySelector('.workspace-tab-target');
    document.querySelector('.workspace-tab')
      .dispatchEvent(new window.MouseEvent('contextmenu', { bubbles: true, cancelable: true }));

    chrome.applyCatalog(catalog(2));
    const replacement = document.querySelector('.workspace-tab-target');
    assert.notEqual(replacement, oldTrigger, 'catalog rendering replaced the tab target');
    assert.equal(chrome.openTrigger, replacement, 'open menu rebinds to the live tab target');
    assert.equal(document.querySelector('.workspace-popover-pane').style.top, '38px');

    document.dispatchEvent(new window.CustomEvent('psx-embedded-frame-pointerdown'));
    assert.equal(document.querySelector('.workspace-popover-pane'), null,
      'validated iframe pointer pings dismiss shell chrome menus');
  } finally {
    window.HTMLButtonElement.prototype.getBoundingClientRect = originalRect;
    chrome.dispose();
  }
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
