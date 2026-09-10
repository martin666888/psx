// paneLayout.test.js — column-model layout engine guards (VS Code
// editor-group model: an ordered list of columns, each with a tab stack, an
// active tab, a focus and normalized ratios). Columns are never collected or
// hidden: a single pure-ratio allocation (pixel floors constrain only the
// drag clamp and sash states) governs the effective presentation.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { flushAgentAnimationFrames, repositoryRoot, installAgentRuntime } from './agentHarness.js';

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

test('PaneLayoutController enumerates column slots without the Agent chunk', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-panes"><div class="workspace-pane" data-column-id="column-1"></div></div>';
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));
  assert.equal(layout.columnCount, 1);
  assert.equal(layout.root.dataset.splitActive, 'false', 'single columns must not paint the split focus ring');
  assert.equal(layout.columnAt(0).dataset.columnId, 'column-1');
  assert.equal(layout.columnById('column-1'), layout.columnAt(0));
  assert.equal(layout.columnById('missing'), null);
  assert.throws(() => new PaneLayoutController(null), /requires/);
});

test('PaneLayoutController drops late or replayed layout snapshots', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-panes"><div class="workspace-pane" data-column-id="column-1"></div></div>';
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));

  assert.equal(layout.applySnapshot({ revision: 2, focusedColumnId: 'column-1', columns: [] }), true);
  assert.equal(layout.applySnapshot({ revision: 1, focusedColumnId: 'column-1', columns: [] }), false, 'late snapshot');
  assert.equal(layout.applySnapshot({ revision: 2, focusedColumnId: 'column-1', columns: [] }), false, 'replayed snapshot');
  assert.equal(layout.applySnapshot({
    revision: 3,
    focusedColumnId: 'column-1',
    columns: [{ columnId: 'column-1', tabs: [], activeTabId: null, ratio: 1 }]
  }), true);
  assert.equal(layout.applySnapshot({}), false, 'malformed snapshot');
  assert.equal(layout.lastRevision, 3);
  assert.equal(layout.snapshot.columns.length, 1);
});

test('PaneLayoutController projects column rects and ring slots from a snapshot', async () => {
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
    focusedColumnId: 'column-2',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'term-1', kind: 'terminal' }], activeTabId: 'term-1', ratio: 0.6 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'agent-1', kind: 'agent' }], activeTabId: 'agent-1', ratio: 0.4 }
    ]
  });

  const { rects } = applied.at(-1);
  assert.deepEqual(rects.get('column-1'), { left: 40, top: 40, width: 936, height: 860 });
  assert.deepEqual(rects.get('column-2'), { left: 976, top: 40, width: 624, height: 860 });

  const slot1 = layout.columnById('column-1');
  const slot2 = layout.columnById('column-2');
  assert.equal(slot1.dataset.focused, 'false');
  assert.equal(slot2.dataset.focused, 'true');
  assert.equal(slot1.style.flexGrow, '0.6');
  assert.equal(layout.root.dataset.splitActive, 'true');

  // The History dock inset shifts every column and the slot row as one block.
  layout.setDockInset(292);
  const shifted = applied.at(-1).rects;
  assert.equal(shifted.get('column-1').left, 292);
  assert.equal(shifted.get('column-1').width, Math.round((1600 - 292) * 0.6));
  assert.equal(layout.root.style.left, '292px');

  // Closing back to one column removes the extra slot.
  layout.applySnapshot({
    revision: 2,
    focusedColumnId: 'column-1',
    columns: [{ columnId: 'column-1', tabs: [{ workspaceId: 'term-1', kind: 'terminal' }], activeTabId: 'term-1', ratio: 1 }]
  });
  assert.equal(layout.columnCount, 1);
  assert.equal(layout.columnById('column-2'), null);
  assert.equal(layout.root.dataset.splitActive, 'false');
  layout.dispose();
});

