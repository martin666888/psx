// main.js — Entry point

(function () {
    const container = document.getElementById('terminal-container');
    const manager = new TerminalManager(container);
    const agentManager = new AgentThreadManager(
        document.getElementById('agent-panel'),
        document.getElementById('agent-thread'),
        document.getElementById('agent-input'),
        document.getElementById('agent-send'),
        document.getElementById('agent-command-menu'),
        {
            status: document.getElementById('agent-status'),
            cwd: document.getElementById('agent-cwd'),
            session: document.getElementById('agent-session'),
            mode: document.getElementById('agent-mode'),
            configOptions: document.getElementById('agent-config-options'),
            contextUsed: document.getElementById('agent-context-used'),
            commandHint: document.getElementById('agent-command-hint'),
            inputRow: document.getElementById('agent-input-row'),
            modeTransitionPrompt: document.getElementById('agent-mode-transition-prompt'),
            attachButton: document.getElementById('agent-attach'),
            attachmentInput: document.getElementById('agent-attachment-input'),
            attachmentStrip: document.getElementById('agent-attachments-strip'),
            imagePreview: document.getElementById('agent-image-preview'),
            imagePreviewImg: document.getElementById('agent-image-preview-img'),
            imagePreviewClose: document.getElementById('agent-image-preview-close'),
            inspector: document.getElementById('agent-inspector'),
            inspectorResizer: document.getElementById('agent-inspector-resizer'),
            planTab: document.getElementById('agent-plan-tab'),
            planUnread: document.getElementById('agent-plan-unread'),
            planPanel: document.getElementById('agent-plan-panel'),
            historyTab: document.getElementById('agent-history-tab'),
            historyPanel: document.getElementById('agent-history-panel'),
            runtimeCard: document.getElementById('agent-runtime-card'),
            runtimeTitle: document.getElementById('agent-runtime-title'),
            runtimeMessage: document.getElementById('agent-runtime-message'),
            runtimeInstall: document.getElementById('agent-runtime-install'),
            runtimeCancel: document.getElementById('agent-runtime-cancel')
        }
    );
    document.getElementById('agent-new').addEventListener('click', () => {
        agentManager.selectInspectorTab('plan');
        Bridge.sendAgentCommand('new');
    });

    document.getElementById('agent-clear').addEventListener('click', () => {
        Bridge.sendAgentCommand('clear');
    });

    document.getElementById('agent-change-cwd').addEventListener('click', () => {
        agentManager.promptForWorkingDirectory();
    });

    // Listen for messages from C# host
    Bridge.onHostMessage((message) => {
        switch (message.type) {
            case BridgeEventType.Settings:
                manager.setSettings(message.settings);
                agentManager.setAgentSettings(message.settings);
                break;
            case BridgeEventType.AppearanceSettings:
                manager.setSettings(message.settings);
                agentManager.setAgentSettings(message.settings);
                break;
            case BridgeEventType.Create:
                manager.createTerminal(message.sessionId);
                break;
            case BridgeEventType.Switch:
                manager.switchTerminal(message.sessionId);
                break;
            case BridgeEventType.Output:
                manager.writeOutput(message.sessionId, message.data);
                break;
            case BridgeEventType.Resize:
                manager.resizeTerminal(message.sessionId, message.cols, message.rows);
                break;
            case BridgeEventType.PasteResponse:
                manager.handlePasteResponse(message);
                break;
            case BridgeEventType.Close:
                manager.closeTerminal(message.sessionId);
                break;
            case BridgeEventType.ViewMode:
                if (message.mode === 'agent') {
                    manager.setViewVisible(false);
                    agentManager.setVisible(true);
                    Bridge.sendAgentCommand('activate');
                } else {
                    agentManager.setVisible(false);
                    manager.setViewVisible(true);
                }
                break;
            case BridgeEventType.AgentReady:
            case BridgeEventType.AgentState:
            case BridgeEventType.RuntimeStatus:
            case BridgeEventType.AgentThreadLoaded:
            case BridgeEventType.AgentThreads:
            case BridgeEventType.AgentHistoryError:
            case BridgeEventType.AgentCommands:
            case BridgeEventType.AgentCommandRejected:
            case BridgeEventType.AgentModes:
            case BridgeEventType.AgentConfigOptions:
            case BridgeEventType.AgentUsageUpdate:
            case BridgeEventType.AgentModeCurrent:
            case BridgeEventType.AgentAttachmentUploaded:
            case BridgeEventType.AgentAttachmentFailed:
            case BridgeEventType.AgentCleared:
            case BridgeEventType.CommandResult:
            case BridgeEventType.UserMessage:
            case BridgeEventType.RunFinished:
            case BridgeEventType.AssistantDelta:
            case BridgeEventType.AssistantMessageDone:
            case BridgeEventType.ThinkingStarted:
            case BridgeEventType.ThinkingFinished:
            case BridgeEventType.ThinkingDelta:
            case BridgeEventType.ToolStarted:
            case BridgeEventType.ToolDelta:
            case BridgeEventType.ToolFinished:
            case BridgeEventType.PermissionRequest:
            case BridgeEventType.PermissionResolved:
            case BridgeEventType.QuestionRequest:
            case BridgeEventType.ElicitationRequest:
            case BridgeEventType.PermissionCancelled:
            case BridgeEventType.ElicitationCancelled:
            case BridgeEventType.RunFailed:
            case BridgeEventType.ResumeFailed:
            case BridgeEventType.PlanUpdate:
            case BridgeEventType.RawTerminalFallback:
                agentManager.handleEvent(message);
                break;
        }
    });

    // Notify C# that the frontend is ready
    Bridge.sendReady();
    setTimeout(() => Bridge.sendAgentCommand('state'), 0);
})();
