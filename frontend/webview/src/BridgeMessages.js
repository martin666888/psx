// BridgeMessages.js — canonical bridge message type constants.
//
// These mirror the C# bridge contract (Services/BridgeMessageParsers.cs for
// JS -> C# payloads, and the C# -> JS events sent by the services). Changing
// a value here WITHOUT the matching C# change breaks the bridge; keep both
// sides in sync per AGENTS.md. Only top-level message `type` values belong in
// these tables — presentation/role/status/kind/decisionState literals carry
// their own semantics and must not be folded in.

/**
 * JS -> C# payload types, parsed by TerminalBridgeMessageParser and
 * AgentBridgeMessageParser on the C# side.
 * @readonly
 * @enum {string}
 */
export const BridgeSendType = Object.freeze({
    Input: 'input',
    Resize: 'resize',
    Title: 'title',
    PasteRequest: 'paste_request',
    Ready: 'ready',
    AgentSubmit: 'agent_submit',
    AgentUploadAttachment: 'agent_upload_attachment',
    AgentCommand: 'agent_command',
    AgentPermissionResponse: 'agent_permission_response',
    AgentQuestionResponse: 'agent_question_response',
    AgentElicitationResponse: 'agent_elicitation_response'
});

/**
 * C# -> JS event types, dispatched in main.js and
 * the Agent app registry (AgentWorkspaceRegistry.handle).
 * @readonly
 * @enum {string}
 */
export const BridgeEventType = Object.freeze({
    Settings: 'settings',
    AppearanceSettings: 'appearance_settings',
    Create: 'create',
    Switch: 'switch',
    Output: 'output',
    Resize: 'resize',
    PasteResponse: 'paste_response',
    Close: 'close',
    ViewMode: 'view_mode',
    WorkspaceActivated: 'workspace_activated',
    AgentWorkspaceCreated: 'agent_workspace_created',
    AgentWorkspaceClosed: 'agent_workspace_closed',
    AgentWorkspaceLimitReached: 'agent_workspace_limit_reached',
    AgentProviders: 'agent_providers',
    AgentReady: 'agent_ready',
    AgentState: 'agent_state',
    RuntimeStatus: 'runtime_status',
    AgentThreadLoaded: 'agent_thread_loaded',
    AgentThreads: 'agent_threads',
    AgentHistoryError: 'agent_history_error',
    AgentHistoryInvalidated: 'agent_history_invalidated',
    AgentThreadOpenError: 'agent_thread_open_error',
    AgentCommands: 'agent_commands',
    AgentCommandRejected: 'agent_command_rejected',
    AgentModes: 'agent_modes',
    AgentConfigOptions: 'agent_config_options',
    AgentUsageUpdate: 'agent_usage_update',
    AgentModeCurrent: 'agent_mode_current',
    AgentAttachmentUploaded: 'agent_attachment_uploaded',
    AgentAttachmentFailed: 'agent_attachment_failed',
    CommandResult: 'command_result',
    UserMessage: 'user_message',
    RunFinished: 'run_finished',
    AssistantDelta: 'assistant_delta',
    AssistantMessageDone: 'assistant_message_done',
    ThinkingStarted: 'thinking_started',
    ThinkingFinished: 'thinking_finished',
    ThinkingDelta: 'thinking_delta',
    ToolStarted: 'tool_started',
    ToolDelta: 'tool_delta',
    ToolFinished: 'tool_finished',
    PermissionRequest: 'permission_request',
    PermissionResolved: 'permission_resolved',
    QuestionRequest: 'question_request',
    ElicitationRequest: 'elicitation_request',
    PermissionCancelled: 'permission_cancelled',
    ElicitationCancelled: 'elicitation_cancelled',
    RunFailed: 'run_failed',
    ResumeFailed: 'resume_failed',
    PlanUpdate: 'plan_update',
    RawTerminalFallback: 'raw_terminal_fallback'
});

// The Agent TS modules and DevTools consumers reference these tables through
// the ambient globals declared in frontend/agent/src/contracts; keep the
// global mirror in lockstep with the ESM exports.
globalThis.BridgeSendType = BridgeSendType;
globalThis.BridgeEventType = BridgeEventType;
