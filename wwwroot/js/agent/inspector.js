AgentThreadManager.prototype._initializeInspector = function() {
        this.inspectorMinWidth = 260;
        this.inspectorMaxWidth = 380;
        this.inspectorDefaultWidth = 300;
        this.inspectorStorageKey = 'psx.agent.inspectorWidth';
        this.legacyInspectorStorageKey = 'psx.agent.planPanelWidth';

        this.inspectorWidth = this._readInspectorWidth();
        this._applyInspectorWidth(this.inspectorWidth);
        this._wireInspectorTabs();
        this._wireInspectorResizer();
        this._resetPlanState();
        this._showHistoryState('empty', 'Open History to load saved Agent threads.');
        this.selectInspectorTab('plan', false);
};

AgentThreadManager.prototype.selectInspectorTab = function(name, refreshHistory) {
        const next = name === 'history' ? 'history' : 'plan';
        const shouldRefresh = next === 'history' && refreshHistory !== false;
        this.activeInspectorTab = next;

        const isPlan = next === 'plan';
        if (this.planTab) {
            this.planTab.setAttribute('aria-selected', String(isPlan));
            this.planTab.tabIndex = isPlan ? 0 : -1;
        }
        if (this.historyTab) {
            this.historyTab.setAttribute('aria-selected', String(!isPlan));
            this.historyTab.tabIndex = isPlan ? -1 : 0;
        }
        if (this.planPanel) this.planPanel.hidden = !isPlan;
        if (this.historyPanel) this.historyPanel.hidden = isPlan;

        if (isPlan) {
            this._clearPlanUnread();
        } else if (shouldRefresh) {
            this._refreshHistory();
        }
};

AgentThreadManager.prototype._wireInspectorTabs = function() {
        const tabs = [this.planTab, this.historyTab].filter(Boolean);
        if (tabs.length === 0) return;

        this.planTab?.addEventListener('click', () => this.selectInspectorTab('plan', false));
        this.historyTab?.addEventListener('click', () => this.selectInspectorTab('history', true));

        tabs.forEach((tab, index) => {
            tab.addEventListener('keydown', (event) => {
                let targetIndex = index;
                if (event.key === 'ArrowLeft') targetIndex = (index - 1 + tabs.length) % tabs.length;
                else if (event.key === 'ArrowRight') targetIndex = (index + 1) % tabs.length;
                else if (event.key === 'Home') targetIndex = 0;
                else if (event.key === 'End') targetIndex = tabs.length - 1;
                else return;

                event.preventDefault();
                const target = tabs[targetIndex];
                const name = target === this.historyTab ? 'history' : 'plan';
                this.selectInspectorTab(name, name === 'history');
                target.focus();
            });
        });
};

AgentThreadManager.prototype._refreshHistory = function() {
        if (this.historyPanel) {
            this.historyScrollTop = this.historyPanel.scrollTop;
        }
        this._showHistoryState('loading', 'Loading history...');
        Bridge.sendAgentCommand('history');
};

AgentThreadManager.prototype._renderHistory = function(threads) {
        if (!this.historyPanel) return;

        this.historyThreads = Array.isArray(threads) ? threads.slice() : [];
        const scrollTop = this.historyScrollTop || this.historyPanel.scrollTop || 0;
        this.historyPanel.innerHTML = '';

        if (this.historyThreads.length === 0) {
            this._showHistoryState('empty', 'No saved Agent threads.');
            return;
        }

        const list = document.createElement('div');
        list.className = 'agent-history-list';
        this.historyThreads.forEach((thread) => {
            const row = document.createElement('button');
            row.type = 'button';
            row.className = 'agent-history-item';
            row.dataset.threadId = thread.threadId || '';
            row.setAttribute('aria-current', thread.threadId === this.currentThreadId ? 'true' : 'false');

            const heading = document.createElement('span');
            heading.className = 'agent-history-heading';

            const title = document.createElement('strong');
            title.textContent = thread.title || 'Agent Chat';
            heading.appendChild(title);

            const current = document.createElement('span');
            current.className = 'agent-history-current';
            current.textContent = 'Current';
            current.hidden = thread.threadId !== this.currentThreadId;
            heading.appendChild(current);

            const cwd = document.createElement('span');
            cwd.className = 'agent-history-cwd';
            cwd.textContent = thread.cwd || '';
            cwd.title = thread.cwd || '';

            const meta = document.createElement('small');
            meta.textContent = this._historyMeta(thread);

            row.appendChild(heading);
            row.appendChild(cwd);
            row.appendChild(meta);
            row.addEventListener('click', () => {
                this.historyScrollTop = this.historyPanel.scrollTop;
                Bridge.sendAgentCommand('load_thread', thread.threadId || '');
            });
            list.appendChild(row);
        });

        this.historyPanel.appendChild(list);
        requestAnimationFrame(() => {
            if (this.historyPanel) this.historyPanel.scrollTop = scrollTop;
        });
};

