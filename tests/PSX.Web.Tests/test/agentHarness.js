import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = path.resolve(testDirectory, '..', '..', '..');

// Tests exercise the Agent TypeScript sources under frontend/agent/src
// directly; Vitest transforms .ts/.tsx on import. Callers keep addressing
// modules by their compiled names ('core/reducer.js'), and this maps them to
// the .ts or .tsx source. The shipped bundle is produced by Vite into
// wwwroot/app and guarded by verify:web.
export function appModule(relative) {
  const base = path.join(repositoryRoot, 'frontend/agent/src', relative.replace(/\.js$/, ''));
  for (const extension of ['.ts', '.tsx']) {
    if (fs.existsSync(base + extension)) {
      return import(pathToFileURL(base + extension).href);
    }
  }
  throw new Error('agent source module not found for: ' + relative);
}

// The bridge modules are ESM under frontend/webview/src and publish the
// Bridge/BridgeSendType/BridgeEventType globals themselves (the shipped app
// relies on the same globalThis mirror for the Agent ambient declarations).
// They are stateless, so a single import is shared across installs; each
// installAgentRuntime call swaps the jsdom window they read at call time.
async function loadBridgeGlobals() {
  await import(pathToFileURL(path.join(repositoryRoot, 'frontend/webview/src/Bridge.js')).href);
}
await loadBridgeGlobals();

// Install a fresh jsdom document plus the browser globals the Agent modules
// touch. The Bridge globals were published once at harness load; they read the
// current jsdom window at call time. Returns the message-capture handles.
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
  globalThis.Node = dom.window.Node;
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
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: dom.window.localStorage
  });

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

// Shell owns responsive layout. Tests install this controller before
// createAgentApp() so both the 1000px narrow breakpoint and ordinary resize
// events exercise the same lifecycle as WebView.
export function installBreakpoint(wide = false) {
  let viewportWidth = wide ? 1440 : 900;
  let matches = viewportWidth >= 1000;
  const listeners = new Set();
  const media = '(min-width: 1000px)';
  const mql = {
    get matches() {
      return matches;
    },
    media,
    addEventListener(type, listener) {
      if (type === 'change') listeners.add(listener);
    },
    removeEventListener(type, listener) {
      if (type === 'change') listeners.delete(listener);
    }
  };
  window.matchMedia = () => mql;
  const setViewportWidth = (value) => {
    const nextWidth = Math.max(1, Math.round(Number(value) || 0));
    if (viewportWidth === nextWidth) return;
    viewportWidth = nextWidth;
    const previousMatches = matches;
    matches = viewportWidth >= 1000;
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: viewportWidth });
    window.dispatchEvent(new Event('resize'));
    if (previousMatches !== matches) {
      for (const listener of listeners) listener({ matches, media });
    }
  };
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: viewportWidth });
  return {
    setWide(value) {
      setViewportWidth(value ? 1440 : 900);
    },
    setViewportWidth
  };
}

let cachedTemplateMarkup;

// Unified DOM fixture extracted from the real frontend/webview/index.html
// #agent-workspace-template, so tests exercise the same node structure
// (data-role attributes, ARIA, CSS classes) the shipped app uses.
export function agentTemplateMarkup() {
  if (!cachedTemplateMarkup) {
    const index = fs.readFileSync(path.join(repositoryRoot, 'frontend/webview/index.html'), 'utf8');
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
// (Group B — the shipped default on this branch).
export async function mountAgentApp({ terminal, wide = false } = {}) {
  const runtime = installAgentRuntime();
  const breakpoint = installBreakpoint(wide);
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
    breakpoint,
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
