import assert from 'node:assert/strict';
import { afterEach, beforeEach, describe, it } from 'vitest';
import { JSDOM } from 'jsdom';
import { Bridge } from '../../../frontend/webview/src/Bridge.js';
import { TerminalManager } from '../../../frontend/webview/src/TerminalManager.js';

// TerminalManager imports the Bridge singleton directly, so the paste request
// capture stubs the method on that shared object (globalThis.Bridge mirrors the
// same instance in the shipped app) and restores it afterwards.
describe('Terminal clipboard bridge', () => {
  let manager;
  let requests;
  let terminal;
  const realSendPasteRequest = Bridge.sendPasteRequest;

  beforeEach(() => {
    const dom = new JSDOM('<!doctype html><html><body><div id="terminal"></div></body></html>', {
      url: 'https://psx.local/',
      runScripts: 'outside-only'
    });
    globalThis.window = dom.window;
    globalThis.document = dom.window.document;
    globalThis.localStorage = dom.window.localStorage;
    globalThis.ResizeObserver = undefined;
    requests = [];
    Bridge.sendPasteRequest = (sessionId, requestId) => {
      requests.push({ sessionId, requestId });
    };

    manager = new TerminalManager(document.getElementById('terminal'));
    manager._newPasteRequestId = () => '8bc6bfac-6d4d-4fdb-a43b-dd7024bd4ccf';
    terminal = { modes: { bracketedPasteMode: false } };
    manager.terminals.set('7a5e9fba-61a6-442a-94e5-34e3f72a26f1', { terminal });
  });

  afterEach(() => {
    Bridge.sendPasteRequest = realSendPasteRequest;
  });

  it('correlates a response once and ignores mismatched or duplicate responses', () => {
    const sessionId = '7a5e9fba-61a6-442a-94e5-34e3f72a26f1';
    const requestId = '8bc6bfac-6d4d-4fdb-a43b-dd7024bd4ccf';
    const pasted = [];
    manager._pasteText = (...args) => pasted.push(args);

    manager._pasteFromClipboard(sessionId, terminal);
    assert.deepEqual(requests, [{ sessionId, requestId }]);

    manager.handlePasteResponse({ sessionId: 'wrong', requestId, ok: true, text: 'ignored' });
    manager.handlePasteResponse({ sessionId, requestId, ok: true, text: 'hello' });
    manager.handlePasteResponse({ sessionId, requestId, ok: true, text: 'duplicate' });

    assert.deepEqual(pasted, [[sessionId, terminal, 'hello']]);
  });

  it('preserves newline conversion and multiline bracketed paste', () => {
    const sent = [];
    manager._sendInput = (_sessionId, text) => sent.push(text);

    manager._pasteText('session', terminal, 'one\r\ntwo');

    assert.deepEqual(sent, ['\x1b[200~one\rtwo\x1b[201~']);
  });
});
