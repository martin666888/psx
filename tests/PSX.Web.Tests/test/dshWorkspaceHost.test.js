import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { beforeEach, describe, it, vi } from 'vitest';
import { flushAgentAnimationFrames, installAgentRuntime, repositoryRoot } from './agentHarness.js';

const hostUrl = pathToFileURL(
  path.join(repositoryRoot, 'frontend', 'webview', 'src', 'DshWorkspaceHost.js')
).href;

function mountContainer() {
  document.body.innerHTML = '<div id="dsh-workspace-container"></div>';
}

function stubPanelRect(panel, rect) {
  panel.getBoundingClientRect = () => ({
    x: rect.left,
    y: rect.top,
    left: rect.left,
    top: rect.top,
    width: rect.width,
    height: rect.height,
    right: rect.left + rect.width,
    bottom: rect.top + rect.height,
    toJSON() { return rect; }
  });
}

describe('DshWorkspaceHost', () => {
  beforeEach(() => {
    installAgentRuntime();
    mountContainer();
  });

  it('projects a panel for a visible dsh_web column and hides it elsewhere', async () => {
    const { DshWorkspaceHost } = await import(hostUrl);
    const host = new DshWorkspaceHost(document.getElementById('dsh-workspace-container'));
    const rects = new Map([['column-2', { left: 500, top: 40, width: 700, height: 600 }]]);
    const snapshot = {
      focusedColumnId: 'column-2',
      columns: [
        { columnId: 'column-1', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 0.4 },
        { columnId: 'column-2', tabs: [{ workspaceId: 'd1', kind: 'dsh_web' }], activeTabId: 'd1', ratio: 0.6 }
      ]
    };
    host.applyLayout(snapshot, rects);
    const panel = document.querySelector('.dsh-panel[data-workspace-id="d1"]');
    assert.ok(panel, 'panel exists for the dsh_web column');
    assert.equal(panel.dataset.columnId, 'column-2');
    assert.equal(panel.hidden, false);
    assert.equal(panel.style.left, '512px');
    assert.equal(panel.style.width, '676px');

    // The DSH tab becomes inactive (a terminal takes its column): hidden.
    const snapshot2 = {
      focusedColumnId: 'column-2',
      columns: [
        { columnId: 'column-1', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 0.4 },
        {
          columnId: 'column-2',
          tabs: [{ workspaceId: 'd1', kind: 'dsh_web' }, { workspaceId: 't2', kind: 'terminal' }],
          activeTabId: 't2',
          ratio: 0.6
        }
      ]
    };
    host.applyLayout(snapshot2, rects);
    assert.equal(panel.hidden, true);
  });

  it('removes a closed workspace panel instead of leaving it hidden', async () => {
    const { DshWorkspaceHost } = await import(hostUrl);
    const host = new DshWorkspaceHost(document.getElementById('dsh-workspace-container'));
    const rects = new Map([['c', { left: 0, top: 40, width: 800, height: 600 }]]);
    host.applyLayout(
      {
        focusedColumnId: 'c',
        columns: [{ columnId: 'c', tabs: [{ workspaceId: 'd1', kind: 'dsh_web' }], activeTabId: 'd1', ratio: 1 }]
      },
      rects
    );
    assert.equal(document.querySelectorAll('.dsh-panel').length, 1);

    host.applyLayout(
      {
        focusedColumnId: 'c',
        columns: [{ columnId: 'c', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 1 }]
      },
      rects
    );
    assert.equal(document.querySelector('.dsh-panel[data-workspace-id="d1"]'), null);
    assert.equal(document.querySelectorAll('.dsh-panel').length, 0);

    host.applyLayout(
      {
        focusedColumnId: 'c',
        columns: [{ columnId: 'c', tabs: [{ workspaceId: 'd2', kind: 'dsh_web' }], activeTabId: 'd2', ratio: 1 }]
      },
      rects
    );
    assert.equal(document.querySelector('.dsh-panel[data-workspace-id="d1"]'), null);
    assert.ok(document.querySelector('.dsh-panel[data-workspace-id="d2"]'));
    assert.equal(document.querySelectorAll('.dsh-panel').length, 1);
  });

  it('renders the not-installed card and switches on runtime status', async () => {
    const { DshWorkspaceHost } = await import(hostUrl);
    const host = new DshWorkspaceHost(document.getElementById('dsh-workspace-container'));
    host.applyLayout(
      {
        focusedColumnId: 'c',
        columns: [{ columnId: 'c', tabs: [{ workspaceId: 'd1', kind: 'dsh_web' }], activeTabId: 'd1', ratio: 1 }]
      },
      new Map([['c', { left: 0, top: 40, width: 800, height: 600 }]])
    );
    const panel = document.querySelector('.dsh-panel[data-workspace-id="d1"]');
    assert.equal(panel.classList.contains('dsh-panel--status'), true);
    const card = panel.querySelector('[data-role="dsh-runtime-card"]');
    assert.ok(card, 'runtime card present');
    assert.equal(card.dataset.state, 'not_installed');
    assert.ok(panel.querySelector('[data-icon="dsh"]'), 'DeepSeek brand mark present');
    assert.ok(panel.querySelector('[data-role="dsh-install"]'), 'install button present');
    assert.match(panel.textContent, /npm/);

    host.applyRuntimeStatus({ state: 'failed', errorClass: 'install_switch_failed' });
    assert.match(panel.textContent, /安装目录切换失败/);
    assert.ok(panel.querySelector('[data-role="dsh-retry"]'), 'retry button present');

    const bridge = globalThis.Bridge;
    const spy = vi.spyOn(bridge, 'sendDshSurfaceBounds');
    stubPanelRect(panel, { left: 12, top: 52, width: 776, height: 576 });
    host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:1234' });
    flushAgentAnimationFrames();
    assert.equal(panel.querySelector('iframe.dsh-frame'), null, 'ready state does not mount an iframe');
    assert.equal(panel.classList.contains('dsh-panel--status'), false);
    assert.equal(panel.classList.contains('dsh-panel--surface'), true);
    assert.equal(panel.querySelector('.dsh-card'), null, 'ready hole has no sibling status card');
    assert.equal(spy.mock.calls.length, 1, 'ready publishes the column hole once');
    assert.deepEqual(spy.mock.calls[0][0], {
      visible: true,
      left: 12,
      top: 52,
      width: 776,
      height: 576,
      columnId: 'c'
    });

    host.applyColorScheme('dark');
    assert.equal(panel.querySelector('iframe.dsh-frame'), null);

    host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:5678' });
    flushAgentAnimationFrames();
    assert.equal(panel.classList.contains('dsh-panel--surface'), true);
    assert.equal(spy.mock.calls.length, 1, 'origin-only readyUrl changes do not remount or resend');
    spy.mockRestore();
  });

  it('focuses the column from panel mousedown', async () => {
    const { DshWorkspaceHost } = await import(hostUrl);
    const bridge = globalThis.Bridge;
    const spy = vi.spyOn(bridge, 'sendPaneFocus');
    try {
      const host = new DshWorkspaceHost(document.getElementById('dsh-workspace-container'));
      host.applyLayout(
        {
          focusedColumnId: 'column-2',
          columns: [
            { columnId: 'column-1', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 0.4 },
            { columnId: 'column-2', tabs: [{ workspaceId: 'd1', kind: 'dsh_web' }], activeTabId: 'd1', ratio: 0.6 }
          ]
        },
        new Map([['column-2', { left: 500, top: 40, width: 700, height: 600 }]])
      );
      const panel = document.querySelector('.dsh-panel[data-workspace-id="d1"]');
      assert.equal(panel.dataset.columnId, 'column-2');
      panel.dispatchEvent(new window.MouseEvent('mousedown', { bubbles: true }));
      assert.deepEqual(spy.mock.calls[0], ['column-2']);
    } finally {
      spy.mockRestore();
    }
  });

  it('hides the overlay while a shell chrome or settings overlay is open', async () => {
    const { DshWorkspaceHost } = await import(hostUrl);
    const bridge = globalThis.Bridge;
    const spy = vi.spyOn(bridge, 'sendDshSurfaceBounds');
    try {
      const host = new DshWorkspaceHost(document.getElementById('dsh-workspace-container'));
      host.applyLayout(
        {
          focusedColumnId: 'c',
          columns: [{ columnId: 'c', tabs: [{ workspaceId: 'd1', kind: 'dsh_web' }], activeTabId: 'd1', ratio: 1 }]
        },
        new Map([['c', { left: 0, top: 40, width: 800, height: 600 }]])
      );
      const panel = document.querySelector('.dsh-panel[data-workspace-id="d1"]');
      stubPanelRect(panel, { left: 12, top: 52, width: 776, height: 576 });
      host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:1234' });
      flushAgentAnimationFrames();
      assert.equal(spy.mock.calls.at(-1)[0].visible, true);

      document.dispatchEvent(new window.CustomEvent('psx-shell-overlay', { detail: { open: true } }));
      flushAgentAnimationFrames();
      assert.equal(spy.mock.calls.at(-1)[0].visible, false);

      document.dispatchEvent(new window.CustomEvent('psx-shell-overlay', { detail: { open: false } }));
      flushAgentAnimationFrames();
      assert.equal(spy.mock.calls.at(-1)[0].visible, true);

      document.dispatchEvent(new window.CustomEvent('psx-settings-state', { detail: { open: true } }));
      flushAgentAnimationFrames();
      assert.equal(spy.mock.calls.at(-1)[0].visible, false);
    } finally {
      spy.mockRestore();
    }
  });
});
