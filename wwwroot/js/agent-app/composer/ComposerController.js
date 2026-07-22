// ComposerController.ts — the Phase 4 Composer domain owner.
//
// Owns the whole Agent composer: the input textarea + send button, the slash
// command menu + hint, the fallback mode <select>, the provider config
// <select>s, and the image-attachment strip / preview. Slash commands, modes
// and config options are rendered purely from the reduced AgentWorkspaceState
// (the reducer folds agent_commands / agent_modes / agent_config_options /
// agent_mode_current). The pending-attachment upload list stays controller-
// local imperative state, matching the legacy AgentThreadManager instance.
//
// Faithful port of the legacy DOM writes and listeners:
//   wwwroot/js/agent/composer.js    (input/send wiring, submit, resize)
//   wwwroot/js/agent/commands.js    (command menu, hint, submission gate)
//   wwwroot/js/agent/config.js      (modes + provider config selects)
//   wwwroot/js/agent/attachments.js (image upload strip + preview)
//   plus the composer-owned parts of thread.js/_updateState and
//   runtime.js/_syncRuntimeControls (send/input/attach/config disabled sync).
// The thread scroll listener, _appendMessageAttachments and _appendSystem stay
// timeline-owned; composer-initiated system messages route through the timeline
// seam so the thread keeps a single writer.
import { createInitialWorkspaceState } from '../contracts/workspace-state.js';
const MAX_INPUT_HEIGHT = 180;
const DEFAULT_INPUT_MIN_HEIGHT = 52;
function role(panel, name) {
    return panel.querySelector('[data-role="' + name + '"]');
}
function asString(value) {
    return typeof value === 'string' ? value : '';
}
/** Mirrors legacy _escape so command-menu markup is byte-identical. */
function escapeHtml(value) {
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}
export class ComposerController {
    workspaceId;
    host;
    panel = null;
    input = null;
    sendButton = null;
    sendLabel = null;
    inputRow = null;
    commandMenu = null;
    commandHint = null;
    mode = null;
    configOptionsHost = null;
    attachButton = null;
    attachmentInput = null;
    attachmentStrip = null;
    imagePreview = null;
    imagePreviewImg = null;
    imagePreviewClose = null;
    state;
    psxCommands = [];
    commandIndex = 0;
    visibleCommands = [];
    pendingAttachments = [];
    lastSubmittedDraft = null;
    imagePreviewReturnFocus = null;
    commandMenuHideTimer = null;
    cleanup = [];
    constructor(workspaceId, host) {
        this.workspaceId = workspaceId;
        this.host = host;
        this.state = createInitialWorkspaceState(workspaceId);
    }
    mount() {
        const panel = this.host.getPanel(this.workspaceId);
        if (!panel)
            return;
        this.panel = panel;
        this.input = role(panel, 'input');
        this.sendButton = role(panel, 'send');
        this.sendLabel = role(panel, 'send-label');
        this.inputRow = role(panel, 'input-row');
        this.commandMenu = role(panel, 'command-menu');
        this.commandHint = role(panel, 'command-hint');
        this.mode = role(panel, 'mode');
        this.configOptionsHost = role(panel, 'config-options');
        this.attachButton = role(panel, 'attach');
        this.attachmentInput = role(panel, 'attachment-input');
        this.attachmentStrip = role(panel, 'attachments-strip');
        this.imagePreview = role(panel, 'image-preview');
        this.imagePreviewImg = role(panel, 'image-preview-img');
        this.imagePreviewClose = role(panel, 'image-preview-close');
        this.rebuildPsxCommands();
        this.wireComposer();
        this.wireModeControl();
        this.wireAttachments();
        this.renderComposerControls();
        this.syncAttachmentControls();
        this.resizeInput();
    }
    update(event, state) {
        if (!this.panel)
            return;
        const identityChanged = state.identity.assistantName !== this.state.identity.assistantName;
        this.state = state;
        if (identityChanged) {
            this.rebuildPsxCommands();
            this.updateModeControlTitle();
        }
        const raw = event.raw;
        switch (event.type) {
            case 'agent_state':
                this.renderComposerControls();
                break;
            case 'runtime_status':
                this.syncRuntimeControls();
                break;
            case 'agent_thread_loaded':
                this.clearCommandHint();
                if (raw.clear === true)
                    this.clearPendingAttachments();
                this.renderComposerControls();
                break;
            case 'agent_commands':
                this.updateCommandMenu();
                break;
            case 'agent_command_rejected':
                this.showCommandHint(asString(raw.command), asString(raw.reason) || 'unsupported');
                break;
            case 'agent_modes':
                this.renderModes();
                break;
            case 'agent_config_options':
                this.renderConfigOptions();
                this.syncFallbackModeVisibility();
                this.syncConfigOptionDisabledState();
                break;
            case 'agent_mode_current':
                this.setCurrentMode();
                break;
            case 'agent_attachment_uploaded':
                this.handleAttachmentUploaded(raw);
                break;
            case 'agent_attachment_failed':
                this.handleAttachmentFailed(raw);
                break;
            case 'agent_cleared':
                this.clearPendingAttachments();
                break;
            case 'user_message':
                this.lastSubmittedDraft = null;
                break;
            case 'run_failed':
                this.restoreSubmittedDraft();
                break;
            default:
                break;
        }
    }
    dispose() {
        if (this.commandMenuHideTimer !== null) {
            clearTimeout(this.commandMenuHideTimer);
            this.commandMenuHideTimer = null;
        }
        for (const item of this.pendingAttachments) {
            if (item.localPreviewUrl && item.url)
                URL.revokeObjectURL(item.url);
        }
        this.pendingAttachments = [];
        for (const off of this.cleanup.splice(0))
            off();
        this.panel = null;
    }
    // --- Decision seam (composer-region coordination) ------------------------
    // The Decision controller owns the mode-transition prompt node, but that
    // prompt takes over the composer region: the input row + attachment strip
    // hide while it is shown and restore when it clears. Those nodes are
    // composer-owned, so the Decision controller drives them through this seam
    // instead of writing them directly (no dual-write). Faithful port of the
    // composer side of _showModeTransitionPrompt / _clearModeTransitionPrompt.
    setModeTransitionPromptActive(active) {
        if (active) {
            if (this.inputRow)
                this.inputRow.hidden = true;
            if (this.attachmentStrip)
                this.attachmentStrip.hidden = true;
            this.clearCommandHint();
            this.hideCommandMenu();
        }
        else {
            if (this.inputRow)
                this.inputRow.hidden = false;
            this.syncAttachmentControls();
        }
    }
    /** Decision seam: return focus to the composer input (legacy this.input.focus()). */
    focusInput() {
        this.input?.focus();
    }
    // --- Derived state -------------------------------------------------------
    get isBusy() { return this.state.session.busy; }
    get isRestoring() { return this.state.session.isRestoring; }
    get isTranscriptOnly() { return this.state.session.isTranscriptOnly; }
    get supportsImage() { return this.state.identity.supportsImage; }
    get assistantName() { return this.state.identity.assistantName; }
    get agentName() { return this.state.identity.agentName; }
    get modes() { return this.state.composer.modes; }
    get configOptions() { return this.state.composer.configOptions; }
    get currentModeId() { return this.state.composer.currentModeId; }
    runtimeReady() { return this.state.runtime.state === 'ready'; }
    configControlsDisabled() {
        return this.isBusy || this.isRestoring || this.isTranscriptOnly || !this.runtimeReady();
    }
    hasConfigOption(id) {
        return this.configOptions.some((option) => option.id === id);
    }
    allCommands() {
        const agentCommands = this.state.composer.agentCommands.map((command) => ({
            source: command.source,
            name: command.name,
            label: command.label,
            agent: true
        }));
        return this.psxCommands.concat(agentCommands);
    }
    rebuildPsxCommands() {
        const assistant = this.assistantName;
        this.psxCommands = [
            { source: 'PSX', name: '/clear', label: 'Clear visible messages only', command: 'clear' },
            { source: 'PSX', name: '/cwd', label: 'Show current working directory', command: 'cwd' },
            { source: 'PSX', name: '/cwd <path>', label: 'Change this draft Agent working directory', fill: '/cwd ' },
            { source: 'PSX', name: '/terminal', label: 'Open raw ' + assistant + ' terminal here', command: 'terminal' },
            { source: 'PSX', name: '/stop', label: 'Stop the current ' + assistant + ' run', command: 'stop' },
            { source: 'PSX', name: '/history', label: 'Show saved Agent threads', command: 'history' },
            { source: 'PSX', name: '/delete', label: 'Delete the current thread', command: 'delete' },
            { source: 'PSX', name: '/help', label: 'Show PSX Agent commands', command: 'help' }
        ];
    }
    updateModeControlTitle() {
        const modeLabel = this.mode?.closest('.agent-mode-control');
        if (modeLabel)
            modeLabel.title = this.assistantName + ' Agent mode';
    }
    // --- Composer wiring + submit --------------------------------------------
    wireComposer() {
        this.on(this.sendButton, 'click', () => this.submit());
        this.on(this.input, 'keydown', (event) => {
            const keyboard = event;
            if (this.commandMenu && !this.commandMenu.hidden && this.handleCommandKey(keyboard))
                return;
            if (keyboard.key === 'Enter' && !keyboard.shiftKey) {
                keyboard.preventDefault();
                // `/history` is global UI navigation and stays available while busy.
                if (!this.isBusy || this.isHistoryNavigation(this.input?.value ?? ''))
                    this.submit();
            }
        });
        this.on(this.input, 'input', () => {
            this.clearCommandHint();
            this.resizeInput();
            this.updateCommandMenu();
        });
        this.on(this.input, 'blur', () => {
            if (this.commandMenuHideTimer !== null)
                clearTimeout(this.commandMenuHideTimer);
            this.commandMenuHideTimer = setTimeout(() => {
                this.commandMenuHideTimer = null;
                this.hideCommandMenu();
            }, 120);
        });
    }
    wireModeControl() {
        this.on(this.mode, 'change', () => {
            const value = this.mode?.value;
            if (!value)
                return;
            if (this.hasConfigOption('mode')) {
                this.bridge()?.sendAgentCommand('set_config_option', value, 'mode');
            }
            else {
                this.bridge()?.sendAgentCommand('set_mode', value);
            }
        });
    }
    submit() {
        if (!this.input)
            return;
        let text = this.input.value.trim();
        // `/history` is global UI navigation, handled before every session guard:
        // it never enters busy state, never writes thread history and never
        // reaches the conversation as a prompt or a loading card.
        if (this.isHistoryNavigation(text)) {
            this.clearCommandHint();
            this.input.value = '';
            this.resizeInput();
            this.hideCommandMenu();
            this.host.openHistory(this.workspaceId);
            return;
        }
        if (!this.runtimeReady())
            return;
        if (this.isRestoring)
            return;
        if (this.isTranscriptOnly)
            return;
        if (this.isBusy) {
            this.bridge()?.sendAgentCommand('stop');
            return;
        }
        const attachmentIds = this.pendingAttachmentIds();
        if (!text && attachmentIds.length === 0)
            return;
        const commandValidation = this.validateSubmissionCommand(text);
        if (!commandValidation.allowed) {
            this.showCommandHint(commandValidation.command || '', commandValidation.reason || 'unsupported');
            return;
        }
        text = commandValidation.text ?? text;
        if (!this.attachmentsReady()) {
            this.host.appendSystemMessage(this.workspaceId, 'Images are still uploading. Wait for upload to finish, then send again.');
            return;
        }
        this.clearCommandHint();
        this.input.value = '';
        this.resizeInput();
        this.hideCommandMenu();
        this.lastSubmittedDraft = { text, attachments: this.pendingAttachments.slice() };
        this.bridge()?.sendAgentMessage(text, attachmentIds);
        this.clearPendingAttachments();
    }
    resizeInput() {
        if (!this.input)
            return;
        const minHeight = this.composerInputMinimumHeight();
        this.input.style.height = 'auto';
        const nextHeight = Math.min(Math.max(this.input.scrollHeight, minHeight), MAX_INPUT_HEIGHT);
        this.input.style.height = nextHeight + 'px';
        this.input.style.overflowY = this.input.scrollHeight > MAX_INPUT_HEIGHT ? 'auto' : 'hidden';
    }
    composerInputMinimumHeight() {
        const raw = getComputedStyle(document.documentElement)
            .getPropertyValue('--agent-composer-input-min-height')
            .trim();
        const parsed = Number.parseFloat(raw);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_INPUT_MIN_HEIGHT;
    }
    // --- Send/input/mode/config disabled sync --------------------------------
    /** Faithful port of the composer-owned part of thread.js/_updateState. */
    renderComposerControls() {
        if (this.sendButton) {
            let label = 'Send';
            if (this.isRestoring) {
                label = 'Loading';
                this.sendButton.title = 'ACP history is loading';
            }
            else if (this.isTranscriptOnly) {
                label = 'Read only';
                this.sendButton.title = 'Create a new Agent tab to continue';
            }
            else {
                label = this.isBusy ? 'Stop' : 'Send';
                this.sendButton.title = this.isBusy ? 'Stop ' + this.assistantName : 'Send message';
            }
            if (this.sendLabel)
                this.sendLabel.textContent = label;
            this.sendButton.setAttribute('aria-label', label);
            this.sendButton.disabled = this.isRestoring || this.isTranscriptOnly || !this.runtimeReady();
            this.sendButton.classList.toggle('agent-send-stop', this.isBusy);
        }
        if (this.mode) {
            this.syncFallbackModeVisibility();
            this.mode.disabled = this.configControlsDisabled() || this.modes.length === 0 || this.hasConfigOption('mode');
        }
        this.syncConfigOptionDisabledState();
        this.syncRuntimeControls();
    }
    /** Faithful port of runtime.js/_syncRuntimeControls (composer-owned nodes). */
    syncRuntimeControls() {
        const blocked = !this.runtimeReady();
        if (this.input) {
            this.input.disabled = blocked || this.isRestoring || this.isTranscriptOnly;
            this.input.placeholder = this.isTranscriptOnly
                ? 'Read-only transcript - create a new Agent tab to continue'
                : blocked
                    ? 'Install the Agent runtime to start messaging'
                    : 'Message ' + this.assistantName + ' Agent - / for commands';
        }
        if (this.sendButton) {
            this.sendButton.disabled = blocked || this.isRestoring || this.isTranscriptOnly;
        }
        this.syncAttachmentControls();
        this.syncConfigOptionDisabledState();
    }
    // --- Command menu + hint -------------------------------------------------
    updateCommandMenu() {
        if (!this.input || !this.commandMenu)
            return;
        const value = this.input.value.trimStart();
        if (!value.startsWith('/')) {
            this.hideCommandMenu();
            return;
        }
        const query = value.toLowerCase();
        const matches = this.allCommands().filter((command) => {
            const name = command.name.toLowerCase();
            return name.startsWith(query) || name.includes(query);
        });
        if (matches.length === 0) {
            this.hideCommandMenu();
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
            row.innerHTML = '<span>' + escapeHtml(command.name) + '</span><small>' + escapeHtml(command.label) + '</small>';
            row.addEventListener('mousedown', (event) => {
                event.preventDefault();
                this.applyCommand(command);
            });
            this.commandMenu.appendChild(row);
        });
        this.commandMenu.hidden = false;
        this.input.setAttribute('aria-expanded', 'true');
        this.input.setAttribute('aria-activedescendant', 'agent-command-option-' + this.commandIndex);
        this.visibleCommands = matches;
    }
    handleCommandKey(event) {
        if (event.key === 'Escape') {
            event.preventDefault();
            this.hideCommandMenu();
            return true;
        }
        if (event.key === 'ArrowDown') {
            event.preventDefault();
            this.commandIndex = Math.min((this.visibleCommands.length || 1) - 1, this.commandIndex + 1);
            this.updateCommandMenu();
            return true;
        }
        if (event.key === 'ArrowUp') {
            event.preventDefault();
            this.commandIndex = Math.max(0, this.commandIndex - 1);
            this.updateCommandMenu();
            return true;
        }
        if (event.key === 'Enter') {
            event.preventDefault();
            const command = this.visibleCommands[this.commandIndex];
            if (command)
                this.applyCommand(command);
            return true;
        }
        return false;
    }
    applyCommand(command) {
        if (!this.input)
            return;
        if (command.fill) {
            this.clearCommandHint();
            this.input.value = command.fill;
            this.input.focus();
            this.input.setSelectionRange(this.input.value.length, this.input.value.length);
            this.hideCommandMenu();
            this.resizeInput();
            return;
        }
        if (this.pendingAttachments.length > 0) {
            this.showCommandHint(command.name, 'attachments_not_allowed');
            return;
        }
        this.clearCommandHint();
        this.input.value = '';
        this.hideCommandMenu();
        this.resizeInput();
        // The broker owns every `history` load; the menu entry only opens the dock.
        if (command.command === 'history') {
            this.host.openHistory(this.workspaceId);
            return;
        }
        if (command.agent) {
            this.bridge()?.sendAgentCommand('agent_command', command.name);
        }
        else if (command.command) {
            this.bridge()?.sendAgentCommand(command.command);
        }
    }
    hideCommandMenu() {
        if (this.commandMenu)
            this.commandMenu.hidden = true;
        if (this.input) {
            this.input.setAttribute('aria-expanded', 'false');
            this.input.removeAttribute('aria-activedescendant');
        }
        this.visibleCommands = [];
        this.commandIndex = 0;
    }
    parseLeadingSlashCommand(text) {
        const trimmed = String(text || '').trim();
        if (!trimmed.startsWith('/'))
            return null;
        const separator = trimmed.search(/\s/);
        if (separator < 0)
            return { name: trimmed, arguments: '' };
        return { name: trimmed.slice(0, separator), arguments: trimmed.slice(separator).trimStart() };
    }
    /** `/history` is global UI navigation, not a prompt. Arguments are ignored
     * (the C# gate matches the command name too), so `/history anything` takes
     * the same navigation seam instead of dying as a dropped broker response. */
    isHistoryNavigation(text) {
        const parsed = this.parseLeadingSlashCommand(text);
        return !!parsed && parsed.name.toLowerCase() === '/history';
    }
    validateSubmissionCommand(text) {
        const parsed = this.parseLeadingSlashCommand(text);
        if (!parsed)
            return { allowed: true, text };
        const psxCommand = this.psxCommands.find((command) => {
            const commandName = command.name.trim().split(/\s/, 1)[0];
            return commandName.toLowerCase() === parsed.name.toLowerCase();
        });
        const agentCommand = this.state.composer.agentCommands.find((command) => command.name.toLowerCase() === parsed.name.toLowerCase());
        const matched = psxCommand || agentCommand;
        if (this.pendingAttachmentIds().length > 0) {
            return { allowed: false, command: parsed.name, reason: 'attachments_not_allowed' };
        }
        if (!matched) {
            return {
                allowed: false,
                command: parsed.name,
                reason: this.state.composer.agentCommandsReady ? 'unsupported' : 'commands_loading'
            };
        }
        const canonicalName = matched.name.trim().split(/\s/, 1)[0];
        return {
            allowed: true,
            text: parsed.arguments ? canonicalName + ' ' + parsed.arguments : canonicalName
        };
    }
    showCommandHint(command, reason) {
        const hint = this.commandHint;
        if (!hint)
            return;
        if (reason === 'attachments_not_allowed') {
            hint.textContent = '斜杠命令不能与附件同时发送，请先移除附件。';
        }
        else if (reason === 'commands_loading') {
            hint.textContent = 'Agent 命令列表仍在加载，请稍后重试。';
        }
        else {
            hint.textContent = '无法识别命令：' + command
                + '\nPSX 只支持命令菜单中显示的指令。输入 / 查看可用命令；部分 ' + this.agentName + ' 指令需要在原生 Terminal 中使用。';
        }
        hint.hidden = false;
    }
    clearCommandHint() {
        const hint = this.commandHint;
        if (!hint)
            return;
        hint.textContent = '';
        hint.hidden = true;
    }
    // --- Modes + config options ----------------------------------------------
    renderModes() {
        if (!this.mode)
            return;
        this.syncFallbackModeVisibility();
        this.mode.innerHTML = '';
        if (this.modes.length === 0) {
            const option = document.createElement('option');
            option.value = '';
            option.textContent = 'default';
            this.mode.appendChild(option);
            this.mode.disabled = true;
            return;
        }
        this.modes.forEach((mode) => {
            const option = document.createElement('option');
            option.value = mode.id;
            option.textContent = mode.name || mode.id;
            option.title = mode.description || '';
            this.mode.appendChild(option);
        });
        this.setCurrentMode();
        this.mode.disabled = this.configControlsDisabled() || this.hasConfigOption('mode');
    }
    setCurrentMode() {
        const modeId = this.currentModeId;
        if (this.mode && modeId)
            this.mode.value = modeId;
        const modeSelect = this.configOptionsHost?.querySelector('select[data-config-id="mode"]');
        if (modeSelect && modeId)
            modeSelect.value = modeId;
    }
    renderConfigOptions() {
        const host = this.configOptionsHost;
        if (!host)
            return;
        host.innerHTML = '';
        this.configOptions.forEach((configOption) => {
            const label = document.createElement('label');
            label.className = 'agent-config-control';
            label.title = configOption.description || configOption.name || configOption.id;
            const name = document.createElement('span');
            name.textContent = configOption.name || configOption.id;
            label.appendChild(name);
            const toggleValues = this.toggleValues(configOption);
            if (configOption.type === 'boolean' || toggleValues) {
                const switchButton = document.createElement('button');
                const checked = configOption.type === 'boolean'
                    ? configOption.currentValue === true
                    : configOption.currentValue === toggleValues?.enabled;
                switchButton.type = 'button';
                switchButton.className = 'agent-config-switch';
                switchButton.dataset.configId = configOption.id;
                switchButton.setAttribute('role', 'switch');
                switchButton.setAttribute('aria-checked', String(checked));
                switchButton.setAttribute('aria-label', configOption.name || configOption.id);
                switchButton.addEventListener('click', () => {
                    const next = switchButton.getAttribute('aria-checked') !== 'true';
                    switchButton.setAttribute('aria-checked', String(next));
                    const value = configOption.type === 'boolean'
                        ? next
                        : next ? toggleValues.enabled : toggleValues.disabled;
                    this.bridge()?.sendAgentCommand('set_config_option', value, configOption.id);
                });
                label.appendChild(switchButton);
                host.appendChild(label);
                return;
            }
            const select = document.createElement('select');
            select.dataset.configId = configOption.id;
            configOption.options.forEach((item) => {
                const option = document.createElement('option');
                option.value = item.value;
                option.textContent = item.name || item.value;
                option.title = item.description || '';
                select.appendChild(option);
            });
            if (typeof configOption.currentValue === 'string' && configOption.currentValue) {
                select.value = configOption.currentValue;
            }
            select.addEventListener('change', () => {
                if (select.value) {
                    this.bridge()?.sendAgentCommand('set_config_option', select.value, configOption.id);
                }
            });
            label.appendChild(select);
            host.appendChild(label);
        });
    }
    syncConfigOptionDisabledState() {
        const disabled = this.configControlsDisabled();
        this.configOptionsHost?.querySelectorAll('select, button').forEach((control) => {
            control.disabled = disabled;
        });
    }
    toggleValues(configOption) {
        if (configOption.type !== 'select' || configOption.options.length !== 2)
            return null;
        const lookup = new Map(configOption.options.map((item) => [item.value.trim().toLowerCase(), item.value]));
        if (lookup.size !== 2)
            return null;
        if (lookup.has('on') && lookup.has('off'))
            return { enabled: lookup.get('on'), disabled: lookup.get('off') };
        if (lookup.has('true') && lookup.has('false'))
            return { enabled: lookup.get('true'), disabled: lookup.get('false') };
        return null;
    }
    syncFallbackModeVisibility() {
        const modeLabel = this.mode?.closest('label');
        if (!modeLabel)
            return;
        modeLabel.hidden = this.hasConfigOption('mode');
    }
    // --- Attachments ---------------------------------------------------------
    wireAttachments() {
        this.lastSubmittedDraft = null;
        if (!this.attachButton || !this.attachmentInput || !this.attachmentStrip)
            return;
        this.on(this.attachButton, 'click', () => {
            if (!this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring)
                return;
            this.attachmentInput?.click();
        });
        this.on(this.attachmentInput, 'change', () => {
            this.handleAttachmentFiles(Array.from(this.attachmentInput?.files || []));
            if (this.attachmentInput)
                this.attachmentInput.value = '';
        });
        this.on(this.panel, 'paste', (event) => {
            if (!this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring)
                return;
            const clipboard = event;
            const files = Array.from(clipboard.clipboardData?.items || [])
                .filter((item) => item.kind === 'file' && item.type.startsWith('image/'))
                .map((item) => item.getAsFile())
                .filter((file) => !!file);
            if (files.length > 0) {
                clipboard.preventDefault();
                this.handleAttachmentFiles(files);
            }
        });
        this.on(this.panel, 'dragover', (event) => {
            if (!this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring)
                return;
            const drag = event;
            if (Array.from(drag.dataTransfer?.items || []).some((item) => item.kind === 'file')) {
                drag.preventDefault();
            }
        });
        this.on(this.panel, 'drop', (event) => {
            if (!this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring)
                return;
            const drag = event;
            const files = Array.from(drag.dataTransfer?.files || []).filter((file) => file.type.startsWith('image/'));
            if (files.length > 0) {
                drag.preventDefault();
                this.handleAttachmentFiles(files);
            }
        });
        if (this.imagePreview) {
            this.on(this.imagePreview, 'cancel', (event) => {
                event.preventDefault();
                this.hideImagePreview();
            });
            this.on(this.imagePreview, 'click', (event) => {
                if (event.target === this.imagePreview)
                    this.hideImagePreview();
            });
        }
        this.on(this.imagePreviewClose, 'click', () => this.hideImagePreview());
    }
    handleAttachmentFiles(files) {
        const images = files.filter((file) => file && file.type && file.type.startsWith('image/'));
        if (!this.supportsImage) {
            this.host.appendSystemMessage(this.workspaceId, 'Current ACP Agent does not support image input.');
            return;
        }
        if (images.length === 0)
            return;
        const allowedTypes = new Set(['image/png', 'image/jpeg', 'image/webp', 'image/gif']);
        const maxSingle = 20 * 1024 * 1024;
        const maxTotal = 50 * 1024 * 1024;
        const maxCount = 5;
        let currentTotal = this.pendingAttachments.reduce((sum, item) => sum + (item.size || 0), 0);
        for (const file of images) {
            if (!allowedTypes.has(file.type)) {
                this.host.appendSystemMessage(this.workspaceId, 'Only PNG, JPEG, WebP, and GIF images are supported.');
                continue;
            }
            if (file.size > maxSingle) {
                this.host.appendSystemMessage(this.workspaceId, file.name + ' is larger than 20MB.');
                continue;
            }
            if (this.pendingAttachments.length >= maxCount) {
                this.host.appendSystemMessage(this.workspaceId, 'You can attach at most 5 images at once.');
                break;
            }
            if (currentTotal + file.size > maxTotal) {
                this.host.appendSystemMessage(this.workspaceId, 'Images in one message must total 50MB or less.');
                break;
            }
            currentTotal += file.size;
            this.uploadAttachmentFile(file);
        }
    }
    uploadAttachmentFile(file) {
        const clientId = 'att-' + Date.now() + '-' + Math.random().toString(16).slice(2);
        const item = {
            clientId,
            id: '',
            fileName: file.name || 'image',
            mimeType: file.type,
            size: file.size,
            status: 'uploading',
            url: URL.createObjectURL(file),
            localPreviewUrl: true
        };
        this.pendingAttachments.push(item);
        this.renderPendingAttachments();
        const reader = new FileReader();
        reader.onload = () => {
            const dataUrl = String(reader.result || '');
            const comma = dataUrl.indexOf(',');
            this.bridge()?.uploadAgentAttachment({
                clientId,
                fileName: item.fileName,
                mimeType: item.mimeType,
                size: item.size,
                dataBase64: comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl
            });
        };
        reader.onerror = () => {
            this.markAttachmentReadError(clientId, 'Failed to read image file.');
        };
        reader.readAsDataURL(file);
    }
    handleAttachmentUploaded(raw) {
        const clientId = asString(raw.clientId);
        const attachment = raw.attachment;
        const item = this.pendingAttachments.find((entry) => entry.clientId === clientId);
        if (!item || !attachment)
            return;
        if (item.localPreviewUrl && item.url)
            URL.revokeObjectURL(item.url);
        Object.assign(item, attachment, { clientId, status: 'uploaded', localPreviewUrl: false });
        this.renderPendingAttachments();
    }
    /** Host push: legacy no longer owns the strip, so the controller marks the
     * failed tile. The system message is emitted here via the timeline seam. */
    handleAttachmentFailed(raw) {
        const clientId = asString(raw.clientId);
        const message = asString(raw.text) || 'Image upload failed.';
        const item = this.pendingAttachments.find((entry) => entry.clientId === clientId);
        if (!item) {
            this.host.appendSystemMessage(this.workspaceId, message);
            return;
        }
        item.status = 'failed';
        item.error = message;
        this.host.appendSystemMessage(this.workspaceId, message);
        this.renderPendingAttachments();
    }
    markAttachmentReadError(clientId, message) {
        const item = this.pendingAttachments.find((entry) => entry.clientId === clientId);
        if (!item) {
            this.host.appendSystemMessage(this.workspaceId, message);
            return;
        }
        item.status = 'failed';
        item.error = message;
        this.host.appendSystemMessage(this.workspaceId, message);
        this.renderPendingAttachments();
    }
    renderPendingAttachments() {
        const strip = this.attachmentStrip;
        if (!strip)
            return;
        strip.innerHTML = '';
        strip.hidden = this.pendingAttachments.length === 0;
        for (const attachment of this.pendingAttachments) {
            strip.appendChild(this.createAttachmentTile(attachment, true));
        }
    }
    /**
     * Build a non-removable message attachment tile. Message attachments live in
     * the timeline thread (timeline domain owns them) but the attachment tile is a
     * composer-owned widget, so the timeline reaches this domain through the seam.
     */
    createMessageAttachmentTile(attachment) {
        const tileData = {
            clientId: asString(attachment.clientId),
            id: asString(attachment.id),
            fileName: asString(attachment.fileName),
            mimeType: asString(attachment.mimeType),
            size: typeof attachment.size === 'number' ? attachment.size : 0,
            status: asString(attachment.status),
            url: asString(attachment.url),
            localPreviewUrl: false
        };
        return this.createAttachmentTile(tileData, false);
    }
    createAttachmentTile(attachment, removable) {
        const shell = document.createElement('div');
        shell.className = 'agent-attachment-shell';
        const tile = document.createElement('button');
        tile.type = 'button';
        tile.className = 'agent-attachment-tile agent-attachment-' + (attachment.status || 'ready');
        tile.title = attachment.fileName || 'Image attachment';
        tile.setAttribute('aria-label', 'Preview ' + (attachment.fileName || 'image attachment'));
        const image = document.createElement('img');
        image.src = attachment.url;
        image.alt = attachment.fileName || 'Image attachment';
        tile.appendChild(image);
        if (attachment.status === 'uploading') {
            const status = document.createElement('span');
            status.className = 'agent-attachment-status';
            status.textContent = '...';
            tile.appendChild(status);
        }
        tile.addEventListener('click', () => this.showImagePreview(attachment.url));
        shell.appendChild(tile);
        if (removable) {
            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'agent-attachment-remove';
            remove.textContent = '×';
            remove.setAttribute('aria-label', 'Remove ' + (attachment.fileName || 'image attachment'));
            remove.addEventListener('click', (event) => {
                event.stopPropagation();
                this.removePendingAttachment(attachment.clientId);
            });
            shell.appendChild(remove);
        }
        return shell;
    }
    removePendingAttachment(clientId) {
        const index = this.pendingAttachments.findIndex((attachment) => attachment.clientId === clientId);
        if (index < 0)
            return;
        const [item] = this.pendingAttachments.splice(index, 1);
        if (item.localPreviewUrl && item.url)
            URL.revokeObjectURL(item.url);
        this.renderPendingAttachments();
    }
    pendingAttachmentIds() {
        return this.pendingAttachments
            .filter((attachment) => attachment.status === 'uploaded' && attachment.id)
            .map((attachment) => attachment.id);
    }
    attachmentsReady() {
        return !this.pendingAttachments.some((attachment) => attachment.status === 'uploading');
    }
    clearPendingAttachments() {
        for (const item of this.pendingAttachments) {
            if (item.localPreviewUrl && item.url)
                URL.revokeObjectURL(item.url);
        }
        this.pendingAttachments = [];
        this.renderPendingAttachments();
    }
    restoreSubmittedDraft() {
        if (!this.lastSubmittedDraft || !this.input)
            return;
        this.input.value = this.lastSubmittedDraft.text || '';
        this.pendingAttachments = (this.lastSubmittedDraft.attachments || []).slice();
        this.lastSubmittedDraft = null;
        this.resizeInput();
        this.renderPendingAttachments();
    }
    showImagePreview(url) {
        if (!this.imagePreview || !this.imagePreviewImg || !url)
            return;
        this.imagePreviewReturnFocus = document.activeElement;
        this.imagePreviewImg.src = url;
        if (!this.imagePreview.open)
            this.imagePreview.showModal();
        this.imagePreviewClose?.focus();
    }
    hideImagePreview() {
        if (!this.imagePreview || !this.imagePreviewImg)
            return;
        if (this.imagePreview.open)
            this.imagePreview.close();
        this.imagePreviewImg.removeAttribute('src');
        const returnFocus = this.imagePreviewReturnFocus;
        this.imagePreviewReturnFocus = null;
        if (returnFocus?.isConnected && typeof returnFocus.focus === 'function')
            returnFocus.focus();
    }
    syncAttachmentControls() {
        if (!this.attachButton)
            return;
        this.attachButton.disabled =
            !this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring || this.isTranscriptOnly;
        this.attachButton.title = this.supportsImage
            ? (this.runtimeReady() ? 'Attach images' : 'Install the Agent runtime before attaching images')
            : 'Current ACP Agent does not support image input';
    }
    // --- Helpers -------------------------------------------------------------
    bridge() {
        return this.host.bridgeFor(this.workspaceId);
    }
    on(node, type, handler) {
        if (!node)
            return;
        node.addEventListener(type, handler);
        this.cleanup.push(() => node.removeEventListener(type, handler));
    }
}
