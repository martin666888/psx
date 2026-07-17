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
     * @param {string} text
     * @param {string[]} [attachments]
     */
    sendAgentMessage(text, attachments) {
        this.sendToHost({
            type: BridgeSendType.AgentSubmit,
            text: text,
            attachments: attachments || []
        });
    },

    /**
     * @param {AgentAttachmentUploadPayload} payload
     */
    uploadAgentAttachment(payload) {
        this.sendToHost({
            type: BridgeSendType.AgentUploadAttachment,
            clientId: payload.clientId,
            fileName: payload.fileName,
            mimeType: payload.mimeType,
            size: payload.size,
            dataBase64: payload.dataBase64
        });
    },

    /**
     * @param {string} command
     * @param {string} [value]
     * @param {string} [requestId]
     */
    sendAgentCommand(command, value, requestId) {
        this.sendToHost({
            type: BridgeSendType.AgentCommand,
            command: command,
            value: value || '',
            requestId: requestId || ''
        });
    },

    /**
     * @param {string} requestId
     * @param {string} value
     */
    sendAgentPermissionResponse(requestId, value) {
        this.sendToHost({
            type: BridgeSendType.AgentPermissionResponse,
            requestId: requestId,
            value: value
        });
    },

    /**
     * @param {string} requestId
     * @param {string} value
     */
    sendAgentQuestionResponse(requestId, value) {
        this.sendToHost({
            type: BridgeSendType.AgentQuestionResponse,
            requestId: requestId,
            value: value
        });
    },

    /**
     * @param {string} requestId
     * @param {string} value JSON-stringified elicitation action.
     */
    sendAgentElicitationResponse(requestId, value) {
        this.sendToHost({
            type: BridgeSendType.AgentElicitationResponse,
            requestId: requestId,
            value: value
        });
    }
};
