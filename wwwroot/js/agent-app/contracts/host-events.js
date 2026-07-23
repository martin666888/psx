// host-events.ts — the AgentHostEvent discriminated union, split by scope.
//
// Wire `type` values mirror BridgeEventType in wwwroot/js/BridgeMessages.js and
// the C# producers. This module never invents new wire values; it only groups
// the existing ones by the scope that decides how they are dispatched.
const APP_EVENT_TYPES = new Set([
    'settings',
    'appearance_settings'
]);
const LIFECYCLE_EVENT_TYPES = new Set([
    'workspace_activated',
    'agent_workspace_created',
    'agent_workspace_closed'
]);
const AGENT_GLOBAL_EVENT_TYPES = new Set([
    'agent_providers',
    'agent_threads',
    'agent_history_error',
    'agent_history_invalidated',
    'agent_thread_open_error',
    'agent_workspace_limit_reached'
]);
const AGENT_WORKSPACE_EVENT_TYPES = new Set([
    'agent_ready',
    'agent_state',
    'runtime_status',
    'agent_thread_loaded',
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
/** Returns the scope a wire `type` belongs to, or null when it is unknown. */
export function scopeOfType(type) {
    if (APP_EVENT_TYPES.has(type))
        return 'app';
    if (LIFECYCLE_EVENT_TYPES.has(type))
        return 'lifecycle';
    if (AGENT_GLOBAL_EVENT_TYPES.has(type))
        return 'agent-global';
    if (AGENT_WORKSPACE_EVENT_TYPES.has(type))
        return 'agent-workspace';
    return null;
}
