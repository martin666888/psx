AgentThreadManager.prototype._appendDecision = function(event, kind) {
        const details = document.createElement('details');
        details.className = 'agent-decision agent-decision-' + kind;
        details.open = true;
        details.dataset.decisionState = 'active';
        if (event.requestId) {
            details.dataset.requestId = event.requestId;
        }

        const header = document.createElement('summary');
        header.className = 'agent-decision-header';

        const content = document.createElement('div');
        content.className = 'agent-decision-header-content';

        const titleRow = document.createElement('div');
        titleRow.className = 'agent-decision-header-title-row';

        const chevron = document.createElement('span');
        chevron.className = 'agent-decision-chevron';

        const titleSpan = document.createElement('span');
        titleSpan.className = 'agent-decision-header-title';
        titleSpan.textContent = event.title || (kind === 'permission' ? 'Permission request' : this.assistantName + ' question');

        const subtitleSpan = document.createElement('span');
        subtitleSpan.className = 'agent-decision-header-subtitle';
        subtitleSpan.textContent = kind === 'permission'
            ? this.assistantName + ' Agent needs your approval before continuing.'
            : this.assistantName + ' Agent is waiting for your answer.';

        titleRow.appendChild(chevron);
        titleRow.appendChild(titleSpan);
        content.appendChild(titleRow);
        content.appendChild(subtitleSpan);
        header.appendChild(content);

        const body = document.createElement('div');
        body.className = 'agent-decision-body';

        const rawInputDetails = document.createElement('details');
        rawInputDetails.className = 'agent-decision-raw-input';

        const rawInputSummary = document.createElement('summary');
        rawInputSummary.className = 'agent-decision-raw-input-summary';
        rawInputSummary.textContent = 'Raw Input';

        const rawInputBody = document.createElement('pre');
        rawInputBody.className = 'agent-decision-raw-input-content';
        rawInputBody.textContent = event.text || '{}';

        rawInputDetails.appendChild(rawInputSummary);
        rawInputDetails.appendChild(rawInputBody);

        const actions = document.createElement('div');
        actions.className = 'agent-decision-actions';

        const hasStructuredOptions = Array.isArray(event.options);
        const options = hasStructuredOptions
            ? event.options
            : [
                { name: kind === 'permission' ? 'Allow' : 'Yes', optionId: kind === 'permission' ? 'allow' : 'yes' },
                { name: kind === 'permission' ? 'Reject' : 'No', optionId: kind === 'permission' ? 'reject' : 'no' }
            ];

        if (options.length === 0) {
            const error = document.createElement('div');
            error.className = 'agent-decision-status agent-decision-options-error';
            error.textContent = 'The Agent did not provide any response options.';
            actions.appendChild(error);
        }

        options.forEach((option) => {
            const optionId = option.optionId || '';
            const optionName = option.name || optionId || 'Select';

            const btn = this._decisionButton(optionName, () => {
                if (details.dataset.decisionState !== 'active') return;
                details.open = false;

                if (kind === 'permission') {
                    Bridge.sendAgentPermissionResponse(event.requestId || '', optionId);
                } else {
                    Bridge.sendAgentQuestionResponse(event.requestId || '', optionId);
                }

                this._disableDecisionCard(details, 'Response sent.');
            });

            const semanticClass = this._decisionOptionClass(option);
            if (semanticClass) btn.classList.add(semanticClass);

            actions.appendChild(btn);
        });

        body.appendChild(rawInputDetails);
        body.appendChild(actions);
        details.appendChild(header);
        details.appendChild(body);

        this._appendToCurrentHost(details);
        this._scrollToBottom();
};

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
            status.textContent = 'Stopping the current Agent run\u2026';
            prompt.querySelectorAll('.agent-composer-decision-option').forEach((button) => {
                button.disabled = true;
            });
            Bridge.sendAgentCommand('stop');
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
                    if (cardStatus) cardStatus.textContent = 'Sending ' + optionName + '\u2026';
                    status.textContent = 'Sending ' + optionName + '\u2026';
                    Bridge.sendAgentPermissionResponse(requestId, optionId);
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

