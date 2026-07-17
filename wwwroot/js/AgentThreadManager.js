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
        this.inspector = meta.inspector || null;
        this.inspectorResizer = meta.inspectorResizer || null;
        this.planTab = meta.planTab || null;
        this.planUnread = meta.planUnread || null;
        this.planPanel = meta.planPanel || null;
        this.historyTab = meta.historyTab || null;
        this.historyPanel = meta.historyPanel || null;
        this.inspectorWidth = null;
        this.isResizingInspector = false;
        this.activeInspectorTab = 'plan';
        this.currentThreadId = '';
        this.historyThreads = [];
        this.historyScrollTop = 0;
        this.currentRunGroup = null;
        this.currentRunGroupBody = null;
        this.currentRunId = null;
        this.toolCards = {};
        this.modeTransitionCards = {};
        this.activeModeTransitionRequestId = '';
        this.currentToolCardId = null;
        this._runToolCounts = null;
        this._historyGroup = null;
        this._historyGroupBody = null;
        this._historyToolCounts = null;
        this.commandIndex = 0;
        this.visibleCommands = [];
        this.providerKey = 'acp-claude';
        this.agentName = 'Claude Code';
        this.assistantName = 'Claude';
        this.agentCommands = [];
        this.agentCommandsReady = false;
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
            { source: 'PSX', name: '/terminal', label: 'Open raw ' + this.assistantName + ' terminal here', command: 'terminal' },
            { source: 'PSX', name: '/stop', label: 'Stop the current ' + this.assistantName + ' run', command: 'stop' },
            { source: 'PSX', name: '/history', label: 'Show saved Agent threads', command: 'history' },
            { source: 'PSX', name: '/delete', label: 'Delete the current thread', command: 'delete' },
            { source: 'PSX', name: '/help', label: 'Show PSX Agent commands', command: 'help' }
        ];

        this._wireComposer();
        this._wireModeControl();
        this._wireAttachments();
        this._wireRuntimeControls();
        this._initializeInspector();
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

    _updateAgentIdentity(event) {
        if (!event || typeof event !== 'object') return;

        if (typeof event.providerKey === 'string' && event.providerKey.trim()) {
            this.providerKey = event.providerKey.trim();
        }
        if (typeof event.agentName === 'string' && event.agentName.trim()) {
            this.agentName = event.agentName.trim();
        }
        if (typeof event.assistantName === 'string' && event.assistantName.trim()) {
            this.assistantName = event.assistantName.trim();
        }

        const terminal = this.psxCommands.find((command) => command.command === 'terminal');
        if (terminal) terminal.label = 'Open raw ' + this.assistantName + ' terminal here';
        const stop = this.psxCommands.find((command) => command.command === 'stop');
        if (stop) stop.label = 'Stop the current ' + this.assistantName + ' run';

        const modeLabel = this.meta.mode?.closest('.agent-mode-control');
        if (modeLabel) modeLabel.title = this.assistantName + ' Agent mode';
    }

    setVisible(visible) {
        this.panel.hidden = !visible;
        if (visible && this._runtimeReady()) {
            setTimeout(() => this.input.focus(), 0);
        }
    }

    promptForWorkingDirectory() {
        Bridge.sendAgentCommand('pick_cwd');
    }

    handleEvent(event) {
        switch (event.type) {
            case BridgeEventType.AgentState:
                this._updateState(event);
                break;
            case BridgeEventType.RuntimeStatus:
                this._updateRuntimeStatus(event);
                break;
            case BridgeEventType.AgentThreadLoaded:
                this._clearCommandHint();
                this._loadThread(event);
                break;
            case BridgeEventType.AgentThreads:
                this.selectInspectorTab('history', false);
                this._renderHistory(event.threads || []);
                break;
            case BridgeEventType.AgentHistoryError:
                this.selectInspectorTab('history', false);
                this._renderHistoryError(event.text || 'Unable to load Agent thread history.');
                break;
            case BridgeEventType.AgentCommands:
                this._setAgentCommands(event.commands || [], event.ready === true);
                break;
            case BridgeEventType.AgentCommandRejected:
                this._showCommandHint(event.command || '', event.reason || 'unsupported');
                break;
            case BridgeEventType.AgentModes:
                this._setModes(event.modes || [], event.currentModeId || '');
                break;
            case BridgeEventType.AgentConfigOptions:
                this._setConfigOptions(event.options || []);
                break;
            case BridgeEventType.AgentUsageUpdate:
                this._setContextUsed(event.contextUsedTokens);
                break;
            case BridgeEventType.AgentAttachmentUploaded:
                this._handleAttachmentUploaded(event);
                break;
            case BridgeEventType.AgentAttachmentFailed:
                this._handleAttachmentFailed(event);
                break;
            case BridgeEventType.AgentModeCurrent:
                this._setCurrentMode(event.currentModeId || '');
                break;
            case BridgeEventType.AgentReady:
                this._markSessionReady(event.sessionId || '');
                break;
            case BridgeEventType.AgentCleared:
                this._clearModeTransitionPrompt('', false);
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
                this.modeTransitionCards = {};
                this.currentToolCardId = null;
                this._runToolCounts = null;
                this._historyGroup = null;
                this._historyGroupBody = null;
                this._historyToolCounts = null;
                this._clearPendingAttachments();
                this._appendSystem('Thread UI cleared. ' + this.assistantName + ' session context is unchanged.');
                break;
            case BridgeEventType.CommandResult:
                this._appendSystem(event.text || '');
                break;
            case BridgeEventType.UserMessage:
                this._finalizeRunGroup();
                this._startTurn();
                this._appendMessage('user', event.text || '');
                this._appendMessageAttachments(event.attachments || []);
                this.lastSubmittedDraft = null;
                this.currentAssistant = null;
                break;
            case BridgeEventType.ThinkingStarted:
                this._showThinking();
                break;
            case BridgeEventType.ThinkingDelta:
                this._appendThinkingDelta(event.text || '');
                break;
            case BridgeEventType.ThinkingFinished:
                this._hideThinking();
                break;
            case BridgeEventType.AssistantDelta:
                this._appendAssistantDelta(event.text || '');
                break;
            case BridgeEventType.AssistantMessageDone:
                this._finalizeAssistantMessage(this.currentAssistant);
                this.currentAssistant = null;
                break;
            case BridgeEventType.RunFinished:
                this._interruptModeTransition('This request is no longer active.');
                this._finalizeAssistantMessage(this.currentAssistant);
                this.currentAssistant = null;
                this._hideThinking();
                this._finalizeRunGroup();
                this.currentTurn = null;
                break;
            case BridgeEventType.ToolStarted:
                this._ensureRunGroup(event.runId || '');
                this._createToolCard(
                    event.toolCallId || ('local-' + Date.now()),
                    event.name || 'Tool',
                    event.input || '',
                    event.summary || '',
                    'running'
                );
                break;
            case BridgeEventType.ToolDelta: {
                const resolved = this._resolveToolCard(event);
                if (resolved) {
                    resolved.card.pre.textContent += (event.text || '');
                } else {
                    this._appendToolDelta(event.name || 'Tool output', event.text || '');
                }
                this._scrollToBottom();
                break;
            }
            case BridgeEventType.ToolFinished: {
                const resolved = this._resolveToolCard(event);
                if (resolved) {
                    if (!resolved.card.pre.textContent.trim()) {
                        resolved.card.pre.textContent = 'Finished.';
                    }
                    if (resolved.card.details.dataset.state === 'running' && this._runToolCounts) {
                        this._runToolCounts.running = Math.max(0, this._runToolCounts.running - 1);
                    }
                    this._setToolCardState(resolved.card, event.status || 'done');
                    this._updateRunGroupSummary();
                    resolved.card.details.removeAttribute('open');
                    delete this.toolCards[resolved.toolCallId];
                } else {
                    this._finishTool();
                }
                this.currentToolCardId = null;
                break;
            }
            case BridgeEventType.PermissionRequest:
                if (event.presentation === 'mode_transition' && event.documentText) {
                    this._appendModeTransition(event, false);
                } else {
                    this._appendDecision(event, 'permission');
                }
                break;
            case BridgeEventType.PermissionResolved:
                this._resolvePermission(event);
                break;
            case BridgeEventType.QuestionRequest:
                this._appendDecision(event, 'question');
                break;
            case BridgeEventType.ElicitationRequest:
                this._appendElicitation(event);
                break;
            case BridgeEventType.PermissionCancelled:
            case BridgeEventType.ElicitationCancelled:
                this._cancelDecision(event.requestId || '', event.text || 'Request cancelled.');
                break;
            case BridgeEventType.RawTerminalFallback:
                this._appendTool('Raw terminal fallback', event.text || 'Unsupported interaction requires terminal fallback.', 'fallback');
                break;
            case BridgeEventType.PlanUpdate:
                this._upsertPlan(event);
                break;
            case BridgeEventType.RunFailed:
                this._interruptModeTransition('The request ended before a selection was completed.');
                this._hideThinking();
                this._restoreSubmittedDraft();
                if (this.currentRunGroup) {
                    this.currentRunGroup.classList.add('agent-run-group-error');
                    Object.values(this.toolCards).forEach((card) => {
                        if (card.details.dataset.state === 'running') {
                            this._setToolCardState(card, 'error');
                        }
                    });
                    if (this._runToolCounts) {
                        this._runToolCounts.running = 0;
                        this._updateRunGroupSummary();
                    }
                }
                this._finalizeRunGroup();
                this._appendTool(this.assistantName + ' error', event.text || 'Unknown error.', 'error');
                if (event.visionContextHint) {
                    this._appendSystem(event.visionContextHint);
                }
                break;
            case BridgeEventType.ResumeFailed:
                this._appendRecovery(
                    event.message || event.text || this.assistantName + ' could not resume this session.',
                    event.detail || ''
                );
                break;
        }
    }
}
