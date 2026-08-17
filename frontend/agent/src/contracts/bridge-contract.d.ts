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

interface PaneFocusPayload {
    type: 'pane_focus';
    paneId: string;
}

interface PaneRatiosCommitPayload {
    type: 'pane_ratios_commit';
    baseRevision: number;
    panes: Array<{ paneId: string; ratio: number }>;
}

interface PaneMovePayload {
    type: 'pane_move';
    workspaceId: string;
    paneId: string;
}

interface WorkspaceLayoutIntentPayload {
    type: 'workspace_layout_intent';
    action: 'activate' | 'close' | 'split_right' | 'collapse_single';
    workspaceId?: string;
    paneId?: string;
}

interface WorkspaceCreatePayload {
    type: 'workspace_create';
    kind: 'terminal' | 'agent' | 'dsh_web' | 'kimi_web';
    providerKey?: string;
    placement: 'focused' | 'new_right';
}

interface DshCommandPayload {
    type: 'dsh_command';
    name: 'install' | 'retry' | 'stop' | 'check_update' | 'update' | 'cancel_update';
}

interface KimiWebCommandPayload {
    type: 'kimi_web_command';
    name: 'stop' | 'retry';
}

interface DshExportPayload {
    type: 'dsh_export';
    url: string;
    filename: string;
}

interface KimiWebExportPayload {
    type: 'kimi_web_export';
    url: string;
    path: string;
    sessionId: string;
}

