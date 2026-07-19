// main.js — WebView entry point.

(function () {
    const terminalManager = new TerminalManager(document.getElementById('terminal-container'));
    const workspaceViews = new WorkspaceViewManager(
        terminalManager,
        document.getElementById('agent-workspace-container'),
        document.getElementById('agent-workspace-template')
    );

    Bridge.onHostMessage((message) => {
        switch (message.type) {
            case BridgeEventType.Settings:
            case BridgeEventType.AppearanceSettings:
                terminalManager.setSettings(message.settings);
                workspaceViews.setSettings(message.settings);
                break;
            case BridgeEventType.Create:
                terminalManager.createTerminal(message.sessionId);
                break;
            case BridgeEventType.Switch:
                terminalManager.switchTerminal(message.sessionId);
                break;
            case BridgeEventType.Output:
                terminalManager.writeOutput(message.sessionId, message.data);
                break;
            case BridgeEventType.Resize:
                terminalManager.resizeTerminal(message.sessionId, message.cols, message.rows);
                break;
            case BridgeEventType.PasteResponse:
                terminalManager.handlePasteResponse(message);
                break;
            case BridgeEventType.Close:
                terminalManager.closeTerminal(message.sessionId);
                break;
            case BridgeEventType.WorkspaceActivated:
                workspaceViews.activate(message);
                break;
            case BridgeEventType.AgentWorkspaceCreated:
                workspaceViews.createAgent(message);
                break;
            case BridgeEventType.AgentWorkspaceClosed:
                workspaceViews.closeAgent(message.workspaceId);
                break;
            case BridgeEventType.AgentWorkspaceLimitReached:
                workspaceViews.showNotice(message);
                break;
            case BridgeEventType.AgentProviders:
                workspaceViews.providers = Array.isArray(message.providers) ? message.providers.slice() : [];
                break;
            case BridgeEventType.ViewMode:
                // Compatibility with older host-side callers during the workspace migration.
                workspaceViews.activate({
                    workspaceId: message.workspaceId || '',
                    kind: message.mode === 'agent' ? 'agent' : 'terminal'
                });
                break;
            case BridgeEventType.AgentReady:
            case BridgeEventType.AgentState:
            case BridgeEventType.RuntimeStatus:
            case BridgeEventType.AgentThreadLoaded:
            case BridgeEventType.AgentThreads:
            case BridgeEventType.AgentHistoryError:
            case BridgeEventType.AgentHistoryInvalidated:
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
                workspaceViews.handleAgentEvent(message);
                break;
        }
    });

    Bridge.sendReady();
})();