test('History dock preview moves columns interactively and settles once at the same width', async () => {
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
  layout.onLayoutApplied((snapshot, rects, options) => applied.push({ rects, options }));
  layout.applySnapshot({
    revision: 1,
    focusedColumnId: 'column-1',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.6 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 0.4 }
    ]
  });

  // Live drag frame: the dock inset moves every column immediately and flags
  // the layout interactive so Terminal fit stays deferred until release.
  layout.setDockInset(350, { interactiveResize: true });
  const previewed = applied.at(-1);
  assert.equal(previewed.rects.get('column-1').left, 350);
  assert.equal(previewed.rects.get('column-1').width, Math.round((1600 - 350) * 0.6));
  assert.equal(previewed.options.interactiveResize, true, 'preview defers terminal fit');
  // Flat redesign: columns fill their rects edge to edge; no dock-adjacent
  // gutter flag exists anymore.
  assert.equal(previewed.rects.get('column-1').dockAdjacent, undefined);
  assert.equal(previewed.rects.get('column-2').dockAdjacent, undefined);

  // An identical preview frame is a no-op: same width AND same interactive mode.
  const count = applied.length;
  layout.setDockInset(350, { interactiveResize: true });
  assert.equal(applied.length, count, 'identical interactive preview is skipped');

  // The commit settles at the SAME width: the early-return must not swallow
  // the non-interactive recompute, so terminals fit exactly once at drag end.
  layout.setDockInset(350, { interactiveResize: false });
  const settled = applied.at(-1);
  assert.equal(settled.rects.get('column-1').left, 350);
  assert.equal(settled.options.interactiveResize, false, 'same-width settle still recomputes non-interactively');

  // Dock closed (inset == rail width): columns start at the rail edge.
  layout.setDockInset(40, { interactiveResize: false });
  const closed = applied.at(-1);
  assert.equal(closed.rects.get('column-1').left, 40,
    'a closed dock leaves the first column at the rail edge');
  assert.equal(closed.rects.get('column-1').dockAdjacent, undefined);
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
  layout.onLayoutApplied((snapshot, rects, options) => applied.push({ rects, options }));
  layout.applySnapshot({
    revision: 1,
    focusedColumnId: 'column-1',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.5 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 0.5 }
    ]
  });

  const fire = (type, props) => {
    const event = new window.Event(type, { bubbles: true, cancelable: true });
    Object.assign(event, props);
    layout.root.querySelector('.workspace-pane-divider').dispatchEvent(event);
  };
  const divider = layout.root.querySelector('.workspace-pane-divider');
  const ratioMessages = () =>
    runtime.postedMessages.filter((entry) => entry.type === 'pane_ratios_commit');

  fire('pointerdown', { button: 0, pointerId: 7, clientX: 800 });
  assert.equal(layout.root.querySelector('.workspace-pane-divider'), divider, 'divider remains stable after pointerdown');
  fire('pointermove', { pointerId: 7, clientX: 480 });
  fire('pointermove', { pointerId: 7, clientX: 500 });
  assert.equal(applied.at(-1).options.interactiveResize, false, 'pointer moves wait for the next frame');
  flushAgentAnimationFrames();
  assert.equal(applied.at(-1).rects.get('column-1').width, 480, 'preview recomputes rects locally');
  assert.equal(applied.at(-1).options.interactiveResize, true, 'preview flags terminal fit as deferred');
  assert.equal(ratioMessages().length, 0, 'no bridge traffic mid-drag');

  fire('pointerup', { pointerId: 7, clientX: 500 });
  assert.equal(applied.at(-1).options.interactiveResize, false, 'pointerup performs one final real layout');
  const sent = ratioMessages();
  assert.equal(sent.length, 1, 'one intent per drag end');
  assert.equal(sent[0].baseRevision, 1);
  assert.deepEqual(sent[0].panes.map((pane) => pane.paneId), ['column-1', 'column-2']);
  assert.ok(Math.abs(sent[0].panes[0].ratio - (480 / 1560)) < 0.001);
  assert.ok(Math.abs(sent[0].panes[1].ratio - (1080 / 1560)) < 0.001);
  assert.equal(layout.root.querySelector('.workspace-pane-divider'), divider, 'divider survives every preview frame');

  // The authoritative snapshot clears any preview.
  layout.applySnapshot({
    revision: 2,
    focusedColumnId: 'column-1',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.3 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 0.7 }
    ]
  });
  assert.equal(layout.previewRatios, null);
  layout.dispose();
});

