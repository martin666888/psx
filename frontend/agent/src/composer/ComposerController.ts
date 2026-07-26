// ComposerController.ts — the Phase 4 Composer domain owner.
//
// Owns the whole Agent composer: the input textarea + send button, the slash
// command menu + hint, the fallback mode menu, the provider config
// dropdowns, and the image-attachment strip / preview. Slash commands, modes
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

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type {
  AgentWorkspaceState,
  ComposerConfigOption
} from '../contracts/workspace-state.js';
import { createInitialWorkspaceState } from '../contracts/workspace-state.js';
import { isReactUiEnabled } from '../core/flags.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import type {
  AttachmentStripProps,
  CommandHintProps,
  ComposerActionsProps
} from './ComposerBits.js';
import { MenuSelect } from './MenuSelect.js';

/** The strangler seam the composer controller needs from the host. */
export interface ComposerHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
  /** Render a composer-initiated system message via the timeline engine. */
  appendSystemMessage(workspaceId: string, text: string): void;
  /** Open the global History dock and refresh it through the broker. */
  openHistory(sourceWorkspaceId: string): void;
}

/** Unified command row for the menu, matching the legacy shape. */
interface MenuCommand {
  source: string;
  name: string;
  label: string;
  command?: string;
  fill?: string;
  agent?: boolean;
}

/** A controller-local pending image attachment (imperative upload state). */
interface PendingAttachment {
  clientId: string;
  id: string;
  fileName: string;
  mimeType: string;
  size: number;
  status: string;
  url: string;
  localPreviewUrl: boolean;
  error?: string;
}

interface SubmittedDraft {
  text: string;
  attachments: PendingAttachment[];
}

const MAX_INPUT_HEIGHT = 180;
const DEFAULT_INPUT_MIN_HEIGHT = 52;

