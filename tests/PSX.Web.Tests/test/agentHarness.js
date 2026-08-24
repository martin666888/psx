import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
export const repositoryRoot = path.resolve(testDirectory, '..', '..', '..');
const nativeFetch = globalThis.fetch.bind(globalThis);
let activeAgentDom = null;
let activeAnimationFrameScheduler = null;
// Teardown callbacks (newest last) for whatever the current test built on top
// of the jsdom window — most importantly the AgentApp, whose dispose() clears
// the History/Usage broker timeout timers and unmounts the React islands.
let activeAgentCleanups = [];

/** Register a teardown callback for the active runtime (e.g. () => app.dispose()). */
export function registerAgentCleanup(cleanup) {
  activeAgentCleanups.push(cleanup);
}

function createAnimationFrameScheduler() {
  let nextId = 1;
  let timestamp = 0;
  const callbacks = new Map();
  return {
    request(callback) {
      const id = nextId++;
      callbacks.set(id, callback);
      return id;
    },
    cancel(id) {
      callbacks.delete(id);
    },
    flush(count = 1) {
      for (let frame = 0; frame < count; frame++) {
        const pending = [...callbacks.values()];
        callbacks.clear();
        timestamp += 1000 / 60;
        for (const callback of pending) callback(timestamp);
      }
    },
    clear() {
      callbacks.clear();
    }
  };
}

/** Advance the active jsdom by an explicit number of animation frames.
 * Self-scheduling callbacks land in the NEXT frame queue, never recurse
 * inline or run forever in the background. */
export function flushAgentAnimationFrames(count = 1) {
  activeAnimationFrameScheduler?.flush(count);
}

/** Dispose the current app(s), then close the jsdom window (which cancels every
 * timer and animation frame bound to it). Run before each fresh install and by
 * the global afterEach so no app instance, broker timer or detached DOM tree
 * survives into the next test/file in the shared vitest fork. */
