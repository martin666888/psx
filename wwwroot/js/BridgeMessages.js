// BridgeMessages.js — canonical bridge message type constants.
//
// These mirror the C# bridge contract (Services/BridgeMessageParsers.cs for
// JS -> C# payloads, and the C# -> JS events sent by the services). Changing
// a value here WITHOUT the matching C# change breaks the bridge; keep both
// sides in sync per AGENTS.md. Only top-level message `type` values belong in
// these tables — presentation/role/status/kind/decisionState literals carry
// their own semantics and must not be folded in.
//
// Load order: must load before Bridge.js.

/**
 * JS -> C# payload types, parsed by TerminalBridgeMessageParser and
 * AgentBridgeMessageParser on the C# side.
 * @readonly
 * @enum {string}
 */
const BridgeSendType = Object.freeze({
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
 * AgentThreadManager.handleEvent.
 * @readonly
 * @enum {string}
 */
const BridgeEventType = Object.freeze({
    Settings: 'settings',
    AppearanceSettings: 'appearance_settings',
    Create: 'create',
    Switch: 'switch',
    Output: 'output',
    Resize: 'resize',
    PasteResponse: 'paste_response',
    Close: 'close',
    ViewMode: 'view_mode',
    AgentReady: 'agent_ready',
    AgentState: 'agent_state',
    RuntimeStatus: 'runtime_status',
    AgentThreadLoaded: 'agent_thread_loaded',
    AgentThreads: 'agent_threads',
    AgentHistoryError: 'agent_history_error',
    AgentCommands: 'agent_commands',
    AgentCommandRejected: 'agent_command_rejected',
    AgentModes: 'agent_modes',
    AgentConfigOptions: 'agent_config_options',
    AgentUsageUpdate: 'agent_usage_update',
    AgentModeCurrent: 'agent_mode_current',
    AgentAttachmentUploaded: 'agent_attachment_uploaded',
    AgentAttachmentFailed: 'agent_attachment_failed',
    AgentCleared: 'agent_cleared',
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
