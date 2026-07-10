AgentThreadManager.prototype._setClaudeCommands = function(commands) {
        this.claudeCommands = commands
            .map((command) => {
                if (typeof command === 'string') {
                    return { name: command, description: 'Send to Claude Agent' };
                }
                return command || {};
            })
            .filter((command) => typeof command.name === 'string' && command.name.trim())
            .map((command) => {
                const trimmed = command.name.trim();
                const name = trimmed.startsWith('/') ? trimmed : '/' + trimmed;
                return {
                    source: 'Claude Agent',
                    name,
                    label: command.description || 'Send to Claude Agent',
                    claude: true
                };
            })
            .filter((command) => command.name.toLowerCase() !== '/model');
        this._updateCommandMenu();
};

AgentThreadManager.prototype._allCommands = function() {
        return this.psxCommands.concat(this.claudeCommands);
};

AgentThreadManager.prototype._updateCommandMenu = function() {
        const value = this.input.value;
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
                group.textContent = currentGroup;
                this.commandMenu.appendChild(group);
            }

            const row = document.createElement('button');
            row.type = 'button';
            row.className = 'agent-command-item' + (index === this.commandIndex ? ' is-active' : '');
            row.innerHTML = '<span>' + this._escape(command.name) + '</span><small>' + this._escape(command.label) + '</small>';
            row.addEventListener('mousedown', (event) => {
                event.preventDefault();
                this._applyCommand(command);
            });
            this.commandMenu.appendChild(row);
        });
        this.commandMenu.hidden = false;
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
            this.input.value = command.fill;
            this.input.focus();
            this.input.setSelectionRange(this.input.value.length, this.input.value.length);
            this._hideCommandMenu();
            this._resizeInput();
            return;
        }

        this.input.value = '';
        this._hideCommandMenu();
        this._resizeInput();

        if (command.claude) {
            Bridge.sendAgentCommand('claude_command', command.name);
        } else {
            Bridge.sendAgentCommand(command.command);
        }
};

AgentThreadManager.prototype._hideCommandMenu = function() {
        this.commandMenu.hidden = true;
        this.visibleCommands = [];
        this.commandIndex = 0;
};
