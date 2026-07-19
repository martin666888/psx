// Mode transition (Plan -> Code) proposal cards and the composer prompt.
// Load order contract: this file must load after permissions.js, which defines
// the shared helpers used here (_decisionOptionClass, _findDecisionCard).

AgentThreadManager.prototype._appendModeTransition = function(event, historical) {
        const requestId = event.requestId || '';
        const toolCallId = event.toolCallId || '';
        const key = requestId || ('tool:' + toolCallId);
        const options = Array.isArray(event.options) ? event.options : [];
        const selectedOptionId = event.selectedOptionId || '';
        const storedState = event.decisionState || (historical ? 'interrupted' : 'pending');
        const pending = !historical && storedState === 'pending';
        const interactive = pending && !!requestId && options.length > 0;
        const displayState = pending && (!requestId || options.length === 0) ? 'error' : storedState;
        const wasPinned = this.autoScrollPinned;

        if (pending && this.activeModeTransitionRequestId && this.activeModeTransitionRequestId !== requestId) {
            this._interruptModeTransition('A newer mode transition request replaced this one.');
        }
        this._removeToolCardForModeTransition(toolCallId);

        const existing = key ? this.modeTransitionCards[key] : null;
        if (existing?.isConnected) existing.remove();

        const card = document.createElement('section');
        card.className = 'agent-mode-transition';
        card.dataset.decisionState = pending ? 'active' : 'disabled';
        if (requestId) card.dataset.requestId = requestId;
        if (toolCallId) card.dataset.toolCallId = toolCallId;

        const header = document.createElement('header');
        header.className = 'agent-mode-transition-header';

        const title = document.createElement('div');
        title.className = 'agent-mode-transition-title';
        title.textContent = event.title || 'Mode transition';

        const headerState = document.createElement('span');
        headerState.className = 'agent-mode-transition-header-state';
        headerState.textContent = this._modeTransitionStateLabel(displayState, interactive);

        header.appendChild(title);
        header.appendChild(headerState);

        const documentDetails = document.createElement('details');
        documentDetails.className = 'agent-mode-transition-details';
        documentDetails.open = !historical;

        const documentSummary = document.createElement('summary');
        documentSummary.className = 'agent-mode-transition-summary';
        documentSummary.textContent = 'Proposal details';

        const documentBody = document.createElement('div');
        documentBody.className = 'agent-mode-transition-document agent-message-body';
        documentBody.innerHTML = this._renderMarkdown(event.documentText || '');

        const documentActions = document.createElement('div');
        documentActions.className = 'agent-message-actions agent-mode-transition-document-actions';
        documentActions.appendChild(this._createCopyButton(() => event.documentText || ''));

        documentDetails.appendChild(documentSummary);
        documentDetails.appendChild(documentBody);
        documentDetails.appendChild(documentActions);

        const decisionSection = document.createElement('div');
        decisionSection.className = 'agent-mode-transition-decision';

        const decisionLabel = document.createElement('div');
        decisionLabel.className = 'agent-mode-transition-decision-label';
        decisionLabel.textContent = 'Choose how to continue';
        decisionSection.appendChild(decisionLabel);

        const actions = document.createElement('div');
        actions.className = 'agent-mode-transition-options';
        actions.setAttribute('role', 'group');
        actions.setAttribute('aria-label', 'Choose how to continue');

        if (options.length === 0) {
            const error = document.createElement('div');
            error.className = 'agent-mode-transition-error';
            error.textContent = 'The ACP Agent did not provide any response options.';
            actions.appendChild(error);
        } else {
            options.forEach((option) => {
                const optionId = option.optionId || '';
                const optionName = option.name || optionId || 'Select';
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'agent-mode-transition-option';
                button.textContent = optionName;
                button.dataset.optionId = optionId;
                button.dataset.optionKind = option.kind || '';

                const semanticClass = this._decisionOptionClass(option);
                if (semanticClass) button.classList.add(semanticClass);

                if (optionId === selectedOptionId) {
                    button.classList.add('agent-mode-transition-option-selected');
                    button.setAttribute('aria-pressed', 'true');
                } else {
                    button.setAttribute('aria-pressed', 'false');
                }

                button.disabled = true;

                actions.appendChild(button);
            });
        }

        decisionSection.appendChild(actions);
        decisionSection.hidden = pending;

        const status = document.createElement('div');
        status.className = 'agent-mode-transition-status';
        status.setAttribute('aria-live', 'polite');
        status.textContent = this._modeTransitionStatusText(displayState, options, selectedOptionId, interactive);

        documentDetails.appendChild(decisionSection);
        documentDetails.appendChild(status);
        card.appendChild(header);
        card.appendChild(documentDetails);
        this._appendToCurrentHost(card);

        if (key) this.modeTransitionCards[key] = card;
        if (requestId) this.modeTransitionCards['request:' + requestId] = card;
        if (toolCallId) this.modeTransitionCards['tool:' + toolCallId] = card;
        if (pending) {
            this._showModeTransitionPrompt(event, card, interactive);
            if (wasPinned) this._scrollModeTransitionToStart(card);
        } else {
            this._scrollToBottom();
        }
};

