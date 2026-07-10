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
        this._runToolCounts = { total: 0, byName: {} };
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
        const { total, byName } = this._runToolCounts;
        const parts = Object.entries(byName)
            .sort((a, b) => b[1] - a[1])
            .map(([name, count]) => count + ' ' + name);
        const label = total === 0 ? 'Tool activity'
            : total + ' tool call' + (total > 1 ? 's' : '') + (parts.length ? ' \u00b7 ' + parts.join(' \u00b7 ') : '');
        this.currentRunGroup.querySelector('.agent-run-group-summary').textContent = label;
};

AgentThreadManager.prototype._createToolCard = function(toolCallId, name, input, summary, state) {
        if (!this.currentRunGroupBody) return;

        const details = document.createElement('details');
        details.className = 'agent-tool-card agent-tool-card-' + state;
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
        this.currentToolCardId = toolCallId;

        if (this._runToolCounts) {
            this._runToolCounts.total++;
            this._runToolCounts.byName[name] = (this._runToolCounts.byName[name] || 0) + 1;
            this._updateRunGroupSummary();
        }

        if (this.currentRunGroup) {
            this.currentRunGroup.style.display = '';
        }
        this._scrollToBottom();
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
        this._historyToolCounts = { total: 0, byName: {} };
};

AgentThreadManager.prototype._appendHistoryToolCard = function(msg) {
        if (!this._historyGroupBody) return;

        const details = document.createElement('details');
        details.className = 'agent-tool-card agent-tool-card-' + (msg.toolStatus || 'done');
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

        if (this._historyToolCounts) {
            this._historyToolCounts.total++;
            var name = msg.name || 'Tool';
            this._historyToolCounts.byName[name] = (this._historyToolCounts.byName[name] || 0) + 1;
            var total = this._historyToolCounts.total;
            var byName = this._historyToolCounts.byName;
            var parts = Object.entries(byName)
                .sort((a, b) => b[1] - a[1])
                .map(([n, c]) => c + ' ' + n);
            var label = total + ' tool call' + (total > 1 ? 's' : '') + (parts.length ? ' \u00b7 ' + parts.join(' \u00b7 ') : '');
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
        const header = document.createElement('div');
        header.className = 'agent-tool-header';
        header.textContent = name;
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
