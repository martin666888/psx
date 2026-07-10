AgentThreadManager.prototype._wireComposer = function() {
        this.sendButton.addEventListener('click', () => this._submit());
        this.input.addEventListener('keydown', (event) => {
            if (!this.commandMenu.hidden && this._handleCommandKey(event)) {
                return;
            }

            if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                if (!this.isBusy) {
                    this._submit();
                }
            }
        });
        this.input.addEventListener('input', () => {
            this._resizeInput();
            this._updateCommandMenu();
        });
        this.input.addEventListener('blur', () => {
            setTimeout(() => this._hideCommandMenu(), 120);
        });
        this.thread.addEventListener('scroll', () => {
            this.autoScrollPinned = this._isNearBottom();
        });
        this._resizeInput();
};

AgentThreadManager.prototype._wireModeControl = function() {
        if (!this.meta.mode) return;
        this.meta.mode.addEventListener('change', () => {
            const value = this.meta.mode.value;
            if (value) {
                if (this._hasConfigOption('mode')) {
                    Bridge.sendAgentCommand('set_config_option', value, 'mode');
                } else {
                    Bridge.sendAgentCommand('set_mode', value);
                }
            }
        });
};

AgentThreadManager.prototype._submit = function() {
        if (this.isRestoring) {
            return;
        }

        if (this.isTranscriptOnly) {
            Bridge.sendAgentCommand('new');
            return;
        }

        if (this.isBusy) {
            Bridge.sendAgentCommand('stop');
            return;
        }

        const text = this.input.value.trim();
        const attachmentIds = this._pendingAttachmentIds();
        if (!text && attachmentIds.length === 0) return;
        if (!this._attachmentsReady()) {
            this._appendSystem('Images are still uploading. Wait for upload to finish, then send again.');
            return;
        }
        this.input.value = '';
        this._resizeInput();
        this._hideCommandMenu();
        this.lastSubmittedDraft = {
            text,
            attachments: this.pendingAttachments.slice()
        };
        Bridge.sendAgentMessage(text, attachmentIds);
        this._clearPendingAttachments();
};

AgentThreadManager.prototype._resizeInput = function() {
        const maxHeight = 180;
        const minHeight = this._composerControlHeight();
        this.input.style.height = 'auto';
        const nextHeight = Math.min(Math.max(this.input.scrollHeight, minHeight), maxHeight);
        this.input.style.height = nextHeight + 'px';
        this.input.style.overflowY = this.input.scrollHeight > maxHeight ? 'auto' : 'hidden';
};

AgentThreadManager.prototype._composerControlHeight = function() {
        const raw = getComputedStyle(document.documentElement)
            .getPropertyValue('--agent-composer-control-height')
            .trim();
        const parsed = Number.parseFloat(raw);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : 46;
};