interface ThemeActionPayload {
    type: 'theme_action';
    action: 'preview' | 'confirm' | 'cancel' | 'refresh' | 'open_folder';
    themeKey?: string;
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

interface AgentGlobalCommandPayload {
    type: 'agent_global_command';
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
    | PaneFocusPayload
    | PaneRatiosCommitPayload
    | PaneMovePayload
    | WorkspaceLayoutIntentPayload
    | WorkspaceCreatePayload
    | DshCommandPayload
    | KimiWebCommandPayload
    | DshExportPayload
    | KimiWebExportPayload
    | ThemeActionPayload
    | AgentSubmitPayload
    | AgentUploadAttachmentPayload
    | AgentCommandPayload
    | AgentGlobalCommandPayload
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
    /** Explicit tool input only; never a whole toolCall JSON fallback. */
    text?: string;
    /** Single-line ordinary explanation from content text. */
    description?: string;
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

/**
 * permission_request with presentation 'form' — ask-user questions lifted from
 * nested rawInput.questions into an elicitation-shaped schema while staying on
 * the permission response channel (agent_permission_response).
 */
interface AgentPermissionFormRequestEvent extends AgentWorkspaceEventBase {
    type: 'permission_request';
    presentation: 'form';
    requestId: string;
    title?: string;
    message?: string;
    /** JSON Schema object rendered by the shared elicitation form body. */
    schema: Record<string, unknown>;
    /** Offered optionId sent with answers on Continue (e.g. proceed_once). */
    formSubmitOptionId: string;
    options: AgentDecisionOptionPayload[];
}

/**
 * permission_request with presentation 'document' — a structured document
 * supplied by an ACP Agent without claiming the switch_mode protocol
 * semantics. It remains an ordinary ACP permission response.
 */
interface AgentDocumentPermissionRequestEvent extends AgentWorkspaceEventBase {
    type: 'permission_request';
    presentation: 'document';
    requestId: string;
    toolCallId: string;
    title?: string;
    /** Explicit tool input, when the Agent supplied one; never a toolCall JSON fallback. */
    text?: string;
    documentText: string;
    options: AgentDecisionOptionPayload[];
}

/** tool_updated — full field-replace snapshot for a live tool card. */
interface AgentToolUpdatedEvent extends AgentWorkspaceEventBase {
    type: 'tool_updated';
    runId: string;
    toolCallId: string;
    name: string;
    summary: string;
    /** Empty string clears the card input. */
    input: string;
    /** Empty string clears the card output. */
    output: string;
    status: string;
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
        | 'runtime_update_status'
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
    requestId?: string;
}

interface WorkspaceHostEvent extends BridgeInboundMessageBase {
    type:
        | 'workspace_activated'
        | 'agent_workspace_created'
        | 'agent_workspace_closed'
        | 'agent_workspace_limit_reached'
        | 'agent_providers';
    workspaceId?: string;
    kind?: 'terminal' | 'agent' | 'dsh_web' | 'kimi_web';
    text?: string;
}

/** kimi_web_runtime_status — process-wide Kimi Web runtime state. The
 * token-bearing readyUrl exists only while Ready (and only in this field and
 * the iframe src); the errorClass/reason values are fixed enum keys the
 * frontend maps to copy — never paths, messages or stdout/stderr. */
interface KimiWebRuntimeStatusEvent extends BridgeInboundMessageBase {
    type: 'kimi_web_runtime_status';
    state: 'unavailable' | 'stopped' | 'starting' | 'ready' | 'stopping' | 'failed' | 'exited';
    /** Only present in the Ready state; carries the #token= fragment. */
    readyUrl?: string | null;
    errorClass?: 'launch_failed' | 'start_timeout' | 'health_check_failed' | null;
    reason?: 'portable_node_missing' | 'runtime_missing' | 'runtime_invalid' | null;
}

interface AgentThreadOpenErrorEvent extends BridgeInboundMessageBase {
    type: 'agent_thread_open_error';
    threadId: string;
    text?: string;
    detail?: string;
}

interface AgentProfileEvent extends BridgeInboundMessageBase {
    type: 'agent_profile';
    requestId?: string;
    displayName: string;
    avatarDataUrl: string | null;
    revision: number;
    error?: string | null;
}

interface AgentUsageWindowEvent {
    totalTokens: number;
}

type AgentUsageGapReason =
    | 'unsupported_source'
    | 'unsupported_format'
    | 'missing_session_logs'
    | 'ambiguous_session_logs'
    | 'unreadable_logs'
    | 'unmatched_sessions'
    | 'missing_session_id'
    | 'damaged_thread_files'
    | 'unregistered_provider';

interface AgentUsageCompletenessEvent {
    status: 'available' | 'partial' | 'unavailable';
    reasons: AgentUsageGapReason[];
    expectedSessions: number | null;
    matchedSessions: number | null;
    skippedFiles: number;
    badLines: number;
    untrackedThreads: number;
}

interface AgentProviderUsageReportEvent {
    providerKey: string;
    displayName: string;
    iconKey: string;
    dailyTokens: number[];
    today: AgentUsageWindowEvent;
    last7Days: AgentUsageWindowEvent;
    last30Days: AgentUsageWindowEvent;
    completeness: AgentUsageCompletenessEvent;
}

interface AgentUsageReportEvent extends BridgeInboundMessageBase {
    type: 'agent_usage_report';
    requestId: string;
    generatedAt: string;
    timezone: string;
    report: {
        heatmapStartDate: string;
        dailyTokens: number[];
        today: AgentUsageWindowEvent;
        last7Days: AgentUsageWindowEvent;
        last30Days: AgentUsageWindowEvent;
        providers: AgentProviderUsageReportEvent[];
    } | null;
    completeness: AgentUsageCompletenessEvent | null;
    error: string | null;
}

interface AgentConfigFactEvent {
    label: string;
    value: string;
}

interface AgentConfigModelEvent {
    id: string;
    name: string | null;
    baseUrl: string | null;
}

interface AgentConfigMcpServerEvent {
    name: string;
    transport: 'stdio' | 'http' | 'sse' | string;
    target: string;
    enabled: boolean;
    envKeys: string[];
    headerKeys: string[];
}

interface AgentConfigSkillEvent {
    name: string;
}

interface AgentProviderConfigReportEvent {
    providerKey: string;
    displayName: string;
    iconKey: string;
    state: 'available' | 'partial' | 'unavailable' | string;
    facts: AgentConfigFactEvent[];
    models: AgentConfigModelEvent[];
    mcpServers: AgentConfigMcpServerEvent[];
    skills: AgentConfigSkillEvent[];
    notes: string[];
}

/** Disk-backed user config for the Usage panel「配置」tab — distinct from
 * live ACP agent_config_options (session Composer). */
interface AgentConfigReportEvent extends BridgeInboundMessageBase {
    type: 'agent_config_report';
    requestId: string;
    generatedAt: string;
    report: {
        providers: AgentProviderConfigReportEvent[];
    } | null;
    error: string | null;
}

/** Agent-global events that deliberately do not belong to a workspace. */
type AgentGlobalHostEvent =
    | AgentThreadOpenErrorEvent
    | AgentProfileEvent
    | AgentUsageReportEvent
    | AgentConfigReportEvent;

/** Every event AgentThreadManager.handleEvent may receive. Narrowing on
 * `type === 'permission_request'` yields the concrete presentation variants
 * above; narrowing on `presentation` then separates them. */
type AgentEvent =
    | AgentGenericEvent
    | AgentPermissionRequestEvent
    | AgentModeTransitionRequestEvent
    | AgentPermissionFormRequestEvent
    | AgentDocumentPermissionRequestEvent
    | AgentToolUpdatedEvent;

/** Every event the C# host may post to the frontend. */
type BridgeInboundMessage =
    | BridgeTerminalEvent
    | WorkspaceHostEvent
    | KimiWebRuntimeStatusEvent
    | AgentGlobalHostEvent
    | AgentEvent;