test('divider drag stays on the pure-ratio geometry when a column sits below its floor', async () => {
  const runtime = installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-stack"><div id="workspace-panes"></div></div>';
  const stack = document.getElementById('workspace-stack');
  Object.defineProperty(stack, 'clientWidth', { configurable: true, value: 1040 });
  Object.defineProperty(stack, 'clientHeight', { configurable: true, value: 900 });
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));
  const applied = [];
  layout.onLayoutApplied((snapshot, rects, options) => applied.push({ rects, options }));
  layout.applySnapshot({
    revision: 1,
    focusedColumnId: 'column-1',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 't', kind: 'terminal' }], activeTabId: 't', ratio: 0.4 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.6 }
    ]
  });

  // 1000px area: the focused terminal's 400px share sits below its 480px
  // floor, yet the allocation stays purely proportional (no expansion). The
  // divider is a net-zero flex item (8px box, -4px margins each side), so
  // its centre lands exactly on the boundary between the ratio slots — the
  // same boundary the content rects use.
  const initial = applied.at(-1).rects;
  assert.equal(initial.get('column-1').width, 400);
  assert.equal(initial.get('column-2').width, 600);
  const dividerCentre = (leftShare) => layout.areaLeft + layout.areaWidth * leftShare;
  const boundary = (rects) => rects.get('column-1').left + rects.get('column-1').width;
  assert.ok(Math.abs(boundary(initial) - dividerCentre(0.4)) <= 1, 'divider boundary == content rect boundary');

  const fire = (type, props) => {
    const event = new window.PointerEvent(type, { bubbles: true, cancelable: true, ...props });
    layout.root.querySelector('.workspace-pane-divider').dispatchEvent(event);
  };
  const ratioMessages = () =>
    runtime.postedMessages.filter((entry) => entry.type === 'pane_ratios_commit');

  // Real pointer sequence: grab the divider at the boundary, drag right.
  fire('pointerdown', { button: 0, pointerId: 7, clientX: 440 });
  fire('pointermove', { pointerId: 7, clientX: 500 });
  flushAgentAnimationFrames();
  // The floor clamp holds the terminal at its 480px floor; the preview stays
  // ratio-consistent with the drag and sends no bridge traffic mid-drag.
  assert.equal(applied.at(-1).rects.get('column-1').width, 480);
  assert.equal(applied.at(-1).options.interactiveResize, true);
  assert.equal(ratioMessages().length, 0, 'no bridge traffic mid-drag');

  fire('pointerup', { pointerId: 7, clientX: 500 });
  const sent = ratioMessages();
  assert.equal(sent.length, 1, 'one intent per drag end');
  assert.equal(sent[0].baseRevision, 1);
  assert.ok(Math.abs(sent[0].panes[0].ratio - 0.48) < 0.001);
  assert.ok(Math.abs(sent[0].panes[1].ratio - 0.52) < 0.001);
  const released = applied.at(-1).rects;
  assert.equal(released.get('column-1').width, 480);
  assert.equal(released.get('column-2').width, 520);

  // C# accepts the vector with a HIGHER revision — a same- or lower-revision
  // echo would be dropped by the guard and the preview would stick forever.
  layout.applySnapshot({
    revision: 2,
    focusedColumnId: 'column-2',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 't', kind: 'terminal' }], activeTabId: 't', ratio: 0.48 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.52 }
    ]
  });
  assert.equal(layout.previewRatios, null, 'the echo clears the preview');
  const echoed = applied.at(-1).rects;
  assert.equal(echoed.get('column-1').width, 480, 'release and echo widths match — no jump');
  assert.equal(echoed.get('column-2').width, 520);
  assert.ok(Math.abs(boundary(echoed) - dividerCentre(0.48)) <= 1, 'divider boundary == content rect boundary');
  layout.dispose();
});

