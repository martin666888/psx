import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { beforeEach, describe, it } from 'vitest';
import { installAgentRuntime, repositoryRoot } from './agentHarness.js';

const hostUrl = pathToFileURL(
  path.join(repositoryRoot, 'frontend', 'webview', 'src', 'DshWorkspaceHost.js')
).href;

function mountContainer() {
  document.body.innerHTML = '<div id="dsh-workspace-container"></div>';
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
    assert.ok(panel.querySelector('.dsh-card-action'), 'install button present');
    assert.match(panel.textContent, /npm/);

    host.applyRuntimeStatus({ state: 'failed' });
    assert.match(panel.textContent, /运行时不可用/);
    assert.ok(panel.querySelector('.dsh-card-action'), 'retry button present');

    host.applyRuntimeStatus({ state: 'ready', readyUrl: 'http://127.0.0.1:1234/' });
    assert.match(panel.textContent, /已就绪/);
  });
});
