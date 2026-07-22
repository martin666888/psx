// agent-history.ts — the global Agent History state shape.
//
// History is process-global (the C# backend serves one thread catalog), so it
// lives outside AgentWorkspaceState: a single AgentHistoryStore owns the list,
// the request status machine and the provider catalog, and every workspace
// Inspector renders from it. The AgentHistoryRequestBroker drives loads
// through whichever Agent workspace can carry the `history` bridge command.
export function createInitialAgentHistoryState() {
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
