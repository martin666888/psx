class AgentThreadManager {
    constructor(panel, thread, input, sendButton, commandMenu, meta) {
        this.panel = panel;
        this.thread = thread;
        this.input = input;
        this.sendButton = sendButton;
        this.commandMenu = commandMenu;
        this.meta = meta;
        this.isBusy = false;
        this.isRestoring = false;
        this.isTranscriptOnly = false;
        this.currentTurn = null;
        this.currentAssistant = null;
        this.currentToolBody = null;
        this.thinkingRow = null;
        this.thinkingContent = null;
        this.thinkingHasContent = false;
        this.currentPlanCard = null;
        this.currentPlanBody = null;
        this.currentPlanRunId = null;
        this.planPanel = meta.planPanel || null;
        this.planResizer = meta.planResizer || null;
        this.planPanelWidth = null;
        this.isResizingPlanPanel = false;
        this.currentRunGroup = null;
        this.currentRunGroupBody = null;
        this.currentRunId = null;
        this.toolCards = {};
        this.currentToolCardId = null;
        this._runToolCounts = null;
        this._historyGroup = null;
        this._historyGroupBody = null;
        this._historyToolCounts = null;
        this.commandIndex = 0;
        this.visibleCommands = [];
        this.claudeCommands = [];
        this.modes = [];
        this.configOptions = [];
        this.currentModeId = '';
        this.contextUsedTokens = null;
        this.autoScrollPinned = true;
        this.pendingAttachments = [];
        this.supportsImage = true;
        this.psxCommands = [
            { source: 'PSX', name: '/new', label: 'Start a new thread', command: 'new' },
            { source: 'PSX', name: '/clear', label: 'Clear visible messages only', command: 'clear' },
            { source: 'PSX', name: '/cwd', label: 'Show current working directory', command: 'cwd' },
            { source: 'PSX', name: '/cwd <path>', label: 'Switch cwd and start a new thread', fill: '/cwd ' },
            { source: 'PSX', name: '/terminal', label: 'Open raw Claude terminal here', command: 'terminal' },
            { source: 'PSX', name: '/stop', label: 'Stop the current Claude run', command: 'stop' },
            { source: 'PSX', name: '/history', label: 'Show saved Agent threads', command: 'history' },
            { source: 'PSX', name: '/delete', label: 'Delete the current thread', command: 'delete' },
            { source: 'PSX', name: '/help', label: 'Show PSX Agent commands', command: 'help' }
        ];

        this._wireComposer();
        this._wireModeControl();
        this._wireAttachments();
        this._initializePlanPanel();
    }

    setAgentSettings(settings) {
        if (!settings || typeof settings !== 'object') return;

        const root = document.documentElement;

        if (Number.isInteger(settings.agentFontSize) && settings.agentFontSize >= 6 && settings.agentFontSize <= 72) {
            root.style.setProperty('--agent-font-size', settings.agentFontSize + 'px');
        }

        if (typeof settings.agentFontFamily === 'string' && settings.agentFontFamily.trim()) {
            root.style.setProperty('--agent-font-ui', settings.agentFontFamily.trim());
        }

        if (typeof settings.agentMonoFontFamily === 'string' && settings.agentMonoFontFamily.trim()) {
            root.style.setProperty('--agent-font-mono', settings.agentMonoFontFamily.trim());
        }

        // 全局主题色 [theme]
        if (settings.themeColors && typeof settings.themeColors === 'object') {
            const t = settings.themeColors;
            this._setVar(root, '--agent-bg', t.background);
            this._setVar(root, '--agent-surface', t.surface);
            this._setVar(root, '--agent-surface-raised', t.surfaceRaised);
            this._setVar(root, '--agent-surface-muted', t.surfaceMuted);
            this._setVar(root, '--agent-hover', t.hover);
            this._setVar(root, '--agent-border', t.border);
            this._setVar(root, '--agent-border-strong', t.borderStrong);
            this._setVar(root, '--agent-text', t.text);
            this._setVar(root, '--agent-text-muted', t.textMuted);
            this._setVar(root, '--agent-text-dim', t.textDim);
            this._setVar(root, '--agent-accent', t.accent);
            this._setVar(root, '--agent-accent-hover', t.accentHover);
            this._setVar(root, '--agent-error', t.error);
            this._setVar(root, '--agent-error-bg', t.errorBg);
            this._setVar(root, '--agent-warning', t.warning);
            this._setVar(root, '--agent-warning-bg', t.warningBg);
            this._setVar(root, '--agent-scrollbar', t.scrollbar);
            this._setVar(root, '--agent-scrollbar-hover', t.scrollbarHover);
        }

        // Agent 细节色 [agentTheme]
        if (settings.agentThemeColors && typeof settings.agentThemeColors === 'object') {
            const a = settings.agentThemeColors;
            this._setVar(root, '--agent-code-block-bg', a.codeBlockBg);
            this._setVar(root, '--agent-code-block-text', a.codeBlockText);
            this._setVar(root, '--agent-code-block-border', a.codeBlockBorder);
            this._setVar(root, '--agent-send-btn', a.sendBtn);
            this._setVar(root, '--agent-send-btn-hover', a.sendBtnHover);
            this._setVar(root, '--agent-send-btn-text', a.sendBtnText);
            this._setVar(root, '--agent-decision-primary', a.decisionPrimary);
            this._setVar(root, '--agent-decision-primary-hover', a.decisionPrimaryHover);
            this._setVar(root, '--agent-decision-primary-text', a.decisionPrimaryText);
            this._setVar(root, '--agent-stop-btn', a.stopBtn);
            this._setVar(root, '--agent-stop-btn-hover', a.stopBtnHover);
            this._setVar(root, '--agent-allow-color', a.allowColor);
            this._setVar(root, '--agent-deny-color', a.denyColor);
            this._setVar(root, '--agent-caution-color', a.cautionColor);
            this._setVar(root, '--agent-permission-bg', a.permissionBg);
            this._setVar(root, '--agent-permission-border', a.permissionBorder);
            this._setVar(root, '--agent-elicitation-bg', a.elicitationBg);
            this._setVar(root, '--agent-elicitation-border', a.elicitationBorder);
            this._setVar(root, '--agent-overlay', a.overlay);
            this._setVar(root, '--agent-shadow', a.shadow);
            this._setVar(root, '--agent-focus-ring', a.focusRing);
        }
    }

    _setVar(el, name, value) {
        if (typeof value === 'string' && value.trim()) {
            el.style.setProperty(name, value.trim());
        }
    }

    setVisible(visible) {
        this.panel.hidden = !visible;
        if (visible) {
            setTimeout(() => this.input.focus(), 0);
        }
    }

    promptForWorkingDirectory() {
        Bridge.sendAgentCommand('pick_cwd');
    }

    handleEvent(event) {
        switch (event.type) {
            case 'agent_state':
                this._updateState(event);
                break;
            case 'agent_thread_loaded':
                this._loadThread(event);
                break;
            case 'agent_threads':
                this._appendHistory(event.threads || []);
                break;
            case 'agent_commands':
                this._setClaudeCommands(event.commands || []);
                break;
            case 'agent_modes':
                this._setModes(event.modes || [], event.currentModeId || '');
                break;
            case 'agent_config_options':
                this._setConfigOptions(event.options || []);
                break;
            case 'agent_usage_update':
                this._setContextUsed(event.contextUsedTokens);
                break;
            case 'agent_attachment_uploaded':
                this._handleAttachmentUploaded(event);
                break;
            case 'agent_attachment_failed':
                this._handleAttachmentFailed(event);
                break;
            case 'agent_mode_current':
                this._setCurrentMode(event.currentModeId || '');
                break;
            case 'agent_ready':
                this._markSessionReady(event.sessionId || '');
                break;
            case 'agent_cleared':
                this.thread.innerHTML = '';
                this.currentTurn = null;
                this.currentAssistant = null;
                this.currentToolBody = null;
                this.thinkingRow = null;
                this.thinkingContent = null;
                this.thinkingHasContent = false;
                this._resetPlanState();
                this.currentRunGroup = null;
                this.currentRunGroupBody = null;
                this.currentRunId = null;
                this.toolCards = {};
                this.currentToolCardId = null;
                this._runToolCounts = null;
                this._historyGroup = null;
                this._historyGroupBody = null;
                this._historyToolCounts = null;
                this._clearPendingAttachments();
                this._appendSystem('Thread UI cleared. Claude session context is unchanged.');
                break;
            case 'command_result':
                this._appendSystem(event.text || '');
                break;
            case 'user_message':
                this._finalizeRunGroup();
                this._startTurn();
                this._appendMessage('user', event.text || '');
                this._appendMessageAttachments(event.attachments || []);
                this.lastSubmittedDraft = null;
                this.currentAssistant = null;
                break;
            case 'thinking_started':
                this._showThinking();
                break;
            case 'thinking_delta':
                this._appendThinkingDelta(event.text || '');
                break;
            case 'thinking_finished':
                this._hideThinking();
                break;
            case 'assistant_delta':
                this._appendAssistantDelta(event.text || '');
                break;
            case 'assistant_message_done':
                this.currentAssistant = null;
                break;
            case 'run_finished':
                this.currentAssistant = null;
                this._hideThinking();
                this._finalizeRunGroup();
                this.currentTurn = null;
                break;
            case 'tool_started':
                this._ensureRunGroup(event.runId || '');
                this._createToolCard(
                    event.toolCallId || ('local-' + Date.now()),
                    event.name || 'Tool',
                    event.input || '',
                    event.summary || '',
                    'running'
                );
                break;
            case 'tool_delta': {
                const card = event.toolCallId ? this.toolCards[event.toolCallId] : null;
                if (card) {
                    card.pre.textContent += (event.text || '');
                } else {
                    this._appendToolDelta(event.name || 'Tool output', event.text || '');
                }
                this._scrollToBottom();
                break;
            }
            case 'tool_finished': {
                const card = event.toolCallId ? this.toolCards[event.toolCallId] : null;
                if (card) {
                    card.details.classList.remove('agent-tool-card-running');
                    card.details.classList.add('agent-tool-card-' + (event.status || 'done'));
                    card.details.removeAttribute('open');
                    delete this.toolCards[event.toolCallId];
                } else {
                    this._finishTool();
                }
                this.currentToolCardId = null;
                break;
            }
            case 'permission_request':
                this._appendDecision(event, 'permission');
                break;
            case 'question_request':
                this._appendDecision(event, 'question');
                break;
            case 'elicitation_request':
                this._appendElicitation(event);
                break;
            case 'permission_cancelled':
            case 'elicitation_cancelled':
                this._cancelDecision(event.requestId || '', event.text || 'Request cancelled.');
                break;
            case 'raw_terminal_fallback':
                this._appendTool('Raw terminal fallback', event.text || 'Unsupported interaction requires terminal fallback.', 'fallback');
                break;
            case 'plan_update':
                this._upsertPlan(event);
                break;
            case 'run_failed':
                this._hideThinking();
                this._restoreSubmittedDraft();
                if (this.currentRunGroup) {
                    this.currentRunGroup.classList.add('agent-run-group-error');
                }
                this._finalizeRunGroup();
                this._appendTool('Claude error', event.text || 'Unknown error.', 'error');
                if (event.visionContextHint) {
                    this._appendSystem(event.visionContextHint);
                }
                break;
            case 'resume_failed':
                this._appendRecovery(event.text || 'Claude could not resume this session.');
                break;
        }
    }
}
