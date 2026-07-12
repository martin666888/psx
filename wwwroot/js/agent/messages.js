const AGENT_COPY_ICON_SVG = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"></rect><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path></svg>';
const AGENT_CHECK_ICON_SVG = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="20 6 9 17 4 12"></polyline></svg>';

AgentThreadManager.prototype._appendMessage = function(role, text) {
        if (!this.currentTurn) {
            this._startTurn();
        }

        const row = document.createElement('article');
        row.className = 'agent-message agent-message-' + role;

        const label = document.createElement('div');
        label.className = 'agent-message-label';
        label.textContent = role === 'user' ? 'You' : this.assistantName;

        const body = document.createElement('div');
        body.className = 'agent-message-body';
        if (role === 'user') {
            const content = document.createElement('div');
            content.className = 'agent-message-content';
            const cleaned = (text || '').replace(
                /\{"type":"image"[^\}]*\}/g,
                '\n\n[image attachment]\n\n'
            );
            content.innerHTML = this._renderMarkdown(cleaned);
            body.appendChild(content);
        } else {
            body.dataset.raw = text || '';
            body.innerHTML = this._renderMarkdown(text);
        }

        row.appendChild(label);
        row.appendChild(body);
        this._appendToCurrentHost(row);
        if (role === 'user') {
            this._maybeCollapseUserMessage(body);
        } else {
            this._finalizeAssistantMessage(body);
        }
        this._scrollToBottom();
        return body;
};

AgentThreadManager.prototype._appendAssistantDelta = function(text) {
        if (!this.currentAssistant) {
            this.currentAssistant = this._appendMessage('assistant', '');
            this.currentAssistant.dataset.raw = '';
        }

        this.currentAssistant.dataset.raw += text;
        this.currentAssistant.innerHTML = this._renderMarkdown(this.currentAssistant.dataset.raw);
        this._scrollToBottom();
};

AgentThreadManager.prototype._maybeCollapseUserMessage = function(body) {
        const content = body?.querySelector('.agent-message-content');
        if (!content || !content.textContent.trim()) return;

        requestAnimationFrame(() => {
            requestAnimationFrame(() => {
                const style = window.getComputedStyle(content);
                const fontSize = parseFloat(style.fontSize) || 15;
                let lineHeight = parseFloat(style.lineHeight);
                if (!Number.isFinite(lineHeight)) {
                    lineHeight = fontSize * 1.65;
                }

                const estimatedLines = Math.ceil(content.scrollHeight / lineHeight);
                if (estimatedLines < 17) return;

                body.classList.add('agent-message-collapsible', 'agent-message-collapsed');
                content.style.setProperty('--agent-user-message-collapsed-height', (lineHeight * 16) + 'px');

                const toggle = document.createElement('button');
                toggle.type = 'button';
                toggle.className = 'agent-message-collapse-toggle';
                toggle.textContent = '展开';
                toggle.addEventListener('click', () => {
                    const collapsed = body.classList.toggle('agent-message-collapsed');
                    toggle.textContent = collapsed ? '展开' : '收起';
                    if (!collapsed) {
                        requestAnimationFrame(() => {
                            const rect = body.getBoundingClientRect();
                            if (rect.bottom > window.innerHeight) {
                                body.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
                            }
                        });
                    }
                });

                content.appendChild(toggle);
            });
        });
};

// Idempotent: called from both assistant_message_done and run_finished, as
// well as directly after appending a historical assistant message. Creates
// the copy action bar once per non-empty assistant body.
AgentThreadManager.prototype._finalizeAssistantMessage = function(body) {
        if (!body) return;
        const raw = body.dataset.raw;
        if (!raw || raw.trim() === '') return;
        const article = body.parentElement;
        if (!article) return;
        if (article.querySelector('.agent-message-actions')) return;

        const actions = document.createElement('div');
        actions.className = 'agent-message-actions';

        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'agent-copy-button';
        button.innerHTML = AGENT_COPY_ICON_SVG;
        button.setAttribute('aria-label', '复制');
        button.title = '复制';
        button.addEventListener('click', () => {
            this._copyAssistantMessage(body, button);
        });

        actions.appendChild(button);
        article.appendChild(actions);
};

AgentThreadManager.prototype._copyAssistantMessage = async function(body, button) {
        if (button._copyTimer) {
            clearTimeout(button._copyTimer);
            button._copyTimer = null;
        }

        const raw = body.dataset.raw || '';
        let ok = false;

        try {
            if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
                await navigator.clipboard.writeText(raw);
                ok = true;
            } else {
                ok = this._legacyCopy(raw);
            }
        } catch (err) {
            try {
                ok = this._legacyCopy(raw);
            } catch (legacyErr) {
                ok = false;
            }
        }

        this._showCopyFeedback(button, ok);
};

// Fallback for non-secure contexts or when the async Clipboard API is blocked.
// The textarea must be attached to the DOM for select()/execCommand to work.
AgentThreadManager.prototype._legacyCopy = function(text) {
        const previousActive = document.activeElement;
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.top = '0';
        ta.style.left = '0';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        let success = false;
        try {
            ta.focus();
            ta.select();
            success = document.execCommand('copy');
        } finally {
            document.body.removeChild(ta);
            if (previousActive && typeof previousActive.focus === 'function') {
                try { previousActive.focus(); } catch (focusErr) { /* ignore */ }
            }
        }
        return success;
};

AgentThreadManager.prototype._showCopyFeedback = function(button, ok) {
        if (button._copyTimer) {
            clearTimeout(button._copyTimer);
            button._copyTimer = null;
        }

        button.classList.remove('agent-copy-success', 'agent-copy-fail');
        // Force reflow so the class change reapplies when retriggered quickly.
        void button.offsetWidth;

        if (ok) {
            button.classList.add('agent-copy-success');
            button.innerHTML = AGENT_CHECK_ICON_SVG;
            button.setAttribute('aria-label', '已复制');
            button.title = '已复制';
        } else {
            button.classList.add('agent-copy-fail');
            button.setAttribute('aria-label', '复制失败');
            button.title = '复制失败';
        }

        button._copyTimer = setTimeout(() => {
            button.classList.remove('agent-copy-success', 'agent-copy-fail');
            button.innerHTML = AGENT_COPY_ICON_SVG;
            button.setAttribute('aria-label', '复制');
            button.title = '复制';
            button._copyTimer = null;
        }, 1500);
};
