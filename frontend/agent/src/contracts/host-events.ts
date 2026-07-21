// host-events.ts — the AgentHostEvent discriminated union, split by scope.
//
// Wire `type` values mirror BridgeEventType in wwwroot/js/BridgeMessages.js and
// the C# producers. This module never invents new wire values; it only groups
// the existing ones by the scope that decides how they are dispatched.

/** Raw message as received from the C# host before decoding. */
export interface RawHostMessage {
  type?: unknown;
  workspaceId?: unknown;
  [key: string]: unknown;
}

// ---------------------------------------------------------------------------
// Scope 1 — global application events (no workspaceId).
// ---------------------------------------------------------------------------
export type AppHostEventType = 'settings' | 'appearance_settings';

export interface AppHostEvent {
  scope: 'app';
  type: AppHostEventType;
  settings?: Record<string, unknown>;
  raw: RawHostMessage;
}

// ---------------------------------------------------------------------------
// Scope 2 — workspace lifecycle (creates / activates / destroys controllers).
// ---------------------------------------------------------------------------
export type WorkspaceLifecycleEventType =
  | 'workspace_activated'
  | 'agent_workspace_created'
  | 'agent_workspace_closed';

export interface WorkspaceLifecycleEvent {
  scope: 'lifecycle';
  type: WorkspaceLifecycleEventType;
  workspaceId: string;
  kind?: 'terminal' | 'agent';
  raw: RawHostMessage;
}

// ---------------------------------------------------------------------------
// Scope 3 — global Agent events (no workspaceId required).
// ---------------------------------------------------------------------------
export type AgentGlobalEventType =
  | 'agent_providers'
  | 'agent_workspace_limit_reached';

export interface AgentGlobalEvent {
  scope: 'agent-global';
  type: AgentGlobalEventType;
  workspaceId?: string;
  providers?: unknown[];
  text?: string;
  raw: RawHostMessage;
}

// ---------------------------------------------------------------------------
// Scope 4 — workspace content events (require a live controller).
// ---------------------------------------------------------------------------
export type AgentWorkspaceEventType =
  | 'agent_ready'
  | 'agent_state'
  | 'runtime_status'
  | 'agent_thread_loaded'
  | 'agent_threads'
  | 'agent_history_error'
  | 'agent_history_invalidated'
  | 'agent_commands'
  | 'agent_command_rejected'
  | 'agent_modes'
  | 'agent_config_options'
  | 'agent_usage_update'
  | 'agent_mode_current'
  | 'agent_attachment_uploaded'
  | 'agent_attachment_failed'
  | 'agent_cleared'
  | 'command_result'
  | 'user_message'
  | 'run_finished'
  | 'assistant_delta'
  | 'assistant_message_done'
  | 'thinking_started'
  | 'thinking_finished'
  | 'thinking_delta'
  | 'tool_started'
  | 'tool_delta'
  | 'tool_finished'
  | 'permission_request'
  | 'permission_resolved'
  | 'question_request'
  | 'elicitation_request'
  | 'permission_cancelled'
  | 'elicitation_cancelled'
  | 'run_failed'
  | 'resume_failed'
  | 'plan_update'
  | 'raw_terminal_fallback';

export interface AgentWorkspaceEvent {
  scope: 'agent-workspace';
  type: AgentWorkspaceEventType;
  workspaceId: string;
  raw: RawHostMessage;
}

/** Any successfully decoded host event. */
export type AgentHostEvent =
  | AppHostEvent
  | WorkspaceLifecycleEvent
  | AgentGlobalEvent
  | AgentWorkspaceEvent;

const APP_EVENT_TYPES: ReadonlySet<string> = new Set<AppHostEventType>([
  'settings',
  'appearance_settings'
]);

const LIFECYCLE_EVENT_TYPES: ReadonlySet<string> = new Set<WorkspaceLifecycleEventType>([
  'workspace_activated',
  'agent_workspace_created',
  'agent_workspace_closed'
]);

const AGENT_GLOBAL_EVENT_TYPES: ReadonlySet<string> = new Set<AgentGlobalEventType>([
  'agent_providers',
  'agent_workspace_limit_reached'
]);

const AGENT_WORKSPACE_EVENT_TYPES: ReadonlySet<string> = new Set<AgentWorkspaceEventType>([
  'agent_ready',
  'agent_state',
  'runtime_status',
  'agent_thread_loaded',
  'agent_threads',
  'agent_history_error',
  'agent_history_invalidated',
  'agent_commands',
  'agent_command_rejected',
  'agent_modes',
  'agent_config_options',
  'agent_usage_update',
  'agent_mode_current',
  'agent_attachment_uploaded',
  'agent_attachment_failed',
  'agent_cleared',
  'command_result',
  'user_message',
  'run_finished',
  'assistant_delta',
  'assistant_message_done',
  'thinking_started',
  'thinking_finished',
  'thinking_delta',
  'tool_started',
  'tool_delta',
  'tool_finished',
  'permission_request',
  'permission_resolved',
  'question_request',
  'elicitation_request',
  'permission_cancelled',
  'elicitation_cancelled',
  'run_failed',
  'resume_failed',
  'plan_update',
  'raw_terminal_fallback'
]);

export type HostEventScope = AgentHostEvent['scope'];

/** Returns the scope a wire `type` belongs to, or null when it is unknown. */
export function scopeOfType(type: string): HostEventScope | null {
  if (APP_EVENT_TYPES.has(type)) return 'app';
  if (LIFECYCLE_EVENT_TYPES.has(type)) return 'lifecycle';
  if (AGENT_GLOBAL_EVENT_TYPES.has(type)) return 'agent-global';
  if (AGENT_WORKSPACE_EVENT_TYPES.has(type)) return 'agent-workspace';
  return null;
}
