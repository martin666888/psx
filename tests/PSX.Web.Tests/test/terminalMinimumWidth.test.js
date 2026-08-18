import assert from 'node:assert/strict';
import { afterEach, beforeEach, describe, it } from 'vitest';
import { JSDOM } from 'jsdom';

let TerminalManager = null;

// Regression guard for the split-capacity gate: the xterm probe element
// (.xterm-char-measure-element) holds a REPEATED probe string (32 chars in
// xterm 6.x), so its bounding rect is the full string width. minimumPaneWidth
// must divide by the character count — otherwise the measured terminal floor
// inflates ~32x (≈14800px) and permanently disables "new right column".
describe('TerminalManager minimumPaneWidth', () => {
  const realResizeObserver = globalThis.ResizeObserver;
  let manager;
  let dom;

  beforeEach(async () => {
    dom = new JSDOM('<!doctype html><html><body><div id="terminal"></div></body></html>', {
      url: 'https://psx.local/',
      runScripts: 'outside-only'
    });
    dom.window.HTMLCanvasElement.prototype.getContext = () => null;
    globalThis.window = dom.window;
    globalThis.document = dom.window.document;
    globalThis.ResizeObserver = undefined;
    TerminalManager ??= (
      await import('../../../frontend/webview/src/TerminalManager.js')
    ).TerminalManager;
    manager = new TerminalManager(document.getElementById('terminal'));
  });

  afterEach(() => {
    globalThis.ResizeObserver = realResizeObserver;
    window.close();
  });

  function installTerminal(sessionId, probeChars, probeWidth) {
    const element = document.createElement('div');
    if (probeChars > 0) {
      const measure = document.createElement('span');
      measure.className = 'xterm-char-measure-element';
      measure.textContent = 'W'.repeat(probeChars);
      measure.getBoundingClientRect = () => ({ width: probeWidth });
      element.appendChild(measure);
    }
    manager.terminals.set(sessionId, { element });
  }

  it('divides the probe element width by its character count', () => {
    // 32-char probe at 246.3125px → 7.697px per cell → ceil(60 * 7.697 + 16) = 478.
    installTerminal('term-1', 32, 246.3125);
    assert.equal(manager.minimumPaneWidth('term-1'), 478);
  });

  it('keeps the 400px floor for narrow cells', () => {
    // 32-char probe at 128px → 4px per cell → 60 * 4 + 16 = 256 → floor 400.
    installTerminal('term-2', 32, 128);
    assert.equal(manager.minimumPaneWidth('term-2'), 400);
  });

  it('falls back to 480 when the probe element is absent or empty', () => {
    installTerminal('term-3', 0, 0);
    assert.equal(manager.minimumPaneWidth('term-3'), 480);
    assert.equal(manager.minimumPaneWidth('unknown'), 480);
  });
});
