// AgentHistoryStore.ts — the single process-global Agent History state owner.
//
// Holds the normalized thread list, the request status machine fields and the
// provider catalog. Pure state + notify: the AgentHistoryRequestBroker writes
// through the apply* methods, and the global HistoryDockController subscribes
// to render from the same snapshot. Payload normalization moved here from the
// per-workspace reducer when History went global.

import type {
  AgentHistoryListener,
  AgentHistoryNotice,
  AgentHistoryState,
  AgentHistoryThread,
  AgentProviderCatalogItem
} from '../contracts/agent-history.js';
import { createInitialAgentHistoryState } from '../contracts/agent-history.js';

/** Fixed locale keys — never stored display sentences. */
export const HISTORY_ERROR_KEYS = {
  loadFailed: 'history.loadFailed',
  openFailed: 'history.openFailed'
} as const;

/** Backend wire codes → agent-locale keys. */
const WIRE_CODE_MAP: Record<string, string> = {
  'history.load_failed': HISTORY_ERROR_KEYS.loadFailed,
  'history.open_failed': HISTORY_ERROR_KEYS.openFailed
};

function toLocaleKey(code: string, fallback: string): string {
  return WIRE_CODE_MAP[code] ?? (code || fallback);
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function nonEmptyTrimmed(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed ? trimmed : null;
}

/** Normalizes the agent_threads payload into the rows the History list renders. */
export function normalizeHistoryThreads(value: unknown): AgentHistoryThread[] {
  const list = Array.isArray(value) ? value : [];
  return list.map((raw) => {
    const t = (raw ?? {}) as Record<string, unknown>;
    return {
      threadId: asString(t.threadId),
      title: asString(t.title),
      cwd: asString(t.cwd),
      updatedAt: asString(t.updatedAt),
      sessionId: asString(t.sessionId),
      // Missing, non-string or blank provider stays 'unknown'; display
      // mapping is a later step, so no branded default is guessed here.
      providerKey: nonEmptyTrimmed(t.provider) ?? 'unknown'
    };
  });
}

/** Normalizes the agent_providers payload into the global catalog. */
export function normalizeProviderCatalog(value: unknown): AgentProviderCatalogItem[] {
  const list = Array.isArray(value) ? value : [];
  return list
    .map((raw) => (raw ?? {}) as Record<string, unknown>)
    .filter((item) => nonEmptyTrimmed(item.key) !== null)
    .map((item) => ({
      key: nonEmptyTrimmed(item.key) ?? '',
      displayName: asString(item.displayName),
      assistantName: asString(item.assistantName),
      isDefault: item.isDefault === true,
      // Missing or blank icon keys normalize to the generic 'agent' mark so
      // rows always have a stable icon slot.
      iconKey: nonEmptyTrimmed(item.iconKey) ?? 'agent'
    }));
}

export class AgentHistoryStore {
  private state: AgentHistoryState = createInitialAgentHistoryState();
  private readonly listeners = new Set<AgentHistoryListener>();

  getState(): AgentHistoryState {
    return this.state;
  }

  subscribe(listener: AgentHistoryListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /** A load landed: replace the list, clear error/dirty/in-flight. */
  applyThreads(threads: AgentHistoryThread[]): void {
    this.set(
      {
        ...this.state,
        threads,
        status: 'idle',
        errorKey: '',
        errorDetail: '',
        dirty: false,
        inFlightWorkspaceId: '',
        loaded: true
      },
      { kind: 'threads' }
    );
  }

  /** A load failed with a host error: keep the cached list, show the error.
   *  `key` is a fixed locale key (or backend wire code); `detail` is optional
   *  raw technical text. */
  applyError(key: string, detail = ''): void {
    this.set(
      {
        ...this.state,
        status: 'error',
        errorKey: toLocaleKey(key, HISTORY_ERROR_KEYS.loadFailed),
        errorDetail: detail,
        dirty: false,
        inFlightWorkspaceId: ''
      },
      { kind: 'error' }
    );
  }

  /** A request wave started on a channel: initial loads show the loading
   * state, refreshes keep the current list (lightweight 'refreshing'). */
  applyLoading(status: 'initial-loading' | 'refreshing', inFlightWorkspaceId: string): void {
    this.set(
      { ...this.state, status, inFlightWorkspaceId },
      { kind: 'status' }
    );
  }

  /** No workspace can carry the request: keep the cached list, stay dirty so
   * the next workspace creation/activation retries automatically. */
  applyUnavailable(): void {
    this.set(
      { ...this.state, status: 'unavailable', dirty: true, inFlightWorkspaceId: '' },
      { kind: 'status' }
    );
  }

  /** The in-flight carrier closed or the retry budget ran out: end the
   * loading state but stay dirty for the next create/activate trigger. */
  applyIdleDirty(): void {
    this.set(
      { ...this.state, status: 'idle', dirty: true, inFlightWorkspaceId: '' },
      { kind: 'status' }
    );
  }

  applyProviders(providers: AgentProviderCatalogItem[]): void {
    this.set(
      { ...this.state, providers },
      { kind: 'providers' }
    );
  }

  /** A row action failed: keep the catalog and its request state intact. */
  applyThreadOpenError(threadId: string, code = '', detail = ''): void {
    if (!threadId) return;
    this.set(
      {
        ...this.state,
        threadOpenError: {
          threadId,
          code: toLocaleKey(code, HISTORY_ERROR_KEYS.openFailed),
          detail
        }
      },
      { kind: 'thread-open-error' }
    );
  }

  clearThreadOpenError(): void {
    if (!this.state.threadOpenError) return;
    this.set(
      { ...this.state, threadOpenError: null },
      { kind: 'thread-open-error' }
    );
  }

  private set(next: AgentHistoryState, notice: AgentHistoryNotice): void {
    this.state = next;
    for (const listener of this.listeners) listener(next, notice);
  }
}