export function disposeActiveAgentRuntime() {
  for (const cleanup of activeAgentCleanups.splice(0).reverse()) {
    try {
      cleanup();
    } catch {
      // Teardown must never fail a test; the window close below still runs.
    }
  }
  activeAnimationFrameScheduler?.clear();
  activeAnimationFrameScheduler = null;
  activeAgentDom?.window.close();
  activeAgentDom = null;
}

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
  disposeActiveAgentRuntime();
  const dom = new JSDOM('<!doctype html><html><body></body></html>', {
    url: 'https://psx.local/',
    runScripts: 'outside-only',
    // Keep browser visibility semantics. rAF itself is replaced below with a
    // deterministic, explicitly advanced scheduler.
    pretendToBeVisual: true
  });
  activeAgentDom = dom;
  globalThis.window = dom.window;
  globalThis.document = dom.window.document;
  Object.defineProperty(globalThis, 'navigator', {
    configurable: true,
    value: dom.window.navigator
  });
  globalThis.HTMLElement = dom.window.HTMLElement;
  globalThis.Element = dom.window.Element;
  globalThis.HTMLButtonElement = dom.window.HTMLButtonElement;
  const capturedPointers = new WeakMap();
  dom.window.HTMLElement.prototype.setPointerCapture = function (pointerId) {
    capturedPointers.set(this, pointerId);
  };
  dom.window.HTMLElement.prototype.hasPointerCapture = function (pointerId) {
    return capturedPointers.get(this) === pointerId;
  };
  dom.window.HTMLElement.prototype.releasePointerCapture = function () {
    capturedPointers.delete(this);
  };
  dom.window.HTMLElement.prototype.scrollIntoView = function () {};
  dom.window.HTMLElement.prototype.attachEvent = function () {};
  dom.window.HTMLElement.prototype.detachEvent = function () {};
  globalThis.HTMLTextAreaElement = dom.window.HTMLTextAreaElement;
  globalThis.HTMLInputElement = dom.window.HTMLInputElement;
  // Radix Select touches bare globals the harness must mirror like the other
  // DOM constructors: the form-association effect tests `instanceof
  // HTMLFormElement`, and SelectContent allocates a `new DocumentFragment()`
  // portal container even while closed.
  globalThis.HTMLFormElement = dom.window.HTMLFormElement;
  globalThis.DocumentFragment = dom.window.DocumentFragment;
  globalThis.Node = dom.window.Node;
  globalThis.NodeFilter = dom.window.NodeFilter;
  globalThis.Event = dom.window.Event;
  globalThis.KeyboardEvent = dom.window.KeyboardEvent;
  globalThis.MouseEvent = dom.window.MouseEvent;
  globalThis.CustomEvent = dom.window.CustomEvent;
  // SessionRuntimeController watches the panel for the composer-rendered
  // context-usage host (3-0).
  globalThis.MutationObserver = dom.window.MutationObserver;
  globalThis.getComputedStyle = dom.window.getComputedStyle.bind(dom.window);
  // A browser schedules callbacks for a future frame and can cancel them. The
  // harness models those semantics without running Radix measurement loops
  // forever in the background (especially costly under V8 coverage).
  const animationFrames = createAnimationFrameScheduler();
  activeAnimationFrameScheduler = animationFrames;
  globalThis.requestAnimationFrame = (callback) => animationFrames.request(callback);
  globalThis.cancelAnimationFrame = (id) => animationFrames.cancel(id);
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
  globalThis.FormData = dom.window.FormData;
  const createdObjectUrls = [];
  const revokedObjectUrls = [];
  const objectUrlBlobs = new Map();
  dom.window.URL.createObjectURL = (blob) => {
    const url = 'blob:psx/' + (createdObjectUrls.length + 1);
    createdObjectUrls.push(url);
    objectUrlBlobs.set(url, blob);
    return url;
  };
  dom.window.URL.revokeObjectURL = (url) => {
    revokedObjectUrls.push(url);
    objectUrlBlobs.delete(url);
  };
  globalThis.URL = dom.window.URL;
  globalThis.fetch = async (input, init) => {
    const url = typeof input === 'string' ? input : input?.url;
    const blob = objectUrlBlobs.get(url);
    if (blob) {
      return {
        ok: true,
        status: 200,
        blob: async () => blob
      };
    }
    return nativeFetch(input, init);
  };
  dom.window.fetch = globalThis.fetch;

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
  let viewportWidth = wide ? 1440 : 480;
  let matches = viewportWidth >= 520;
  const listeners = new Set();
  const media = '(min-width: 520px)';
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
    matches = viewportWidth >= 520;
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: viewportWidth });
    window.dispatchEvent(new Event('resize'));
    if (previousMatches !== matches) {
      for (const listener of listeners) listener({ matches, media });
    }
  };
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: viewportWidth });
  return {
    setWide(value) {
      setViewportWidth(value ? 1440 : 480);
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
export async function mountAgentApp({ terminal, wide = false, paneLayout = null } = {}) {
  const runtime = installAgentRuntime();
  const breakpoint = installBreakpoint(wide);
  document.body.innerHTML = `<button type="button" data-role="global-history-toggle">History</button><div id="agents"></div><div id="global-portal-root" class="agent-ui"></div>${agentTemplateMarkup()}`;
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
    template: document.getElementById('agent-workspace-template'),
    ...(paneLayout ? { paneLayout } : {})
  });
  document.querySelector('[data-role="global-history-toggle"]')?.addEventListener('click', () => {
    document.dispatchEvent(new CustomEvent('psx-history-toggle'));
  });
  // The global afterEach (vitest.setup.js) disposes the app before closing the
  // jsdom window, so broker timers and islands never leak into the next test.
  registerAgentCleanup(() => app.dispose());
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
      messageCode: 'runtime.ready',
      canInstall: false,
      canCancel: false
    });
  }
  return document.querySelector(`.agent-panel[data-workspace-id="${workspaceId}"]`);
}

// The ComposerView island (3-0) renders the whole composer subtree
// asynchronously after workspace creation. The controller finishes wiring
// (node listeners and initial resize) inside the island's first commit, so the
// React-owned [data-role="mode"] trigger doubles
// as the wiring-complete signal. Poll for it before touching composer nodes.
export async function composerReady(panel) {
  for (let attempt = 0; attempt < 200 && !panel.querySelector('[data-role="mode"]'); attempt++) {
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  if (!panel.querySelector('[data-role="mode"]')) {
    throw new Error('composer island did not mount');
  }
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
