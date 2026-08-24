// ComposerController.ts — the Composer state/action adapter.
//
// ComposerView is the single React owner of the complete composer subtree and
// its UI state: controlled draft/IME handling, PromptInput attachments, slash
// menu, mode/config controls, submit/cancel button, transition prompt and
// preview dialog. This controller projects reduced AgentWorkspaceState into
// view props and retains protocol responsibilities: command validation,
// Bridge actions, attachment upload/server IDs and external restore tokens.
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
import { t as i18nT } from '../../../webview/src/i18n.js';
import { BridgeProtocolLimits } from '../contracts/bridgeProtocolLimits.generated.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type {
  AgentWorkspaceState,
  ComposerConfigOption
} from '../contracts/workspace-state.js';
import { createInitialWorkspaceState } from '../contracts/workspace-state.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import type {
  AttachmentStripProps,
  ComposerActionsProps
} from './ComposerBits.js';
import type {
  AttachmentRestoreItem,
  AttachmentSnapshotItem,
  PreparedAttachment
} from './AttachmentBridge.js';
import type {
  ComposerCommandMenuProps,
  ComposerControlsProps,
  ComposerViewProps
} from './ComposerView.js';
import type { ComposerModeTransitionPromptVM } from './ModeTransitionPrompt.js';

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

/** A controller-local pending image attachment (Bridge/upload state). */
interface PendingAttachment {
  clientId: string;
  retryKey?: string;
  file?: File;
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
  attachments: AttachmentRestoreItem[];
}

