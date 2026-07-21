import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { beforeEach, describe, it } from 'node:test';
import { JSDOM } from 'jsdom';
import { repositoryRoot } from './agentHarness.js';

describe('Terminal clipboard bridge', () => {
  let manager;
  let requests;
  let terminal;

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
    globalThis.Bridge = {
      sendPasteRequest(sessionId, requestId) {
        requests.push({ sessionId, requestId });
      }
    };

    const source = fs.readFileSync(path.join(repositoryRoot, 'wwwroot/js/TerminalManager.js'), 'utf8');
    const TerminalManagerClass = vm.runInThisContext('(function () {\n' + source + '\n;return TerminalManager;\n})()', {
      filename: 'TerminalManager.js'
    });
    manager = new TerminalManagerClass(document.getElementById('terminal'));
    manager._newPasteRequestId = () => '8bc6bfac-6d4d-4fdb-a43b-dd7024bd4ccf';
    terminal = { modes: { bracketedPasteMode: false } };
    manager.terminals.set('7a5e9fba-61a6-442a-94e5-34e3f72a26f1', { terminal });
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
