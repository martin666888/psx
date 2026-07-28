// bridge-contract.d.ts — type-level mirror of the C# <-> JS bridge contract.
//
// Scope note: this file currently gates the JS -> C# SEND direction
// (Bridge.js references these types under tsc --checkJs). The C# -> JS event
// types are contract documentation — main.js and AgentThreadManager.js are
// not part of the checked set yet, so inbound payloads are not enforced at
// their consumers. Values must stay in sync with
// Services/BridgeMessageParsers.cs and the C# event producers.

/** WebView2 host object injected by the C# side. */
interface WebView2HostBridge {
    postMessage(message: string): void;
    addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

interface Window {
    chrome?: {
        webview?: WebView2HostBridge;
    };
}

// ---------------------------------------------------------------------------
// JS -> C# payloads (parsed by TerminalBridgeMessageParser /
// AgentBridgeMessageParser)
// ---------------------------------------------------------------------------

interface TerminalInputPayload {
    type: 'input';
    sessionId: string;
    data: string;
}

interface TerminalResizePayload {
    type: 'resize';
    sessionId: string;
    cols: number;
    rows: number;
}

interface TerminalTitlePayload {
    type: 'title';
    sessionId: string;
    title: string;
}

interface TerminalPasteRequestPayload {
    type: 'paste_request';
    sessionId: string;
    requestId: string;
}

interface TerminalReadyPayload {
    type: 'ready';
}

interface AgentSubmitPayload {
    type: 'agent_submit';
    workspaceId: string;
    text: string;
    attachments: string[];
}

/** Input payload accepted by Bridge.uploadAgentAttachment. */
interface AgentAttachmentUploadPayload {
    clientId: string;
    fileName: string;
    mimeType: string;
    size: number;
    dataBase64: string;
}

interface AgentUploadAttachmentPayload extends AgentAttachmentUploadPayload {
    type: 'agent_upload_attachment';
    workspaceId: string;
}

interface AgentCommandPayload {
    type: 'agent_command';
    workspaceId: string;
    command: string;
    value: string | boolean;
    requestId: string;
}

interface AgentPermissionResponsePayload {
    type: 'agent_permission_response';
    workspaceId: string;
    requestId: string;
    value: string;
}

interface AgentQuestionResponsePayload {
    type: 'agent_question_response';
    workspaceId: string;
    requestId: string;
    value: string;
}

interface AgentElicitationResponsePayload {
    type: 'agent_elicitation_response';
    workspaceId: string;
    requestId: string;
    value: string;
}

/** Every payload Bridge.sendToHost may carry. */
type BridgeOutboundMessage =
    | TerminalInputPayload
    | TerminalResizePayload
    | TerminalTitlePayload
    | TerminalPasteRequestPayload
    | TerminalReadyPayload
    | AgentSubmitPayload
    | AgentUploadAttachmentPayload
    | AgentCommandPayload
    | AgentPermissionResponsePayload
    | AgentQuestionResponsePayload
    | AgentElicitationResponsePayload;

// ---------------------------------------------------------------------------
// C# -> JS events (documentation until main.js / AgentThreadManager.js join
// the checked set)
// ---------------------------------------------------------------------------

/** Base shape shared by all host events. */
interface BridgeInboundMessageBase {
    type: string;
}

interface AgentWorkspaceEventBase extends BridgeInboundMessageBase {
    workspaceId: string;
}

/** Terminal- and settings-scoped events, consumed by main.js / TerminalManager. */
interface BridgeTerminalEvent extends BridgeInboundMessageBase {
    type:
        | 'settings'
        | 'appearance_settings'
        | 'create'
        | 'switch'
        | 'output'
        | 'resize'
        | 'paste_response'
        | 'close'
        | 'view_mode';
    sessionId?: string;
    data?: string;
    cols?: number;
    rows?: number;
    requestId?: string;
    ok?: boolean;
    text?: string;
    mode?: string;
    settings?: Record<string, unknown>;
}

/** ACP decision option offered by the Agent. */
interface AgentDecisionOptionPayload {
    optionId: string;
    name: string;
    kind?: string;
}

/** permission_request — ordinary permission prompt. */
interface AgentPermissionRequestEvent extends AgentWorkspaceEventBase {
    type: 'permission_request';
    requestId: string;
    title?: string;
    text?: string;
    toolKind?: string;
    options: AgentDecisionOptionPayload[];
    presentation?: null;
}

/**
 * permission_request with presentation 'mode_transition' — the Plan -> Code
 * proposal. Mode transition is a presentation variant of permission_request,
 * not a top-level event type.
 */
interface AgentModeTransitionRequestEvent extends AgentWorkspaceEventBase {
    type: 'permission_request';
    presentation: 'mode_transition';
    requestId: string;
    toolCallId: string;
    title?: string;
    documentText: string;
    options: AgentDecisionOptionPayload[];
}

/** Agent events other than the permission_request variants modeled above.
 * Kept free of 'permission_request' so narrowing on `type` can never fall
 * back to a wide branch without requestId/presentation. (Terminal, settings
 * and paste_response host events are NOT part of the AgentEvent union by
 * design.) */
interface AgentGenericEvent extends AgentWorkspaceEventBase {
    type:
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
        | 'permission_resolved'
        | 'question_request'
        | 'elicitation_request'
        | 'permission_cancelled'
        | 'elicitation_cancelled'
        | 'run_failed'
        | 'resume_failed'
        | 'plan_update'
        | 'raw_terminal_fallback';
    title?: string;
    cwd?: string;
    status?: string;
    busy?: boolean;
    isDraft?: boolean;
}

interface WorkspaceHostEvent extends BridgeInboundMessageBase {
    type:
        | 'workspace_activated'
        | 'agent_workspace_created'
        | 'agent_workspace_closed'
        | 'agent_workspace_limit_reached'
        | 'agent_providers';
    workspaceId?: string;
    kind?: 'terminal' | 'agent';
    text?: string;
}

/** Agent-global events that deliberately do not belong to a workspace. */
interface AgentGlobalHostEvent extends BridgeInboundMessageBase {
    type: 'agent_thread_open_error';
    threadId: string;
    text?: string;
    detail?: string;
}

/** Every event AgentThreadManager.handleEvent may receive. Narrowing on
 * `type === 'permission_request'` yields exactly the two concrete variants
 * above; narrowing on `presentation` then separates them. */
type AgentEvent =
    | AgentGenericEvent
    | AgentPermissionRequestEvent
    | AgentModeTransitionRequestEvent;

/** Every event the C# host may post to the frontend. */
type BridgeInboundMessage =
    | BridgeTerminalEvent
    | WorkspaceHostEvent
    | AgentGlobalHostEvent
    | AgentEvent;
