// paneLayout.test.js — split-pane Phase 0 seam guards.
//
// Phase 0 introduces the neutral pane root without changing behavior: one
// .workspace-pane wraps both workspace layers, and PaneLayoutController
// (always-loaded webview layer, no Agent chunk required) enumerates it.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { repositoryRoot, installAgentRuntime } from './agentHarness.js';

const webviewRoot = path.join(repositoryRoot, 'frontend', 'webview');

test('index.html nests both workspace layers inside a single neutral pane', () => {
  const html = fs.readFileSync(path.join(webviewRoot, 'index.html'), 'utf8');
  const paneBlock = /id="workspace-panes"[\s\S]*?class="workspace-pane"[^>]*>([\s\S]*?)<\/div>\s*<\/div>/.exec(html);
  assert.ok(paneBlock, 'a .workspace-pane wrapper must exist inside #workspace-panes');
  assert.ok(paneBlock[1].includes('id="terminal-container"'), 'the pane hosts the terminal layer');
  assert.ok(paneBlock[1].includes('id="agent-workspace-container"'), 'the pane hosts the agent layer');
});

test('PaneLayoutController enumerates panes without the Agent chunk', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-panes"><div class="workspace-pane" data-pane-id="pane-1"></div></div>';
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));
  assert.equal(layout.paneCount, 1);
  assert.equal(layout.paneAt(0).dataset.paneId, 'pane-1');
  assert.equal(layout.paneById('pane-1'), layout.paneAt(0));
  assert.equal(layout.paneById('missing'), null);
  assert.throws(() => new PaneLayoutController(null), /requires/);
});

test('PaneLayoutController drops late or replayed layout snapshots', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-panes"><div class="workspace-pane" data-pane-id="pane-1"></div></div>';
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));

  assert.equal(layout.applySnapshot({ revision: 2, focusedPaneId: 'pane-1', panes: [] }), true);
  assert.equal(layout.applySnapshot({ revision: 1, focusedPaneId: 'pane-1', panes: [] }), false, 'late snapshot');
  assert.equal(layout.applySnapshot({ revision: 2, focusedPaneId: 'pane-1', panes: [] }), false, 'replayed snapshot');
  assert.equal(layout.applySnapshot({ revision: 3, focusedPaneId: 'pane-1', panes: [{ paneId: 'pane-1' }] }), true);
  assert.equal(layout.applySnapshot({}), false, 'malformed snapshot');
  assert.equal(layout.lastRevision, 3);
  assert.equal(layout.snapshot.panes.length, 1);
});
