AgentThreadManager.prototype._updateState = function(event) {
        this.lastState = event;
        if (event.threadId) {
            this.currentThreadId = event.threadId;
            this._syncHistoryCurrentState();
        }
        const status = event.status || 'ready';
        this.isRestoring = status === 'restoring';
        this.isTranscriptOnly = status === 'transcript_only';
        this.isBusy = !!event.busy;
        this.supportsImage = event.supportsImage !== false;
        if ('contextUsedTokens' in event) {
            this._setContextUsed(event.contextUsedTokens);
        }
        this._syncAttachmentControls();
        this.meta.status.textContent = this._formatStatus(status);
        this.meta.status.dataset.status = status;
        this.meta.cwd.textContent = event.cwd || 'cwd not set';
        this.meta.session.textContent = event.sessionId ? 'session ' + event.sessionId.slice(0, 8) : 'no session';
        if (this.isRestoring) {
            this.sendButton.textContent = 'Loading';
            this.sendButton.title = 'ACP history is loading';
        } else if (this.isTranscriptOnly) {
            this.sendButton.textContent = 'Start';
            this.sendButton.title = 'Start a new ACP Agent thread';
        } else {
            this.sendButton.textContent = this.isBusy ? 'Stop' : 'Send';
            this.sendButton.title = this.isBusy ? 'Stop Claude' : 'Send message';
        }
        this.sendButton.disabled = this.isRestoring || !this._runtimeReady();
        this.sendButton.classList.toggle('agent-send-stop', this.isBusy);
        if (this.meta.mode) {
            this._syncFallbackModeVisibility();
            this.meta.mode.disabled = this._configControlsDisabled() || this.modes.length === 0 || this._hasConfigOption('mode');
        }
        this._syncConfigOptionDisabledState();
        this._syncRuntimeControls();
};

AgentThreadManager.prototype._formatStatus = function(status) {
        return String(status || 'ready')
            .split('_')
            .filter(Boolean)
            .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
            .join(' ');
};

AgentThreadManager.prototype._setContextUsed = function(value) {
        if (value === null || value === undefined || value === '') {
            this.contextUsedTokens = null;
            if (this.meta.contextUsed) {
                this.meta.contextUsed.textContent = this._formatContextUsed(this.contextUsedTokens);
            }
            return;
        }

        const numeric = Number(value);
        this.contextUsedTokens = Number.isFinite(numeric) && numeric >= 0 ? numeric : null;
        if (this.meta.contextUsed) {
            this.meta.contextUsed.textContent = this._formatContextUsed(this.contextUsedTokens);
        }
};

AgentThreadManager.prototype._formatContextUsed = function(value) {
        return value === null ? 'Context used --k' : 'Context used ' + (value / 1000).toFixed(2) + 'k';
};

AgentThreadManager.prototype._loadThread = function(event) {
            this.currentThreadId = event.threadId || '';
            if (event.selectPlan === true) {
                this.selectInspectorTab('plan', false);
            }
            if (event.clear) {
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
                this._clearPendingAttachments();
        }

        if ('contextUsedTokens' in event) {
            this._setContextUsed(event.contextUsedTokens);
        }

        const messages = event.messages || [];
        let lastRunId = null;

        for (const msg of messages) {
            if (msg.role === 'user') {
                if (this._historyGroup) {
                    this._finalizeHistoryGroup();
                }
                this._startTurn();
                this._appendMessage('user', msg.text || '');
                this._appendMessageAttachments(msg.attachments || []);
                lastRunId = null;
                continue;
            }

            if (msg.role === 'thinking') {
                if (this._historyGroup) {
                    this._finalizeHistoryGroup();
                }
                if (!this.currentTurn) {
                    this._startTurn();
                }
                this._appendThinkingBlock(msg.text || '', false);
                lastRunId = null;
                continue;
            }

            if (msg.role === 'plan') {
                if (this._historyGroup) {
                    this._finalizeHistoryGroup();
                }
                this._upsertPlan({
                    runId: msg.runId || '',
                    entries: msg.planEntries || [],
                    text: msg.text || ''
                });
                lastRunId = null;
                continue;
            }

            if (msg.role === 'tool' && msg.runId) {
                if (msg.runId !== lastRunId) {
                    this._finalizeHistoryGroup();
                    this._createHistoryGroup(msg.runId);
                    lastRunId = msg.runId;
                }
                this._appendHistoryToolCard(msg);
            } else {
                if (this._historyGroup) {
                    this._finalizeHistoryGroup();
                }
                if (msg.role === 'tool') {
                    this._appendTool(msg.name || 'Tool', msg.text || '', 'done');
                } else if (msg.role === 'system') {
                    this._appendSystem(msg.text || '');
                } else {
                    this._appendMessage(msg.role === 'user' ? 'user' : 'assistant', msg.text || '');
                }
            }
        }
        this._finalizeHistoryGroup();
        this.currentTurn = null;

        if (messages.length === 0) {
            this._appendSystem('Ready. Claude will start on the first message. Working directory and session state are shown above.');
        }

        this._updateState({
            status: this.lastState?.status || 'ready',
            busy: this.lastState?.busy || false,
            cwd: event.cwd,
            sessionId: event.sessionId,
            threadId: this.currentThreadId
        });
        this._syncHistoryCurrentState();
};

AgentThreadManager.prototype._appendSystem = function(text) {
        const row = document.createElement('div');
        row.className = 'agent-system';
        row.textContent = text;
        this._appendToCurrentHost(row);
        this._scrollToBottom();
};

AgentThreadManager.prototype._markSessionReady = function(sessionId) {
        if (!sessionId || this.lastReadySessionId === sessionId) {
            return;
        }

        this.lastReadySessionId = sessionId;
};

AgentThreadManager.prototype._appendRecovery = function(text) {
        const card = document.createElement('section');
        card.className = 'agent-decision';

        const title = document.createElement('div');
        title.className = 'agent-decision-title';
        title.textContent = 'Resume failed';

        const body = document.createElement('pre');
        body.textContent = text;

        const actions = document.createElement('div');
        actions.className = 'agent-decision-actions';
        actions.appendChild(this._decisionButton('Start new thread', () => {
            this.selectInspectorTab('plan', false);
            Bridge.sendAgentCommand('new');
        }));
        actions.appendChild(this._decisionButton('Open terminal', () => Bridge.sendAgentCommand('terminal')));

        card.appendChild(title);
        card.appendChild(body);
        card.appendChild(actions);
        this.thread.appendChild(card);
        this._scrollToBottom();
};

AgentThreadManager.prototype._historyMeta = function(thread) {
        const parts = [];
        if (thread.updatedAt) parts.push(thread.updatedAt);
        if (thread.sessionId) parts.push('session ' + thread.sessionId.slice(0, 8));
        return parts.join(' | ');
};

AgentThreadManager.prototype._startTurn = function() {
        const turn = document.createElement('section');
        turn.className = 'agent-turn';
        this.thread.appendChild(turn);
        this.currentTurn = turn;
        return turn;
};

AgentThreadManager.prototype._appendToCurrentHost = function(element) {
        const host = this.currentTurn || this.thread;
        host.appendChild(element);
};
