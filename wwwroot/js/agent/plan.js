AgentThreadManager.prototype._initializePlanPanel = function() {
        this.planPanelMinWidth = 260;
        this.planPanelMaxWidth = 380;
        this.planPanelDefaultWidth = 300;
        this.planPanelStorageKey = 'psx.agent.planPanelWidth';

        this.planPanelWidth = this._readPlanPanelWidth();
        this._applyPlanPanelWidth(this.planPanelWidth);
        this._resetPlanState();
        this._wirePlanPanelResizer();
};

AgentThreadManager.prototype._resetPlanState = function() {
        this.currentPlanCard = null;
        this.currentPlanBody = null;
        this.currentPlanRunId = null;

        if (!this.planPanel) return;

        this.planPanel.innerHTML = '';

        const header = document.createElement('div');
        header.className = 'agent-plan-panel-header';
        header.textContent = 'Plan';

        const body = document.createElement('div');
        body.className = 'agent-plan-panel-body';

        const empty = document.createElement('div');
        empty.className = 'agent-plan-empty';
        empty.textContent = 'No active plan';

        body.appendChild(empty);
        this.planPanel.appendChild(header);
        this.planPanel.appendChild(body);

        this.currentPlanBody = body;
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
};

AgentThreadManager.prototype._createPlanPanelContent = function(runId) {
        if (!this.planPanel) return;

        this.planPanel.innerHTML = '';

        const header = document.createElement('div');
        header.className = 'agent-plan-panel-header';
        header.textContent = 'Plan';

        const body = document.createElement('div');
        body.className = 'agent-plan-panel-body';

        this.planPanel.appendChild(header);
        this.planPanel.appendChild(body);

        this.currentPlanCard = this.planPanel;
        this.currentPlanBody = body;
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

AgentThreadManager.prototype._wirePlanPanelResizer = function() {
        if (!this.planResizer || !this.planPanel) return;

        this.planResizer.addEventListener('pointerdown', (event) => {
            if (event.button !== 0) return;

            this.isResizingPlanPanel = true;
            this.planResizer.setPointerCapture(event.pointerId);
            document.body.classList.add('agent-plan-resizing');
            event.preventDefault();
        });

        this.planResizer.addEventListener('pointermove', (event) => {
            if (!this.isResizingPlanPanel) return;

            const rect = this.panel.getBoundingClientRect();
            const width = rect.right - event.clientX - this.planResizer.offsetWidth;
            this._setPlanPanelWidth(width, true);
        });

        const endResize = (event) => {
            if (!this.isResizingPlanPanel) return;

            this.isResizingPlanPanel = false;
            document.body.classList.remove('agent-plan-resizing');
            try {
                this.planResizer.releasePointerCapture(event.pointerId);
            } catch {
                // Pointer capture may already be gone if the window lost focus.
            }
            this._savePlanPanelWidth();
        };

        this.planResizer.addEventListener('pointerup', endResize);
        this.planResizer.addEventListener('pointercancel', endResize);

        this.planResizer.addEventListener('keydown', (event) => {
            const step = event.shiftKey ? 40 : 16;
            if (event.key === 'ArrowLeft') {
                this._setPlanPanelWidth(this.planPanelWidth + step, false);
                event.preventDefault();
            } else if (event.key === 'ArrowRight') {
                this._setPlanPanelWidth(this.planPanelWidth - step, false);
                event.preventDefault();
            } else if (event.key === 'Home') {
                this._setPlanPanelWidth(this.planPanelMaxWidth, false);
                event.preventDefault();
            } else if (event.key === 'End') {
                this._setPlanPanelWidth(this.planPanelMinWidth, false);
                event.preventDefault();
            }
        });
};

AgentThreadManager.prototype._readPlanPanelWidth = function() {
        try {
            const stored = window.localStorage?.getItem(this.planPanelStorageKey);
            const width = Number(stored);
            if (Number.isFinite(width)) {
                return this._clampPlanPanelWidth(width);
            }
        } catch {
            // localStorage can be unavailable in constrained WebView profiles.
        }

        return this.planPanelDefaultWidth;
};

AgentThreadManager.prototype._setPlanPanelWidth = function(width, deferSave) {
        this.planPanelWidth = this._clampPlanPanelWidth(width);
        this._applyPlanPanelWidth(this.planPanelWidth);
        if (!deferSave) {
            this._savePlanPanelWidth();
        }
};

AgentThreadManager.prototype._applyPlanPanelWidth = function(width) {
        if (!this.panel) return;

        this.panel.style.setProperty('--agent-plan-panel-width', this._clampPlanPanelWidth(width) + 'px');
};

AgentThreadManager.prototype._savePlanPanelWidth = function() {
        try {
            window.localStorage?.setItem(this.planPanelStorageKey, String(this.planPanelWidth));
        } catch {
            // Width persistence is nice-to-have; resizing should still work.
        }
};

AgentThreadManager.prototype._clampPlanPanelWidth = function(width) {
        const numeric = Number(width);
        if (!Number.isFinite(numeric)) {
            return this.planPanelDefaultWidth;
        }

        return Math.max(this.planPanelMinWidth, Math.min(this.planPanelMaxWidth, Math.round(numeric)));
};
