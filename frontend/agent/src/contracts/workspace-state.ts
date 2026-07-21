// workspace-state.ts — the per-workspace state shape.
//
// Phase 3 grows the identity/session/runtime slices so the reducer can fold
// decoded events into a semantic state that runs in parallel with the legacy
// engine (tests assert equivalence). The timeline/composer/inspector/decisions
// slices stay opaque here and are filled in Phase 4 when controllers take over
// rendering. The LegacyAgentAdapter still owns all DOM during Phase 3.

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

/** Plan panel state folded from plan_update / agent_thread_loaded / agent_cleared. */
export interface WorkspacePlanState {
  // false renders the "No active plan" empty state; true renders entries/fallback.
  active: boolean;
  runId: string;
  entries: InspectorPlanEntry[];
  fallbackText: string;
}

/** A saved thread row rendered in the History tab. */
export interface WorkspaceHistoryThread {
  threadId: string;
  title: string;
  cwd: string;
  updatedAt: string;
  sessionId: string;
}

/** History data folded from agent_threads / agent_history_error. The transient
 * loading/empty-initial display states are controller-local UI, not host state. */
export interface WorkspaceHistoryState {
  threads: WorkspaceHistoryThread[];
  errorText: string;
}

export interface WorkspaceInspectorState {
  plan: WorkspacePlanState;
  history: WorkspaceHistoryState;
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

/** A provider-driven config <select> rendered next to the composer. */
export interface ComposerConfigOption {
  id: string;
  name: string;
  description: string;
  currentValue: string;
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
      contextUsedTokens: null
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
      plan: { active: false, runId: '', entries: [], fallbackText: '' },
      history: { threads: [], errorText: '' }
    },
    decisions: []
  };
}
