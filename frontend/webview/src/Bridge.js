// Bridge.js — WebView2 communication wrapper.
// Message type values come from BridgeMessages.js (BridgeSendType); do not
// reintroduce string literals here — the C# parsers depend on exact values.

import { BridgeSendType } from './BridgeMessages.js';

export const Bridge = {
    /**
     * Posts a JSON message to the C# host.
     * @param {BridgeOutboundMessage} message
     */
    sendToHost(message) {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage(JSON.stringify(message));
        }
    },

    /**
     * Subscribes to C# -> JS events. Malformed JSON messages are dropped.
     * @param {(message: BridgeInboundMessage) => void} callback
     */
    onHostMessage(callback) {
        if (window.chrome?.webview) {
            window.chrome.webview.addEventListener('message', (event) => {
                try {
                    const message = typeof event.data === 'string'
                        ? JSON.parse(event.data)
                        : event.data;
                    callback(message);
                } catch (e) {
                    console.error('Failed to parse host message:', e);
                }
            });
        }
    },

    /**
     * @param {string} sessionId
     * @param {string} base64Data
     */
    sendInput(sessionId, base64Data) {
        this.sendToHost({
            type: BridgeSendType.Input,
            sessionId: sessionId,
            data: base64Data
        });
    },

    /**
     * @param {string} sessionId
     * @param {number} cols
     * @param {number} rows
     */
    sendResize(sessionId, cols, rows) {
        this.sendToHost({
            type: BridgeSendType.Resize,
            sessionId: sessionId,
            cols: cols,
            rows: rows
        });
    },

    /**
     * @param {string} sessionId
     * @param {string} title
     */
    sendTitle(sessionId, title) {
        this.sendToHost({
            type: BridgeSendType.Title,
            sessionId: sessionId,
            title: title
        });
    },

    /**
     * @param {string} sessionId
     * @param {string} requestId
     */
    sendPasteRequest(sessionId, requestId) {
        this.sendToHost({
            type: BridgeSendType.PasteRequest,
            sessionId: sessionId,
            requestId: requestId
        });
    },

    sendReady() {
        this.sendToHost({ type: BridgeSendType.Ready });
    },

    /** Click-to-focus intent for a column: focuses it without changing its
     * active tab. The pane_focus wire name and paneId payload key are
     * preserved by contract; the id space is the column id.
     * @param {string} columnId
     */
    sendPaneFocus(columnId) {
        this.sendToHost({ type: BridgeSendType.PaneFocus, paneId: columnId });
    },

    /** Divider drag end: one atomic vector for the requested layout revision.
     * @param {number} baseRevision
     * @param {{paneId: string, ratio: number}[]} columns
     */
    sendPaneRatios(baseRevision, columns) {
        this.sendToHost({ type: BridgeSendType.PaneRatiosCommit, baseRevision, panes: columns });
    },

    /** Drag a workspace onto a column: it becomes a tab of that column.
     * @param {string} workspaceId
     * @param {string} columnId
     */
    sendPaneMove(workspaceId, columnId) {
        this.sendToHost({ type: BridgeSendType.PaneMove, workspaceId: workspaceId, paneId: columnId });
    },

    /** @param {'activate'|'close'|'split_right'|'collapse_single'} action
     * @param {string} [workspaceId]
     */
    sendWorkspaceLayoutIntent(action, workspaceId) {
        this.sendToHost({
            type: BridgeSendType.WorkspaceLayoutIntent,
            action,
            ...(workspaceId ? { workspaceId } : {})
        });
    },

    /** @param {'terminal'|'agent'|'dsh_web'} kind
     * @param {string} [providerKey]
     * @param {'focused'|'new_right'} [placement]
     */
    sendWorkspaceCreate(kind, providerKey, placement = 'focused') {
        this.sendToHost({
            type: BridgeSendType.WorkspaceCreate,
            kind,
            placement,
            ...(providerKey ? { providerKey } : {})
        });
    },

    /** @param {'stop'|'retry'} name */
    sendKimiWebCommand(name) {
        this.sendToHost({
            type: BridgeSendType.KimiWebCommand,
            name
        });
    },

    /** @param {'install'|'retry'|'stop'|'check_update'|'update'|'cancel_update'} name */
    sendDshCommand(name) {
        this.sendToHost({
            type: BridgeSendType.DshCommand,
            name
        });
    },

    /** Kimi Web session-export mediation: the export URL the kimi frame
     * built (validated host-side against the current ready origin +
     * /api/v1/sessions/{id}/export path) plus the frame-provided path and
     * sessionId. Forwarded from the injected frame script's postMessage —
     * the WebView2 native download path is never used; the URL never carries
     * the token, which the host supplies from memory.
     * @param {string} url
     * @param {string} path
     * @param {string} sessionId
     */
    sendKimiWebExport(url, path, sessionId) {
        this.sendToHost({
            type: BridgeSendType.KimiWebExport,
            url,
            path,
            sessionId
        });
    },

    /** DSH session-log export mediation: the export URL DSH built (validated
     * host-side against the current ready origin + /api/session.export path)
     * and a suggested archive filename. Forwarded when the DSH frame's export
     * anchor click is intercepted by the injected script - the WebView2 native
     * download path is never used.
     * @param {string} url
     * @param {string} filename
     */
    sendDshExport(url, filename) {
        this.sendToHost({
            type: BridgeSendType.DshExport,
            url,
            filename
        });
    },

    /** @param {'preview'|'confirm'|'cancel'|'refresh'|'open_folder'} action
     * @param {string} [themeKey]
     */
    sendThemeAction(action, themeKey) {
        this.sendToHost({
            type: BridgeSendType.ThemeAction,
            action,
            ...(themeKey ? { themeKey } : {})
        });
    },

    /**
     * @param {string} workspaceId
     * @param {string} text
     * @param {string[]} [attachments]
     */
    sendAgentMessage(workspaceId, text, attachments) {
        this.sendToHost({
            type: BridgeSendType.AgentSubmit,
            workspaceId: workspaceId,
            text: text,
            attachments: attachments || []
        });
    },

    /**
     * @param {string} workspaceId
     * @param {AgentAttachmentUploadPayload} payload
     */
    uploadAgentAttachment(workspaceId, payload) {
        this.sendToHost({
            type: BridgeSendType.AgentUploadAttachment,
            workspaceId: workspaceId,
            clientId: payload.clientId,
            fileName: payload.fileName,
            mimeType: payload.mimeType,
            size: payload.size,
            dataBase64: payload.dataBase64
        });
    },

    /**
     * @param {string} workspaceId
     * @param {string} command
     * @param {string|boolean} [value]
     * @param {string} [requestId]
     */
    sendAgentCommand(workspaceId, command, value, requestId) {
        this.sendToHost({
            type: BridgeSendType.AgentCommand,
            workspaceId: workspaceId,
            command: command,
            value: value ?? '',
            requestId: requestId || ''
        });
    },

    /**
     * Sends a process-wide Agent command that does not require a live Agent
     * workspace. Process-global History, profile, usage and config operations
     * use this channel during terminal-only startup and normal Agent use.
     * @param {string} command
     * @param {string|boolean} [value]
     * @param {string} [requestId]
     */
    sendAgentGlobalCommand(command, value, requestId) {
        this.sendToHost({
            type: BridgeSendType.AgentGlobalCommand,
            command: command,
            value: value ?? '',
            requestId: requestId || ''
        });
    },

    /**
     * @param {string} workspaceId
     * @param {string} requestId
     * @param {string} value
     */
    sendAgentPermissionResponse(workspaceId, requestId, value) {
        this.sendToHost({
            type: BridgeSendType.AgentPermissionResponse,
            workspaceId: workspaceId,
            requestId: requestId,
            value: value
        });
    },

    /**
     * @param {string} workspaceId
     * @param {string} requestId
     * @param {string} value
     */
    sendAgentQuestionResponse(workspaceId, requestId, value) {
        this.sendToHost({
            type: BridgeSendType.AgentQuestionResponse,
            workspaceId: workspaceId,
            requestId: requestId,
            value: value
        });
    },

    /**
     * @param {string} workspaceId
     * @param {string} requestId
     * @param {string} value JSON-stringified elicitation action.
     */
    sendAgentElicitationResponse(workspaceId, requestId, value) {
        this.sendToHost({
            type: BridgeSendType.AgentElicitationResponse,
            workspaceId: workspaceId,
            requestId: requestId,
            value: value
        });
    },

    /**
     * @param {string} workspaceId
     */
    createAgentScope(workspaceId) {
        return {
            /** @param {string} text @param {string[]} [attachments] */
            sendAgentMessage: (text, attachments) => this.sendAgentMessage(workspaceId, text, attachments),
            /** @param {AgentAttachmentUploadPayload} payload */
            uploadAgentAttachment: (payload) => this.uploadAgentAttachment(workspaceId, payload),
            /** @param {string} command @param {string|boolean} [value] @param {string} [requestId] */
            sendAgentCommand: (command, value, requestId) => this.sendAgentCommand(workspaceId, command, value, requestId),
            /** @param {string} requestId @param {string} value */
            sendAgentPermissionResponse: (requestId, value) => this.sendAgentPermissionResponse(workspaceId, requestId, value),
            /** @param {string} requestId @param {string} value */
            sendAgentQuestionResponse: (requestId, value) => this.sendAgentQuestionResponse(workspaceId, requestId, value),
            /** @param {string} requestId @param {string} value */
            sendAgentElicitationResponse: (requestId, value) => this.sendAgentElicitationResponse(workspaceId, requestId, value)
        };
    }
};

// WorkspaceHost.ts calls Bridge.createAgentScope through the ambient global
// declared in frontend/agent/src/contracts/globals.d.ts; keep the global
// mirror in lockstep with the ESM export.
globalThis.Bridge = Bridge;