AgentThreadManager.prototype._decisionOptionClass = function(option) {
        const kind = String(option?.kind || '').toLowerCase();
        if (kind === 'allow_once') return 'agent-btn-allow';
        if (kind === 'allow_always') return 'agent-btn-always-allow';
        if (kind === 'reject_once' || kind === 'reject_always') return 'agent-btn-reject';

        const optionId = String(option?.optionId || '').toLowerCase();
        if (optionId === 'allow' || optionId === 'yes') return 'agent-btn-allow';
        if (optionId === 'always_allow') return 'agent-btn-always-allow';
        if (optionId === 'reject' || optionId === 'no') return 'agent-btn-reject';
        return '';
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

AgentThreadManager.prototype._resolvePermission = function(event) {
        const requestId = event.requestId || '';
        const card = this._findDecisionCard(requestId);
        if (!card) {
            this._clearModeTransitionPrompt(requestId);
            return;
        }

        if (!card.classList.contains('agent-mode-transition')) {
            const text = event.optionName ? 'Selected: ' + event.optionName : 'Response sent.';
            if (card.dataset.decisionState === 'disabled') {
                const status = card.querySelector('.agent-decision-status');
                if (status) status.textContent = text;
            } else {
                this._disableDecisionCard(card, text);
            }
            return;
        }

        card.dataset.decisionState = 'disabled';
        card.classList.remove('agent-mode-transition-sending');
        const decisionSection = card.querySelector('.agent-mode-transition-decision');
        if (decisionSection) decisionSection.hidden = false;
        const selectedOptionId = event.optionId || '';
        card.querySelectorAll('.agent-mode-transition-option').forEach((button) => {
            button.disabled = true;
            button.classList.remove('agent-mode-transition-option-pending');
            const selected = button.dataset.optionId === selectedOptionId;
            button.classList.toggle('agent-mode-transition-option-selected', selected);
            button.setAttribute('aria-pressed', selected ? 'true' : 'false');
        });

        const headerState = card.querySelector('.agent-mode-transition-header-state');
        if (headerState) headerState.textContent = 'Selected';
        const status = card.querySelector('.agent-mode-transition-status');
        if (status) status.textContent = event.optionName ? 'Selected: ' + event.optionName : 'Selection recorded.';
        this._clearModeTransitionPrompt(requestId);
};

AgentThreadManager.prototype._appendElicitation = function(event) {
        const card = document.createElement('section');
        card.className = 'agent-decision agent-decision-elicitation';
        card.dataset.decisionState = 'active';
        if (event.requestId) {
            card.dataset.requestId = event.requestId;
        }

        const title = document.createElement('div');
        title.className = 'agent-decision-title';
        title.textContent = this.assistantName + ' Agent needs input';

        const subtitle = document.createElement('div');
        subtitle.className = 'agent-decision-subtitle';
        subtitle.textContent = event.message || 'Provide the requested information to continue.';

        const form = document.createElement('form');
        form.className = 'agent-elicitation-form';
        form.noValidate = true;

        const schema = event.schema || {};
        const properties = schema.properties || {};
        const required = new Set(Array.isArray(schema.required) ? schema.required : []);
        const controls = [];
        const fieldEntries = Object.entries(properties).map(([name, property], index) => ({
            name,
            property: property || {},
            index,
            isSupplement: this._isSupplementalElicitationField(name, property || {}, required.has(name))
        }));

        fieldEntries
            .sort((a, b) => Number(a.isSupplement) - Number(b.isSupplement) || a.index - b.index)
            .forEach((entry) => {
                const field = this._createElicitationField(entry.name, entry.property, required.has(entry.name), entry.isSupplement);
                controls.push(field);
                form.appendChild(field.row);
            });

        if (event.mode === 'url' && event.url) {
            const urlRow = document.createElement('div');
            urlRow.className = 'agent-elicitation-url';
            const safeUrl = this._safeHref(event.url);
            if (safeUrl) {
                const link = document.createElement('a');
                link.href = safeUrl;
                link.target = '_blank';
                link.rel = 'noreferrer';
                link.textContent = event.url;
                urlRow.appendChild(link);
            } else {
                urlRow.textContent = event.url;
            }
            form.appendChild(urlRow);
        }

        if (controls.length === 0 && event.mode !== 'url') {
            const field = this._createElicitationField('response', { type: 'string', title: 'Response' }, true, false);
            controls.push(field);
            form.appendChild(field.row);
        }

        const actions = document.createElement('div');
        actions.className = 'agent-decision-actions agent-elicitation-actions';

        actions.appendChild(this._decisionButton('Continue', () => {
            if (card.dataset.decisionState !== 'active') return;

            let valid = true;
            const content = {};
            controls.forEach((field) => {
                if (!field.validate()) {
                    valid = false;
                }
            });

            if (!valid) {
                return;
            }

            controls.forEach((field) => {
                const value = field.read();
                if (value !== undefined) {
                    content[field.name] = value;
                }
            });

            Bridge.sendAgentElicitationResponse(event.requestId || '', JSON.stringify({
                action: 'accept',
                content
            }));
            this._disableDecisionCard(card, 'Response sent.');
        }, 'agent-btn-primary'));

        actions.appendChild(this._decisionButton('Decline', () => {
            if (card.dataset.decisionState !== 'active') return;
            Bridge.sendAgentElicitationResponse(event.requestId || '', JSON.stringify({ action: 'decline' }));
            this._disableDecisionCard(card, 'Declined.');
        }, 'agent-btn-subtle'));

        actions.appendChild(this._decisionButton('Cancel', () => {
            if (card.dataset.decisionState !== 'active') return;
            Bridge.sendAgentElicitationResponse(event.requestId || '', JSON.stringify({ action: 'cancel' }));
            this._disableDecisionCard(card, 'Cancelled.');
        }, 'agent-btn-subtle'));

        card.appendChild(title);
        card.appendChild(subtitle);
        card.appendChild(form);
        card.appendChild(actions);
        this._appendToCurrentHost(card);
        this._scrollToBottom();
};

AgentThreadManager.prototype._createElicitationField = function(name, property, required, isSupplement) {
        const row = document.createElement('div');
        row.className = 'agent-elicitation-field';
        if (isSupplement) {
            row.classList.add('agent-elicitation-field-supplement');
        }

        const label = document.createElement('div');
        label.className = 'agent-elicitation-label';
        label.textContent = (property.title || name) + (required ? ' *' : '');
        row.appendChild(label);

        const description = property.description || '';
        const error = document.createElement('div');
        error.className = 'agent-elicitation-error';
        error.hidden = true;

        const options = this._readElicitationOptions(property);
        const isArray = property.type === 'array' && property.items;
        let read;
        let validate;
        let clearError;

        const setError = (message) => {
            row.classList.toggle('agent-elicitation-field-error', !!message);
            error.textContent = message || '';
            error.hidden = !message;
        };
        clearError = () => setError('');

        if (property.type === 'boolean') {
            const labelRow = document.createElement('label');
            labelRow.className = 'agent-elicitation-checkbox';
            const control = document.createElement('input');
            control.type = 'checkbox';
            control.checked = !!property.default;
            control.addEventListener('change', clearError);
            labelRow.appendChild(control);
            row.appendChild(labelRow);
            read = () => control.checked;
            validate = () => true;
        } else if (options.length && !isArray) {
            let selected = this._defaultOptionValue(options, property.default, true);
            const list = document.createElement('div');
            list.className = 'agent-elicitation-option-list';
            list.setAttribute('role', 'radiogroup');
            list.setAttribute('aria-label', property.title || name);

            const sync = () => {
                list.querySelectorAll('.agent-elicitation-option').forEach((button) => {
                    const isSelected = button._optionValue === selected;
                    button.classList.toggle('agent-elicitation-option-selected', isSelected);
                    button.setAttribute('aria-checked', String(isSelected));
                });
            };

            options.forEach((option) => {
                const button = this._createElicitationOptionButton(option, false);
                button._optionValue = option.value;
                button.addEventListener('click', () => {
                    selected = option.value;
                    clearError();
                    sync();
                });
                list.appendChild(button);
            });

            row.appendChild(list);
            sync();
            read = () => selected;
            validate = () => {
                if (required && (selected === undefined || selected === null || selected === '')) {
                    setError('Choose an option to continue.');
                    return false;
                }
                clearError();
                return true;
            };
        } else if (isArray) {
            const arrayOptions = this._readElicitationOptions(property.items || {});
            const selected = new Set(Array.isArray(property.default) ? property.default : []);
            const list = document.createElement('div');
            list.className = 'agent-elicitation-option-list';
            list.setAttribute('role', 'group');
            list.setAttribute('aria-label', property.title || name);

            const sync = () => {
                list.querySelectorAll('.agent-elicitation-option').forEach((button) => {
                    const isSelected = selected.has(button._optionValue);
                    button.classList.toggle('agent-elicitation-option-selected', isSelected);
                    button.setAttribute('aria-pressed', String(isSelected));
                });
            };

            arrayOptions.forEach((option) => {
                const button = this._createElicitationOptionButton(option, true);
                button._optionValue = option.value;
                button.addEventListener('click', () => {
                    if (selected.has(option.value)) {
                        selected.delete(option.value);
                    } else {
                        selected.add(option.value);
                    }
                    clearError();
                    sync();
                });
                list.appendChild(button);
            });

            row.appendChild(list);
            sync();
            read = () => Array.from(selected);
            validate = () => {
                if (required && selected.size === 0) {
                    setError('Choose at least one option to continue.');
                    return false;
                }
                clearError();
                return true;
            };
        } else {
            const control = property.type === 'integer' || property.type === 'number'
                ? document.createElement('input')
                : document.createElement('textarea');

            if (control.tagName === 'INPUT') {
                control.type = 'number';
                if (property.type === 'integer') control.step = '1';
                read = () => {
                    const raw = control.value.trim();
                    if (raw === '') return undefined;
                    return Number(raw);
                };
                validate = () => {
                    const raw = control.value.trim();
                    if (required && raw === '') {
                        setError('Enter a number to continue.');
                        return false;
                    }
                    if (raw !== '') {
                        const numeric = Number(raw);
                        if (!Number.isFinite(numeric) || (property.type === 'integer' && !Number.isInteger(numeric))) {
                            setError(property.type === 'integer' ? 'Enter a whole number.' : 'Enter a valid number.');
                            return false;
                        }
                    }
                    clearError();
                    return true;
                };
            } else {
                control.rows = isSupplement ? 2 : 3;
                read = () => {
                    const value = control.value;
                    return value === '' && !required ? undefined : value;
                };
                validate = () => {
                    if (required && control.value.trim() === '') {
                        setError('Enter a response to continue.');
                        return false;
                    }
                    clearError();
                    return true;
                };
            }

            if (property.default !== undefined && property.default !== null) {
                control.value = property.default;
            }

            control.addEventListener('input', clearError);
            row.appendChild(control);
        }

        if (description) {
            const hint = document.createElement('small');
            hint.textContent = description;
            row.appendChild(hint);
        }

        row.appendChild(error);

        return { name, row, read, validate };
};

AgentThreadManager.prototype._readElicitationOptions = function(property) {
        if (!property || typeof property !== 'object') return [];

        if (Array.isArray(property.oneOf)) {
            return property.oneOf
                .filter((item) => item && Object.prototype.hasOwnProperty.call(item, 'const'))
                .map((item) => this._normalizeElicitationOption(item.const, item.title, item.description));
        }

        if (Array.isArray(property.anyOf)) {
            return property.anyOf
                .filter((item) => item && Object.prototype.hasOwnProperty.call(item, 'const'))
                .map((item) => this._normalizeElicitationOption(item.const, item.title, item.description));
        }

        if (Array.isArray(property.enum)) {
            return property.enum.map((value) => this._normalizeElicitationOption(value, null, null));
        }

        return [];
};

AgentThreadManager.prototype._normalizeElicitationOption = function(value, title, description) {
        const fallback = value === null || value === undefined ? '' : String(value);
        return {
            value,
            title: title === undefined || title === null || title === '' ? fallback : String(title),
            description: description === undefined || description === null || description === '' ? '' : String(description)
        };
};

AgentThreadManager.prototype._defaultOptionValue = function(options, defaultValue, selectFirst) {
        if (defaultValue !== undefined && defaultValue !== null) {
            const exact = options.find((option) => option.value === defaultValue);
            if (exact) return exact.value;
        }
        return selectFirst && options.length ? options[0].value : undefined;
};

AgentThreadManager.prototype._createElicitationOptionButton = function(option, multi) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'agent-elicitation-option';
        button.setAttribute('role', multi ? 'button' : 'radio');

        const title = document.createElement('span');
        title.className = 'agent-elicitation-option-title';
        title.textContent = option.title;
        button.appendChild(title);

        if (option.description) {
            const description = document.createElement('small');
            description.className = 'agent-elicitation-option-description';
            description.textContent = option.description;
            button.appendChild(description);
        }

        return button;
};

