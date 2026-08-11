import type { CodeHighlighterPlugin, HighlightResult } from '@streamdown/code';

interface HighlightResponse {
  id: number;
  ok: boolean;
  result?: HighlightResult;
}

const aliases = new Map<string, string>([
  ['bash', 'shellscript'], ['sh', 'shellscript'], ['shell', 'shellscript'],
  ['js', 'javascript'], ['mjs', 'javascript'], ['cjs', 'javascript'],
  ['ts', 'typescript'], ['py', 'python'], ['cs', 'csharp'],
  ['ps1', 'powershell'], ['pwsh', 'powershell'], ['yml', 'yaml'],
  ['md', 'markdown'], ['htm', 'html'], ['dockerfile', 'docker']
]);
const supported = new Set([
  'c', 'cpp', 'csharp', 'css', 'diff', 'docker', 'go', 'html', 'java',
  'javascript', 'json', 'jsx', 'markdown', 'powershell', 'python', 'rust',
  'shellscript', 'sql', 'tsx', 'typescript', 'xml', 'yaml'
]);
const themes = ['github-light', 'github-dark'] as const;
const resultCache = new Map<string, HighlightResult>();
const callbacksByKey = new Map<string, Set<(result: HighlightResult) => void>>();
const requestKeys = new Map<number, string>();
const MAX_CACHE_ENTRIES = 128;
const WORKER_IDLE_MS = 30_000;

let worker: Worker | null = null;
let nextRequestId = 1;
let idleTimer = 0;

function normalizeLanguage(language: string): string {
  const normalized = language.trim().toLowerCase();
  return aliases.get(normalized) ?? normalized;
}

function cacheKey(code: string, language: string): string {
  return `${language}\0${code}`;
}

function cacheResult(key: string, result: HighlightResult): void {
  resultCache.delete(key);
  resultCache.set(key, result);
  while (resultCache.size > MAX_CACHE_ENTRIES) {
    const oldest = resultCache.keys().next().value;
    if (oldest === undefined) break;
    resultCache.delete(oldest);
  }
}

function stopWorker(): void {
  if (idleTimer) window.clearTimeout(idleTimer);
  idleTimer = 0;
  worker?.terminate();
  worker = null;
  requestKeys.clear();
  callbacksByKey.clear();
}

function scheduleWorkerStop(): void {
  if (requestKeys.size > 0 || typeof window === 'undefined') return;
  if (idleTimer) window.clearTimeout(idleTimer);
  idleTimer = window.setTimeout(stopWorker, WORKER_IDLE_MS);
}

function getWorker(): Worker | null {
  if (
    import.meta.env.MODE === 'test'
    || typeof Worker === 'undefined'
    || typeof window === 'undefined'
    || window !== globalThis
  ) return null;
  if (worker) return worker;
  worker = new Worker('/app/assets/shiki-worker.js', {
    type: 'module',
    name: 'psx-markdown-shiki'
  });
  worker.addEventListener('message', (event: MessageEvent<HighlightResponse>) => {
    const response = event.data;
    const key = requestKeys.get(response.id);
    if (!key) return;
    requestKeys.delete(response.id);
    const callbacks = callbacksByKey.get(key);
    callbacksByKey.delete(key);
    if (response.ok && response.result) {
      cacheResult(key, response.result);
      callbacks?.forEach((notify) => notify(response.result as HighlightResult));
    }
    scheduleWorkerStop();
  });
  worker.addEventListener('error', stopWorker);
  return worker;
}

/** Streamdown adapter backed by a local, idle-terminated Shiki worker. */
export const codePlugin: CodeHighlighterPlugin = {
  name: 'shiki',
  type: 'code-highlighter',
  supportsLanguage(language) {
    return supported.has(normalizeLanguage(language));
  },
  getSupportedLanguages() {
    return Array.from(supported) as ReturnType<CodeHighlighterPlugin['getSupportedLanguages']>;
  },
  getThemes() {
    return [...themes];
  },
  highlight({ code, language }, callback) {
    const normalized = normalizeLanguage(language);
    if (!supported.has(normalized)) return null;
    const key = cacheKey(code, normalized);
    const cached = resultCache.get(key);
    if (cached) return cached;
    if (callback) {
      const callbacks = callbacksByKey.get(key) ?? new Set();
      callbacks.add(callback);
      callbacksByKey.set(key, callbacks);
    }
    if ([...requestKeys.values()].includes(key)) return null;
    const target = getWorker();
    if (!target) return null;
    if (idleTimer) window.clearTimeout(idleTimer);
    const id = nextRequestId++;
    requestKeys.set(id, key);
    target.postMessage({ id, code, language: normalized });
    return null;
  }
};
