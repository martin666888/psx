// Decision cards: ordinary permission/question prompts plus the shared card
// infrastructure (_decisionButton, _findDecisionCard, _cancelDecision,
// _disableDecisionCard, _decisionOptionClass, _resolvePermission).
// Load order contract: this file must load before modeTransition.js and
// elicitation.js, which call the shared helpers defined here.

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
                    this.bridge.sendAgentPermissionResponse(event.requestId || '', optionId);
                } else {
                    this.bridge.sendAgentQuestionResponse(event.requestId || '', optionId);
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
