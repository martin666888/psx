import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { beforeEach, describe, it, vi } from 'vitest';
import { installAgentRuntime, repositoryRoot } from './agentHarness.js';

const hostUrl = pathToFileURL(
  path.join(repositoryRoot, 'frontend', 'webview', 'src', 'KimiWebWorkspaceHost.js')
).href;

function mountContainer() {
  document.body.innerHTML = '<div id="kimi-web-workspace-container"></div>';
}

describe('KimiWebWorkspaceHost', () => {
  beforeEach(() => {
    installAgentRuntime();
    mountContainer();
  });

  it('projects a panel for a visible kimi_web column and hides it elsewhere', async () => {
    const { KimiWebWorkspaceHost } = await import(hostUrl);
    const host = new KimiWebWorkspaceHost(document.getElementById('kimi-web-workspace-container'));
    const rects = new Map([['column-2', { left: 500, top: 40, width: 700, height: 600 }]]);
    const snapshot = {
      focusedColumnId: 'column-2',
      columns: [
        { columnId: 'column-1', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 0.4 },
        { columnId: 'column-2', tabs: [{ workspaceId: 'k1', kind: 'kimi_web' }], activeTabId: 'k1', ratio: 0.6 }
      ]
    };
    host.applyLayout(snapshot, rects);
    const panel = document.querySelector('.kimi-web-panel[data-workspace-id="k1"]');
    assert.ok(panel, 'panel exists for the kimi_web column');
    assert.equal(panel.dataset.columnId, 'column-2');
    assert.equal(panel.hidden, false);
    assert.equal(panel.style.left, '512px');
    assert.equal(panel.style.width, '676px');

    // The kimi tab becomes inactive (a terminal takes its column): hidden.
    const snapshot2 = {
      focusedColumnId: 'column-2',
      columns: [
        { columnId: 'column-1', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 0.4 },
        {
          columnId: 'column-2',
          tabs: [{ workspaceId: 'k1', kind: 'kimi_web' }, { workspaceId: 't2', kind: 'terminal' }],
          activeTabId: 't2',
          ratio: 0.6
        }
      ]
    };
    host.applyLayout(snapshot2, rects);
    assert.equal(panel.hidden, true);
  });

  it('removes a closed workspace panel instead of leaving it hidden', async () => {
    const { KimiWebWorkspaceHost } = await import(hostUrl);
    const host = new KimiWebWorkspaceHost(document.getElementById('kimi-web-workspace-container'));
    const rects = new Map([['c', { left: 0, top: 40, width: 800, height: 600 }]]);
    host.applyLayout(
      {
        focusedColumnId: 'c',
        columns: [{ columnId: 'c', tabs: [{ workspaceId: 'k1', kind: 'kimi_web' }], activeTabId: 'k1', ratio: 1 }]
      },
      rects
    );
    assert.equal(document.querySelectorAll('.kimi-web-panel').length, 1);

    host.applyLayout(
      {
        focusedColumnId: 'c',
        columns: [{ columnId: 'c', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 1 }]
      },
      rects
    );
    assert.equal(document.querySelector('.kimi-web-panel[data-workspace-id="k1"]'), null);
    assert.equal(document.querySelectorAll('.kimi-web-panel').length, 0);

    host.applyLayout(
      {
        focusedColumnId: 'c',
        columns: [{ columnId: 'c', tabs: [{ workspaceId: 'k2', kind: 'kimi_web' }], activeTabId: 'k2', ratio: 1 }]
      },
      rects
    );
    assert.equal(document.querySelector('.kimi-web-panel[data-workspace-id="k1"]'), null);
    assert.ok(document.querySelector('.kimi-web-panel[data-workspace-id="k2"]'));
    assert.equal(document.querySelectorAll('.kimi-web-panel').length, 1);
  });

  it('renders every status card with wire-safe copy and remounts on a new ready URL', async () => {
    const { KimiWebWorkspaceHost } = await import(hostUrl);
    const host = new KimiWebWorkspaceHost(document.getElementById('kimi-web-workspace-container'));
    host.applyLayout(
      {
        focusedColumnId: 'c',
        columns: [{ columnId: 'c', tabs: [{ workspaceId: 'k1', kind: 'kimi_web' }], activeTabId: 'k1', ratio: 1 }]
      },
      new Map([['c', { left: 0, top: 40, width: 800, height: 600 }]])
    );
    const panel = document.querySelector('.kimi-web-panel[data-workspace-id="k1"]');
    const card = () => panel.querySelector('[data-role="kimi-web-runtime-card"]');

    // Initial projection renders the idle stopped card.
    assert.ok(card(), 'status card present before any runtime status');
    assert.equal(card().dataset.state, 'stopped');
    assert.ok(panel.querySelector('[data-icon="kimi"]'), 'Kimi brand mark present');
    assert.match(panel.textContent, /已停止/);

    // Unavailable reason copy maps the wire-safe reason key.
    host.applyRuntimeStatus({ state: 'unavailable', reason: 'runtime_invalid' });
    assert.equal(card().dataset.state, 'unavailable');
    assert.match(panel.textContent, /Kimi 运行时文件无效/);
    assert.match(panel.textContent, /重新解压或下载 PSX/);
    assert.ok(panel.querySelector('[data-role="kimi-web-retry"]'), 'retry action present');

    // Failed errorClass copy maps the fixed wire-safe key.
    host.applyRuntimeStatus({ state: 'failed', errorClass: 'launch_failed' });
    assert.match(panel.textContent, /Kimi Web 进程未能启动/);
    assert.ok(panel.querySelector('[data-role="kimi-web-retry"]'));

    // Starting renders the busy state without a retry action.
    host.applyRuntimeStatus({ state: 'starting' });
    assert.equal(card().dataset.state, 'starting');
    assert.equal(card().getAttribute('aria-busy'), 'true');
    assert.equal(panel.querySelector('[data-role="kimi-web-retry"]'), null);

    // Ready mounts the cross-origin iframe whose src carries the token
    // fragment; the status card is gone.
    host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:1234/#token=abc123' });
    const iframe = panel.querySelector('iframe.kimi-web-frame');
    assert.ok(iframe, 'ready state mounts the cross-origin iframe');
    assert.equal(iframe.src, 'http://127.0.0.1:1234/#token=abc123');
    assert.ok(iframe.src.includes('#token=abc123'), 'the iframe src carries the ready fragment');
    assert.equal(panel.classList.contains('kimi-web-panel--status'), false);
    assert.equal(panel.querySelector('.kimi-web-card'), null, 'ready iframe has no sibling status card');
    const sandboxAttr = iframe.getAttribute('sandbox') || '';
    assert.ok(sandboxAttr.includes('allow-scripts'), 'sandbox includes allow-scripts');
    assert.ok(sandboxAttr.includes('allow-downloads'), 'sandbox includes allow-downloads');

    iframe.dataset.keep = '1';
    host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:1234/#token=abc123' });
    assert.equal(panel.querySelector('iframe.kimi-web-frame')?.dataset.keep, '1', 'same ready URL does not remount');

    host.applyColorScheme('dark');
    assert.equal(panel.querySelector('iframe.kimi-web-frame').style.colorScheme, 'dark');

    host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:5678/#token=new456' });
    const remounted = panel.querySelector('iframe.kimi-web-frame');
    assert.ok(remounted, 'new ready URL remounts');
    assert.notEqual(remounted.dataset.keep, '1');
    assert.equal(remounted.src, 'http://127.0.0.1:5678/#token=new456');
  });

  it('focuses the column from panel mousedown and a matching iframe ping', async () => {
    const { KimiWebWorkspaceHost } = await import(hostUrl);
    const bridge = globalThis.Bridge;
    const spy = vi.spyOn(bridge, 'sendPaneFocus');
    let embeddedPointerPings = 0;
    const onEmbeddedPointer = () => { embeddedPointerPings += 1; };
    document.addEventListener('psx-embedded-frame-pointerdown', onEmbeddedPointer);
    try {
      const host = new KimiWebWorkspaceHost(document.getElementById('kimi-web-workspace-container'));
      host.applyLayout(
        {
          focusedColumnId: 'column-2',
          columns: [
            { columnId: 'column-1', tabs: [{ workspaceId: 't1', kind: 'terminal' }], activeTabId: 't1', ratio: 0.4 },
            { columnId: 'column-2', tabs: [{ workspaceId: 'k1', kind: 'kimi_web' }], activeTabId: 'k1', ratio: 0.6 }
          ]
        },
        new Map([['column-2', { left: 500, top: 40, width: 700, height: 600 }]])
      );
      const panel = document.querySelector('.kimi-web-panel[data-workspace-id="k1"]');
      assert.equal(panel.dataset.columnId, 'column-2');
      panel.dispatchEvent(new window.MouseEvent('mousedown', { bubbles: true }));
      assert.deepEqual(spy.mock.calls[0], ['column-2']);

      host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:5998/#token=abc' });
      window.dispatchEvent(new window.MessageEvent('message', {
        origin: 'http://127.0.0.1:5998',
        data: { source: 'psx-kimi-web-focus' }
      }));
      assert.equal(spy.mock.calls.length, 2, 'matching origin focus ping forwarded');
      assert.deepEqual(spy.mock.calls[1], ['column-2']);
      assert.equal(embeddedPointerPings, 1, 'matching origin closes shell chrome overlays');

      window.dispatchEvent(new window.MessageEvent('message', {
        origin: 'http://127.0.0.1:1',
        data: { source: 'psx-kimi-web-focus' }
      }));
      assert.equal(spy.mock.calls.length, 2, 'wrong origin focus ping dropped');
      assert.equal(embeddedPointerPings, 1, 'wrong origin cannot close shell chrome overlays');
    } finally {
      document.removeEventListener('psx-embedded-frame-pointerdown', onEmbeddedPointer);
      spy.mockRestore();
    }
  });

  it('forwards a kimi export handoff only from the ready origin', async () => {
    const { KimiWebWorkspaceHost } = await import(hostUrl);
    const bridge = globalThis.Bridge;
    assert.ok(bridge, 'Bridge global is installed');
    const spy = vi.spyOn(bridge, 'sendKimiWebExport');
    try {
      // A unique port isolates this host from window listeners accumulated
      // by earlier tests in the same file.
      const host = new KimiWebWorkspaceHost(document.getElementById('kimi-web-workspace-container'));
      host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:5999/#token=abc' });

      window.dispatchEvent(new window.MessageEvent('message', {
        origin: 'http://127.0.0.1:5999',
        data: {
          source: 'psx-kimi-web-export',
          url: 'http://127.0.0.1:5999/api/v1/sessions/s1/export',
          path: '/api/v1/sessions/s1/export',
          sessionId: 's1'
        }
      }));
      assert.equal(spy.mock.calls.length, 1, 'ready origin forwards once');
      assert.deepEqual(spy.mock.calls[0],
        ['http://127.0.0.1:5999/api/v1/sessions/s1/export', '/api/v1/sessions/s1/export', 's1']);

      // Wrong origin -> dropped (no new call).
      window.dispatchEvent(new window.MessageEvent('message', {
        origin: 'http://127.0.0.1:9999',
        data: {
          source: 'psx-kimi-web-export',
          url: 'http://127.0.0.1:9999/api/v1/sessions/s2/export',
          path: '/api/v1/sessions/s2/export',
          sessionId: 's2'
        }
      }));
      assert.equal(spy.mock.calls.length, 1, 'wrong origin dropped');

      // Not ready (readyUrl cleared) -> dropped.
      host.applyRuntimeStatus({ state: 'stopped' });
      window.dispatchEvent(new window.MessageEvent('message', {
        origin: 'http://127.0.0.1:5999',
        data: {
          source: 'psx-kimi-web-export',
          url: 'http://127.0.0.1:5999/api/v1/sessions/s3/export',
          path: '/api/v1/sessions/s3/export',
          sessionId: 's3'
        }
      }));
      assert.equal(spy.mock.calls.length, 1, 'not-ready dropped');
    } finally {
      spy.mockRestore();
    }
  });
});
