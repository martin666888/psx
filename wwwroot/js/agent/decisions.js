AgentThreadManager.prototype._appendDecision = function(event, kind) {
        const details = document.createElement('details');
        details.className = 'agent-decision agent-decision-' + kind;
        details.open = true;
        details.dataset.decisionState = 'active';
        if (event.requestId) {
            details.dataset.requestId = event.requestId;
        }

        const header = document.createElement('summary');
        header.className = 'agent-decision-header';

        const content = document.createElement('div');
        content.className = 'agent-decision-header-content';

        const titleRow = document.createElement('div');
        titleRow.className = 'agent-decision-header-title-row';

        const chevron = document.createElement('span');
        chevron.className = 'agent-decision-chevron';

        const titleSpan = document.createElement('span');
        titleSpan.className = 'agent-decision-header-title';
        titleSpan.textContent = event.title || (kind === 'permission' ? 'Permission request' : this.assistantName + ' question');

        const subtitleSpan = document.createElement('span');
        subtitleSpan.className = 'agent-decision-header-subtitle';
        subtitleSpan.textContent = kind === 'permission'
            ? this.assistantName + ' Agent needs your approval before continuing.'
            : this.assistantName + ' Agent is waiting for your answer.';

        titleRow.appendChild(chevron);
        titleRow.appendChild(titleSpan);
        content.appendChild(titleRow);
        content.appendChild(subtitleSpan);
        header.appendChild(content);

        const body = document.createElement('div');
        body.className = 'agent-decision-body';

        const rawInputDetails = document.createElement('details');
        rawInputDetails.className = 'agent-decision-raw-input';

        const rawInputSummary = document.createElement('summary');
        rawInputSummary.className = 'agent-decision-raw-input-summary';
        rawInputSummary.textContent = 'Raw Input';

        const rawInputBody = document.createElement('pre');
        rawInputBody.className = 'agent-decision-raw-input-content';
        rawInputBody.textContent = event.text || '{}';

        rawInputDetails.appendChild(rawInputSummary);
        rawInputDetails.appendChild(rawInputBody);

        const actions = document.createElement('div');
        actions.className = 'agent-decision-actions';

        const options = Array.isArray(event.options) && event.options.length
            ? event.options
            : [
                { name: kind === 'permission' ? 'Allow' : 'Yes', optionId: kind === 'permission' ? 'allow' : 'yes' },
                { name: kind === 'permission' ? 'Reject' : 'No', optionId: kind === 'permission' ? 'reject' : 'no' }
            ];

        options.forEach((option) => {
            const optionId = option.optionId || '';
            const optionName = option.name || optionId || 'Select';

            const btn = this._decisionButton(optionName, () => {
                if (details.dataset.decisionState !== 'active') return;
                details.open = false;

                if (kind === 'permission') {
                    Bridge.sendAgentPermissionResponse(event.requestId || '', optionId);
                } else {
                    Bridge.sendAgentQuestionResponse(event.requestId || '', optionId);
                }

                this._disableDecisionCard(details, 'Response sent.');
            });

            if (optionId === 'allow' || optionId === 'yes') {
                btn.classList.add('agent-btn-allow');
            } else if (optionId === 'reject' || optionId === 'no') {
                btn.classList.add('agent-btn-reject');
            } else if (optionId === 'always_allow') {
                btn.classList.add('agent-btn-always-allow');
            }

            actions.appendChild(btn);
        });

        body.appendChild(rawInputDetails);
        body.appendChild(actions);
        details.appendChild(header);
        details.appendChild(body);

        this._appendToCurrentHost(details);
        this._scrollToBottom();
};