AgentThreadManager.prototype._modeTransitionStateLabel = function(state, active) {
        if (active) return 'Decision required';
        if (state === 'error') return 'Unavailable';
        if (state === 'selected') return 'Selected';
        if (state === 'cancelled') return 'Cancelled';
        return 'Interrupted';
};

AgentThreadManager.prototype._modeTransitionStatusText = function(state, options, selectedOptionId, active) {
        if (active) return 'The Agent is waiting for your selection.';
        if (state === 'error') return 'The request cannot continue without an ACP response option.';
        if (state === 'selected') {
            const selected = options.find((option) => option.optionId === selectedOptionId);
            return selected ? 'Selected: ' + selected.name : 'Selection recorded.';
        }
        if (state === 'cancelled') return 'Request cancelled.';
        return 'This request is no longer active.';
};

AgentThreadManager.prototype._showModeTransitionPrompt = function(event, card, interactive) {
        const prompt = this.meta.modeTransitionPrompt;
        const inputRow = this.meta.inputRow;
        if (!prompt || !inputRow) return;

        const requestId = event.requestId || '';
        const options = Array.isArray(event.options) ? event.options : [];
        const composerHadFocus = inputRow.contains(document.activeElement);

        this._clearModeTransitionPrompt('', false);
        this.activeModeTransitionRequestId = requestId;
        prompt.innerHTML = '';

        const header = document.createElement('div');
        header.className = 'agent-composer-decision-header';

        const heading = document.createElement('div');
        heading.className = 'agent-composer-decision-heading';

        const title = document.createElement('div');
        title.className = 'agent-composer-decision-title';
        title.textContent = event.title || 'Mode transition';

        const instruction = document.createElement('div');
        instruction.className = 'agent-composer-decision-instruction';
        instruction.textContent = 'Choose how to continue';

        heading.appendChild(title);
        heading.appendChild(instruction);

        const stopButton = document.createElement('button');
        stopButton.type = 'button';
        stopButton.className = 'agent-composer-decision-stop';
        stopButton.textContent = 'Stop';
        stopButton.title = 'Stop ' + this.assistantName;
        stopButton.addEventListener('click', () => {
            if (stopButton.disabled) return;
            stopButton.disabled = true;
            stopButton.textContent = 'Stopping';
            status.textContent = 'Stopping the current Agent run…';
            prompt.querySelectorAll('.agent-composer-decision-option').forEach((button) => {
                button.disabled = true;
            });
            this.bridge.sendAgentCommand('stop');
        });

        header.appendChild(heading);
        header.appendChild(stopButton);

        const actions = document.createElement('div');
        actions.className = 'agent-composer-decision-options';
        actions.setAttribute('role', 'group');
        actions.setAttribute('aria-label', 'Choose how to continue');

        if (interactive) {
            options.forEach((option) => {
                const optionId = option.optionId || '';
                const optionName = option.name || optionId || 'Select';
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'agent-composer-decision-option';
                button.textContent = optionName;
                button.dataset.optionId = optionId;
                button.dataset.optionKind = option.kind || '';

                const semanticClass = this._decisionOptionClass(option);
                if (semanticClass) button.classList.add(semanticClass);

                button.addEventListener('click', () => {
                    if (this.activeModeTransitionRequestId !== requestId || !optionId) return;

                    prompt.dataset.decisionState = 'sending';
                    prompt.querySelectorAll('.agent-composer-decision-option').forEach((candidate) => {
                        candidate.disabled = true;
                    });
                    button.classList.add('agent-mode-transition-option-pending');
                    card.dataset.decisionState = 'sending';
                    card.classList.add('agent-mode-transition-sending');

                    const cardHeaderState = card.querySelector('.agent-mode-transition-header-state');
                    if (cardHeaderState) cardHeaderState.textContent = 'Sending';
                    const cardStatus = card.querySelector('.agent-mode-transition-status');
                    if (cardStatus) cardStatus.textContent = 'Sending ' + optionName + '…';
                    status.textContent = 'Sending ' + optionName + '…';
                    this.bridge.sendAgentPermissionResponse(requestId, optionId);
                });

                actions.appendChild(button);
            });
        } else {
            const error = document.createElement('div');
            error.className = 'agent-mode-transition-error';
            error.textContent = requestId
                ? 'The ACP Agent did not provide any response options.'
                : 'The ACP Agent did not provide a valid request identifier.';
            actions.appendChild(error);
        }

        const status = document.createElement('div');
        status.className = 'agent-composer-decision-status';
        status.setAttribute('role', 'status');
        status.setAttribute('aria-live', 'polite');
        status.textContent = interactive
            ? 'The Agent is waiting for your selection.'
            : 'The request cannot continue until the Agent provides valid ACP response data.';

        prompt.appendChild(header);
        prompt.appendChild(actions);
        prompt.appendChild(status);
        prompt.dataset.decisionState = interactive ? 'active' : 'error';
        prompt.hidden = false;
        inputRow.hidden = true;
        if (this.meta.attachmentStrip) this.meta.attachmentStrip.hidden = true;
        this._clearCommandHint();
        this._hideCommandMenu();

        if (composerHadFocus) {
            requestAnimationFrame(() => {
                const target = prompt.querySelector('.agent-composer-decision-option:not(:disabled)') || stopButton;
                target.focus();
            });
        }
};

