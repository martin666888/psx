// workspace-state.ts — the per-workspace state shape.
//
// Decoded events fold into this semantic state. Timeline presentation remains
// controller-local in TimelineProjection; global History has its own state.

export interface WorkspaceIdentity {
  workspaceId: string;
  providerKey: string;
  agentName: string;
  assistantName: string;
  supportsImage: boolean;
}

export interface WorkspaceSessionState {
  status: string;
  cwd: string;
  sessionId: string;
  currentThreadId: string;
  busy: boolean;
  isDraft: boolean;
  isRestoring: boolean;
  isTranscriptOnly: boolean;
  readySessionId: string;
  contextUsedTokens: number | null;
  contextWindowTokens: number | null;
  contextCostAmount: number | null;
  contextCostCurrency: string;
}

export interface WorkspaceRuntimeState {
  state: string;
  message: string;
  canInstall: boolean;
  canCancel: boolean;
}

/** A single normalized Plan entry (mirrors core/plan.ts PlanEntry). */
export interface InspectorPlanEntry {
  content: string;
  status: string;
  priority: string;
}

/** Plan panel state folded from plan_update / agent_thread_loaded. */
export interface WorkspacePlanState {
  // false renders the "No active plan" empty state; true renders entries/fallback.
  active: boolean;
  runId: string;
  entries: InspectorPlanEntry[];
  fallbackText: string;
}

/** A saved thread row rendered in the History list lives in the global
 * AgentHistoryState (contracts/agent-history.ts), not per workspace. */

/** Plan data folded from plan_update / agent_thread_loaded.
 * The transient loading/empty-initial display states are controller-local UI,
 * not host state. */
export interface WorkspaceInspectorState {
  plan: WorkspacePlanState;
}

/** A normalized slash-command row shown in the composer command menu. */
export interface ComposerCommand {
  source: string;
  name: string;
  label: string;
  agent: boolean;
}

/** A selectable session mode (fallback mode <select>). */
export interface ComposerMode {
  id: string;
  name: string;
  description: string;
}

/** One option of a provider config <select> (mode/model/effort/...). */
export interface ComposerConfigOptionItem {
  value: string;
  name: string;
  description: string;
}

/** A provider-driven config control rendered next to the composer. */
export interface ComposerConfigOption {
  id: string;
  name: string;
  description: string;
  type: 'select' | 'boolean';
  currentValue: string | boolean;
  options: ComposerConfigOptionItem[];
}

/** Composer slice folded from agent_commands / agent_modes /
 * agent_config_options / agent_mode_current. The pending-attachment upload
 * state stays controller-local (imperative File I/O), matching the legacy
 * AgentThreadManager instance state. */
export interface WorkspaceComposerState {
  agentCommands: ComposerCommand[];
  agentCommandsReady: boolean;
  modes: ComposerMode[];
  currentModeId: string;
  configOptions: ComposerConfigOption[];
}

export interface AgentWorkspaceState {
  identity: WorkspaceIdentity;
  session: WorkspaceSessionState;
  runtime: WorkspaceRuntimeState;
  timeline: unknown[];
  composer: WorkspaceComposerState;
  inspector: WorkspaceInspectorState;
  decisions: unknown[];
}

/**
 * Neutral defaults for an unknown provider — no branded Provider fallback.
 * Mirrors the legacy AgentThreadManager constructor defaults (isDraft=true,
 * supportsImage=true, runtime 'missing') so the parallel reducer state matches
 * the legacy engine before the first agent_state/runtime_status arrives.
 */
export function createInitialWorkspaceState(workspaceId: string): AgentWorkspaceState {
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