AgentThreadManager.prototype._renderHistoryError = function(text) {
        if (!this.historyPanel) return;

        this.historyPanel.innerHTML = '';
        const state = document.createElement('div');
        state.className = 'agent-history-state agent-history-error';
        state.setAttribute('role', 'alert');

        const message = document.createElement('p');
        message.textContent = text || 'Unable to load Agent thread history.';

        const retry = document.createElement('button');
        retry.type = 'button';
        retry.className = 'agent-history-retry';
        retry.textContent = 'Retry';
        retry.addEventListener('click', () => this._refreshHistory());

        state.appendChild(message);
        state.appendChild(retry);
        this.historyPanel.appendChild(state);
};

AgentThreadManager.prototype._showHistoryState = function(stateName, text) {
        if (!this.historyPanel) return;

        this.historyPanel.innerHTML = '';
        const state = document.createElement('div');
        state.className = 'agent-history-state agent-history-' + stateName;
        if (stateName === 'loading') {
            state.setAttribute('role', 'status');
            state.setAttribute('aria-live', 'polite');
        }
        state.textContent = text;
        this.historyPanel.appendChild(state);
};

AgentThreadManager.prototype._syncHistoryCurrentState = function() {
        if (!this.historyPanel) return;

        this.historyPanel.querySelectorAll('.agent-history-item').forEach((row) => {
            const isCurrent = row.dataset.threadId === this.currentThreadId;
            row.setAttribute('aria-current', isCurrent ? 'true' : 'false');
            const badge = row.querySelector('.agent-history-current');
            if (badge) badge.hidden = !isCurrent;
        });
};

AgentThreadManager.prototype._markPlanUnread = function() {
        if (this.activeInspectorTab === 'plan' || !this.planUnread) return;
        this.planUnread.hidden = false;
};

AgentThreadManager.prototype._clearPlanUnread = function() {
        if (this.planUnread) this.planUnread.hidden = true;
};

AgentThreadManager.prototype._wireInspectorResizer = function() {
        if (!this.inspectorResizer || !this.inspector) return;

        this.inspectorResizer.addEventListener('pointerdown', (event) => {
            if (event.button !== 0) return;
            this.isResizingInspector = true;
            this.inspectorResizer.setPointerCapture(event.pointerId);
            document.body.classList.add('agent-inspector-resizing');
            event.preventDefault();
        });

        this.inspectorResizer.addEventListener('pointermove', (event) => {
            if (!this.isResizingInspector) return;
            const rect = this.panel.getBoundingClientRect();
            const width = rect.right - event.clientX - this.inspectorResizer.offsetWidth;
            this._setInspectorWidth(width, true);
        });

        const endResize = (event) => {
            if (!this.isResizingInspector) return;
            this.isResizingInspector = false;
            document.body.classList.remove('agent-inspector-resizing');
            try {
                this.inspectorResizer.releasePointerCapture(event.pointerId);
            } catch {
                // Pointer capture may already be gone if the window lost focus.
            }
            this._saveInspectorWidth();
        };

        this.inspectorResizer.addEventListener('pointerup', endResize);
        this.inspectorResizer.addEventListener('pointercancel', endResize);
        this.inspectorResizer.addEventListener('keydown', (event) => {
            const step = event.shiftKey ? 40 : 16;
            if (event.key === 'ArrowLeft') this._setInspectorWidth(this.inspectorWidth + step, false);
            else if (event.key === 'ArrowRight') this._setInspectorWidth(this.inspectorWidth - step, false);
            else if (event.key === 'Home') this._setInspectorWidth(this.inspectorMaxWidth, false);
            else if (event.key === 'End') this._setInspectorWidth(this.inspectorMinWidth, false);
            else return;
            event.preventDefault();
        });
};

AgentThreadManager.prototype._readInspectorWidth = function() {
        try {
            const stored = window.localStorage?.getItem(this.inspectorStorageKey)
                ?? window.localStorage?.getItem(this.legacyInspectorStorageKey);
            const width = Number(stored);
            if (Number.isFinite(width)) return this._clampInspectorWidth(width);
        } catch {
            // localStorage can be unavailable in constrained WebView profiles.
        }
        return this.inspectorDefaultWidth;
};

AgentThreadManager.prototype._setInspectorWidth = function(width, deferSave) {
        this.inspectorWidth = this._clampInspectorWidth(width);
        this._applyInspectorWidth(this.inspectorWidth);
        if (!deferSave) this._saveInspectorWidth();
};

AgentThreadManager.prototype._applyInspectorWidth = function(width) {
        if (this.panel) {
            this.panel.style.setProperty('--agent-inspector-width', this._clampInspectorWidth(width) + 'px');
        }
};

AgentThreadManager.prototype._saveInspectorWidth = function() {
        try {
            window.localStorage?.setItem(this.inspectorStorageKey, String(this.inspectorWidth));
        } catch {
            // Width persistence is optional; resizing remains available.
        }
};

AgentThreadManager.prototype._clampInspectorWidth = function(width) {
        const numeric = Number(width);
        if (!Number.isFinite(numeric)) return this.inspectorDefaultWidth;
        return Math.max(this.inspectorMinWidth, Math.min(this.inspectorMaxWidth, Math.round(numeric)));
};