test('columns below their floors stay visible under pure ratio allocation (no focus expansion)', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-stack"><div id="workspace-panes"></div></div>';
  const stack = document.getElementById('workspace-stack');
  const setWidth = (value) =>
    Object.defineProperty(stack, 'clientWidth', { configurable: true, value });
  setWidth(1040);
  Object.defineProperty(stack, 'clientHeight', { configurable: true, value: 900 });
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));
  const applied = [];
  layout.onLayoutApplied((snapshot, rects) => applied.push({ snapshot, rects }));
  layout.applySnapshot({
    revision: 1,
    focusedColumnId: 'column-3',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.4 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'agent' }], activeTabId: 'b', ratio: 0.4 },
      { columnId: 'column-3', tabs: [{ workspaceId: 'c', kind: 'agent' }], activeTabId: 'c', ratio: 0.2 }
    ]
  });

  // 1000px area, Agent floors 320 each (960 total): the focused column's
  // ratio share (200px) falls below its floor, yet the allocation stays
  // purely proportional — focus never expands a column.
  const allocated = applied.at(-1).rects;
  assert.equal(allocated.get('column-1').width, 400);
  assert.equal(allocated.get('column-2').width, 400);
  assert.equal(allocated.get('column-3').width, 200);
  assert.equal(layout.columnCount, 3, 'no column is ever hidden');

  // Switching the focus leaves every column width pixel-identical: geometry
  // is one pure-ratio rule; focus only marks the active column.
  layout.applySnapshot({
    revision: 2,
    focusedColumnId: 'column-1',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.4 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'agent' }], activeTabId: 'b', ratio: 0.4 },
      { columnId: 'column-3', tabs: [{ workspaceId: 'c', kind: 'agent' }], activeTabId: 'c', ratio: 0.2 }
    ]
  });
  const refocused = applied.at(-1).rects;
  assert.equal(refocused.get('column-1').width, 400);
  assert.equal(refocused.get('column-2').width, 400);
  assert.equal(refocused.get('column-3').width, 200);

  // Below the total floor the ratio allocation squeezes every column under
  // its floor proportionally — all remain visible (decision K).
  setWidth(840);
  layout.recompute();
  const squeezed = applied.at(-1).rects;
  assert.equal(squeezed.get('column-1').width, 320);
  assert.equal(squeezed.get('column-2').width, 320);
  assert.equal(squeezed.get('column-3').width, 160, 'the narrowest column squeezes below its floor too');
  assert.equal(layout.columnCount, 3, 'never collected, never hidden');
  layout.dispose();
});

test('tab drag-in highlights the target column and sends pane_move on drop', async () => {
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
    focusedColumnId: 'column-1',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.5 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 0.5 }
    ]
  });
  const slot1 = layout.columnById('column-1');
  const slot2 = layout.columnById('column-2');
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
  assert.equal(moves[0].paneId, 'column-2');
  assert.equal(slot2.dataset.dropTarget, 'false');

  // Non-workspace payloads never move anything.
  fire('drop', { clientX: 1200, dataTransfer: { dropEffect: '', getData: () => 'not-a-guid' } });
  assert.equal(runtime.postedMessages.filter(m => m.type === 'pane_move').length, 1);
  layout.dispose();
});

test('zoom shows only the focused column, follows focus, and toggles back', async () => {
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
    focusedColumnId: 'column-2',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.5 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 0.5 }
    ]
  });

  assert.equal(layout.toggleZoom(), true);
  const zoomed = applied.at(-1);
  assert.equal(zoomed.columns.length, 1, 'only the focused column survives zoom');
  assert.equal(zoomed.columns[0].columnId, 'column-2');
  assert.equal(layout.columnCount, 1);
  assert.equal(layout.snapshot.columns.length, 2, 'requested layout untouched');

  // Focus moving while zoomed switches the zoomed column.
  layout.applySnapshot({
    revision: 2,
    focusedColumnId: 'column-1',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.5 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 0.5 }
    ]
  });
  assert.equal(applied.at(-1).columns[0].columnId, 'column-1');

  assert.equal(layout.toggleZoom(), false);
  assert.equal(applied.at(-1).columns.length, 2, 'toggle back restores every column');
  layout.dispose();
});

