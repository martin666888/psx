AgentThreadManager.prototype._showThinking = function() {
        if (this.thinkingRow) return;
        const row = document.createElement('div');
        row.className = 'agent-thinking';
        row.innerHTML = '<span class="agent-spinner"></span><span>Thinking</span>';
        this.thinkingRow = row;
        this.thinkingContent = null;
        this.thinkingHasContent = false;
        this._appendToCurrentHost(row);
        this._scrollToBottom();
};

AgentThreadManager.prototype._appendThinkingDelta = function(text) {
        if (!text) return;

        if (!this.thinkingRow || !this.thinkingContent) {
            this._upgradeThinkingBlock();
        }

        this.thinkingHasContent = true;
        this.thinkingContent.textContent += text;
        this._scrollToBottom();
};

AgentThreadManager.prototype._hideThinking = function() {
        if (!this.thinkingRow) return;
        if (!this.thinkingHasContent) {
            this.thinkingRow.remove();
        } else {
            this.thinkingRow.classList.remove('agent-thinking-running');
            this.thinkingRow.removeAttribute('open');
            const spinner = this.thinkingRow.querySelector('.agent-spinner');
            if (spinner) spinner.remove();
        }
        this.thinkingRow = null;
        this.thinkingContent = null;
        this.thinkingHasContent = false;
};

AgentThreadManager.prototype._upgradeThinkingBlock = function() {
        const previous = this.thinkingRow;
        const block = this._createThinkingBlock('', true);
        if (previous && previous.parentNode) {
            previous.replaceWith(block);
        } else {
            this._appendToCurrentHost(block);
        }
        this.thinkingRow = block;
        this.thinkingContent = block.querySelector('.agent-thinking-content');
};

AgentThreadManager.prototype._appendThinkingBlock = function(text, running) {
        const block = this._createThinkingBlock(text, running);
        this._appendToCurrentHost(block);
        this._scrollToBottom();
        return block;
};

AgentThreadManager.prototype._createThinkingBlock = function(text, running) {
        const details = document.createElement('details');
        details.className = 'agent-thinking-block' + (running ? ' agent-thinking-running' : '');
        details.open = !!running;

        const header = document.createElement('summary');
        header.className = 'agent-thinking-header';

        const chevron = document.createElement('span');
        chevron.className = 'agent-thinking-chevron';

        const title = document.createElement('span');
        title.className = 'agent-thinking-title';
        title.textContent = 'Thinking';

        header.appendChild(chevron);
        if (running) {
            const spinner = document.createElement('span');
            spinner.className = 'agent-spinner';
            header.appendChild(spinner);
        }
        header.appendChild(title);

        const content = document.createElement('pre');
        content.className = 'agent-thinking-content';
        content.textContent = text || '';

        details.appendChild(header);
        details.appendChild(content);
        return details;
};