AgentThreadManager.prototype._appendElicitation = function(event) {
        const card = document.createElement('section');
        card.className = 'agent-decision agent-decision-elicitation';
        card.dataset.decisionState = 'active';
        if (event.requestId) {
            card.dataset.requestId = event.requestId;
        }

        const title = document.createElement('div');
        title.className = 'agent-decision-title';
        title.textContent = this.assistantName + ' Agent needs input';

        const subtitle = document.createElement('div');
        subtitle.className = 'agent-decision-subtitle';
        subtitle.textContent = event.message || 'Provide the requested information to continue.';

        const form = document.createElement('form');
        form.className = 'agent-elicitation-form';
        form.noValidate = true;

        const schema = event.schema || {};
        const properties = schema.properties || {};
        const required = new Set(Array.isArray(schema.required) ? schema.required : []);
        const controls = [];
        const fieldEntries = Object.entries(properties).map(([name, property], index) => ({
            name,
            property: property || {},
            index,
            isSupplement: this._isSupplementalElicitationField(name, property || {}, required.has(name))
        }));

        fieldEntries
            .sort((a, b) => Number(a.isSupplement) - Number(b.isSupplement) || a.index - b.index)
            .forEach((entry) => {
                const field = this._createElicitationField(entry.name, entry.property, required.has(entry.name), entry.isSupplement);
                controls.push(field);
                form.appendChild(field.row);
            });

        if (event.mode === 'url' && event.url) {
            const urlRow = document.createElement('div');
            urlRow.className = 'agent-elicitation-url';
            const link = document.createElement('a');
            link.href = event.url;
            link.target = '_blank';
            link.rel = 'noreferrer';
            link.textContent = event.url;
            urlRow.appendChild(link);
            form.appendChild(urlRow);
        }

        if (controls.length === 0 && event.mode !== 'url') {
            const field = this._createElicitationField('response', { type: 'string', title: 'Response' }, true, false);
            controls.push(field);
            form.appendChild(field.row);
        }

        const actions = document.createElement('div');
        actions.className = 'agent-decision-actions agent-elicitation-actions';

        actions.appendChild(this._decisionButton('Continue', () => {
            if (card.dataset.decisionState !== 'active') return;

            let valid = true;
            const content = {};
            controls.forEach((field) => {
                if (!field.validate()) {
                    valid = false;
                }
            });

            if (!valid) {
                return;
            }

            controls.forEach((field) => {
                const value = field.read();
                if (value !== undefined) {
                    content[field.name] = value;
                }
            });

            Bridge.sendAgentElicitationResponse(event.requestId || '', JSON.stringify({
                action: 'accept',
                content
            }));
            this._disableDecisionCard(card, 'Response sent.');
        }, 'agent-btn-primary'));

        actions.appendChild(this._decisionButton('Decline', () => {
            if (card.dataset.decisionState !== 'active') return;
            Bridge.sendAgentElicitationResponse(event.requestId || '', JSON.stringify({ action: 'decline' }));
            this._disableDecisionCard(card, 'Declined.');
        }, 'agent-btn-subtle'));

        actions.appendChild(this._decisionButton('Cancel', () => {
            if (card.dataset.decisionState !== 'active') return;
            Bridge.sendAgentElicitationResponse(event.requestId || '', JSON.stringify({ action: 'cancel' }));
            this._disableDecisionCard(card, 'Cancelled.');
        }, 'agent-btn-subtle'));

        card.appendChild(title);
        card.appendChild(subtitle);
        card.appendChild(form);
        card.appendChild(actions);
        this._appendToCurrentHost(card);
        this._scrollToBottom();
};

