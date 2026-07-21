import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = path.resolve(testDirectory, '..', '..', '..');

// The Agent app ships as compiled ES modules under wwwroot/js/agent-app. The
// repository path contains '#', so import through an encoded file URL.
export function appModule(relative) {
  const abs = path.join(repositoryRoot, 'wwwroot/js/agent-app', relative);
  return import(pathToFileURL(abs).href);
}

// The surviving classic scripts (BridgeMessages.js + Bridge.js) are not ES
// modules; they declare `const Bridge/BridgeSendType/BridgeEventType` at the top
// level. Concatenate them inside an IIFE and publish the three symbols onto
// globalThis so the compiled agent-app modules (which reference the ambient
// global Bridge, exactly like the shipped app) resolve them.
let bridgeSource;
function readBridgeSource() {
  if (!bridgeSource) {
    const files = ['wwwroot/js/BridgeMessages.js', 'wwwroot/js/Bridge.js'];
    const body = files
      .map((relativePath) => fs.readFileSync(path.join(repositoryRoot, relativePath), 'utf8'))
      .join('\n;\n');
    bridgeSource =
      '(function () {\n' +
      body +
      '\n;globalThis.Bridge = Bridge; globalThis.BridgeSendType = BridgeSendType;' +
      ' globalThis.BridgeEventType = BridgeEventType;\n})();\n';
  }
  return bridgeSource;
}

// Install a fresh jsdom document plus the browser globals the Agent modules
// touch, and the Bridge classic globals. Returns the message-capture handles.
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

  globalThis.FileReader = dom.window.FileReader;
  globalThis.File = dom.window.File;
  globalThis.Blob = dom.window.Blob;
  globalThis.FileList = dom.window.FileList;
  const createdObjectUrls = [];
  const revokedObjectUrls = [];
  dom.window.URL.createObjectURL = () => {
    const url = 'blob:psx/' + (createdObjectUrls.length + 1);
    createdObjectUrls.push(url);
    return url;
  };
  dom.window.URL.revokeObjectURL = (url) => {
    revokedObjectUrls.push(url);
  };
  globalThis.URL = dom.window.URL;

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
  vm.runInThisContext(readBridgeSource(), { filename: 'bridge-bundle.js' });

  return {
    Bridge: globalThis.Bridge,
    BridgeSendType: globalThis.BridgeSendType,
    BridgeEventType: globalThis.BridgeEventType,
    postedMessages,
    createdObjectUrls,
    revokedObjectUrls,
    emitHostMessage(data) {
      hostListeners.forEach((listener) => listener({ data }));
    }
  };
}

let cachedTemplateMarkup;

// Unified DOM fixture extracted from the real wwwroot/index.html
// #agent-workspace-template, so tests exercise the same node structure
// (data-role attributes, ARIA, CSS classes) the shipped app uses.
export function agentTemplateMarkup() {
  if (!cachedTemplateMarkup) {
    const index = fs.readFileSync(path.join(repositoryRoot, 'wwwroot/index.html'), 'utf8');
    const match = index.match(/<template id="agent-workspace-template">[\s\S]*?<\/template>/);
    if (!match) throw new Error('agent-workspace-template not found in index.html');
    cachedTemplateMarkup = match[0];
  }
  return cachedTemplateMarkup;
}

// Mount the compiled Agent app the way production main.js does: a fresh jsdom +
// Bridge globals, the real workspace template, and createAgentApp wired to a
// stub terminal. Returns the runtime capture handles, the app sink, the stub
// terminal, and a panelFor(workspaceId) helper reading the live panel shell.
export async function mountAgentApp({ terminal } = {}) {
  const runtime = installAgentRuntime();
  document.body.innerHTML = `<div id="agents"></div>${agentTemplateMarkup()}`;
  const { createAgentApp } = await appModule('entry.js');
  const terminalStub = terminal || {
    visible: true,
    setViewVisible(value) {
      this.visible = value;
    }
  };
  const app = createAgentApp({
    terminalManager: terminalStub,
    container: document.getElementById('agents'),
    template: document.getElementById('agent-workspace-template')
  });
  return {
    runtime,
    app,
    terminal: terminalStub,
    panelFor: (workspaceId) =>
      document.querySelector(`.agent-panel[data-workspace-id="${workspaceId}"]`)
  };
}

// Create an Agent workspace through the app and mark its runtime ready (default)
// so composer/attachment paths run the same way the shipped app does after
// install. Returns the workspace's live panel element.
export function createAgentWorkspace(app, workspaceId, { ready = true } = {}) {
  app.handle({ type: 'agent_workspace_created', workspaceId });
  if (ready) {
    app.handle({
      type: 'runtime_status',
      workspaceId,
      state: 'ready',
      message: 'Ready',
      canInstall: false,
      canCancel: false
    });
  }
  return document.querySelector(`.agent-panel[data-workspace-id="${workspaceId}"]`);
}

export function modeTransitionEvent(overrides = {}) {
  return {
    type: 'permission_request',
    workspaceId: '11111111-1111-4111-8111-111111111111',
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