function role(panel: HTMLElement, name: string): HTMLElement | null {
  return panel.querySelector<HTMLElement>('[data-role="' + name + '"]');
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

export class ComposerController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: ComposerHost;

  private panel: HTMLElement | null = null;
  // Live regions only announce while this workspace's pane is focused.
  private paneFocused = true;

  setPaneFocused(focused: boolean): void {
    if (this.paneFocused === focused) return;
    this.paneFocused = focused;
    this.renderComposerIsland();
  }

  private state: AgentWorkspaceState;
  private psxCommands: MenuCommand[] = [];
  private commandIndex = 0;
  private visibleCommands: MenuCommand[] = [];
  private commandMenuOpen = false;
  private pendingAttachments: PendingAttachment[] = [];
  private attachmentSnapshot: AttachmentSnapshotItem[] = [];
  private attachmentResetToken = 0;
  private attachmentRestoreToken = 0;
  private attachmentRestoreItems: AttachmentRestoreItem[] = [];
  private readonly attachmentReaders = new Map<string, FileReader>();
  private lastSubmittedDraft: SubmittedDraft | null = null;
  private imagePreviewRequestToken = 0;
  private imagePreviewCloseToken = 0;
  private imagePreviewSrc = '';
  private draftText = '';
  private draftToken = 0;
  private focusToken = 0;

  // The island loader retains the latest projection while its dynamic import
  // is pending. The host is the only composer DOM node cached here.
  private composerHost: HTMLElement | null = null;
  private composerIsland: IslandLoader<ComposerViewProps> | null = null;
  private hintText = '';
  private hintVisible = false;
  private modeTransitionPrompt: ComposerModeTransitionPromptVM | null = null;

  constructor(workspaceId: string, host: ComposerHost) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.state = createInitialWorkspaceState(workspaceId);
  }

  mount(): void {
    const panel = this.host.getPanel(this.workspaceId);
    if (!panel) return;
    this.panel = panel;
    this.composerHost = role(panel, 'composer-host');
    this.lastSubmittedDraft = null;
    this.rebuildPsxCommands();
    // The first commit replays the current controller projection once more so
    // events received while the island loaded cannot be lost.
    this.renderComposerIsland();
  }

  /** Replay the current projection after the island's first commit. */
  private handleComposerReady(): void {
    this.renderComposerControls();
    this.renderPendingAttachments();
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    if (!this.panel) return;
    const identityChanged = state.identity.assistantName !== this.state.identity.assistantName;
    this.state = state;
    if (identityChanged) {
      this.rebuildPsxCommands();
      this.renderComposerIsland();
    }

    const raw = event.raw;
    switch (event.type) {
      case 'agent_state':
        this.renderComposerControls();
        this.renderComposerIsland();
        break;
      case 'runtime_status':
        this.syncRuntimeControls();
        this.renderComposerIsland();
        break;
      case 'agent_thread_loaded':
        this.clearCommandHint();
        if (raw.clear === true) {
          this.clearPendingAttachments();
          this.projectDraft('');
        }
        this.renderComposerControls();
        this.renderComposerIsland();
        break;
      case 'agent_commands':
        this.updateCommandMenu();
        break;
      case 'agent_command_rejected':
        this.showCommandHint(asString(raw.command), asString(raw.reason) || 'unsupported');
        break;
      case 'agent_modes':
        this.renderComposerIsland();
        break;
      case 'agent_config_options':
        this.renderComposerIsland();
        break;
      case 'agent_mode_current':
        this.renderComposerIsland();
        break;
      case 'agent_attachment_uploaded':
        this.handleAttachmentUploaded(raw);
        break;
      case 'agent_attachment_failed':
        this.handleAttachmentFailed(raw);
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
    for (const reader of this.attachmentReaders.values()) reader.abort();
    this.attachmentReaders.clear();
    this.pendingAttachments = [];
    this.attachmentSnapshot = [];
    this.attachmentRestoreItems = [];
    this.composerIsland?.dispose();
    this.composerIsland = null;
    this.composerHost = null;
    this.panel = null;
  }

  // --- Decision seam (composer-region coordination) ------------------------
  // The Decision controller owns the mode-transition prompt node, but that
  // prompt takes over the composer region: the input row + attachment strip
  // hide while it is shown and restore when it clears. Those nodes are
  // composer-owned, so the Decision controller drives them through this seam
  // instead of writing them directly (no dual-write). Faithful port of the
  // composer side of _showModeTransitionPrompt / _clearModeTransitionPrompt.

  setModeTransitionPrompt(prompt: ComposerModeTransitionPromptVM | null): void {
    this.modeTransitionPrompt = prompt;
    if (prompt) {
      this.clearCommandHint();
      this.hideCommandMenu();
    }
    this.renderComposerIsland();
  }

  /** Decision seam: return focus to the composer input (legacy this.input.focus()). */
  focusInput(): void {
    this.focusToken += 1;
    this.renderComposerIsland();
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

  private runtimeReady(): boolean {
    return this.state.runtime.state === 'ready';
  }

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
      { source: 'PSX', name: '/cwd', label: 'Show current working directory', command: 'cwd' },
      { source: 'PSX', name: '/cwd <path>', label: 'Change this draft Agent working directory', fill: '/cwd ' },
      { source: 'PSX', name: '/terminal', label: 'Open raw ' + assistant + ' terminal here', command: 'terminal' },
      { source: 'PSX', name: '/stop', label: 'Stop the current ' + assistant + ' run', command: 'stop' },
      { source: 'PSX', name: '/history', label: 'Show saved Agent threads', command: 'history' },
      { source: 'PSX', name: '/delete', label: 'Delete the current thread', command: 'delete' },
      { source: 'PSX', name: '/help', label: 'Show PSX Agent commands', command: 'help' }
    ];
  }

  // --- Composer draft + submit ---------------------------------------------

  private handleModeSelected(value: string): void {
    if (!value) return;
    if (this.hasConfigOption('mode')) {
      this.bridge()?.sendAgentCommand('set_config_option', value, 'mode');
    } else {
      this.bridge()?.sendAgentCommand('set_mode', value);
    }
  }

  private async submit(rawText: string, submittedFileCount: number): Promise<void> {
    let text = rawText.trim();
    // `/history` is global UI navigation, handled before every session guard:
    // it never enters busy state, never writes thread history and never
    // reaches the conversation as a prompt or a loading card.
    if (this.isHistoryNavigation(text)) {
      this.clearCommandHint();
      this.hideCommandMenu();
      this.host.openHistory(this.workspaceId);
      return;
    }
    if (!this.runtimeReady()) throw new Error('runtime_not_ready');
    if (this.isRestoring) throw new Error('restoring');
    if (this.isTranscriptOnly) throw new Error('transcript_only');
    if (this.isBusy) {
      this.bridge()?.sendAgentCommand('stop');
      throw new Error('busy');
    }
    if (text.length > BridgeProtocolLimits.agentPromptTextCharacters) {
      this.host.appendSystemMessage(this.workspaceId, 'Messages must be 1MB or smaller.');
      throw new Error('message_too_large');
    }

    const attachmentIds = this.pendingAttachmentIds();
    if (!text && attachmentIds.length === 0) throw new Error('empty');
    if (submittedFileCount !== this.attachmentSnapshot.length) {
      this.host.appendSystemMessage(
        this.workspaceId,
        'Attachments changed while sending. Review them, then send again.'
      );
      throw new Error('attachment_snapshot_mismatch');
    }

    const commandValidation = this.validateSubmissionCommand(text);
    if (!commandValidation.allowed) {
      this.showCommandHint(commandValidation.command || '', commandValidation.reason || 'unsupported');
      throw new Error('command_rejected');
    }
    text = commandValidation.text ?? text;
    if (!this.attachmentsReady()) {
      this.host.appendSystemMessage(
        this.workspaceId,
        'Images are still uploading. Wait for upload to finish, then send again.'
      );
      throw new Error('attachments_uploading');
    }
    this.clearCommandHint();
    this.hideCommandMenu();
    const retryAttachments = this.attachmentSnapshot
      .map((snapshot) => this.pendingAttachments.find((item) => item.clientId === snapshot.id))
      .filter((attachment): attachment is PendingAttachment =>
        !!attachment && !!attachment.file && !!attachment.retryKey)
      .map((attachment) => ({
        retryKey: attachment.retryKey!,
        file: attachment.file!,
        serverAttachmentId: attachment.id || undefined
      }));
    this.lastSubmittedDraft = { text, attachments: retryAttachments };
    this.draftText = '';
    this.bridge()?.sendAgentMessage(text, attachmentIds);
  }

  private cancel(): void {
    if (this.isBusy) this.bridge()?.sendAgentCommand('stop');
  }

  private handleDraftChange(text: string): void {
    this.draftText = text;
    this.clearCommandHint();
    this.updateCommandMenu();
  }

  private projectDraft(text: string): void {
    this.draftText = text;
    this.draftToken += 1;
    this.renderComposerIsland();
  }

  // --- Send/input/mode/config disabled sync --------------------------------

  /** Faithful port of the composer-owned part of thread.js/_updateState. */
  private renderComposerControls(): void {
    this.syncRuntimeControls();
  }

  /** Faithful port of runtime.js/_syncRuntimeControls (composer-owned nodes). */
  private syncRuntimeControls(): void {
    this.syncAttachmentControls();
  }

  // --- Command menu + hint -------------------------------------------------

  private updateCommandMenu(): void {
    const value = this.draftText.trimStart();
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

    this.commandIndex = Math.min(this.commandIndex, matches.length - 1);
    this.visibleCommands = matches;
    this.commandMenuOpen = true;
    this.renderComposerIsland();
  }

  private applyCommand(command: MenuCommand): void {
    if (command.fill) {
      this.clearCommandHint();
      this.projectDraft(command.fill);
      this.focusToken += 1;
      this.hideCommandMenu();
      return;
    }

    if (this.pendingAttachments.length > 0) {
      this.showCommandHint(command.name, 'attachments_not_allowed');
      return;
    }

    this.clearCommandHint();
    this.projectDraft('');
    this.hideCommandMenu();

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
    const shouldRender = this.commandMenuOpen || this.visibleCommands.length > 0 || this.commandIndex !== 0;
    this.commandMenuOpen = false;
    this.visibleCommands = [];
    this.commandIndex = 0;
    if (shouldRender) this.renderComposerIsland();
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
    // Whole-sentence keys with interpolation — never half-sentence splicing.
    const text = reason === 'attachments_not_allowed'
      ? i18nT('composer.hint.attachmentsNotAllowed', { ns: 'agent' })
      : reason === 'commands_loading'
        ? i18nT('composer.hint.commandsLoading', { ns: 'agent' })
        : i18nT('composer.hint.unsupported', {
            ns: 'agent',
            command,
            agentName: this.agentName
          });
    this.hintText = text;
    this.hintVisible = true;
    this.renderComposerIsland();
  }

  private clearCommandHint(): void {
    const hadVisibleHint = this.hintVisible;
    this.hintText = '';
    this.hintVisible = false;
    if (hadVisibleHint) this.renderComposerIsland();
  }

  // --- Modes + config options ----------------------------------------------

  private commandMenuId(): string {
    return 'agent-command-menu-' + this.workspaceId;
  }

  private commandOptionId(index: number): string {
    return this.commandMenuId() + '-option-' + index;
  }

  private composerCommandMenuProps(): ComposerCommandMenuProps {
    const items = this.visibleCommands.map((command, index) => ({
      id: this.commandOptionId(index),
      source: command.source,
      name: command.name,
      label: command.label
    }));
    return {
      id: this.commandMenuId(),
      open: this.commandMenuOpen,
      activeId: items[this.commandIndex]?.id ?? '',
      items,
      onHighlight: (id) => {
        const nextIndex = items.findIndex((item) => item.id === id);
        if (nextIndex < 0 || nextIndex === this.commandIndex) return;
        this.commandIndex = nextIndex;
        this.renderComposerIsland();
      },
      onSelect: (id) => {
        const index = items.findIndex((item) => item.id === id);
        const command = this.visibleCommands[index];
        if (command) this.applyCommand(command);
      },
      onDismiss: () => this.hideCommandMenu()
    };
  }

  private composerControlsProps(): ComposerControlsProps {
    const disabled = this.configControlsDisabled();
    return {
      mode: {
        id: 'mode',
        role: 'mode',
        label: 'mode',
        title: this.assistantName + ' Agent mode',
        value: this.currentModeId,
        items: this.modes.map((mode) => ({
          value: mode.id,
          label: mode.name || mode.id,
          title: mode.description || ''
        })),
        disabled: disabled || this.modes.length === 0 || this.hasConfigOption('mode'),
        hidden: this.hasConfigOption('mode'),
        onValueChange: (value) => this.handleModeSelected(value)
      },
      configs: this.configOptions.map((configOption) => {
        const label = configOption.name || configOption.id;
        const title = configOption.description || label;
        const toggleValues = this.toggleValues(configOption);
        if (configOption.type === 'boolean' || toggleValues) {
          const checked = configOption.type === 'boolean'
            ? configOption.currentValue === true
            : configOption.currentValue === toggleValues?.enabled;
          return {
            kind: 'switch' as const,
            id: configOption.id,
            label,
            title,
            checked,
            disabled,
            onCheckedChange: (next: boolean) => {
              const value = configOption.type === 'boolean'
                ? next
                : next ? toggleValues!.enabled : toggleValues!.disabled;
              this.bridge()?.sendAgentCommand('set_config_option', value, configOption.id);
            }
          };
        }
        return {
          kind: 'select' as const,
          id: configOption.id,
          label,
          title,
          value: typeof configOption.currentValue === 'string' ? configOption.currentValue : '',
          items: configOption.options.map((item) => ({
            value: item.value,
            label: item.name || item.value,
            title: item.description || ''
          })),
          disabled,
          onValueChange: (value: string) => {
            if (value) this.bridge()?.sendAgentCommand('set_config_option', value, configOption.id);
          }
        };
      })
    };
  }

  private toggleValues(configOption: ComposerConfigOption): { enabled: string; disabled: string } | null {
    if (configOption.type !== 'select' || configOption.options.length !== 2) return null;
    const lookup = new Map(configOption.options.map((item) => [item.value.trim().toLowerCase(), item.value]));
    if (lookup.size !== 2) return null;
    if (lookup.has('on') && lookup.has('off')) return { enabled: lookup.get('on')!, disabled: lookup.get('off')! };
    if (lookup.has('true') && lookup.has('false')) return { enabled: lookup.get('true')!, disabled: lookup.get('false')! };
    return null;
  }

  // --- Attachments ---------------------------------------------------------

  private handleAttachmentSnapshot(items: AttachmentSnapshotItem[]): void {
    if (
      items.length === this.attachmentSnapshot.length &&
      items.every((item, index) => {
        const current = this.attachmentSnapshot[index];
        return current?.id === item.id && current.url === item.url;
      })
    ) {
      return;
    }
    this.attachmentSnapshot = items.slice();
    this.renderPendingAttachments();
  }

  private handlePreparedAttachment(prepared: PreparedAttachment): void {
    if (this.pendingAttachments.some((item) => item.clientId === prepared.clientId)) return;

    const file = prepared.file;
    const item: PendingAttachment = {
      clientId: prepared.clientId,
      retryKey: prepared.retryKey,
      file,
      id: prepared.serverAttachmentId ?? '',
      fileName: file.name || 'image',
      mimeType: file.type,
      size: file.size,
      status: prepared.serverAttachmentId ? 'uploaded' : 'uploading',
      url: prepared.previewUrl,
      localPreviewUrl: false
    };
    this.pendingAttachments.push(item);
    this.renderPendingAttachments();

    if (prepared.serverAttachmentId) return;

    const reader = new FileReader();
    this.attachmentReaders.set(item.clientId, reader);
    reader.onload = () => {
      this.attachmentReaders.delete(item.clientId);
      if (!this.pendingAttachments.some((entry) => entry.clientId === item.clientId)) return;
      const dataUrl = String(reader.result || '');
      const comma = dataUrl.indexOf(',');
      this.bridge()?.uploadAgentAttachment({
        clientId: item.clientId,
        fileName: item.fileName,
        mimeType: item.mimeType,
        size: item.size,
        dataBase64: comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl
      });
    };
    reader.onerror = () => {
      this.attachmentReaders.delete(item.clientId);
      this.markAttachmentReadError(item.clientId, 'Failed to read image file.');
    };
    reader.readAsDataURL(file);
  }

  private handleAttachmentConstraintError(message: string): void {
    const normalized = message === 'All files exceed the maximum size.'
      ? 'Images must be 20MB or smaller.'
      : message === 'Too many files. Some were not added.'
      ? 'You can attach at most 5 images at once.'
      : message === 'No files match the accepted types.'
      ? 'Only image attachments are supported.'
      : message;
    this.host.appendSystemMessage(this.workspaceId, normalized);
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

  /** Marks the failed tile and emits a system message through the timeline seam. */
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
    this.renderComposerIsland();
  }

  // ----- React composer island ----------------------------------------------
  //
  // One island owns the whole composer subtree. The props below merge the
  // former attachment-strip / attach-actions / command-hint islands into one
  // React projection; the loader preserves the latest props until mount.

  private attachmentStripProps(): AttachmentStripProps {
    return {
      attachments: this.pendingAttachments.map((attachment) => ({
        clientId: attachment.clientId,
        status: attachment.status
      })),
      readOnly: this.configControlsDisabled(),
      onPreview: (url) => this.showImagePreview(url)
    };
  }

  private composerActionsProps(): ComposerActionsProps {
    return {
      attachDisabled:
        !this.runtimeReady() || !this.supportsImage || this.isBusy || this.isRestoring || this.isTranscriptOnly,
      attachTitle: this.supportsImage
        ? (this.runtimeReady() ? 'Attach images' : 'Install the Agent runtime before attaching images')
        : 'Current ACP Agent does not support image input',
      canPick: this.runtimeReady() && this.supportsImage && !this.isBusy && !this.isRestoring
    };
  }

  private renderComposerIsland(): void {
    const host = this.composerHost;
    if (!host) return;
    this.composerIsland ??= createIslandLoader<ComposerViewProps>({
      name: 'composer',
      load: async () => {
        const mod = await import('./composerIsland.js');
        return (islandHost, reportFailure) =>
          mod.mountComposerIsland(islandHost, reportFailure);
      },
      host
    });
    this.composerIsland.render({
      announce: this.paneFocused,
      attachmentBridge: {
        readOnly: this.configControlsDisabled() || !this.supportsImage,
        resetToken: this.attachmentResetToken,
        restoreToken: this.attachmentRestoreToken,
        restoreItems: this.attachmentRestoreItems,
        onSnapshot: (items) => this.handleAttachmentSnapshot(items),
        onPrepared: (item) => this.handlePreparedAttachment(item),
        onRemoved: (clientId) => this.removePendingAttachment(clientId),
        onError: (message) => this.handleAttachmentConstraintError(message)
      },
      attachments: this.attachmentStripProps(),
      actions: this.composerActionsProps(),
      attachmentsVisible:
        this.attachmentSnapshot.length > 0 && this.modeTransitionPrompt === null,
      commands: this.composerCommandMenuProps(),
      controls: this.composerControlsProps(),
      draft: {
        initialDraft: this.draftText,
        draftToken: this.draftToken,
        focusToken: this.focusToken,
        disabled: !this.runtimeReady() || this.isRestoring || this.isTranscriptOnly,
        busy: this.isBusy,
        placeholder: this.isTranscriptOnly
          ? 'Read-only transcript - create a new Agent tab to continue'
          : !this.runtimeReady()
          ? 'Install the Agent runtime to start messaging'
          : 'Message ' + this.assistantName + ' Agent - / for commands',
        submitDisabled: !this.runtimeReady() || this.isRestoring || this.isTranscriptOnly,
        submitLabel: this.isRestoring
          ? 'Loading'
          : this.isTranscriptOnly
          ? 'Read only'
          : this.isBusy
          ? 'Stop'
          : 'Send',
        submitTitle: this.isRestoring
          ? 'ACP history is loading'
          : this.isTranscriptOnly
          ? 'Create a new Agent tab to continue'
          : this.isBusy
          ? 'Stop ' + this.assistantName
          : 'Send message',
        onDraftChange: (text) => this.handleDraftChange(text),
        onSubmit: (message) => this.submit(message.text, message.files.length),
        onCancel: () => this.cancel()
      },
      hint: { text: this.hintText, visible: this.hintVisible },
      modeTransitionPrompt: this.modeTransitionPrompt,
      preview: {
        requestToken: this.imagePreviewRequestToken,
        closeToken: this.imagePreviewCloseToken,
        src: this.imagePreviewSrc
      },
      onReady: () => this.handleComposerReady()
    });
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
    return this.createAttachmentTile(tileData);
  }

  private createAttachmentTile(attachment: PendingAttachment): HTMLElement {
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
      status.textContent = '…';
      tile.appendChild(status);
    }

    tile.addEventListener('click', () => {
      if (!tile.classList.contains('agent-attachment-broken'))
        this.showImagePreview(attachment.url);
    });
    shell.appendChild(tile);

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
    const reader = this.attachmentReaders.get(clientId);
    if (reader) {
      reader.abort();
      this.attachmentReaders.delete(clientId);
    }
    if (index < 0) return;
    this.pendingAttachments.splice(index, 1);
    this.renderPendingAttachments();
  }

  private pendingAttachmentIds(): string[] {
    return this.attachmentSnapshot
      .map((snapshot) => this.pendingAttachments.find((item) => item.clientId === snapshot.id))
      .filter((attachment): attachment is PendingAttachment =>
        !!attachment && attachment.status === 'uploaded' && !!attachment.id)
      .map((attachment) => attachment.id);
  }

  private attachmentsReady(): boolean {
    return this.attachmentSnapshot.every((snapshot) => {
      const attachment = this.pendingAttachments.find((item) => item.clientId === snapshot.id);
      return !!attachment && attachment.status !== 'uploading';
    });
  }

  private clearPendingAttachments(): void {
    for (const reader of this.attachmentReaders.values()) reader.abort();
    this.attachmentReaders.clear();
    this.pendingAttachments = [];
    this.attachmentSnapshot = [];
    this.attachmentRestoreItems = [];
    this.attachmentResetToken += 1;
    this.hideImagePreview();
    this.renderPendingAttachments();
  }

  private restoreSubmittedDraft(): void {
    if (!this.lastSubmittedDraft) return;
    this.draftText = this.lastSubmittedDraft.text || '';
    this.draftToken += 1;
    this.pendingAttachments = [];
    this.attachmentSnapshot = [];
    this.attachmentRestoreItems = this.lastSubmittedDraft.attachments.slice();
    this.attachmentRestoreToken += 1;
    this.lastSubmittedDraft = null;
    this.renderPendingAttachments();
  }

  private showImagePreview(url: string): void {
    if (!url) return;
    this.imagePreviewSrc = url;
    this.imagePreviewRequestToken += 1;
    this.renderComposerIsland();
  }

  private hideImagePreview(): void {
    if (!this.imagePreviewSrc) return;
    this.imagePreviewSrc = '';
    this.imagePreviewCloseToken += 1;
  }

  private syncAttachmentControls(): void {
    this.renderComposerIsland();
  }

  // --- Helpers -------------------------------------------------------------

  private bridge(): AgentBridgePort | null {
    return this.host.bridgeFor(this.workspaceId);
  }
}
