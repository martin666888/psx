AgentThreadManager.prototype._createRunGroup = function(runId) {
        const details = document.createElement('details');
        details.className = 'agent-run-group';
        details.open = true;
        details.dataset.runId = runId;

        const header = document.createElement('summary');
        header.className = 'agent-run-group-header';
        const chevron = document.createElement('span');
        chevron.className = 'agent-run-group-chevron';
        const summarySpan = document.createElement('span');
        summarySpan.className = 'agent-run-group-summary';
        summarySpan.textContent = 'Tool activity';
        header.appendChild(chevron);
        header.appendChild(summarySpan);

        const body = document.createElement('div');
        body.className = 'agent-run-group-body';

        details.appendChild(header);
        details.appendChild(body);
        details.style.display = 'none';

        this._appendToCurrentHost(details);
        this.currentRunGroup = details;
        this.currentRunGroupBody = body;
        this.currentRunId = runId;
        this.toolCards = {};
        this._runToolCounts = { total: 0, running: 0 };
        this._scrollToBottom();
};

AgentThreadManager.prototype._ensureRunGroup = function(runId) {
        if (this.currentAssistant && (this.currentAssistant.dataset.raw || '').trim()) {
            this.currentAssistant = null;
        }

        if (!runId) {
            if (!this.currentRunGroup) this._createRunGroup('local-' + Date.now());
            return;
        }

        if (this.currentRunGroup && this.currentRunId === runId) {
            return;
        }

        this._finalizeRunGroup();
        this._createRunGroup(runId);
};

AgentThreadManager.prototype._updateRunGroupSummary = function() {
        if (!this.currentRunGroup || !this._runToolCounts) return;
        const { total, running } = this._runToolCounts;
        const label = total === 0 ? 'Tool activity'
            : 'Tool activity \u00b7 ' + total + ' call' + (total > 1 ? 's' : '')
                + (running > 0 ? ' \u00b7 ' + running + ' running' : '');
        this.currentRunGroup.querySelector('.agent-run-group-summary').textContent = label;
};

AgentThreadManager.prototype._setToolCardState = function(card, state) {
        if (!card?.details) return;
        const raw = String(state || 'done').toLowerCase();
        const normalized = ['done', 'completed', 'complete', 'success', 'succeeded'].includes(raw) ? 'done'
            : ['error', 'failed', 'failure'].includes(raw) ? 'error'
            : ['cancelled', 'canceled'].includes(raw) ? 'cancelled'
            : raw === 'fallback' ? 'fallback'
            : raw === 'running' ? 'running'
            : 'done';
        const labels = {
            running: 'Running',
            done: 'Done',
            error: 'Failed',
            cancelled: 'Cancelled',
            fallback: 'Needs terminal'
        };

        Array.from(card.details.classList)
            .filter((name) => name.startsWith('agent-tool-card-'))
            .forEach((name) => card.details.classList.remove(name));
        card.details.classList.add('agent-tool-card-' + normalized);
        card.details.dataset.state = normalized;
        const status = card.details.querySelector('.agent-tool-card-status');
        if (status) {
            status.textContent = labels[normalized];
            status.setAttribute('aria-label', 'Tool status: ' + labels[normalized]);
        }
};

AgentThreadManager.prototype._createToolCard = function(toolCallId, name, input, summary, state) {
        if (!this.currentRunGroupBody) return;

        const existing = this.toolCards[toolCallId];
        if (existing) {
            if (input && !existing.pre.textContent) {
                existing.pre.textContent = input;
            }
            return existing;
        }

        const details = document.createElement('details');
        details.className = 'agent-tool-card';
        details.open = true;
        details.dataset.toolId = toolCallId;

        const header = document.createElement('summary');
        header.className = 'agent-tool-card-header';
        const summarySpan = document.createElement('span');
        summarySpan.className = 'agent-tool-card-summary';
        summarySpan.textContent = summary || name;
        const statusSpan = document.createElement('span');
        statusSpan.className = 'agent-tool-card-status';
        header.appendChild(summarySpan);
        header.appendChild(statusSpan);

        const body = document.createElement('div');
        body.className = 'agent-tool-card-body';
        const pre = document.createElement('pre');
        pre.className = 'agent-tool-card-content';
        pre.textContent = input;
        body.appendChild(pre);

        details.appendChild(header);
        details.appendChild(body);
        this.currentRunGroupBody.appendChild(details);

        this.toolCards[toolCallId] = { details, body, pre };
        this._setToolCardState(this.toolCards[toolCallId], state);
        this.currentToolCardId = toolCallId;

        if (this._runToolCounts) {
            this._runToolCounts.total++;
            if (details.dataset.state === 'running') this._runToolCounts.running++;
            this._updateRunGroupSummary();
        }

        if (this.currentRunGroup) {
            this.currentRunGroup.style.display = '';
        }
        this._scrollToBottom();
        return this.toolCards[toolCallId];
};