test('capacity helpers measure the requested layout, never the effective columns', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-stack"><div id="workspace-panes"></div></div>';
  const stack = document.getElementById('workspace-stack');
  Object.defineProperty(stack, 'clientWidth', { configurable: true, value: 900 });
  Object.defineProperty(stack, 'clientHeight', { configurable: true, value: 900 });
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));
  layout.setTerminalMinimumWidthResolver(() => 640);
  layout.applySnapshot({
    revision: 1,
    focusedColumnId: 'column-3',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 1 / 3 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 1 / 3 },
      { columnId: 'column-3', tabs: [{ workspaceId: 'c', kind: 'agent' }], activeTabId: 'c', ratio: 1 / 3 }
    ]
  });
  // 860px after the rail cannot hold the 1280px floor sum: the columns
  // squeeze below their floors and stay visible (no collection), while the
  // helpers keep measuring the full C# request so a squeezed layout never
  // unlocks "create".
  assert.equal(layout.effectiveColumns.length, 3, 'narrow layouts never collect columns');
  assert.equal(layout.availableWidth(), 860);
  assert.equal(layout.requestedMinimumWidthSum(), 400 + 640 + 400);
  assert.equal(layout.minimumWidthForNewPane('agent'), 400);
  assert.equal(layout.minimumWidthForNewPane('terminal'), 640, 'new terminal reuses the measured width');

  // Without any open terminal the new-terminal minimum falls back to 480.
  layout.applySnapshot({
    revision: 2,
    focusedColumnId: 'column-1',
    columns: [{ columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 1 }]
  });
  assert.equal(layout.minimumWidthForNewPane('terminal'), 480);
  layout.dispose();
});

test('divider at floor reports at-minimum sash state and disables at both floors', async () => {
  installAgentRuntime();
  const { PaneLayoutController } = await import(
    pathToFileURL(path.join(webviewRoot, 'src', 'PaneLayoutController.js')).href
  );
  document.body.innerHTML =
    '<div id="workspace-stack"><div id="workspace-panes"></div></div>';
  const stack = document.getElementById('workspace-stack');
  const setWidth = (value) =>
    Object.defineProperty(stack, 'clientWidth', { configurable: true, value });
  setWidth(1000);
  Object.defineProperty(stack, 'clientHeight', { configurable: true, value: 900 });
  const layout = new PaneLayoutController(document.getElementById('workspace-panes'));
  layout.applySnapshot({
    revision: 1,
    focusedColumnId: 'column-2',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.5 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 0.5 }
    ]
  });
  const divider = layout.root.querySelector('.workspace-pane-divider');

  // 960px area, floors 320/480: 480/480 allocation puts the terminal exactly
  // on its floor -> at-minimum, still draggable.
  assert.equal(divider.dataset.sashState, 'at-minimum');
  assert.equal(divider.getAttribute('aria-disabled'), 'false');

  // 800px area: pure ratios 0.4/0.6 place the agent on its 320px floor and
  // the terminal on its 480px floor — both sides pinned to their floors, so
  // the divider disables entirely (pure ratio allocation, no focus expansion).
  setWidth(840);
  layout.applySnapshot({
    revision: 2,
    focusedColumnId: 'column-2',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.4 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'terminal' }], activeTabId: 'b', ratio: 0.6 }
    ]
  });
  assert.equal(divider.dataset.sashState, 'at-minimum');
  assert.equal(divider.getAttribute('aria-disabled'), 'true', 'both sides at their floors disable the divider');

  // A wider window releases both states.
  setWidth(1600);
  layout.recompute();
  assert.equal(divider.dataset.sashState, '');
  assert.equal(divider.getAttribute('aria-disabled'), 'false');
  layout.dispose();
});

test('double-click divider equalizes the adjacent pair', async () => {
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
    focusedColumnId: 'column-1',
    columns: [
      { columnId: 'column-1', tabs: [{ workspaceId: 'a', kind: 'agent' }], activeTabId: 'a', ratio: 0.7 },
      { columnId: 'column-2', tabs: [{ workspaceId: 'b', kind: 'agent' }], activeTabId: 'b', ratio: 0.3 }
    ]
  });
  layout.root.querySelector('.workspace-pane-divider').dispatchEvent(
    new window.MouseEvent('dblclick', { bubbles: true, cancelable: true })
  );
  const sent = runtime.postedMessages.filter((entry) => entry.type === 'pane_ratios_commit');
  assert.equal(sent.length, 1);
  assert.equal(sent[0].baseRevision, 1);
  assert.deepEqual(sent[0].panes.map((pane) => pane.paneId), ['column-1', 'column-2']);
  assert.equal(sent[0].panes[0].ratio, 0.5);
  assert.equal(sent[0].panes[1].ratio, 0.5);
  layout.dispose();
});