AgentThreadManager.prototype._createElicitationField = function(name, property, required, isSupplement) {
        const row = document.createElement('div');
        row.className = 'agent-elicitation-field';
        if (isSupplement) {
            row.classList.add('agent-elicitation-field-supplement');
        }

        const label = document.createElement('div');
        label.className = 'agent-elicitation-label';
        label.textContent = (property.title || name) + (required ? ' *' : '');
        row.appendChild(label);

        const description = property.description || '';
        const error = document.createElement('div');
        error.className = 'agent-elicitation-error';
        error.hidden = true;

        const options = this._readElicitationOptions(property);
        const isArray = property.type === 'array' && property.items;
        let read;
        let validate;
        let clearError;

        const setError = (message) => {
            row.classList.toggle('agent-elicitation-field-error', !!message);
            error.textContent = message || '';
            error.hidden = !message;
        };
        clearError = () => setError('');

        if (property.type === 'boolean') {
            const labelRow = document.createElement('label');
            labelRow.className = 'agent-elicitation-checkbox';
            const control = document.createElement('input');
            control.type = 'checkbox';
            control.checked = !!property.default;
            control.addEventListener('change', clearError);
            labelRow.appendChild(control);
            row.appendChild(labelRow);
            read = () => control.checked;
            validate = () => true;
        } else if (options.length && !isArray) {
            let selected = this._defaultOptionValue(options, property.default, true);
            const list = document.createElement('div');
            list.className = 'agent-elicitation-option-list';
            list.setAttribute('role', 'radiogroup');
            list.setAttribute('aria-label', property.title || name);

            const sync = () => {
                list.querySelectorAll('.agent-elicitation-option').forEach((button) => {
                    const isSelected = button._optionValue === selected;
                    button.classList.toggle('agent-elicitation-option-selected', isSelected);
                    button.setAttribute('aria-checked', String(isSelected));
                });
            };

            options.forEach((option) => {
                const button = this._createElicitationOptionButton(option, false);
                button._optionValue = option.value;
                button.addEventListener('click', () => {
                    selected = option.value;
                    clearError();
                    sync();
                });
                list.appendChild(button);
            });

            row.appendChild(list);
            sync();
            read = () => selected;
            validate = () => {
                if (required && (selected === undefined || selected === null || selected === '')) {
                    setError('Choose an option to continue.');
                    return false;
                }
                clearError();
                return true;
            };
        } else if (isArray) {
            const arrayOptions = this._readElicitationOptions(property.items || {});
            const selected = new Set(Array.isArray(property.default) ? property.default : []);
            const list = document.createElement('div');
            list.className = 'agent-elicitation-option-list';
            list.setAttribute('role', 'group');
            list.setAttribute('aria-label', property.title || name);

            const sync = () => {
                list.querySelectorAll('.agent-elicitation-option').forEach((button) => {
                    const isSelected = selected.has(button._optionValue);
                    button.classList.toggle('agent-elicitation-option-selected', isSelected);
                    button.setAttribute('aria-pressed', String(isSelected));
                });
            };

            arrayOptions.forEach((option) => {
                const button = this._createElicitationOptionButton(option, true);
                button._optionValue = option.value;
                button.addEventListener('click', () => {
                    if (selected.has(option.value)) {
                        selected.delete(option.value);
                    } else {
                        selected.add(option.value);
                    }
                    clearError();
                    sync();
                });
                list.appendChild(button);
            });

            row.appendChild(list);
            sync();
            read = () => Array.from(selected);
            validate = () => {
                if (required && selected.size === 0) {
                    setError('Choose at least one option to continue.');
                    return false;
                }
                clearError();
                return true;
            };
        } else {
            const control = property.type === 'integer' || property.type === 'number'
                ? document.createElement('input')
                : document.createElement('textarea');

            if (control.tagName === 'INPUT') {
                control.type = 'number';
                if (property.type === 'integer') control.step = '1';
                read = () => {
                    const raw = control.value.trim();
                    if (raw === '') return undefined;
                    return Number(raw);
                };
                validate = () => {
                    const raw = control.value.trim();
                    if (required && raw === '') {
                        setError('Enter a number to continue.');
                        return false;
                    }
                    if (raw !== '') {
                        const numeric = Number(raw);
                        if (!Number.isFinite(numeric) || (property.type === 'integer' && !Number.isInteger(numeric))) {
                            setError(property.type === 'integer' ? 'Enter a whole number.' : 'Enter a valid number.');
                            return false;
                        }
                    }
                    clearError();
                    return true;
                };
            } else {
                control.rows = isSupplement ? 2 : 3;
                read = () => {
                    const value = control.value;
                    return value === '' && !required ? undefined : value;
                };
                validate = () => {
                    if (required && control.value.trim() === '') {
                        setError('Enter a response to continue.');
                        return false;
                    }
                    clearError();
                    return true;
                };
            }

            if (property.default !== undefined && property.default !== null) {
                control.value = property.default;
            }

            control.addEventListener('input', clearError);
            row.appendChild(control);
        }

        if (description) {
            const hint = document.createElement('small');
            hint.textContent = description;
            row.appendChild(hint);
        }

        row.appendChild(error);

        return { name, row, read, validate };
};

