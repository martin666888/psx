AgentThreadManager.prototype._resetPlanState = function() {
        this.currentPlanCard = null;
        this.currentPlanBody = null;
        this.currentPlanRunId = null;
        this._clearPlanUnread();

        if (!this.planPanel) return;

        this.planPanel.innerHTML = '';

        const empty = document.createElement('div');
        empty.className = 'agent-plan-empty';
        empty.textContent = 'No active plan';

        this.planPanel.appendChild(empty);
        this.currentPlanBody = this.planPanel;
};

AgentThreadManager.prototype._upsertPlan = function(event) {
        const runId = event.runId || this.currentRunId || 'plan';
        const entries = this._normalizePlanEntries(event.entries || []);
        const effectiveEntries = entries.length > 0
            ? entries
            : this._readRawPlanEntries(event.text || '');

        if (effectiveEntries.length === 0 && this._isRawPlanPayload(event.text || '')) {
            return;
        }

        if (!this.planPanel) return;

        if (!this.currentPlanBody || this.currentPlanRunId !== runId) {
            this._createPlanPanelContent(runId);
        }

        this._renderPlanEntries(effectiveEntries, event.text || '');
        this._markPlanUnread();
};

AgentThreadManager.prototype._createPlanPanelContent = function(runId) {
        if (!this.planPanel) return;

        this.planPanel.innerHTML = '';

        this.currentPlanCard = this.planPanel;
        this.currentPlanBody = this.planPanel;
        this.currentPlanRunId = runId;
};

AgentThreadManager.prototype._renderPlanEntries = function(entries, fallbackText) {
        if (!this.currentPlanBody) return;

        const normalized = this._normalizePlanEntries(entries);

        this.currentPlanBody.innerHTML = '';
        if (normalized.length === 0) {
            const fallback = document.createElement('pre');
            fallback.className = 'agent-plan-fallback';
            fallback.textContent = fallbackText || 'No plan items.';
            this.currentPlanBody.appendChild(fallback);
            return;
        }

        const list = document.createElement('ol');
        list.className = 'agent-plan-list';
        normalized.forEach((entry) => {
            const item = document.createElement('li');
            const statusClass = this._planStatusClass(entry.status);
            item.className = 'agent-plan-item ' + statusClass;
            if (entry.priority) {
                item.dataset.priority = entry.priority;
            }

            const marker = document.createElement('span');
            marker.className = 'agent-plan-marker';
            marker.textContent = this._planStatusMarker(entry.status);

            const content = document.createElement('span');
            content.className = 'agent-plan-content';
            content.textContent = entry.content;

            item.appendChild(marker);
            item.appendChild(content);
            list.appendChild(item);
        });

        this.currentPlanBody.appendChild(list);
};

AgentThreadManager.prototype._normalizePlanEntries = function(entries) {
        return (Array.isArray(entries) ? entries : [])
            .filter((entry) => entry && typeof entry.content === 'string' && entry.content.trim())
            .map((entry) => ({
                content: entry.content.trim(),
                status: String(entry.status || '').trim(),
                priority: String(entry.priority || '').trim()
            }));
};

AgentThreadManager.prototype._isRawPlanPayload = function(text) {
        const value = String(text || '').trim();
        if (!value.startsWith('{') || !value.endsWith('}')) {
            return false;
        }

        try {
            const payload = JSON.parse(value);
            return payload
                && payload.sessionUpdate === 'plan'
                && Array.isArray(payload.entries);
        } catch {
            return false;
        }
};

AgentThreadManager.prototype._readRawPlanEntries = function(text) {
        const value = String(text || '').trim();
        if (!value.startsWith('{') || !value.endsWith('}')) {
            return [];
        }

        try {
            const payload = JSON.parse(value);
            if (!payload || payload.sessionUpdate !== 'plan' || !Array.isArray(payload.entries)) {
                return [];
            }

            return this._normalizePlanEntries(payload.entries.map((entry) => ({
                content: entry?.content || entry?.title || '',
                status: entry?.status || '',
                priority: entry?.priority || ''
            })));
        } catch {
            return [];
        }
};

AgentThreadManager.prototype._planStatusClass = function(status) {
        const value = String(status || '').toLowerCase();
        return value === 'completed'
            ? 'agent-plan-item-completed'
            : 'agent-plan-item-pending';
};

AgentThreadManager.prototype._planStatusMarker = function(status) {
        const value = String(status || '').toLowerCase();
        return value === 'completed' ? '\u2713' : '\u25cb';
};
