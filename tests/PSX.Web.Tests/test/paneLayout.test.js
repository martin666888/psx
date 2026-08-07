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

test('divider drag previews locally and sends one ratio intent on pointerup', async () => {
  const runtime = installAgentRuntime();
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
  layout.onLayoutApplied((snapshot, rects) => applied.push(rects));
  layout.applySnapshot({
    revision: 1,
    focusedPaneId: 'pane-1',
    panes: [
      { paneId: 'pane-1', workspaceId: 'a', kind: 'agent', ratio: 0.5 },
      { paneId: 'pane-2', workspaceId: 'b', kind: 'terminal', ratio: 0.5 }
    ]
  });

  const fire = (type, props) => {
    const event = new window.Event(type, { bubbles: true, cancelable: true });
    Object.assign(event, props);
    layout.root.querySelector('.workspace-pane-divider').dispatchEvent(event);
  };
  const ratioMessages = () =>
    runtime.postedMessages.filter((entry) => entry.type === 'pane_ratio');

  fire('pointerdown', { button: 0, pointerId: 7 });
  fire('pointermove', { pointerId: 7, clientX: 480 });
  assert.equal(applied.at(-1).get('pane-1').width, 480, 'preview recomputes rects locally');
  assert.equal(ratioMessages().length, 0, 'no bridge traffic mid-drag');

  fire('pointerup', { pointerId: 7, clientX: 480 });
  const sent = ratioMessages();
  assert.equal(sent.length, 1, 'one intent per drag end');
  assert.equal(sent[0].paneId, 'pane-1');
  assert.ok(Math.abs(sent[0].ratio - 0.3) < 0.001);

  // The authoritative snapshot clears any preview.
  layout.applySnapshot({
    revision: 2,
    focusedPaneId: 'pane-1',
    panes: [
      { paneId: 'pane-1', workspaceId: 'a', kind: 'agent', ratio: 0.3 },
      { paneId: 'pane-2', workspaceId: 'b', kind: 'terminal', ratio: 0.7 }
    ]
  });
  assert.equal(layout.previewRatios, null);
  layout.dispose();
});

test('narrow stacks collapse right-hand panes effectively and restore on widen', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-stack"><div id="workspace-panes"></div></div>';
  const stack = document.getElementById('workspace-stack');
  const setWidth = (value) =>
    Object.defineProperty(stack, 'clientWidth', { configurable: true, value });
  setWidth(1600);
  Object.defineProperty(stack, 'clientHeight', { configurable: true, value: 900 });
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));
  const applied = [];
  layout.onLayoutApplied((snapshot) => applied.push(snapshot));
  layout.applySnapshot({
    revision: 1,
    focusedPaneId: 'pane-3',
    panes: [
      { paneId: 'pane-1', workspaceId: 'a', kind: 'agent', ratio: 1 / 3 },
      { paneId: 'pane-2', workspaceId: 'b', kind: 'terminal', ratio: 1 / 3 },
      { paneId: 'pane-3', workspaceId: 'c', kind: 'agent', ratio: 1 / 3 }
    ]
  });
  assert.equal(applied.at(-1).panes.length, 3, 'all panes fit at 1600px');

  // 900px only fits two 400px panes: pane-3 collapses, focus falls left.
  setWidth(900);
  layout.recompute();
  const collapsed = applied.at(-1);
  assert.equal(collapsed.panes.length, 2);
  assert.equal(collapsed.focusedPaneId, 'pane-2', 'focus never points into a collapsed pane');
  assert.equal(layout.paneById('pane-3'), null, 'the collapsed slot is removed');
  // The requested snapshot itself is untouched.
  assert.equal(layout.snapshot.panes.length, 3);

  // Widening restores the collapsed pane with its original assignment.
  setWidth(1600);
  layout.recompute();
  const restored = applied.at(-1);
  assert.equal(restored.panes.length, 3);
  assert.equal(restored.focusedPaneId, 'pane-3');
  assert.ok(layout.paneById('pane-3'));
  layout.dispose();
});

