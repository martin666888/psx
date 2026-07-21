// main.js — WebView entry point.
//
// Startup timing (plan Phase 2): synchronously create the TerminalManager,
// register the host listener (terminal events handled inline, Agent events
// buffered into a staging queue), send `ready` IMMEDIATELY, then dynamically
// import the Agent ESM app. The C# host calls PublishStateAsync before it sees
// `ready` (MainWindow.xaml.cs), so Agent events such as agent_providers can
// arrive before entry.js finishes importing — the staging queue is mandatory,
// not a fallback. If the Agent module fails to load, the terminal is already
// live and only the Agent panel is disabled.

(function () {
    const terminalManager = new TerminalManager(document.getElementById('terminal-container'));

    let agentApp = null;
    let agentDisabled = false;
    const agentQueue = [];

    function showAgentLoadFailure() {
        const workspace = document.getElementById('agent-workspace-container');
        if (!workspace) return null;

        let error = workspace.querySelector('.agent-load-error');
        if (!error) {
            error = document.createElement('section');
            error.className = 'agent-load-error';
            error.setAttribute('role', 'alert');
            error.setAttribute('aria-live', 'assertive');

            const card = document.createElement('div');
            card.className = 'agent-load-error-card';
            const title = document.createElement('h2');
            title.textContent = 'Agent UI unavailable';
            const message = document.createElement('p');
            message.textContent = 'The Agent panel could not load. Terminal workspaces remain available; restart PSX to retry the Agent panel.';
            card.append(title, message);
            error.appendChild(card);
            error.hidden = true;
            workspace.appendChild(error);
        }

        workspace.setAttribute('data-agent-load-error', 'true');
        return error;
    }

    function handleAgentFallback(message) {
        let activation = message;
        if (message.type === BridgeEventType.ViewMode) {
            activation = {
                type: BridgeEventType.WorkspaceActivated,
                workspaceId: message.workspaceId || '',
                kind: message.mode === 'agent' ? 'agent' : 'terminal'
            };
        }

        if (activation.type !== BridgeEventType.WorkspaceActivated) return;
        const showingAgent = activation.kind === 'agent';
        terminalManager.setViewVisible(!showingAgent);
        const error = showAgentLoadFailure();
        if (error) error.hidden = !showingAgent;
    }

    function forwardToAgent(message) {
        if (agentDisabled) {
            handleAgentFallback(message);
            return;
        }
        if (agentApp) {
            agentApp.handle(message);
        } else {
            agentQueue.push(message);
        }
    }

    Bridge.onHostMessage((message) => {
        switch (message.type) {
            // Terminal + shared settings: handled inline so they are never
            // blocked by the Agent module import. Settings also feed the Agent
            // app (as an 'app'-scope host event).
            case BridgeEventType.Settings:
            case BridgeEventType.AppearanceSettings:
                terminalManager.setSettings(message.settings);
                forwardToAgent(message);
                return;
            case BridgeEventType.Create:
                terminalManager.createTerminal(message.sessionId);
                return;
            case BridgeEventType.Switch:
                terminalManager.switchTerminal(message.sessionId);
                return;
            case BridgeEventType.Output:
                terminalManager.writeOutput(message.sessionId, message.data);
                return;
            case BridgeEventType.Resize:
                terminalManager.resizeTerminal(message.sessionId, message.cols, message.rows);
                return;
            case BridgeEventType.PasteResponse:
                terminalManager.handlePasteResponse(message);
                return;
            case BridgeEventType.Close:
                terminalManager.closeTerminal(message.sessionId);
                return;
            case BridgeEventType.ViewMode:
                // Compatibility shim: older host callers send view_mode; the
                // Agent app only understands workspace_activated, so translate
                // before forwarding.
                forwardToAgent({
                    type: BridgeEventType.WorkspaceActivated,
                    workspaceId: message.workspaceId || '',
                    kind: message.mode === 'agent' ? 'agent' : 'terminal'
                });
                return;
            default:
                // Every other event is Agent-owned (lifecycle, providers,
                // content). The decoder inside the Agent app ignores any wire
                // type it does not own.
                forwardToAgent(message);
                return;
        }
    });

    // Send ready IMMEDIATELY so the host creates the initial terminal without
    // waiting for the Agent module to import (or even if it fails to).
    Bridge.sendReady();

    // Load the Agent ESM app. Success: drain the staging queue and route every
    // later Agent event through it. Failure: disable the Agent panel; the
    // terminal is already live and unaffected.
    import('./agent-app/entry.js')
        .then((module) => {
            const app = module.createAgentApp({
                terminalManager,
                container: document.getElementById('agent-workspace-container'),
                template: document.getElementById('agent-workspace-template')
            });
            agentApp = app;
            for (const queued of agentQueue.splice(0)) {
                app.handle(queued);
            }
        })
        .catch((error) => {
            agentDisabled = true;
            console.error('[agent] failed to load Agent app; terminal remains available.', error);
            showAgentLoadFailure();
            for (const queued of agentQueue.splice(0)) {
                handleAgentFallback(queued);
            }
        });
})();
