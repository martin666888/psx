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

/** One entry of the global agent_providers catalog (display mapping is a
 * later step; this step only stores the raw fields). */
export interface AgentProviderCatalogItem {
  key: string;
  displayName: string;
  assistantName: string;
  isDefault: boolean;
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
  errorText: string;
  // A load was desired but could not complete (no channel, carrier closed);
  // the broker retries dirty state when a workspace is created/activated.
  dirty: boolean;
  // Workspace currently carrying the in-flight `history` command ('' = none).
  inFlightWorkspaceId: string;
  // false until the first successful load, so the Inspector can tell
  // "never loaded" ('Open History...') apart from "loaded, empty" ('No saved...').
  loaded: boolean;
  providers: AgentProviderCatalogItem[];
}

/** What changed in the store. Landing renders are driven purely by state, so
 * the notice only names the slice that changed. */
export interface AgentHistoryNotice {
  kind: 'threads' | 'error' | 'status' | 'providers';
}

export type AgentHistoryListener = (state: AgentHistoryState, notice: AgentHistoryNotice) => void;

export function createInitialAgentHistoryState(): AgentHistoryState {
  return {
    threads: [],
    status: 'idle',
    errorText: '',
    dirty: false,
    inFlightWorkspaceId: '',
    loaded: false,
    providers: []
  };
}
