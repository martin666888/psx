// Bridge.js — WebView2 communication wrapper
const Bridge = {
    sendToHost(message) {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage(JSON.stringify(message));
        }
    },

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

    sendInput(sessionId, base64Data) {
        this.sendToHost({
            type: 'input',
            sessionId: sessionId,
            data: base64Data
        });
    },

    sendResize(sessionId, cols, rows) {
        this.sendToHost({
            type: 'resize',
            sessionId: sessionId,
            cols: cols,
            rows: rows
        });
    },

    sendTitle(sessionId, title) {
        this.sendToHost({
            type: 'title',
            sessionId: sessionId,
            title: title
        });
    },

    sendReady() {
        this.sendToHost({ type: 'ready' });
    },

    sendAgentMessage(text, attachments) {
        this.sendToHost({
            type: 'agent_submit',
            text: text,
            attachments: attachments || []
        });
    },

    uploadAgentAttachment(payload) {
        this.sendToHost({
            type: 'agent_upload_attachment',
            clientId: payload.clientId,
            fileName: payload.fileName,
            mimeType: payload.mimeType,
            size: payload.size,
            dataBase64: payload.dataBase64
        });
    },

    sendAgentCommand(command, value, requestId) {
        this.sendToHost({
            type: 'agent_command',
            command: command,
            value: value || '',
            requestId: requestId || ''
        });
    },

    sendAgentPermissionResponse(requestId, value) {
        this.sendToHost({
            type: 'agent_permission_response',
            requestId: requestId,
            value: value
        });
    },

    sendAgentQuestionResponse(requestId, value) {
        this.sendToHost({
            type: 'agent_question_response',
            requestId: requestId,
            value: value
        });
    },

    sendAgentElicitationResponse(requestId, value) {
        this.sendToHost({
            type: 'agent_elicitation_response',
            requestId: requestId,
            value: value
        });
    }
};