AgentThreadManager.prototype._resolveToolCard = function(event) {
        const requestedId = event.toolCallId || this.currentToolCardId || '';
        if (requestedId && this.toolCards[requestedId]) {
            return { toolCallId: requestedId, card: this.toolCards[requestedId] };
        }

        this._ensureRunGroup(event.runId || '');
        const toolCallId = requestedId || ('orphan-' + Date.now());
        const card = this._createToolCard(
            toolCallId,
            event.name || 'Tool',
            event.input || '',
            event.summary || event.name || 'Tool output',
            'running'
        );

        return card ? { toolCallId, card } : null;
};

AgentThreadManager.prototype._finalizeRunGroup = function() {
        if (!this.currentRunGroup) return;
        if (this._runToolCounts && this._runToolCounts.total === 0) {
            this.currentRunGroup.remove();
        } else if (!this.currentRunGroup.classList.contains('agent-run-group-error')) {
            this.currentRunGroup.removeAttribute('open');
        }
        this.currentRunGroup = null;
        this.currentRunGroupBody = null;
        this.currentRunId = null;
        this.toolCards = {};
        this.currentToolCardId = null;
};

AgentThreadManager.prototype._createHistoryGroup = function(runId) {
        const details = document.createElement('details');
        details.className = 'agent-run-group';
        details.dataset.runId = runId;

        const header = document.createElement('summary');
        header.className = 'agent-run-group-header';
        const chevron = document.createElement('span');
        chevron.className = 'agent-run-group-chevron';
        const summarySpan = document.createElement('span');
        summarySpan.className = 'agent-run-group-summary';
        summarySpan.textContent = 'Tool activity';
        header.appendChild(chevron);
        header.appendChild(summarySpan);

        const body = document.createElement('div');
        body.className = 'agent-run-group-body';

        details.appendChild(header);
        details.appendChild(body);
        this._appendToCurrentHost(details);

        this._historyGroup = details;
        this._historyGroupBody = body;
        this._historyToolCounts = { total: 0 };
};

AgentThreadManager.prototype._appendHistoryToolCard = function(msg) {
        if (!this._historyGroupBody) return;

        const details = document.createElement('details');
        details.className = 'agent-tool-card';
        details.dataset.toolId = msg.toolCallId || '';

        const header = document.createElement('summary');
        header.className = 'agent-tool-card-header';
        const summarySpan = document.createElement('span');
        summarySpan.className = 'agent-tool-card-summary';
        summarySpan.textContent = msg.summary || msg.name || 'Tool';
        const statusSpan = document.createElement('span');
        statusSpan.className = 'agent-tool-card-status';
        header.appendChild(summarySpan);
        header.appendChild(statusSpan);

        const body = document.createElement('div');
        body.className = 'agent-tool-card-body';
        const pre = document.createElement('pre');
        pre.className = 'agent-tool-card-content';
        pre.textContent = msg.toolOutput || msg.text || '';
        body.appendChild(pre);

        details.appendChild(header);
        details.appendChild(body);
        this._historyGroupBody.appendChild(details);
        this._setToolCardState({ details, body, pre }, msg.toolStatus || 'done');

        if (this._historyToolCounts) {
            this._historyToolCounts.total++;
            var total = this._historyToolCounts.total;
            var label = 'Tool activity \u00b7 ' + total + ' call' + (total > 1 ? 's' : '');
            this._historyGroup.querySelector('.agent-run-group-summary').textContent = label;
        }
};

AgentThreadManager.prototype._finalizeHistoryGroup = function() {
        this._historyGroup = null;
        this._historyGroupBody = null;
        this._historyToolCounts = null;
};

AgentThreadManager.prototype._appendTool = function(name, text, state) {
        const card = document.createElement('section');
        card.className = 'agent-tool agent-tool-' + state;
        card.dataset.state = state;
        const header = document.createElement('div');
        header.className = 'agent-tool-header';
        const title = document.createElement('span');
        title.textContent = name;
        const status = document.createElement('span');
        status.className = 'agent-tool-card-status';
        status.textContent = state === 'error' ? 'Failed' : state === 'fallback' ? 'Needs terminal' : 'Done';
        header.appendChild(title);
        header.appendChild(status);
        const body = document.createElement('pre');
        body.textContent = text;
        card.appendChild(header);
        card.appendChild(body);
        this._appendToCurrentHost(card);
        this._scrollToBottom();
        return body;
};

AgentThreadManager.prototype._appendToolDelta = function(name, text) {
        if (!this.currentToolBody) {
            this.currentToolBody = this._appendTool(name, '', 'output');
        }

        this.currentToolBody.textContent += text;
        this._scrollToBottom();
};

AgentThreadManager.prototype._finishTool = function() {
        if (!this.currentToolBody) {
            this._appendTool('Tool', 'Finished.', 'done');
            return;
        }

        if (this.currentToolBody.textContent.trim()) {
            this.currentToolBody.textContent += '\n\nFinished.';
        } else {
            this.currentToolBody.textContent = 'Finished.';
        }

        this.currentToolBody = null;
        this._scrollToBottom();
};
