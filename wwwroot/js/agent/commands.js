AgentThreadManager.prototype._setAgentCommands = function(commands, ready) {
        this.agentCommandsReady = ready === true;
        this.agentCommands = commands
            .map((command) => {
                if (typeof command === 'string') {
                    return { name: command, description: 'Send to ' + this.assistantName + ' Agent' };
                }
                return command || {};
            })
            .filter((command) => typeof command.name === 'string' && command.name.trim())
            .map((command) => {
                const trimmed = command.name.trim();
                const name = trimmed.startsWith('/') ? trimmed : '/' + trimmed;
                return {
                    source: this.assistantName + ' Agent',
                    name,
                    label: command.description || 'Send to ' + this.assistantName + ' Agent',
                    agent: true
                };
            });
        this._updateCommandMenu();
};

AgentThreadManager.prototype._allCommands = function() {
        return this.psxCommands.concat(this.agentCommands);
};

AgentThreadManager.prototype._updateCommandMenu = function() {
        const value = this.input.value.trimStart();
        if (!value.startsWith('/')) {
            this._hideCommandMenu();
            return;
        }

        const query = value.toLowerCase();
        const matches = this._allCommands().filter((command) => {
            const name = command.name.toLowerCase();
            return name.startsWith(query) || name.includes(query);
        });

        if (matches.length === 0) {
            this._hideCommandMenu();
            return;
        }

        this.commandMenu.innerHTML = '';
        this.commandIndex = Math.min(this.commandIndex, matches.length - 1);
        let currentGroup = '';
        matches.forEach((command, index) => {
            if (command.source !== currentGroup) {
                currentGroup = command.source;
                const group = document.createElement('div');
                group.className = 'agent-command-group';
                group.setAttribute('role', 'presentation');
                group.textContent = currentGroup;
                this.commandMenu.appendChild(group);
            }

            const row = document.createElement('button');
            row.type = 'button';
            row.id = 'agent-command-option-' + index;
            row.tabIndex = -1;
            row.setAttribute('role', 'option');
            row.setAttribute('aria-selected', index === this.commandIndex ? 'true' : 'false');
            row.className = 'agent-command-item' + (index === this.commandIndex ? ' is-active' : '');
            row.innerHTML = '<span>' + this._escape(command.name) + '</span><small>' + this._escape(command.label) + '</small>';
            row.addEventListener('mousedown', (event) => {
                event.preventDefault();
                this._applyCommand(command);
            });
            this.commandMenu.appendChild(row);
        });
        this.commandMenu.hidden = false;
        this.input.setAttribute('aria-expanded', 'true');
        this.input.setAttribute('aria-activedescendant', 'agent-command-option-' + this.commandIndex);
        this.visibleCommands = matches;
};

AgentThreadManager.prototype._handleCommandKey = function(event) {
        if (event.key === 'Escape') {
            event.preventDefault();
            this._hideCommandMenu();
            return true;
        }

        if (event.key === 'ArrowDown') {
            event.preventDefault();
            this.commandIndex = Math.min((this.visibleCommands.length || 1) - 1, this.commandIndex + 1);
            this._updateCommandMenu();
            return true;
        }

        if (event.key === 'ArrowUp') {
            event.preventDefault();
            this.commandIndex = Math.max(0, this.commandIndex - 1);
            this._updateCommandMenu();
            return true;
        }

        if (event.key === 'Enter') {
            event.preventDefault();
            const command = this.visibleCommands[this.commandIndex];
            if (command) {
                this._applyCommand(command);
            }
            return true;
        }

        return false;
};

AgentThreadManager.prototype._applyCommand = function(command) {
        if (command.fill) {
            this._clearCommandHint();
            this.input.value = command.fill;
            this.input.focus();
            this.input.setSelectionRange(this.input.value.length, this.input.value.length);
            this._hideCommandMenu();
            this._resizeInput();
            return;
        }

        if (this.pendingAttachments.length > 0) {
            this._showCommandHint(command.name, 'attachments_not_allowed');
            return;
        }

        this._clearCommandHint();
        this.input.value = '';
        this._hideCommandMenu();
        this._resizeInput();

        if (command.command === 'history') {
            this.selectInspectorTab('history', false);
            this._showHistoryState('loading', 'Loading history...');
        }

        if (command.agent) {
            this.bridge.sendAgentCommand('agent_command', command.name);
        } else {
            this.bridge.sendAgentCommand(command.command);
        }
};

AgentThreadManager.prototype._hideCommandMenu = function() {
        this.commandMenu.hidden = true;
        this.input.setAttribute('aria-expanded', 'false');
        this.input.removeAttribute('aria-activedescendant');
        this.visibleCommands = [];
        this.commandIndex = 0;
};

AgentThreadManager.prototype._parseLeadingSlashCommand = function(text) {
        const trimmed = String(text || '').trim();
        if (!trimmed.startsWith('/')) return null;

        const separator = trimmed.search(/\s/);
        if (separator < 0) {
            return { name: trimmed, arguments: '' };
        }

        return {
            name: trimmed.slice(0, separator),
            arguments: trimmed.slice(separator).trimStart()
        };
};

AgentThreadManager.prototype._validateSubmissionCommand = function(text, attachmentIds) {
        const parsed = this._parseLeadingSlashCommand(text);
        if (!parsed) return { allowed: true, text };

        const psxCommand = this.psxCommands.find((command) => {
            const commandName = command.name.trim().split(/\s/, 1)[0];
            return commandName.toLowerCase() === parsed.name.toLowerCase();
        });
        const agentCommand = this.agentCommands.find(
            (command) => command.name.toLowerCase() === parsed.name.toLowerCase());
        const matched = psxCommand || agentCommand;

        if (attachmentIds.length > 0) {
            return { allowed: false, command: parsed.name, reason: 'attachments_not_allowed' };
        }

        if (!matched) {
            return {
                allowed: false,
                command: parsed.name,
                reason: this.agentCommandsReady ? 'unsupported' : 'commands_loading'
            };
        }

        const canonicalName = matched.name.trim().split(/\s/, 1)[0];
        return {
            allowed: true,
            text: parsed.arguments ? canonicalName + ' ' + parsed.arguments : canonicalName,
            psxCommand: psxCommand?.command || ''
        };
};

AgentThreadManager.prototype._showCommandHint = function(command, reason) {
        const hint = this.meta.commandHint;
        if (!hint) return;

        if (reason === 'attachments_not_allowed') {
            hint.textContent = '斜杠命令不能与附件同时发送，请先移除附件。';
        } else if (reason === 'commands_loading') {
            hint.textContent = 'Agent 命令列表仍在加载，请稍后重试。';
        } else {
            hint.textContent = '无法识别命令：' + command
                + '\nPSX 只支持命令菜单中显示的指令。输入 / 查看可用命令；部分 ' + this.agentName + ' 指令需要在原生 Terminal 中使用。';
        }
        hint.hidden = false;
};

AgentThreadManager.prototype._clearCommandHint = function() {
        const hint = this.meta.commandHint;
        if (!hint) return;
        hint.textContent = '';
        hint.hidden = true;
};
