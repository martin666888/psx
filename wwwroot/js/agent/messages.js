AgentThreadManager.prototype._appendMessage = function(role, text) {
        if (!this.currentTurn) {
            this._startTurn();
        }

        const row = document.createElement('article');
        row.className = 'agent-message agent-message-' + role;

        const label = document.createElement('div');
        label.className = 'agent-message-label';
        label.textContent = role === 'user' ? 'You' : 'Claude';

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
            body.innerHTML = this._renderMarkdown(text);
        }

        row.appendChild(label);
        row.appendChild(body);
        this._appendToCurrentHost(row);
        if (role === 'user') {
            this._maybeCollapseUserMessage(body);
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
