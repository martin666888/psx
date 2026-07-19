AgentThreadManager.prototype._setModes = function(modes, currentModeId) {
        this.modes = modes.filter((mode) => mode && mode.id);
        this.currentModeId = currentModeId || this.currentModeId;
        if (!this.meta.mode) return;

        this._syncFallbackModeVisibility();
        this.meta.mode.innerHTML = '';
        if (this.modes.length === 0) {
            const option = document.createElement('option');
            option.value = '';
            option.textContent = 'default';
            this.meta.mode.appendChild(option);
            this.meta.mode.disabled = true;
            return;
        }

        this.modes.forEach((mode) => {
            const option = document.createElement('option');
            option.value = mode.id;
            option.textContent = mode.name || mode.id;
            option.title = mode.description || '';
            this.meta.mode.appendChild(option);
        });
        this._setCurrentMode(this.currentModeId);
        this.meta.mode.disabled = this._configControlsDisabled() || this._hasConfigOption('mode');
};

AgentThreadManager.prototype._setCurrentMode = function(modeId) {
        this.currentModeId = modeId || '';
        if (this.meta.mode && this.currentModeId) {
            this.meta.mode.value = this.currentModeId;
        }
        const modeSelect = this.meta.configOptions?.querySelector('select[data-config-id="mode"]');
        if (modeSelect && this.currentModeId) {
            modeSelect.value = this.currentModeId;
        }
};

AgentThreadManager.prototype._setConfigOptions = function(options) {
        this.configOptions = options
            .filter((option) => option && option.id && option.type === 'select' && Array.isArray(option.options) && option.options.length)
            .sort((a, b) => this._configOptionRank(a.id) - this._configOptionRank(b.id));

        const modeOption = this.configOptions.find((option) => option.id === 'mode');
        if (modeOption?.currentValue) {
            this.currentModeId = modeOption.currentValue;
        }

        this._renderConfigOptions();
        this._syncFallbackModeVisibility();
        this._syncConfigOptionDisabledState();
};

AgentThreadManager.prototype._renderConfigOptions = function() {
        const host = this.meta.configOptions;
        if (!host) return;

        host.innerHTML = '';
        this.configOptions.forEach((configOption) => {
            const label = document.createElement('label');
            label.className = 'agent-config-control';
            label.title = configOption.description || configOption.name || configOption.id;

            const name = document.createElement('span');
            name.textContent = configOption.name || configOption.id;
            label.appendChild(name);

            const select = document.createElement('select');
            select.dataset.configId = configOption.id;
            configOption.options.forEach((item) => {
                const option = document.createElement('option');
                option.value = item.value;
                option.textContent = item.name || item.value;
                option.title = item.description || '';
                select.appendChild(option);
            });
            if (configOption.currentValue) {
                select.value = configOption.currentValue;
            }
            select.addEventListener('change', () => {
                if (select.value) {
                    this.bridge.sendAgentCommand('set_config_option', select.value, configOption.id);
                }
            });

            label.appendChild(select);
            host.appendChild(label);
        });
};

AgentThreadManager.prototype._syncConfigOptionDisabledState = function() {
        const disabled = this._configControlsDisabled();
        this.meta.configOptions?.querySelectorAll('select').forEach((select) => {
            select.disabled = disabled;
        });
};

AgentThreadManager.prototype._syncFallbackModeVisibility = function() {
        const modeLabel = this.meta.mode?.closest('label');
        if (!modeLabel) return;
        modeLabel.hidden = this._hasConfigOption('mode');
};

AgentThreadManager.prototype._configControlsDisabled = function() {
        return this.isBusy || this.isRestoring || this.isTranscriptOnly || !this._runtimeReady();
};

AgentThreadManager.prototype._hasConfigOption = function(id) {
        return this.configOptions.some((option) => option.id === id);
};

AgentThreadManager.prototype._configOptionRank = function(id) {
        const order = ['mode', 'model', 'effort'];
        const index = order.indexOf(id);
        return index === -1 ? order.length : index;
};
