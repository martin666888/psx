// Bridge.js — WebView2 communication wrapper.
// Message type values come from BridgeMessages.js (BridgeSendType); do not
// reintroduce string literals here — the C# parsers depend on exact values.

const Bridge = {
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
