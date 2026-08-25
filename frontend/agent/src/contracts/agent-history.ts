// agent-history.ts — the global Agent History state shape.
//
// History is process-global (the C# backend serves one thread catalog), so it
// lives outside AgentWorkspaceState: a single AgentHistoryStore owns the list,
// the request status machine and the provider catalog, and every workspace
// Inspector renders from it. The AgentHistoryRequestBroker drives loads
// through whichever Agent workspace can carry the `history` bridge command.

/** A saved thread row rendered in the History list. */
export interface AgentHistoryThread {
  threadId: string;
  title: string;
  cwd: string;
  updatedAt: string;
  sessionId: string;
  providerKey: string;
}

/** One entry of the global agent_providers catalog. iconKey selects the
 * brand mark rendered in History rows; unknown or missing keys fall back to
 * the generic 'agent' icon (never a provider-name string check). */
export interface AgentProviderCatalogItem {
  key: string;
  displayName: string;
  assistantName: string;
  isDefault: boolean;
  iconKey: string;
}

/** A failed load_thread action. This is independent from the catalog request
 *  status so the cached thread list remains usable and Retry can reopen the
 *  exact row that failed. `code` is a fixed locale key (history.*); detail
 *  carries optional raw technical text, never a PSX-composed sentence. */
export interface AgentThreadOpenError {
  threadId: string;
  code: string;
  detail: string;
}

/** Request status machine owned by AgentHistoryRequestBroker. */
export type AgentHistoryRequestStatus =
  | 'idle'
  | 'initial-loading'
  | 'refreshing'
  | 'unavailable'
  | 'error';

export interface AgentHistoryState {
  threads: AgentHistoryThread[];
  status: AgentHistoryRequestStatus;
  /** Fixed locale key for the load failure (resolved at render). */
  errorKey: string;
  /** Raw technical detail from the host, rendered as a secondary line. */
  errorDetail: string;
  // A load was desired but could not complete (no channel, carrier closed);
  // the broker retries dirty state when a workspace is created/activated.
  dirty: boolean;
  // Workspace currently carrying the in-flight `history` command ('' = none).
  inFlightWorkspaceId: string;
  // false until the first successful load, so the Inspector can tell
  // "never loaded" ('Open History...') apart from "loaded, empty" ('No saved...').
  loaded: boolean;
  providers: AgentProviderCatalogItem[];
  threadOpenError: AgentThreadOpenError | null;
}

/** What changed in the store. Landing renders are driven purely by state, so
 * the notice only names the slice that changed. */
export interface AgentHistoryNotice {
  kind: 'threads' | 'error' | 'status' | 'providers' | 'thread-open-error';
}

export type AgentHistoryListener = (state: AgentHistoryState, notice: AgentHistoryNotice) => void;

export function createInitialAgentHistoryState(): AgentHistoryState {
  return {
    threads: [],
    status: 'idle',
    errorKey: '',
    errorDetail: '',
    dirty: false,
    inFlightWorkspaceId: '',
    loaded: false,
    providers: [],
    threadOpenError: null
  };
}