AgentThreadManager.prototype._readElicitationOptions = function(property) {
        if (!property || typeof property !== 'object') return [];

        if (Array.isArray(property.oneOf)) {
            return property.oneOf
                .filter((item) => item && Object.prototype.hasOwnProperty.call(item, 'const'))
                .map((item) => this._normalizeElicitationOption(item.const, item.title, item.description));
        }

        if (Array.isArray(property.anyOf)) {
            return property.anyOf
                .filter((item) => item && Object.prototype.hasOwnProperty.call(item, 'const'))
                .map((item) => this._normalizeElicitationOption(item.const, item.title, item.description));
        }

        if (Array.isArray(property.enum)) {
            return property.enum.map((value) => this._normalizeElicitationOption(value, null, null));
        }

        return [];
};

AgentThreadManager.prototype._normalizeElicitationOption = function(value, title, description) {
        const fallback = value === null || value === undefined ? '' : String(value);
        return {
            value,
            title: title === undefined || title === null || title === '' ? fallback : String(title),
            description: description === undefined || description === null || description === '' ? '' : String(description)
        };
};

AgentThreadManager.prototype._defaultOptionValue = function(options, defaultValue, selectFirst) {
        if (defaultValue !== undefined && defaultValue !== null) {
            const exact = options.find((option) => option.value === defaultValue);
            if (exact) return exact.value;
        }
        return selectFirst && options.length ? options[0].value : undefined;
};

AgentThreadManager.prototype._createElicitationOptionButton = function(option, multi) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'agent-elicitation-option';
        button.setAttribute('role', multi ? 'button' : 'radio');

        const title = document.createElement('span');
        title.className = 'agent-elicitation-option-title';
        title.textContent = option.title;
        button.appendChild(title);

        if (option.description) {
            const description = document.createElement('small');
            description.className = 'agent-elicitation-option-description';
            description.textContent = option.description;
            button.appendChild(description);
        }

        return button;
};

AgentThreadManager.prototype._isSupplementalElicitationField = function(name, property, required) {
        if (required || property.type !== 'string') return false;
        const text = ((name || '') + ' ' + (property.title || '')).toLowerCase();
        return /\b(other|custom|response|answer|comment|note|details?)\b/.test(text);
};

AgentThreadManager.prototype._findDecisionCard = function(requestId) {
        if (!requestId) return null;
        return Array.from(this.thread.querySelectorAll('[data-request-id]'))
            .find((element) => element.dataset.requestId === requestId) || null;
};

AgentThreadManager.prototype._cancelDecision = function(requestId, text) {
        const card = this._findDecisionCard(requestId);
        if (!card) return;
        this._disableDecisionCard(card, text || 'Request cancelled.');
};

AgentThreadManager.prototype._disableDecisionCard = function(card, text) {
        if (!card || card.dataset.decisionState === 'disabled') return;
        card.dataset.decisionState = 'disabled';
        card.classList.add('agent-decision-disabled');
        card.querySelectorAll('button, input, textarea, select').forEach((control) => {
            control.disabled = true;
        });

        let status = card.querySelector('.agent-decision-status');
        if (!status) {
            status = document.createElement('div');
            status.className = 'agent-decision-status';
            card.appendChild(status);
        }
        status.textContent = text || 'Request closed.';
};

AgentThreadManager.prototype._decisionButton = function(label, action, className) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = label;
        if (className) {
            button.classList.add(className);
        }
        button.addEventListener('click', () => {
            const card = button.closest('[data-decision-state]');
            if (card && card.dataset.decisionState !== 'active') return;
            action();
        });
        return button;
};
