class WorkspaceViewManager {
    constructor(terminalManager, agentContainer, agentTemplate) {
        this.terminalManager = terminalManager;
        this.agentContainer = agentContainer;
        this.agentTemplate = agentTemplate;
        this.agents = new Map();
        this.activeWorkspaceId = '';
        this.settings = null;
        this.providers = [];
    }

    createAgent(message) {
        const workspaceId = String(message.workspaceId || '');
        if (!workspaceId || this.agents.has(workspaceId)) return;

        const fragment = this.agentTemplate.content.cloneNode(true);
        const panel = fragment.querySelector('.agent-panel');
        if (!panel) return;
        panel.dataset.workspaceId = workspaceId;
        this.agentContainer.appendChild(fragment);

        const role = (name) => panel.querySelector('[data-role="' + name + '"]');
        const manager = new AgentThreadManager(
            workspaceId,
            panel,
            role('thread'),
            role('input'),
            role('send'),
            role('command-menu'),
            {
                status: role('status'),
                cwd: role('cwd'),
                session: role('session'),
                changeCwd: role('change-cwd'),
                mode: role('mode'),
                configOptions: role('config-options'),
                contextUsed: role('context-used'),
                commandHint: role('command-hint'),
                inputRow: role('input-row'),
                modeTransitionPrompt: role('mode-transition-prompt'),
                attachButton: role('attach'),
                attachmentInput: role('attachment-input'),
                attachmentStrip: role('attachments-strip'),
                imagePreview: role('image-preview'),
                imagePreviewImg: role('image-preview-img'),
                imagePreviewClose: role('image-preview-close'),
                inspector: role('inspector'),
                inspectorResizer: role('inspector-resizer'),
                planTab: role('plan-tab'),
                planUnread: role('plan-unread'),
                planPanel: role('plan-panel'),
                historyTab: role('history-tab'),
                historyPanel: role('history-panel'),
                runtimeCard: role('runtime-card'),
                runtimeTitle: role('runtime-title'),
                runtimeMessage: role('runtime-message'),
                runtimeInstall: role('runtime-install'),
                runtimeCancel: role('runtime-cancel')
            }
        );

        role('clear')?.addEventListener('click', () => manager.bridge.sendAgentCommand('clear'));
        role('change-cwd')?.addEventListener('click', () => manager.promptForWorkingDirectory());
        if (this.settings) manager.setAgentSettings(this.settings);
        this.agents.set(workspaceId, { panel, manager });
        manager.setVisible(false);
        manager.bridge.sendAgentCommand('state');
    }

    closeAgent(workspaceId) {
        const entry = this.agents.get(String(workspaceId || ''));
        if (!entry) return;
        entry.panel.remove();
        this.agents.delete(String(workspaceId || ''));
        if (this.activeWorkspaceId === String(workspaceId || '')) {
            this.activeWorkspaceId = '';
        }
    }

    activate(message) {
        const workspaceId = String(message.workspaceId || '');
        const kind = String(message.kind || 'terminal');
        this.activeWorkspaceId = workspaceId;

        if (kind === 'terminal') {
            for (const entry of this.agents.values()) entry.manager.setVisible(false);
            this.terminalManager.setViewVisible(true);
            return;
        }

        this.terminalManager.setViewVisible(false);
        for (const [id, entry] of this.agents) {
            entry.manager.setVisible(id === workspaceId);
        }
    }

    handleAgentEvent(message) {
        const workspaceId = String(message.workspaceId || '');
        const entry = this.agents.get(workspaceId);
        if (entry) entry.manager.handleEvent(message);
    }

    setSettings(settings) {
        this.settings = settings;
        for (const entry of this.agents.values()) {
            entry.manager.setAgentSettings(settings);
        }
    }

    showNotice(message) {
        const entry = this.agents.get(this.activeWorkspaceId);
        if (entry) entry.manager._appendSystem(message.text || 'Unable to create Agent workspace.');
    }
}
