import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, it } from 'vitest';
import { JSDOM } from 'jsdom';
import {
  installRuntimeDiagnostics,
  isBenignResizeObserverMessage
} from '../../../frontend/webview/src/RuntimeDiagnostics.js';
import { repositoryRoot } from './agentHarness.js';

describe('RuntimeDiagnostics', () => {
  let dom;
  let dispose;

  beforeEach(() => {
    dom = new JSDOM('<!doctype html><html><body></body></html>', {
      url: 'https://psx.local/app/index.html'
    });
    dispose = installRuntimeDiagnostics(dom.window, dom.window.document);
  });

  afterEach(() => {
    dispose?.();
    dom.window.close();
  });

  it('classifies only the two Chromium ResizeObserver delivery warnings as benign', () => {
    assert.equal(isBenignResizeObserverMessage('ResizeObserver loop completed with undelivered notifications.'), true);
    assert.equal(isBenignResizeObserverMessage('ResizeObserver loop limit exceeded'), true);
    assert.equal(isBenignResizeObserverMessage('ResizeObserver failed'), false);
    assert.equal(isBenignResizeObserverMessage('ordinary application error'), false);
  });

  it('suppresses a benign ResizeObserver warning without rendering UI', () => {
    const event = new dom.window.ErrorEvent('error', {
      message: 'ResizeObserver loop completed with undelivered notifications.',
      cancelable: true
    });
    dom.window.dispatchEvent(event);

    assert.equal(event.defaultPrevented, true);
    assert.equal(dom.window.document.querySelector('[data-role="runtime-diagnostic"]'), null);
  });

  it('renders one dismissible and sanitized notice for a real uncaught error', () => {
    const secret = 'C:\\private\\workspace api-key-value';
    const dispatch = () => dom.window.dispatchEvent(new dom.window.ErrorEvent('error', {
      message: secret,
      cancelable: true
    }));
    dispatch();
    dispatch();

    const notices = dom.window.document.querySelectorAll('[data-role="runtime-diagnostic"]');
    assert.equal(notices.length, 1, 'duplicate errors share one user-facing notice');
    assert.equal(notices[0].textContent.includes(secret), false, 'paths and raw error text stay out of the UI');
    assert.match(notices[0].textContent, /界面发生异常/);

    notices[0].querySelector('button').click();
    assert.equal(dom.window.document.querySelector('[data-role="runtime-diagnostic"]'), null);
  });

  it('uses the same sanitized surface for an unhandled promise rejection', () => {
    const event = new dom.window.Event('unhandledrejection', { cancelable: true });
    Object.defineProperty(event, 'reason', { value: 'private rejection detail' });
    dom.window.dispatchEvent(event);

    const notice = dom.window.document.querySelector('[data-role="runtime-diagnostic"]');
    assert.ok(notice);
    assert.equal(notice.textContent.includes('private rejection detail'), false);
  });
});

it('production entry contains no persistent raw-error overlay', () => {
  const entry = fs.readFileSync(path.join(repositoryRoot, 'frontend', 'webview', 'index.html'), 'utf8');
  const main = fs.readFileSync(path.join(repositoryRoot, 'frontend', 'webview', 'src', 'main.js'), 'utf8');
  const css = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'runtime-diagnostics.css'),
    'utf8'
  );

  assert.doesNotMatch(entry, /window\.onerror|#f38ba8|\[JS ERROR\]/);
  assert.match(main, /installRuntimeDiagnostics\(\)/);
  assert.match(css, /\.psx-runtime-diagnostic-dismiss:focus-visible/);
});