AgentThreadManager.prototype._clearModeTransitionPrompt = function(requestId, restoreFocus) {
        const prompt = this.meta.modeTransitionPrompt;
        const inputRow = this.meta.inputRow;
        if (!prompt || !inputRow) return;
        if (requestId && this.activeModeTransitionRequestId !== requestId) return;

        const promptHadFocus = prompt.contains(document.activeElement);
        prompt.hidden = true;
        prompt.innerHTML = '';
        delete prompt.dataset.decisionState;
        inputRow.hidden = false;
        this.activeModeTransitionRequestId = '';
        this._syncAttachmentControls();

        if (restoreFocus !== false && promptHadFocus) {
            requestAnimationFrame(() => this.input.focus());
        }
};

AgentThreadManager.prototype._scrollModeTransitionToStart = function(card) {
        requestAnimationFrame(() => {
            if (!card?.isConnected) return;
            const threadRect = this.thread.getBoundingClientRect();
            const cardRect = card.getBoundingClientRect();
            this.thread.scrollTop += cardRect.top - threadRect.top - 12;
            this.autoScrollPinned = this._isNearBottom();
        });
};

AgentThreadManager.prototype._interruptModeTransition = function(text) {
        const requestId = this.activeModeTransitionRequestId;
        const prompt = this.meta.modeTransitionPrompt;
        if (!requestId && (!prompt || prompt.hidden)) return;

        const card = requestId
            ? this._findDecisionCard(requestId)
            : this.thread.querySelector('.agent-mode-transition[data-decision-state="active"]');
        if (card?.classList.contains('agent-mode-transition')) {
            card.dataset.decisionState = 'disabled';
            card.classList.remove('agent-mode-transition-sending');
            const decisionSection = card.querySelector('.agent-mode-transition-decision');
            if (decisionSection) decisionSection.hidden = false;
            const headerState = card.querySelector('.agent-mode-transition-header-state');
            if (headerState) headerState.textContent = 'Interrupted';
            const status = card.querySelector('.agent-mode-transition-status');
            if (status) status.textContent = text || 'This request is no longer active.';
        }

        this._clearModeTransitionPrompt(requestId || '');
};

AgentThreadManager.prototype._removeToolCardForModeTransition = function(toolCallId) {
        if (!toolCallId) return;

        const tracked = this.toolCards[toolCallId];
        let removedFromCurrentGroup = 0;
        if (tracked?.details) {
            const wasRunning = tracked.details.dataset.state === 'running';
            tracked.details.remove();
            delete this.toolCards[toolCallId];

            if (this._runToolCounts) {
                this._runToolCounts.total = Math.max(0, this._runToolCounts.total - 1);
                if (wasRunning) {
                    this._runToolCounts.running = Math.max(0, this._runToolCounts.running - 1);
                }
                this._updateRunGroupSummary();
                if (this._runToolCounts.total === 0 && this.currentRunGroup) {
                    this.currentRunGroup.style.display = 'none';
                }
            }
        }

        this.thread.querySelectorAll('[data-tool-id]').forEach((element) => {
            if (element.dataset.toolId !== toolCallId) return;
            if (element.closest('.agent-run-group') === this.currentRunGroup) {
                removedFromCurrentGroup++;
            }
            element.remove();
        });

        if (!tracked && removedFromCurrentGroup > 0 && this._runToolCounts) {
            this._runToolCounts.total = Math.max(0, this._runToolCounts.total - removedFromCurrentGroup);
            this._updateRunGroupSummary();
            if (this._runToolCounts.total === 0 && this.currentRunGroup) {
                this.currentRunGroup.style.display = 'none';
            }
        }
        if (this.currentToolCardId === toolCallId) this.currentToolCardId = null;
};