AgentThreadManager.prototype._isSupplementalElicitationField = function(name, property, required) {
        if (required || property.type !== 'string') return false;
        const text = ((name || '') + ' ' + (property.title || '')).toLowerCase();
        return /\b(other|custom|response|answer|comment|note|details?)\b/.test(text);
};

AgentThreadManager.prototype._findDecisionCard = function(requestId) {
        if (!requestId) return null;
        return Array.from(this.thread.querySelectorAll('[data-request-id]'))
            .find((element) => element.dataset.requestId === requestId) || null;
};

AgentThreadManager.prototype._cancelDecision = function(requestId, text) {
        const card = this._findDecisionCard(requestId);
        if (!card) {
            this._clearModeTransitionPrompt(requestId);
            return;
        }

        if (card.classList.contains('agent-mode-transition')) {
            card.dataset.decisionState = 'disabled';
            card.classList.remove('agent-mode-transition-sending');
            const decisionSection = card.querySelector('.agent-mode-transition-decision');
            if (decisionSection) decisionSection.hidden = false;
            card.querySelectorAll('.agent-mode-transition-option').forEach((button) => {
                button.disabled = true;
                button.classList.remove('agent-mode-transition-option-pending');
            });
            const headerState = card.querySelector('.agent-mode-transition-header-state');
            if (headerState) headerState.textContent = 'Cancelled';
            const status = card.querySelector('.agent-mode-transition-status');
            if (status) status.textContent = text || 'Request cancelled.';
            this._clearModeTransitionPrompt(requestId);
            return;
        }

        this._disableDecisionCard(card, text || 'Request cancelled.');
};

AgentThreadManager.prototype._disableDecisionCard = function(card, text) {
        if (!card) return;
        if (card.dataset.decisionState !== 'disabled') {
            card.dataset.decisionState = 'disabled';
            card.classList.add('agent-decision-disabled');
            card.querySelectorAll('button, input, textarea, select').forEach((control) => {
                control.disabled = true;
            });
        }

        let status = card.querySelector('.agent-decision-status');
        if (!status) {
            status = document.createElement('div');
            status.className = 'agent-decision-status';
            card.appendChild(status);
        }
        status.textContent = text || 'Request closed.';
};

AgentThreadManager.prototype._decisionButton = function(label, action, className) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = label;
        if (className) {
            button.classList.add(className);
        }
        button.addEventListener('click', () => {
            const card = button.closest('[data-decision-state]');
            if (card && card.dataset.decisionState !== 'active') return;
            action();
        });
        return button;
};
