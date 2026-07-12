AgentThreadManager.prototype._wireRuntimeControls = function() {
    this.runtimeState = 'missing';
    this.runtimeMessage = 'Agent runtime is not installed.';

    this.meta.runtimeInstall?.addEventListener('click', () => {
        if (this.runtimeState === 'installing' || this.runtimeState === 'ready') return;
        Bridge.sendAgentCommand('install_runtime');
    });

    this.meta.runtimeCancel?.addEventListener('click', () => {
        if (this.runtimeState !== 'installing') return;
        Bridge.sendAgentCommand('cancel_runtime_install');
    });

    this._syncRuntimeControls();
};

AgentThreadManager.prototype._updateRuntimeStatus = function(event) {
    this._updateAgentIdentity(event);
    const allowedStates = new Set(['missing', 'installing', 'ready', 'failed', 'cancelled']);
    this.runtimeState = allowedStates.has(event.state) ? event.state : 'missing';
    this.runtimeMessage = event.message || 'Agent runtime is not installed.';

    if (this.meta.runtimeCard) {
        this.meta.runtimeCard.hidden = this.runtimeState === 'ready';
        this.meta.runtimeCard.dataset.state = this.runtimeState;
        this.meta.runtimeCard.setAttribute('aria-busy', this.runtimeState === 'installing' ? 'true' : 'false');
    }
    if (this.meta.runtimeTitle) {
        const titles = {
            missing: 'Agent runtime required',
            installing: 'Installing Agent runtime',
            failed: 'Agent runtime installation failed',
            cancelled: 'Agent runtime installation cancelled'
        };
        this.meta.runtimeTitle.textContent = titles[this.runtimeState] || 'Agent runtime';
    }
    if (this.meta.runtimeMessage) {
        this.meta.runtimeMessage.textContent = this.runtimeMessage;
    }
    if (this.meta.runtimeInstall) {
        this.meta.runtimeInstall.hidden = !event.canInstall;
        this.meta.runtimeInstall.disabled = !event.canInstall;
        this.meta.runtimeInstall.textContent = this.runtimeState === 'missing' ? 'Install runtime' : 'Retry installation';
    }
    if (this.meta.runtimeCancel) {
        this.meta.runtimeCancel.hidden = !event.canCancel;
        this.meta.runtimeCancel.disabled = !event.canCancel;
    }

    this._syncRuntimeControls();
};

AgentThreadManager.prototype._runtimeReady = function() {
    return this.runtimeState === 'ready';
};

AgentThreadManager.prototype._syncRuntimeControls = function() {
    const blocked = !this._runtimeReady();
    this.input.disabled = blocked || this.isRestoring;
    this.input.placeholder = blocked
        ? 'Install the Agent runtime to start messaging'
        : 'Message ' + this.assistantName + ' Agent - / for commands';
    this.sendButton.disabled = blocked || this.isRestoring;
    this._syncAttachmentControls();
    this._syncConfigOptionDisabledState();
};
