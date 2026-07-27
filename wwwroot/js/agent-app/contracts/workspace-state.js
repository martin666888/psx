// workspace-state.ts — the per-workspace state shape.
//
// Decoded events fold into this semantic state. Timeline presentation remains
// controller-local in TimelineProjection; global History has its own state.
/**
 * Neutral defaults for an unknown provider — no branded Provider fallback.
 * Mirrors the legacy AgentThreadManager constructor defaults (isDraft=true,
 * supportsImage=true, runtime 'missing') so the parallel reducer state matches
 * the legacy engine before the first agent_state/runtime_status arrives.
 */
export function createInitialWorkspaceState(workspaceId) {
    return {
        identity: {
            workspaceId,
            providerKey: '',
            agentName: 'Agent',
            assistantName: 'Agent',
            supportsImage: true
        },
        session: {
            status: '',
            cwd: '',
            sessionId: '',
            currentThreadId: '',
            busy: false,
            isDraft: true,
            isRestoring: false,
            isTranscriptOnly: false,
            readySessionId: '',
            contextUsedTokens: null,
            contextWindowTokens: null,
            contextCostAmount: null,
            contextCostCurrency: ''
        },
        runtime: {
            state: 'missing',
            message: 'Agent runtime is not installed.',
            canInstall: false,
            canCancel: false
        },
        timeline: [],
        composer: {
            agentCommands: [],
            agentCommandsReady: false,
            modes: [],
            currentModeId: '',
            configOptions: []
        },
        inspector: {
            plan: { active: false, runId: '', entries: [], fallbackText: '' }
        },
        decisions: []
    };
}