function role(panel: HTMLElement, name: string): HTMLElement | null {
  return panel.querySelector<HTMLElement>('[data-role="' + name + '"]');
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/** Mirrors legacy _escape so command-menu markup is byte-identical. */
function escapeHtml(value: unknown): string {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

export class ComposerController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: ComposerHost;

  private panel: HTMLElement | null = null;
  private input: HTMLTextAreaElement | null = null;
  private sendButton: HTMLButtonElement | null = null;
  private sendLabel: HTMLElement | null = null;
  private inputRow: HTMLElement | null = null;
  private commandMenu: HTMLElement | null = null;
  private commandHint: HTMLElement | null = null;
  private modeSelect: MenuSelect | null = null;
  private readonly configSelects = new Map<string, MenuSelect>();
  private configOptionsHost: HTMLElement | null = null;
  private attachButton: HTMLButtonElement | null = null;
  private attachmentInput: HTMLInputElement | null = null;
  private attachmentStrip: HTMLElement | null = null;
  private imagePreview: HTMLDialogElement | null = null;
  private imagePreviewImg: HTMLImageElement | null = null;
  private imagePreviewClose: HTMLButtonElement | null = null;

  private state: AgentWorkspaceState;
  private psxCommands: MenuCommand[] = [];
  private commandIndex = 0;
  private visibleCommands: MenuCommand[] = [];
  private pendingAttachments: PendingAttachment[] = [];
  private lastSubmittedDraft: SubmittedDraft | null = null;
  private imagePreviewReturnFocus: Element | null = null;
  private commandMenuHideTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly cleanup: Array<() => void> = [];

  // React island state (flag: psx.agent.experimental.react, default on). The
  // textarea + keyboard/IME + MenuSelect popups are excluded legacy regions.
  private reactUiEnabled = false;
  private attachmentsIsland: IslandLoader<AttachmentStripProps> | null = null;
  private actionsIsland: IslandLoader<ComposerActionsProps> | null = null;
  private hintIsland: IslandLoader<CommandHintProps> | null = null;

  constructor(workspaceId: string, host: ComposerHost) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.state = createInitialWorkspaceState(workspaceId);
  }

  mount(): void {
    const panel = this.host.getPanel(this.workspaceId);
    if (!panel) return;
    this.panel = panel;
    this.input = role(panel, 'input') as HTMLTextAreaElement | null;
    this.sendButton = role(panel, 'send') as HTMLButtonElement | null;
    this.sendLabel = role(panel, 'send-label');
    this.inputRow = role(panel, 'input-row');
    this.commandMenu = role(panel, 'command-menu');
    this.commandHint = role(panel, 'command-hint');
    this.modeSelect = new MenuSelect((value) => this.handleModeSelected(value), 'Agent mode');
    this.modeSelect.element.dataset.role = 'mode';
    panel.querySelector('.agent-mode-control')?.appendChild(this.modeSelect.element);
    this.configOptionsHost = role(panel, 'config-options');
    this.attachButton = role(panel, 'attach') as HTMLButtonElement | null;
    this.attachmentInput = role(panel, 'attachment-input') as HTMLInputElement | null;
    this.attachmentStrip = role(panel, 'attachments-strip');
    this.imagePreview = role(panel, 'image-preview') as HTMLDialogElement | null;
    this.imagePreviewImg = role(panel, 'image-preview-img') as HTMLImageElement | null;
    this.imagePreviewClose = role(panel, 'image-preview-close') as HTMLButtonElement | null;

    this.rebuildPsxCommands();
    this.wireComposer();
    this.wireAttachments();
    this.reactUiEnabled = isReactUiEnabled();
    this.renderComposerControls();
    this.syncAttachmentControls();
    this.resizeInput();
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    if (!this.panel) return;
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
        if (raw.clear === true) this.clearPendingAttachments();
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

  dispose(): void {
    // Menus portal their popup onto document.body: destroy them explicitly or
    // an open menu (and its global listeners) would outlive the workspace.
    this.modeSelect?.destroy();
    this.modeSelect = null;
    this.configSelects.forEach((menu) => menu.destroy());
    this.configSelects.clear();
    if (this.commandMenuHideTimer !== null) {
      clearTimeout(this.commandMenuHideTimer);
      this.commandMenuHideTimer = null;
    }
    for (const item of this.pendingAttachments) {
      if (item.localPreviewUrl && item.url) URL.revokeObjectURL(item.url);
    }
    this.pendingAttachments = [];
    for (const off of this.cleanup.splice(0)) off();
    this.attachmentsIsland?.dispose();
    this.attachmentsIsland = null;
    this.actionsIsland?.dispose();
    this.actionsIsland = null;
    this.hintIsland?.dispose();
    this.hintIsland = null;
    this.panel = null;
  }

  // --- Decision seam (composer-region coordination) ------------------------
  // The Decision controller owns the mode-transition prompt node, but that
  // prompt takes over the composer region: the input row + attachment strip
  // hide while it is shown and restore when it clears. Those nodes are
  // composer-owned, so the Decision controller drives them through this seam
  // instead of writing them directly (no dual-write). Faithful port of the
  // composer side of _showModeTransitionPrompt / _clearModeTransitionPrompt.

  setModeTransitionPromptActive(active: boolean): void {
    if (active) {
      if (this.inputRow) this.inputRow.hidden = true;
      if (this.attachmentStrip) this.attachmentStrip.hidden = true;
      this.clearCommandHint();
      this.hideCommandMenu();
    } else {
      if (this.inputRow) this.inputRow.hidden = false;
      this.syncAttachmentControls();
    }
  }

  /** Decision seam: return focus to the composer input (legacy this.input.focus()). */
  focusInput(): void {
    this.input?.focus();
  }

  // --- Derived state -------------------------------------------------------

  private get isBusy(): boolean { return this.state.session.busy; }
  private get isRestoring(): boolean { return this.state.session.isRestoring; }
  private get isTranscriptOnly(): boolean { return this.state.session.isTranscriptOnly; }
  private get supportsImage(): boolean { return this.state.identity.supportsImage; }
  private get assistantName(): string { return this.state.identity.assistantName; }
  private get agentName(): string { return this.state.identity.agentName; }
  private get modes() { return this.state.composer.modes; }
  private get configOptions(): ComposerConfigOption[] { return this.state.composer.configOptions; }
  private get currentModeId(): string { return this.state.composer.currentModeId; }

  private runtimeReady(): boolean { return this.state.runtime.state === 'ready'; }

  private configControlsDisabled(): boolean {
    return this.isBusy || this.isRestoring || this.isTranscriptOnly || !this.runtimeReady();
  }

  private hasConfigOption(id: string): boolean {
    return this.configOptions.some((option) => option.id === id);
  }

  private allCommands(): MenuCommand[] {
    const agentCommands: MenuCommand[] = this.state.composer.agentCommands.map((command) => ({
      source: command.source,
      name: command.name,
      label: command.label,
      agent: true
    }));
    return this.psxCommands.concat(agentCommands);
  }

  private rebuildPsxCommands(): void {
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

  private updateModeControlTitle(): void {
    const modeLabel = this.modeSelect?.element.closest('.agent-mode-control') as HTMLElement | null;
    if (modeLabel) modeLabel.title = this.assistantName + ' Agent mode';
  }

  // --- Composer wiring + submit --------------------------------------------

  private wireComposer(): void {
    this.on(this.sendButton, 'click', () => this.submit());
    this.on(this.input, 'keydown', (event) => {
      const keyboard = event as KeyboardEvent;
      if (this.commandMenu && !this.commandMenu.hidden && this.handleCommandKey(keyboard)) return;
      if (keyboard.key === 'Enter' && !keyboard.shiftKey) {
        keyboard.preventDefault();
        // `/history` is global UI navigation and stays available while busy.
        if (!this.isBusy || this.isHistoryNavigation(this.input?.value ?? '')) this.submit();
      }
    });
    this.on(this.input, 'input', () => {
      this.clearCommandHint();
      this.resizeInput();
      this.updateCommandMenu();
    });
    this.on(this.input, 'blur', () => {
      if (this.commandMenuHideTimer !== null) clearTimeout(this.commandMenuHideTimer);
      this.commandMenuHideTimer = setTimeout(() => {
        this.commandMenuHideTimer = null;
        this.hideCommandMenu();
      }, 120);
    });
  }

  private handleModeSelected(value: string): void {
    if (!value) return;
    if (this.hasConfigOption('mode')) {
      this.bridge()?.sendAgentCommand('set_config_option', value, 'mode');
    } else {
      this.bridge()?.sendAgentCommand('set_mode', value);
    }
  }

  private submit(): void {
    if (!this.input) return;

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
    if (!this.runtimeReady()) return;
    if (this.isRestoring) return;
    if (this.isTranscriptOnly) return;
    if (this.isBusy) {
      this.bridge()?.sendAgentCommand('stop');
      return;
    }

    const attachmentIds = this.pendingAttachmentIds();
    if (!text && attachmentIds.length === 0) return;

    const commandValidation = this.validateSubmissionCommand(text);
    if (!commandValidation.allowed) {
      this.showCommandHint(commandValidation.command || '', commandValidation.reason || 'unsupported');
      return;
    }
    text = commandValidation.text ?? text;
    if (!this.attachmentsReady()) {
      this.host.appendSystemMessage(
        this.workspaceId,
        'Images are still uploading. Wait for upload to finish, then send again.'
      );
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

  private resizeInput(): void {
    if (!this.input) return;
    const minHeight = this.composerInputMinimumHeight();
    this.input.style.height = 'auto';
    const nextHeight = Math.min(Math.max(this.input.scrollHeight, minHeight), MAX_INPUT_HEIGHT);
    this.input.style.height = nextHeight + 'px';
    this.input.style.overflowY = this.input.scrollHeight > MAX_INPUT_HEIGHT ? 'auto' : 'hidden';
  }

  private composerInputMinimumHeight(): number {
    const raw = getComputedStyle(document.documentElement)
      .getPropertyValue('--agent-composer-input-min-height')
      .trim();
    const parsed = Number.parseFloat(raw);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_INPUT_MIN_HEIGHT;
  }

  // --- Send/input/mode/config disabled sync --------------------------------

  /** Faithful port of the composer-owned part of thread.js/_updateState. */
  private renderComposerControls(): void {
    if (this.sendButton) {
      let label = 'Send';
      if (this.isRestoring) {
        label = 'Loading';
        this.sendButton.title = 'ACP history is loading';
      } else if (this.isTranscriptOnly) {
        label = 'Read only';
        this.sendButton.title = 'Create a new Agent tab to continue';
      } else {
        label = this.isBusy ? 'Stop' : 'Send';
        this.sendButton.title = this.isBusy ? 'Stop ' + this.assistantName : 'Send message';
      }
      if (this.sendLabel) this.sendLabel.textContent = label;
      this.sendButton.setAttribute('aria-label', label);
      this.sendButton.disabled = this.isRestoring || this.isTranscriptOnly || !this.runtimeReady();
      this.sendButton.classList.toggle('agent-send-stop', this.isBusy);
    }
    if (this.modeSelect) {
      this.syncFallbackModeVisibility();
      this.modeSelect.setDisabled(this.configControlsDisabled() || this.modes.length === 0 || this.hasConfigOption('mode'));
    }
    this.syncConfigOptionDisabledState();
    this.syncRuntimeControls();
  }

  /** Faithful port of runtime.js/_syncRuntimeControls (composer-owned nodes). */
  private syncRuntimeControls(): void {
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

  private updateCommandMenu(): void {
    if (!this.input || !this.commandMenu) return;
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
        this.commandMenu!.appendChild(group);
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
      this.commandMenu!.appendChild(row);
    });
    this.commandMenu.hidden = false;
    this.input.setAttribute('aria-expanded', 'true');
    this.input.setAttribute('aria-activedescendant', 'agent-command-option-' + this.commandIndex);
    this.visibleCommands = matches;
  }

  private handleCommandKey(event: KeyboardEvent): boolean {
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
      if (command) this.applyCommand(command);
      return true;
    }
    return false;
  }

  private applyCommand(command: MenuCommand): void {
    if (!this.input) return;
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
    } else if (command.command) {
      this.bridge()?.sendAgentCommand(command.command);
    }
  }

  private hideCommandMenu(): void {
    if (this.commandMenu) this.commandMenu.hidden = true;
    if (this.input) {
      this.input.setAttribute('aria-expanded', 'false');
      this.input.removeAttribute('aria-activedescendant');
    }
    this.visibleCommands = [];
    this.commandIndex = 0;
  }

  private parseLeadingSlashCommand(text: string): { name: string; arguments: string } | null {
    const trimmed = String(text || '').trim();
    if (!trimmed.startsWith('/')) return null;
    const separator = trimmed.search(/\s/);
    if (separator < 0) return { name: trimmed, arguments: '' };
    return { name: trimmed.slice(0, separator), arguments: trimmed.slice(separator).trimStart() };
  }

  /** `/history` is global UI navigation, not a prompt. Arguments are ignored
   * (the C# gate matches the command name too), so `/history anything` takes
   * the same navigation seam instead of dying as a dropped broker response. */
  private isHistoryNavigation(text: string): boolean {
    const parsed = this.parseLeadingSlashCommand(text);
    return !!parsed && parsed.name.toLowerCase() === '/history';
  }

  private validateSubmissionCommand(
    text: string
  ): { allowed: boolean; text?: string; command?: string; reason?: string } {
    const parsed = this.parseLeadingSlashCommand(text);
    if (!parsed) return { allowed: true, text };

    const psxCommand = this.psxCommands.find((command) => {
      const commandName = command.name.trim().split(/\s/, 1)[0];
      return commandName.toLowerCase() === parsed.name.toLowerCase();
    });
    const agentCommand = this.state.composer.agentCommands.find(
      (command) => command.name.toLowerCase() === parsed.name.toLowerCase()
    );
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

  private showCommandHint(command: string, reason: string): void {
    const hint = this.commandHint;
    if (!hint) return;
    let text: string;
    if (reason === 'attachments_not_allowed') {
      text = '斜杠命令不能与附件同时发送，请先移除附件。';
    } else if (reason === 'commands_loading') {
      text = 'Agent 命令列表仍在加载，请稍后重试。';
    } else {
      text = '无法识别命令：' + command
        + '\nPSX 只支持命令菜单中显示的指令。输入 / 查看可用命令；部分 ' + this.agentName + ' 指令需要在原生 Terminal 中使用。';
    }
    if (this.reactUiEnabled && !this.hintIsland?.hasFailed()) {
      this.renderHintReact(text);
    } else {
      hint.textContent = text;
    }
    hint.hidden = false;
  }

  private clearCommandHint(): void {
    const hint = this.commandHint;
    if (!hint) return;
    if (this.reactUiEnabled && !this.hintIsland?.hasFailed()) {
      // Only rendered islands need clearing; an untouched hint stays empty.
      if (this.hintIsland) this.renderHintReact('');
    } else {
      hint.textContent = '';
    }
    hint.hidden = true;
  }

  // --- Modes + config options ----------------------------------------------

  private renderModes(): void {
    if (!this.modeSelect) return;
    this.syncFallbackModeVisibility();
    if (this.modes.length === 0) {
      this.modeSelect.setItems([{ value: '', label: 'default' }], '');
      this.modeSelect.setDisabled(true);
      return;
    }

    this.modeSelect.setItems(
      this.modes.map((mode) => ({
        value: mode.id,
        label: mode.name || mode.id,
        title: mode.description || ''
      })),
      this.currentModeId ?? '');
    this.modeSelect.setDisabled(this.configControlsDisabled() || this.hasConfigOption('mode'));
  }

    private setCurrentMode(): void {
    const modeId = this.currentModeId;
    if (this.modeSelect && modeId) this.modeSelect.setValue(modeId);
    if (modeId) this.configSelects.get('mode')?.setValue(modeId);
  }

  private renderConfigOptions(): void {
    const host = this.configOptionsHost;
    if (!host) return;

    this.configSelects.forEach((menu) => menu.destroy());
    this.configSelects.clear();
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
            : next ? toggleValues!.enabled : toggleValues!.disabled;
          this.bridge()?.sendAgentCommand('set_config_option', value, configOption.id);
        });
        label.appendChild(switchButton);
        host.appendChild(label);
        return;
      }

      const menu = new MenuSelect((value) => {
        if (value) this.bridge()?.sendAgentCommand('set_config_option', value, configOption.id);
      }, configOption.name || configOption.id);
      menu.element.dataset.configId = configOption.id;
      menu.setItems(
        configOption.options.map((item) => ({
          value: item.value,
          label: item.name || item.value,
          title: item.description || ''
        })),
        typeof configOption.currentValue === 'string' ? configOption.currentValue : '');
      this.configSelects.set(configOption.id, menu);
      label.appendChild(menu.element);
      host.appendChild(label);
    });
  }

  private syncConfigOptionDisabledState(): void {
    const disabled = this.configControlsDisabled();
    this.configOptionsHost?.querySelectorAll<HTMLButtonElement>('button').forEach((control) => {
      control.disabled = disabled;
    });
    this.configSelects.forEach((menu) => menu.setDisabled(disabled));
  }

  private toggleValues(configOption: ComposerConfigOption): { enabled: string; disabled: string } | null {
    if (configOption.type !== 'select' || configOption.options.length !== 2) return null;
    const lookup = new Map(configOption.options.map((item) => [item.value.trim().toLowerCase(), item.value]));
    if (lookup.size !== 2) return null;
    if (lookup.has('on') && lookup.has('off')) return { enabled: lookup.get('on')!, disabled: lookup.get('off')! };
    if (lookup.has('true') && lookup.has('false')) return { enabled: lookup.get('true')!, disabled: lookup.get('false')! };
    return null;
  }

  private syncFallbackModeVisibility(): void {
    const modeLabel = this.modeSelect?.element.closest('label') as HTMLElement | null;
    if (!modeLabel) return;
    modeLabel.hidden = this.hasConfigOption('mode');
  }

  // --- Attachments ---------------------------------------------------------

  private wireAttachments(): void {
    this.lastSubmittedDraft = null;
    if (!this.attachButton || !this.attachmentInput || !this.attachmentStrip) return;

    this.on(this.attachButton, 'click', () => {
      if (!this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring) return;
      this.attachmentInput?.click();
    });

    this.on(this.attachmentInput, 'change', () => {
      this.handleAttachmentFiles(Array.from(this.attachmentInput?.files || []));
      if (this.attachmentInput) this.attachmentInput.value = '';
    });

    this.on(this.panel, 'paste', (event) => {
      if (!this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring) return;
      const clipboard = event as ClipboardEvent;
      const files = Array.from(clipboard.clipboardData?.items || [])
        .filter((item) => item.kind === 'file' && item.type.startsWith('image/'))
        .map((item) => item.getAsFile())
        .filter((file): file is File => !!file);
      if (files.length > 0) {
        clipboard.preventDefault();
        this.handleAttachmentFiles(files);
      }
    });

    this.on(this.panel, 'dragover', (event) => {
      if (!this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring) return;
      const drag = event as DragEvent;
      if (Array.from(drag.dataTransfer?.items || []).some((item) => item.kind === 'file')) {
        drag.preventDefault();
      }
    });

    this.on(this.panel, 'drop', (event) => {
      if (!this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring) return;
      const drag = event as DragEvent;
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
        if (event.target === this.imagePreview) this.hideImagePreview();
      });
    }
    this.on(this.imagePreviewClose, 'click', () => this.hideImagePreview());
  }

  private handleAttachmentFiles(files: File[]): void {
    const images = files.filter((file) => file && file.type && file.type.startsWith('image/'));
    if (!this.supportsImage) {
      this.host.appendSystemMessage(this.workspaceId, 'Current ACP Agent does not support image input.');
      return;
    }
    if (images.length === 0) return;

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

  private uploadAttachmentFile(file: File): void {
    const clientId = 'att-' + Date.now() + '-' + Math.random().toString(16).slice(2);
    const item: PendingAttachment = {
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

  private handleAttachmentUploaded(raw: RawHostMessage): void {
    const clientId = asString(raw.clientId);
    const attachment = raw.attachment as Record<string, unknown> | undefined;
    const item = this.pendingAttachments.find((entry) => entry.clientId === clientId);
    if (!item || !attachment) return;

    if (item.localPreviewUrl && item.url) URL.revokeObjectURL(item.url);
    Object.assign(item, attachment, { clientId, status: 'uploaded', localPreviewUrl: false });
    this.renderPendingAttachments();
  }

  /** Host push: legacy no longer owns the strip, so the controller marks the
   * failed tile. The system message is emitted here via the timeline seam. */
  private handleAttachmentFailed(raw: RawHostMessage): void {
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

  private markAttachmentReadError(clientId: string, message: string): void {
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

  private renderPendingAttachments(): void {
    const strip = this.attachmentStrip;
    if (!strip) return;
    strip.hidden = this.pendingAttachments.length === 0;
    if (this.reactUiEnabled && !this.attachmentsIsland?.hasFailed()) {
      this.renderAttachmentsReact();
      return;
    }
    strip.innerHTML = '';
    for (const attachment of this.pendingAttachments) {
      strip.appendChild(this.createAttachmentTile(attachment, true));
    }
  }

  // ----- React composer islands ---------------------------------------------
  //
  // Three independent roots (single-owner rule): the attachment strip pills,
  // the attach action row and the command hint text. Host visibility
  // attributes stay controller-owned; a load failure falls back permanently
  // to the legacy DOM writes via the shared island loader.

  private attachmentStripProps(): AttachmentStripProps {
    return {
      attachments: this.pendingAttachments.map((attachment) => ({
        clientId: attachment.clientId,
        fileName: attachment.fileName,
        status: attachment.status,
        url: attachment.url
      })),
      onPreview: (url) => this.showImagePreview(url),
      onRemove: (clientId) => this.removePendingAttachment(clientId)
    };
  }

  private renderAttachmentsReact(): void {
    this.attachmentsIsland ??= createIslandLoader<AttachmentStripProps>({
      name: 'composer-attachments',
      load: async () => {
        const mod = await import('./composerIsland.js');
        return (host) => mod.mountAttachmentStripIsland(host);
      },
      createHost: () => this.attachmentStrip,
      onLoadFailed: () => {
        if (this.panel) this.renderPendingAttachments();
      }
    });
    this.attachmentsIsland.render(this.attachmentStripProps());
  }

  private composerActionsProps(): ComposerActionsProps {
    return {
      attachDisabled:
        !this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring || this.isTranscriptOnly,
      attachTitle: this.supportsImage
        ? (this.runtimeReady() ? 'Attach images' : 'Install the Agent runtime before attaching images')
        : 'Current ACP Agent does not support image input',
      canPick: this.runtimeReady() && this.supportsImage && !this.isBusy && !this.isRestoring,
      onFiles: (files) => this.handleAttachmentFiles(files)
    };
  }

  private renderActionsReact(): void {
    this.actionsIsland ??= createIslandLoader<ComposerActionsProps>({
      name: 'composer-actions',
      load: async () => {
        const mod = await import('./composerIsland.js');
        return (host) => mod.mountComposerActionsIsland(host);
      },
      createHost: () => this.panel?.querySelector<HTMLElement>('.agent-composer-actions') ?? null,
      onLoadFailed: () => {
        if (this.panel) this.syncAttachmentControls();
      }
    });
    this.actionsIsland.render(this.composerActionsProps());
  }

  private renderHintReact(text: string): void {
    this.hintIsland ??= createIslandLoader<CommandHintProps>({
      name: 'command-hint',
      load: async () => {
        const mod = await import('./composerIsland.js');
        return (host) => mod.mountCommandHintIsland(host);
      },
      createHost: () => this.commandHint,
      onLoadFailed: (props) => {
        if (this.commandHint) this.commandHint.textContent = props.text;
      }
    });
    this.hintIsland.render({ text });
  }

  /**
   * Build a non-removable message attachment tile. Message attachments live in
   * the timeline thread (timeline domain owns them) but the attachment tile is a
   * composer-owned widget, so the timeline reaches this domain through the seam.
   */
  createMessageAttachmentTile(attachment: RawHostMessage): HTMLElement {
    const tileData: PendingAttachment = {
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

  private createAttachmentTile(attachment: PendingAttachment, removable: boolean): HTMLElement {
    const shell = document.createElement('div');
    shell.className = 'agent-attachment-shell';

    const tile = document.createElement('button');
    tile.type = 'button';
    tile.className = 'agent-attachment-tile agent-attachment-' + (attachment.status || 'ready');
    tile.title = attachment.fileName || 'Image attachment';
    tile.setAttribute('aria-label', 'Preview ' + (attachment.fileName || 'image attachment'));

    const image = document.createElement('img');
    // History-loaded attachments keep their original URL, but the backing file
    // may be long gone (cleaned .psx data, portable moves, provider transcript
    // replays). Swap in a neutral glyph and disable the preview instead of
    // showing the browser's broken-image icon.
    image.alt = attachment.fileName || 'Image attachment';
    image.addEventListener('error', () => {
      tile.classList.add('agent-attachment-broken');
      tile.title = (attachment.fileName || 'Image attachment') + ' (image unavailable)';
      image.replaceWith(ComposerController.createAttachmentGlyph());
    });
    if (attachment.url) {
      image.src = attachment.url;
      tile.appendChild(image);
    }
    else {
      tile.classList.add('agent-attachment-broken');
      tile.title = (attachment.fileName || 'Image attachment') + ' (image unavailable)';
      tile.appendChild(ComposerController.createAttachmentGlyph());
    }

    const name = document.createElement('span');
    name.className = 'agent-attachment-name';
    name.textContent = attachment.fileName || 'Image';
    tile.appendChild(name);

    if (attachment.status === 'uploading') {
      const status = document.createElement('span');
      status.className = 'agent-attachment-status';
      status.textContent = '...';
      tile.appendChild(status);
    }

    tile.addEventListener('click', () => {
      if (!tile.classList.contains('agent-attachment-broken'))
        this.showImagePreview(attachment.url);
    });
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

  private static createAttachmentGlyph(): SVGSVGElement {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    svg.classList.add('agent-attachment-glyph');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute(
      'd',
      'M19 5v14H5V5h14zm0-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-4.86 8.86-3 3.87L9 13.14 6 17h12l-3.86-5.14z'
    );
    svg.appendChild(path);
    return svg;
  }

  private removePendingAttachment(clientId: string): void {
    const index = this.pendingAttachments.findIndex((attachment) => attachment.clientId === clientId);
    if (index < 0) return;
    const [item] = this.pendingAttachments.splice(index, 1);
    if (item.localPreviewUrl && item.url) URL.revokeObjectURL(item.url);
    this.renderPendingAttachments();
  }

  private pendingAttachmentIds(): string[] {
    return this.pendingAttachments
      .filter((attachment) => attachment.status === 'uploaded' && attachment.id)
      .map((attachment) => attachment.id);
  }

  private attachmentsReady(): boolean {
    return !this.pendingAttachments.some((attachment) => attachment.status === 'uploading');
  }

  private clearPendingAttachments(): void {
    for (const item of this.pendingAttachments) {
      if (item.localPreviewUrl && item.url) URL.revokeObjectURL(item.url);
    }
    this.pendingAttachments = [];
    this.renderPendingAttachments();
  }

  private restoreSubmittedDraft(): void {
    if (!this.lastSubmittedDraft || !this.input) return;
    this.input.value = this.lastSubmittedDraft.text || '';
    this.pendingAttachments = (this.lastSubmittedDraft.attachments || []).slice();
    this.lastSubmittedDraft = null;
    this.resizeInput();
    this.renderPendingAttachments();
  }

  private showImagePreview(url: string): void {
    if (!this.imagePreview || !this.imagePreviewImg || !url) return;
    this.imagePreviewReturnFocus = document.activeElement;
    this.imagePreviewImg.src = url;
    if (!this.imagePreview.open) this.imagePreview.showModal();
    this.imagePreviewClose?.focus();
  }

  private hideImagePreview(): void {
    if (!this.imagePreview || !this.imagePreviewImg) return;
    if (this.imagePreview.open) this.imagePreview.close();
    this.imagePreviewImg.removeAttribute('src');
    const returnFocus = this.imagePreviewReturnFocus as HTMLElement | null;
    this.imagePreviewReturnFocus = null;
    if (returnFocus?.isConnected && typeof returnFocus.focus === 'function') returnFocus.focus();
  }

  private syncAttachmentControls(): void {
    if (this.reactUiEnabled && !this.actionsIsland?.hasFailed()) {
      this.renderActionsReact();
      return;
    }
    if (!this.attachButton) return;
    this.attachButton.disabled =
      !this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring || this.isTranscriptOnly;
    this.attachButton.title = this.supportsImage
      ? (this.runtimeReady() ? 'Attach images' : 'Install the Agent runtime before attaching images')
      : 'Current ACP Agent does not support image input';
  }

  // --- Helpers -------------------------------------------------------------

  private bridge(): AgentBridgePort | null {
    return this.host.bridgeFor(this.workspaceId);
  }

  private on(node: HTMLElement | null, type: string, handler: (event: Event) => void): void {
    if (!node) return;
    node.addEventListener(type, handler as EventListener);
    this.cleanup.push(() => node.removeEventListener(type, handler as EventListener));
  }
}
