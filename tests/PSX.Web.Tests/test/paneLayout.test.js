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

test('index.html stacks both workspace layers under the neutral pane row', () => {
  const html = fs.readFileSync(path.join(webviewRoot, 'index.html'), 'utf8');
  const stack = /id="workspace-stack"([\s\S]*?)<template/.exec(html);
  assert.ok(stack, 'a #workspace-stack wrapper must exist');
  assert.ok(stack[1].includes('id="terminal-container"'), 'the stack hosts the terminal layer');
  assert.ok(stack[1].includes('id="agent-workspace-container"'), 'the stack hosts the agent layer');
  assert.ok(stack[1].includes('id="workspace-panes"'), 'the stack hosts the pane row');
  // Pane slots float above the content layers (ring + divider only).
  assert.ok(
    stack[1].indexOf('id="workspace-panes"') > stack[1].indexOf('id="agent-workspace-container"'),
    'the pane row renders above the workspace layers'
  );
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

test('PaneLayoutController projects pane rects and ring slots from a snapshot', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-stack"><div id="workspace-panes"></div></div>';
  const stack = document.getElementById('workspace-stack');
  Object.defineProperty(stack, 'clientWidth', { configurable: true, value: 1600 });
  Object.defineProperty(stack, 'clientHeight', { configurable: true, value: 900 });
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));

  const applied = [];
  layout.onLayoutApplied((snapshot, rects) => applied.push({ snapshot, rects }));
  layout.applySnapshot({
    revision: 1,
    focusedPaneId: 'pane-2',
    panes: [
      { paneId: 'pane-1', workspaceId: 'term-1', kind: 'terminal', ratio: 0.6 },
      { paneId: 'pane-2', workspaceId: 'agent-1', kind: 'agent', ratio: 0.4 }
    ]
  });

  const { rects } = applied.at(-1);
  assert.deepEqual(rects.get('pane-1'), { left: 0, top: 0, width: 960, height: 900 });
  assert.deepEqual(rects.get('pane-2'), { left: 960, top: 0, width: 640, height: 900 });

  const slot1 = layout.paneById('pane-1');
  const slot2 = layout.paneById('pane-2');
  assert.equal(slot1.dataset.focused, 'false');
  assert.equal(slot2.dataset.focused, 'true');
  assert.equal(slot1.style.flexGrow, '0.6');

  // The History dock inset shifts every pane and the slot row as one block.
  layout.setDockInset(292);
  const shifted = applied.at(-1).rects;
  assert.equal(shifted.get('pane-1').left, 292);
  assert.equal(shifted.get('pane-1').width, Math.round((1600 - 292) * 0.6));
  assert.equal(layout.root.style.left, '292px');

  // Closing back to one pane removes the extra slot.
  layout.applySnapshot({
    revision: 2,
    focusedPaneId: 'pane-1',
    panes: [{ paneId: 'pane-1', workspaceId: 'term-1', kind: 'terminal', ratio: 1 }]
  });
  assert.equal(layout.paneCount, 1);
  assert.equal(layout.paneById('pane-2'), null);
  layout.dispose();
});