test('tab drag-in highlights the target pane and sends pane_move on drop', async () => {
  const runtime = installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-stack"><div id="workspace-panes"></div></div>';
  const stack = document.getElementById('workspace-stack');
  Object.defineProperty(stack, 'clientWidth', { configurable: true, value: 1600 });
  Object.defineProperty(stack, 'clientHeight', { configurable: true, value: 900 });
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));
  layout.applySnapshot({
    revision: 1,
    focusedPaneId: 'pane-1',
    panes: [
      { paneId: 'pane-1', workspaceId: 'a', kind: 'agent', ratio: 0.5 },
      { paneId: 'pane-2', workspaceId: 'b', kind: 'terminal', ratio: 0.5 }
    ]
  });
  const slot1 = layout.paneById('pane-1');
  const slot2 = layout.paneById('pane-2');
  slot1.getBoundingClientRect = () => ({ left: 0, right: 800, top: 0, bottom: 900 });
  slot2.getBoundingClientRect = () => ({ left: 800, right: 1600, top: 0, bottom: 900 });

  const fire = (type, props) => {
    const event = new window.Event(type, { bubbles: true, cancelable: true });
    Object.assign(event, props);
    layout.root.dispatchEvent(event);
    return event;
  };
  const workspaceId = 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa';
  const transfer = { dropEffect: '', getData: () => workspaceId };

  fire('dragover', { clientX: 1200, dataTransfer: transfer });
  assert.equal(slot2.dataset.dropTarget, 'true');
  assert.equal(slot1.dataset.dropTarget, 'false');
  assert.equal(runtime.postedMessages.filter(m => m.type === 'pane_move').length, 0);

  fire('drop', { clientX: 1200, dataTransfer: transfer });
  const moves = runtime.postedMessages.filter(m => m.type === 'pane_move');
  assert.equal(moves.length, 1);
  assert.equal(moves[0].workspaceId, workspaceId);
  assert.equal(moves[0].paneId, 'pane-2');
  assert.equal(slot2.dataset.dropTarget, 'false');

  // Non-workspace payloads never move anything.
  fire('drop', { clientX: 1200, dataTransfer: { dropEffect: '', getData: () => 'not-a-guid' } });
  assert.equal(runtime.postedMessages.filter(m => m.type === 'pane_move').length, 1);
  layout.dispose();
});

test('zoom shows only the focused pane, follows focus, and toggles back', async () => {
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
  layout.onLayoutApplied((snapshot) => applied.push(snapshot));
  layout.applySnapshot({
    revision: 1,
    focusedPaneId: 'pane-2',
    panes: [
      { paneId: 'pane-1', workspaceId: 'a', kind: 'agent', ratio: 0.5 },
      { paneId: 'pane-2', workspaceId: 'b', kind: 'terminal', ratio: 0.5 }
    ]
  });

  assert.equal(layout.toggleZoom(), true);
  const zoomed = applied.at(-1);
  assert.equal(zoomed.panes.length, 1, 'only the focused pane survives zoom');
  assert.equal(zoomed.panes[0].paneId, 'pane-2');
  assert.equal(layout.paneCount, 1);
  assert.equal(layout.snapshot.panes.length, 2, 'requested layout untouched');

  // Focus moving while zoomed switches the zoomed pane.
  layout.applySnapshot({
    revision: 2,
    focusedPaneId: 'pane-1',
    panes: [
      { paneId: 'pane-1', workspaceId: 'a', kind: 'agent', ratio: 0.5 },
      { paneId: 'pane-2', workspaceId: 'b', kind: 'terminal', ratio: 0.5 }
    ]
  });
  assert.equal(applied.at(-1).panes[0].paneId, 'pane-1');

  assert.equal(layout.toggleZoom(), false);
  assert.equal(applied.at(-1).panes.length, 2, 'toggle back restores every pane');
  layout.dispose();
});
