import fs from 'node:fs';
import vm from 'node:vm';
import { JSDOM } from 'jsdom';
import { buildProductionBundle, generatedBundlePath } from './productionBundle.js';

let productionBundle;

function readProductionBundle() {
  if (!productionBundle) {
    if (!fs.existsSync(generatedBundlePath)) buildProductionBundle();
    productionBundle = fs.readFileSync(generatedBundlePath, 'utf8');
  }
  return productionBundle;
}

export function installAgentRuntime() {
  const dom = new JSDOM('<!doctype html><html><body></body></html>', {
    url: 'https://psx.local/',
    runScripts: 'outside-only'
  });
  globalThis.window = dom.window;
  globalThis.document = dom.window.document;
  Object.defineProperty(globalThis, 'navigator', {
    configurable: true,
    value: dom.window.navigator
  });
  globalThis.HTMLElement = dom.window.HTMLElement;
  globalThis.Event = dom.window.Event;
  globalThis.KeyboardEvent = dom.window.KeyboardEvent;
  globalThis.MouseEvent = dom.window.MouseEvent;
  globalThis.CustomEvent = dom.window.CustomEvent;
  globalThis.getComputedStyle = dom.window.getComputedStyle.bind(dom.window);
  globalThis.requestAnimationFrame = (callback) => {
    callback(0);
    return 1;
  };
  globalThis.cancelAnimationFrame = () => {};
  dom.window.requestAnimationFrame = globalThis.requestAnimationFrame;
  dom.window.cancelAnimationFrame = globalThis.cancelAnimationFrame;
  dom.window.matchMedia = () => ({
    matches: false,
    addEventListener() {},
    removeEventListener() {}
  });
  dom.window.document.execCommand = () => true;

  const postedMessages = [];
  const hostListeners = [];
  window.chrome = {
    webview: {
      postMessage(message) {
        postedMessages.push(JSON.parse(message));
      },
      addEventListener(name, listener) {
        if (name === 'message') hostListeners.push(listener);
      }
    }
  };
  vm.runInThisContext(readProductionBundle(), { filename: generatedBundlePath });

  return {
    Bridge: globalThis.Bridge,
    AgentThreadManager: globalThis.AgentThreadManager,
    postedMessages,
    emitHostMessage(data) {
      hostListeners.forEach((listener) => listener({ data }));
    }
  };
}

export function createAgentManager(AgentThreadManager) {
  document.body.innerHTML = `
    <main id="panel">
      <section id="thread"></section>
      <div id="input-row"><textarea id="input"></textarea><button id="send"></button></div>
      <section id="mode-transition-prompt" hidden></section>
      <div id="command-menu" hidden></div>
      <div id="command-hint" hidden></div>
      <button id="attach"></button><input id="attachment-input" type="file">
      <div id="attachments-strip"></div>
      <dialog id="image-preview"><img id="image-preview-img"><button id="image-preview-close"></button></dialog>
      <aside id="inspector"><button id="plan-tab"></button><span id="plan-unread" hidden></span><section id="plan-panel"></section><button id="history-tab"></button><section id="history-panel"></section></aside>
      <div id="inspector-resizer"></div>
      <select id="mode"></select><div id="config-options"></div>
      <span id="status"></span><span id="cwd"></span><span id="session"></span><span id="context-used"></span>
      <section id="runtime-card"><span id="runtime-title"></span><span id="runtime-message"></span><button id="runtime-install"></button><button id="runtime-cancel"></button></section>
    </main>`;

  const byId = (id) => document.getElementById(id);
  const manager = new AgentThreadManager(
    byId('panel'),
    byId('thread'),
    byId('input'),
    byId('send'),
    byId('command-menu'),
    {
      status: byId('status'),
      cwd: byId('cwd'),
      session: byId('session'),
      mode: byId('mode'),
      configOptions: byId('config-options'),
      contextUsed: byId('context-used'),
      commandHint: byId('command-hint'),
      inputRow: byId('input-row'),
      modeTransitionPrompt: byId('mode-transition-prompt'),
      attachButton: byId('attach'),
      attachmentInput: byId('attachment-input'),
      attachmentStrip: byId('attachments-strip'),
      imagePreview: byId('image-preview'),
      imagePreviewImg: byId('image-preview-img'),
      imagePreviewClose: byId('image-preview-close'),
      inspector: byId('inspector'),
      inspectorResizer: byId('inspector-resizer'),
      planTab: byId('plan-tab'),
      planUnread: byId('plan-unread'),
      planPanel: byId('plan-panel'),
      historyTab: byId('history-tab'),
      historyPanel: byId('history-panel'),
      runtimeCard: byId('runtime-card'),
      runtimeTitle: byId('runtime-title'),
      runtimeMessage: byId('runtime-message'),
      runtimeInstall: byId('runtime-install'),
      runtimeCancel: byId('runtime-cancel')
    }
  );
  manager._updateRuntimeStatus({ state: 'ready', message: 'Ready', canInstall: false, canCancel: false });
  return manager;
}

export function modeTransitionEvent(overrides = {}) {
  return {
    type: 'permission_request',
    presentation: 'mode_transition',
    requestId: 'request-1',
    toolCallId: 'tool-1',
    title: 'Ready to code?',
    documentText: '# Plan\n\n1. Make the change\n2. Verify it',
    options: [
      { optionId: 'approve', name: 'Approve once', kind: 'allow_once' },
      { optionId: 'reject', name: 'Keep planning', kind: 'reject_once' }
    ],
    ...overrides
  };
}
