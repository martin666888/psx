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
            attachButton: document.getElementById('agent-attach'),
            attachmentInput: document.getElementById('agent-attachment-input'),
            attachmentStrip: document.getElementById('agent-attachments-strip'),
            imagePreview: document.getElementById('agent-image-preview'),
            imagePreviewImg: document.getElementById('agent-image-preview-img'),
            planPanel: document.getElementById('agent-plan-panel'),
            planResizer: document.getElementById('agent-plan-resizer'),
            runtimeCard: document.getElementById('agent-runtime-card'),
            runtimeTitle: document.getElementById('agent-runtime-title'),
            runtimeMessage: document.getElementById('agent-runtime-message'),
            runtimeInstall: document.getElementById('agent-runtime-install'),
            runtimeCancel: document.getElementById('agent-runtime-cancel')
        }
    );
    let resizeTimer = null;

    document.getElementById('agent-new').addEventListener('click', () => {
        Bridge.sendAgentCommand('new');
    });

    document.getElementById('agent-history').addEventListener('click', () => {
        Bridge.sendAgentCommand('history');
    });

    document.getElementById('agent-clear').addEventListener('click', () => {
        Bridge.sendAgentCommand('clear');
    });

    document.getElementById('agent-change-cwd').addEventListener('click', () => {
        agentManager.promptForWorkingDirectory();
    });

    window.addEventListener('resize', () => {
        if (resizeTimer) {
            clearTimeout(resizeTimer);
        }

        resizeTimer = setTimeout(() => {
            resizeTimer = null;
            manager.fitActiveTerminal();
        }, 100);
    });

    // Listen for messages from C# host
    Bridge.onHostMessage((message) => {
        switch (message.type) {
            case 'settings':
                manager.setSettings(message.settings);
                agentManager.setAgentSettings(message.settings);
                break;
            case 'appearance_settings':
                manager.setSettings(message.settings);
                agentManager.setAgentSettings(message.settings);
                break;
            case 'create':
                manager.createTerminal(message.sessionId);
                break;
            case 'switch':
                manager.switchTerminal(message.sessionId);
                break;
            case 'output':
                manager.writeOutput(message.sessionId, message.data);
                break;
            case 'resize':
                manager.resizeTerminal(message.sessionId, message.cols, message.rows);
                break;
            case 'close':
                manager.closeTerminal(message.sessionId);
                break;
            case 'view_mode':
                container.hidden = message.mode === 'agent';
                agentManager.setVisible(message.mode === 'agent');
                if (message.mode === 'agent') {
                    Bridge.sendAgentCommand('activate');
                }
                if (message.mode !== 'agent') {
                    manager.fitActiveTerminal();
                }
                break;
            case 'agent_ready':
            case 'agent_state':
            case 'runtime_status':
            case 'agent_thread_loaded':
            case 'agent_threads':
            case 'agent_commands':
            case 'agent_modes':
            case 'agent_config_options':
            case 'agent_usage_update':
            case 'agent_mode_current':
            case 'agent_attachment_uploaded':
            case 'agent_attachment_failed':
            case 'agent_cleared':
            case 'command_result':
            case 'user_message':
            case 'run_finished':
            case 'assistant_delta':
            case 'assistant_message_done':
            case 'thinking_started':
            case 'thinking_finished':
            case 'thinking_delta':
            case 'tool_started':
            case 'tool_delta':
            case 'tool_finished':
            case 'permission_request':
            case 'question_request':
            case 'elicitation_request':
            case 'permission_cancelled':
            case 'elicitation_cancelled':
            case 'run_failed':
            case 'resume_failed':
            case 'plan_update':
            case 'raw_terminal_fallback':
                agentManager.handleEvent(message);
                break;
        }
    });

    // Notify C# that the frontend is ready
    Bridge.sendReady();
    setTimeout(() => Bridge.sendAgentCommand('state'), 0);
})();
