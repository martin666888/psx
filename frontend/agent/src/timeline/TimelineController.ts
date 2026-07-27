// TimelineController.ts — the Phase 4 Timeline domain owner.
//
// Owns the conversation thread and everything that streams into it:
//   * turns                         (agent-turn sections)
//   * user / assistant messages     (agent-message-*, streaming assistant deltas)
//   * thinking blocks               (agent-thinking / agent-thinking-block)
//   * tool run-groups + tool cards  (agent-run-group / agent-tool-card)
//   * inline tool output + system   (agent-tool / agent-system)
//   * recovery cards                (agent-recovery)
//   * the agent_thread_loaded replay loop and agent_cleared reset
//
// Faithful port of the legacy timeline engine so the produced DOM is
// byte-identical to the pre-refactor engine:
//   wwwroot/js/agent/thread.js    (_loadThread, _appendSystem, _appendRecovery,
//                                   _startTurn, _appendToCurrentHost)
//   wwwroot/js/agent/messages.js  (_appendMessage, _appendAssistantDelta,
//                                   _maybeCollapseUserMessage,
//                                   _finalizeAssistantMessage, _createCopyButton,
//                                   _copyText, _legacyCopy, _showCopyFeedback)
//   wwwroot/js/agent/thinking.js  (_showThinking, _appendThinkingDelta,
//                                   _hideThinking, _upgradeThinkingBlock,
//                                   _appendThinkingBlock, _createThinkingBlock)
//   wwwroot/js/agent/tools.js     (run-group + tool-card lifecycle)
//   wwwroot/js/agent/scroll.js    (_scrollToBottom, _isNearBottom)
//   wwwroot/js/agent/attachments.js (_appendMessageAttachments)
//   wwwroot/js/agent/modeTransition.js (_removeToolCardForModeTransition,
//                                   _scrollModeTransitionToStart)
//
// The thread is the single writer for its DOM. Two pieces the timeline arranges
// but does not own reach their domains through the TimelineHost seam:
//   * historical mode-transition cards during replay  -> DecisionController
//   * message attachment tiles                         -> ComposerController
// Plan replay (role === 'plan') is PlanController-owned; the reducer folds its
// own pass over the same messages, so the timeline skips that role while
// preserving the run-group bookkeeping the transition triggers.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';
import { createInitialWorkspaceState } from '../contracts/workspace-state.js';
import { isReactUiEnabled } from '../core/flags.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import { renderMarkdown as renderMarkdownCore, safeHref as safeHrefCore } from '../core/markdown.js';
import type { TimelineCallbacks, TimelineViewProps } from './TimelineView.js';
import { TimelineProjection, type DecisionItem, type DecisionOptionVM } from './timelineViewModel.js';

const AGENT_COPY_ICON_SVG = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"></rect><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path></svg>';
const AGENT_CHECK_ICON_SVG = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><polyline points="20 6 9 17 4 12"></polyline></svg>';

/** The strangler seam the timeline controller needs from the host. */
export interface TimelineHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
  /** Render a historical mode-transition card during replay (decisions domain). */
  renderHistoricalModeTransition(workspaceId: string, msg: RawHostMessage): void;
  /** Build a non-removable message attachment tile (composer domain). */
  createMessageAttachmentTile(workspaceId: string, attachment: RawHostMessage): HTMLElement;
  /** Replay one buffered event through the Decision legacy switch after the
   * timeline island failed to load (island fallback recovery). */
  replayDecisionEvent(workspaceId: string, type: string, raw: RawHostMessage): void;
}

/** One entry of the pre-commit replay buffer (island fallback recovery). */
type ReplayEntry =
  | { kind: 'event'; type: string; raw: RawHostMessage }
  | { kind: 'system'; text: string };

interface ToolCard {
  details: HTMLElement;
  body: HTMLElement;
  pre: HTMLElement;
}

interface CopyButton extends HTMLButtonElement {
  _copyTimer?: ReturnType<typeof setTimeout> | null;
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function asMessageArray(value: unknown): RawHostMessage[] {
  return Array.isArray(value) ? (value as RawHostMessage[]) : [];
}

export class TimelineController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: TimelineHost;

  private state: AgentWorkspaceState;

  private currentTurn: HTMLElement | null = null;
  private currentAssistant: HTMLElement | null = null;
  private currentToolBody: HTMLElement | null = null;
  private thinkingRow: HTMLElement | null = null;
  private thinkingContent: HTMLElement | null = null;
  private thinkingHasContent = false;
  private currentRunGroup: HTMLElement | null = null;
  private currentRunGroupBody: HTMLElement | null = null;
  private currentRunId: string | null = null;
  private toolCards: Record<string, ToolCard> = {};
  private currentToolCardId: string | null = null;
  private runToolCounts: { total: number; running: number } | null = null;
  private historyGroup: HTMLElement | null = null;
  private historyGroupBody: HTMLElement | null = null;
  private historyToolCounts: { total: number } | null = null;
  private autoScrollPinnedFlag = true;
  private scrollListener: (() => void) | null = null;
  private readonly animationFrames = new Set<number>();
  private readonly copyTimers = new Set<ReturnType<typeof setTimeout>>();

  // React timeline state (flag: psx.agent.experimental.react, default on).
  // One React root owns the whole thread subtree; events fold into the
  // controller-local projection and the tree re-renders from it. The legacy
  // writes below stay as the emergency fallback path.
  private reactUiEnabled = false;
  private readonly projection = new TimelineProjection();
  private timelineIsland: IslandLoader<TimelineViewProps> | null = null;
  // Until the React tree has committed once, every folded event is also kept
  // raw so a failed island load can replay it through the legacy writes with
  // nothing lost. After the first commit the import can no longer fail, so
  // the buffer is dropped (null = no longer collecting).
  private replayBuffer: ReplayEntry[] | null = [];

  constructor(workspaceId: string, host: TimelineHost) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.state = createInitialWorkspaceState(workspaceId);
  }

  mount(): void {
    // Thread scroll pinning belongs to the timeline domain (legacy composer.js
    // wired it regardless of composer ownership); track it here.
    const thread = this.thread;
    if (thread) {
      this.scrollListener = () => {
        this.autoScrollPinnedFlag = this.isNearBottom();
      };
      thread.addEventListener('scroll', this.scrollListener);
    }
    this.reactUiEnabled = isReactUiEnabled();
    // Kick the island load before the first bridge event so a load failure
    // almost always resolves while the thread is still empty (clean fallback).
    if (this.reactUiEnabled) this.renderReact();
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    this.state = state;
    const raw = event.raw;
    if (this.reactUiEnabled && !this.timelineIsland?.hasFailed()) {
      // The projection also folds the decision-domain events; the Decision
      // controller keeps only the composer-region prompt in react mode.
      this.replayBuffer?.push({ kind: 'event', type: event.type, raw });
      if (this.projection.apply(event.type, raw, this.assistantName)) {
        this.renderReact();
      }
      return;
    }
    this.applyLegacyEvent(event.type, raw);
  }

  /** The legacy event switch: the emergency render path and, after a failed
   * island load, the replay target for the buffered React-path events. */
  private applyLegacyEvent(type: string, raw: RawHostMessage): void {
    switch (type) {
      case 'agent_thread_loaded':
        this.loadThread(raw);
        break;
      case 'agent_cleared':
        this.handleCleared();
        break;
      case 'command_result':
        this.appendSystem(asString(raw.text));
        break;
      case 'user_message':
        this.finalizeRunGroup();
        this.startTurn();
        this.appendMessage('user', asString(raw.text));
        this.appendMessageAttachments(asMessageArray(raw.attachments));
        this.currentAssistant = null;
        break;
      case 'thinking_started':
        this.showThinking();
        break;
      case 'thinking_delta':
        this.appendThinkingDelta(asString(raw.text));
        break;
      case 'thinking_finished':
        this.hideThinking();
        break;
      case 'assistant_delta':
        this.appendAssistantDelta(asString(raw.text));
        break;
      case 'assistant_message_done':
        this.finalizeAssistantMessage(this.currentAssistant);
        this.currentAssistant = null;
        break;
      case 'run_finished':
        // The decisions domain interrupts a pending mode transition; the
        // timeline finalizes streaming state.
        this.finalizeAssistantMessage(this.currentAssistant);
        this.currentAssistant = null;
        this.hideThinking();
        this.finalizeRunGroup();
        this.currentTurn = null;
        break;
      case 'tool_started':
        this.ensureRunGroup(asString(raw.runId));
        this.createToolCard(
          asString(raw.toolCallId) || ('local-' + Date.now()),
          asString(raw.name) || 'Tool',
          asString(raw.input),
          asString(raw.summary),
          'running'
        );
        break;
      case 'tool_delta': {
        const resolved = this.resolveToolCard(raw);
        if (resolved) {
          resolved.card.pre.textContent += asString(raw.text);
        } else {
          this.appendToolDelta(asString(raw.name) || 'Tool output', asString(raw.text));
        }
        this.scrollToBottom();
        break;
      }
      case 'tool_finished': {
        const resolved = this.resolveToolCard(raw);
        if (resolved) {
          if (!resolved.card.pre.textContent || !resolved.card.pre.textContent.trim()) {
            resolved.card.pre.textContent = 'Finished.';
          }
          if (resolved.card.details.dataset.state === 'running' && this.runToolCounts) {
            this.runToolCounts.running = Math.max(0, this.runToolCounts.running - 1);
          }
          this.setToolCardState(resolved.card, asString(raw.status) || 'done');
          this.updateRunGroupSummary();
          resolved.card.details.removeAttribute('open');
          delete this.toolCards[resolved.toolCallId];
        } else {
          this.finishTool();
        }
        this.currentToolCardId = null;
        break;
      }
      case 'raw_terminal_fallback':
        this.appendTool(
          'Raw terminal fallback',
          asString(raw.text) || 'Unsupported interaction requires terminal fallback.',
          'fallback'
        );
        break;
      case 'run_failed':
        // The decisions domain interrupts the pending mode transition and the
        // composer restores the submitted draft; the timeline marks the failed
        // run and appends the error card.
        this.hideThinking();
        if (this.currentRunGroup) {
          this.currentRunGroup.classList.add('agent-run-group-error');
          Object.values(this.toolCards).forEach((card) => {
            if (card.details.dataset.state === 'running') {
              this.setToolCardState(card, 'error');
            }
          });
          if (this.runToolCounts) {
            this.runToolCounts.running = 0;
            this.updateRunGroupSummary();
          }
        }
        this.finalizeRunGroup();
        this.appendTool(this.assistantName + ' error', asString(raw.text) || 'Unknown error.', 'error');
        if (asString(raw.visionContextHint)) {
          this.appendSystem(asString(raw.visionContextHint));
        }
        break;
      case 'resume_failed':
        this.appendRecovery(
          asString(raw.message) || asString(raw.text) || (this.assistantName + ' could not resume this session.'),
          asString(raw.detail)
        );
        break;
      default:
        break;
    }
  }

  dispose(): void {
    const thread = this.thread;
    if (thread && this.scrollListener) {
      thread.removeEventListener('scroll', this.scrollListener);
    }
    for (const frame of this.animationFrames) cancelAnimationFrame(frame);
    this.animationFrames.clear();
    for (const timer of this.copyTimers) clearTimeout(timer);
    this.copyTimers.clear();
    this.timelineIsland?.dispose();
    this.timelineIsland = null;
    this.replayBuffer = null;
    this.scrollListener = null;
    this.currentTurn = null;
    this.currentAssistant = null;
    this.currentToolBody = null;
    this.thinkingRow = null;
    this.thinkingContent = null;
    this.toolCards = {};
  }

  // --- React timeline root ---------------------------------------------------

  /** The stable callback surface the React tree needs from this domain. */
  private timelineCallbacks(): TimelineCallbacks {
    return {
      copyText: (text) => this.copyPlainText(text),
      onOpenTerminal: () => {
        this.bridge()?.sendAgentCommand('terminal');
      },
      onDecisionOption: (item, option) => this.resolveDecisionFromReact(item, option),
      onElicitationAction: (item, payload, statusText) => {
        if (!item.requestId) return;
        this.bridge()?.sendAgentElicitationResponse(item.requestId, payload);
        this.projection.disableDecision(item.requestId, statusText);
        this.renderReact();
      },
      createAttachmentTile: (attachment) =>
        this.host.createMessageAttachmentTile(this.workspaceId, attachment as unknown as RawHostMessage)
    };
  }

  private resolveDecisionFromReact(item: DecisionItem, option: DecisionOptionVM): void {
    if (!item.requestId) return;
    if (item.kind === 'permission') {
      this.bridge()?.sendAgentPermissionResponse(item.requestId, option.optionId);
    } else if (item.kind === 'question') {
      this.bridge()?.sendAgentQuestionResponse(item.requestId, option.optionId);
    } else {
      return;
    }
    this.projection.selectDecisionOption(item.requestId, option.optionId, option.name);
    this.renderReact();
  }

  private renderReact(): void {
    this.timelineIsland ??= createIslandLoader<TimelineViewProps>({
      name: 'timeline',
      load: async () => {
        const mod = await import('./timelineIsland.js');
        return (host) => mod.mountTimelineIsland(host);
      },
      createHost: () => this.thread,
      onLoadFailed: () => {
        // Replay every event buffered before the first React commit through
        // the legacy writes so nothing folded into the projection is lost
        // (the load is kicked at mount, so the buffer is usually empty).
        this.replayBufferedEvents();
      }
    });
    this.timelineIsland.render({
      rows: this.projection.snapshot().rows,
      assistantName: this.assistantName,
      callbacks: this.timelineCallbacks(),
      // Scroll only after React commits: root.render() returns before the
      // DOM is updated, so an immediate scroll would read stale layout.
      onCommitted: () => {
        // First successful commit: the island import can no longer fail, so
        // stop collecting replay entries.
        this.replayBuffer = null;
        this.scrollToBottom();
      }
    });
  }

  /** Feed the pre-commit buffer through the legacy engines in arrival order:
   * the Decision legacy switch first, then the Timeline legacy switch — the
   * same per-event order the workspace controller uses in legacy mode. */
  private replayBufferedEvents(): void {
    const buffered = this.replayBuffer;
    this.replayBuffer = null;
    if (!buffered) return;
    for (const entry of buffered) {
      if (entry.kind === 'system') {
        this.appendSystem(entry.text);
      } else {
        this.host.replayDecisionEvent(this.workspaceId, entry.type, entry.raw);
        this.applyLegacyEvent(entry.type, entry.raw);
      }
    }
  }

  /** Plain clipboard write (legacy _copyText minus the button feedback). */
  private async copyPlainText(text: string): Promise<boolean> {
    const raw = typeof text === 'string' ? text : '';
    try {
      if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        await navigator.clipboard.writeText(raw);
        return true;
      }
      return this.legacyCopy(raw);
    } catch {
      try {
        return this.legacyCopy(raw);
      } catch {
        return false;
      }
    }
  }

  // --- Public timeline seam (used by decision / composer controllers) ------

  /** Append an element to the current turn host (legacy _appendToCurrentHost). */
  appendToTimeline(element: HTMLElement): void {
    this.appendToCurrentHost(element);
  }

  /** Scroll the thread to the bottom when pinned (legacy _scrollToBottom). */
  scrollTimelineToBottom(): void {
    this.scrollToBottom();
  }

  /** Render Markdown the same way the timeline engine does. */
  renderMarkdown(text: string): string {
    return renderMarkdownCore(text);
  }

  /** Sanitize an outbound href (legacy _safeHref); returns '' when unsafe. */
  safeHref(url: string): string {
    return safeHrefCore(url);
  }

  /** Build the shared copy button the timeline engine uses. */
  createCopyButton(getText: () => string): HTMLElement {
    const button = document.createElement('button') as CopyButton;
    button.type = 'button';
    button.className = 'agent-copy-button';
    button.innerHTML = AGENT_COPY_ICON_SVG;
    button.setAttribute('aria-label', '复制');
    button.title = '复制';
    button.addEventListener('click', () => {
      const text = typeof getText === 'function' ? getText() : getText;
      void this.copyText(text, button);
    });
    return button;
  }

  /** Whether the timeline is currently pinned to the bottom. */
  autoScrollPinned(): boolean {
    return this.autoScrollPinnedFlag;
  }

  /** Append a system row to the timeline (legacy _appendSystem). */
  appendSystemMessage(text: string): void {
    if (this.reactUiEnabled && !this.timelineIsland?.hasFailed()) {
      this.replayBuffer?.push({ kind: 'system', text });
      this.projection.appendSystemMessage(text);
      this.renderReact();
      return;
    }
    this.appendSystem(text);
  }

  /** Whether the React timeline island failed to load (island fallback).
   * The Decision controller gates its own React routing on this so both
   * domains fall back to the legacy engines together. */
  hasReactFailed(): boolean {
    return this.timelineIsland?.hasFailed() ?? false;
  }

  /** Remove the tool card a mode-transition replaces (legacy internals). */
  removeToolCardForModeTransition(toolCallId: string): void {
    if (!toolCallId) return;

    const tracked = this.toolCards[toolCallId];
    if (tracked?.details) {
      const wasRunning = tracked.details.dataset.state === 'running';
      tracked.details.remove();
      delete this.toolCards[toolCallId];

      if (this.runToolCounts) {
        this.runToolCounts.total = Math.max(0, this.runToolCounts.total - 1);
        if (wasRunning) {
          this.runToolCounts.running = Math.max(0, this.runToolCounts.running - 1);
        }
        this.updateRunGroupSummary();
        if (this.runToolCounts.total === 0 && this.currentRunGroup) {
          this.currentRunGroup.style.display = 'none';
        }
      }
    }

    const thread = this.thread;
    if (!thread) return;
    thread.querySelectorAll<HTMLElement>('[data-tool-id]').forEach((element) => {
      if (element.dataset.toolId !== toolCallId) return;
      if (element.closest('.agent-run-group') === this.currentRunGroup) return;
      element.remove();
    });
  }

  /** Scroll a freshly appended mode-transition card to the top of the viewport. */
  scrollModeTransitionToStart(card: HTMLElement): void {
    const thread = this.thread;
    if (!thread) return;
    this.requestFrame(() => {
      if (!card?.isConnected) return;
      const threadRect = thread.getBoundingClientRect();
      const cardRect = card.getBoundingClientRect();
      thread.scrollTop += cardRect.top - threadRect.top - 12;
      this.autoScrollPinnedFlag = this.isNearBottom();
    });
  }

  // --- Derived state -------------------------------------------------------

  private get assistantName(): string {
    return this.state.identity.assistantName;
  }

  private bridge(): AgentBridgePort | null {
    return this.host.bridgeFor(this.workspaceId);
  }

  private get thread(): HTMLElement | null {
    const panel = this.host.getPanel(this.workspaceId);
    return panel ? panel.querySelector<HTMLElement>('[data-role="thread"]') : null;
  }

  // --- Turns / hosting -----------------------------------------------------

  /** Faithful port of _startTurn. */
  private startTurn(): HTMLElement | null {
    const thread = this.thread;
    if (!thread) return null;
    const turn = document.createElement('section');
    turn.className = 'agent-turn';
    thread.appendChild(turn);
    this.currentTurn = turn;
    return turn;
  }

  /** Faithful port of _appendToCurrentHost. */
  private appendToCurrentHost(element: HTMLElement): void {
    const host = this.currentTurn || this.thread;
    if (host) host.appendChild(element);
  }

  // --- Scroll --------------------------------------------------------------

  /** Faithful port of _scrollToBottom. */
  private scrollToBottom(): void {
    const thread = this.thread;
    if (!this.autoScrollPinnedFlag || !thread) return;
    thread.scrollTop = thread.scrollHeight;
    this.autoScrollPinnedFlag = true;
  }

  /** Faithful port of _isNearBottom. */
  private isNearBottom(): boolean {
    const thread = this.thread;
    if (!thread) return true;
    const distance = thread.scrollHeight - thread.scrollTop - thread.clientHeight;
    return distance <= 48;
  }

  // --- System / recovery ---------------------------------------------------

  /** Faithful port of _appendSystem. */
  private appendSystem(text: string): void {
    const row = document.createElement('div');
    row.className = 'agent-system';
    row.textContent = text;
    this.appendToCurrentHost(row);
    this.scrollToBottom();
  }

  /** Faithful port of _appendRecovery. */
  private appendRecovery(message: string, detail: string): void {
    const thread = this.thread;
    if (!thread) return;
    let card = thread.querySelector<HTMLElement>('.agent-recovery');
    if (!card) {
      card = document.createElement('section');
      card.className = 'agent-recovery';
      card.setAttribute('role', 'status');
      card.setAttribute('aria-live', 'polite');
      card.setAttribute('aria-atomic', 'true');

      const content = document.createElement('div');
      content.className = 'agent-recovery-content';

      const title = document.createElement('div');
      title.className = 'agent-recovery-title';
      title.textContent = 'Session could not be resumed';

      const body = document.createElement('p');
      body.className = 'agent-recovery-message';

      const technicalDetails = document.createElement('details');
      technicalDetails.className = 'agent-recovery-details';

      const technicalSummary = document.createElement('summary');
      technicalSummary.textContent = 'Technical details';

      const technicalBody = document.createElement('pre');
      technicalBody.className = 'agent-recovery-technical';

      technicalDetails.appendChild(technicalSummary);
      technicalDetails.appendChild(technicalBody);
      content.appendChild(title);
      content.appendChild(body);
      content.appendChild(technicalDetails);

      const actions = document.createElement('div');
      actions.className = 'agent-recovery-actions';
      actions.appendChild(this.decisionButton(
        'Open terminal',
        () => this.bridge()?.sendAgentCommand('terminal'),
        'agent-btn-subtle'
      ));

      card.appendChild(content);
      card.appendChild(actions);
    }

    const body = card.querySelector<HTMLElement>('.agent-recovery-message');
    const technicalDetails = card.querySelector<HTMLDetailsElement>('.agent-recovery-details');
    const technicalBody = card.querySelector<HTMLElement>('.agent-recovery-technical');
    const technicalText = typeof detail === 'string' ? detail.trim() : '';

    if (body) body.textContent = message;
    if (technicalBody) technicalBody.textContent = technicalText;
    if (technicalDetails) {
      technicalDetails.hidden = !technicalText;
      if (!technicalText) technicalDetails.open = false;
    }

    thread.appendChild(card);
    this.scrollToBottom();
  }

  /** Minimal decision button used only by the recovery card action. */
  private decisionButton(label: string, action: () => void, className?: string): HTMLButtonElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    if (className) button.classList.add(className);
    button.addEventListener('click', () => action());
    return button;
  }

  // --- Messages ------------------------------------------------------------

  /** Faithful port of _appendMessage. */
  private appendMessage(role: string, text: string): HTMLElement | null {
    if (!this.currentTurn) {
      this.startTurn();
    }
    const turn = this.currentTurn;
    if (!turn) return null;

    const row = document.createElement('article');
    row.className = 'agent-message agent-message-' + role;
    row.setAttribute('aria-label', role === 'user' ? 'You' : this.assistantName);

    const hasTurnLabel = !!turn.querySelector('.agent-message-' + role);
    if (!hasTurnLabel) {
      const label = document.createElement('div');
      label.className = 'agent-message-label';
      label.textContent = role === 'user' ? 'You' : this.assistantName;
      row.appendChild(label);
    } else {
      row.classList.add('agent-message-continuation');
    }

    const body = document.createElement('div');
    body.className = 'agent-message-body';
    if (role === 'user') {
      const content = document.createElement('div');
      content.className = 'agent-message-content';
      const cleaned = (text || '').replace(
        /\{"type":"image"[^\}]*\}/g,
        '\n\n[image attachment]\n\n'
      );
      content.innerHTML = this.renderMarkdown(cleaned);
      body.appendChild(content);
    } else {
      body.dataset.raw = text || '';
      body.innerHTML = this.renderMarkdown(text);
    }

    row.appendChild(body);
    this.appendToCurrentHost(row);
    if (role === 'user') {
      this.maybeCollapseUserMessage(body);
    } else {
      this.finalizeAssistantMessage(body);
    }
    this.scrollToBottom();
    return body;
  }

  /** Faithful port of _appendAssistantDelta. */
  private appendAssistantDelta(text: string): void {
    if (!this.currentAssistant) {
      this.currentAssistant = this.appendMessage('assistant', '');
      if (this.currentAssistant) this.currentAssistant.dataset.raw = '';
    }
    if (!this.currentAssistant) return;

    this.currentAssistant.dataset.raw = (this.currentAssistant.dataset.raw || '') + text;
    this.currentAssistant.innerHTML = this.renderMarkdown(this.currentAssistant.dataset.raw);
    this.scrollToBottom();
  }

  /** Faithful port of _maybeCollapseUserMessage. */
  private maybeCollapseUserMessage(body: HTMLElement | null): void {
    const content = body?.querySelector<HTMLElement>('.agent-message-content');
    if (!body || !content || !content.textContent || !content.textContent.trim()) return;

    this.requestFrame(() => {
      this.requestFrame(() => {
        const style = window.getComputedStyle(content);
        const fontSize = parseFloat(style.fontSize) || 15;
        let lineHeight = parseFloat(style.lineHeight);
        if (!Number.isFinite(lineHeight)) {
          lineHeight = fontSize * 1.65;
        }

        const estimatedLines = Math.ceil(content.scrollHeight / lineHeight);
        if (estimatedLines < 17) return;

        body.classList.add('agent-message-collapsible', 'agent-message-collapsed');
        content.style.setProperty('--agent-user-message-collapsed-height', (lineHeight * 16) + 'px');

        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'agent-message-collapse-toggle';
        toggle.textContent = '展开';
        toggle.addEventListener('click', () => {
          const collapsed = body.classList.toggle('agent-message-collapsed');
          toggle.textContent = collapsed ? '展开' : '收起';
          if (!collapsed) {
            this.requestFrame(() => {
              const rect = body.getBoundingClientRect();
              if (rect.bottom > window.innerHeight) {
                const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
                body.scrollIntoView({ block: 'nearest', behavior: reduceMotion ? 'auto' : 'smooth' });
              }
            });
          }
        });

        content.appendChild(toggle);
      });
    });
  }

  /** Faithful port of _finalizeAssistantMessage (idempotent copy action bar). */
  private finalizeAssistantMessage(body: HTMLElement | null): void {
    if (!body) return;
    const raw = body.dataset.raw;
    if (!raw || raw.trim() === '') return;
    const article = body.parentElement;
    if (!article) return;
    if (article.querySelector('.agent-message-actions')) return;

    const actions = document.createElement('div');
    actions.className = 'agent-message-actions';

    const button = this.createCopyButton(() => body.dataset.raw || '');

    actions.appendChild(button);
    article.appendChild(actions);
  }

  /** Faithful port of _copyText. */
  private async copyText(text: string, button: CopyButton): Promise<void> {
    if (button._copyTimer) {
      clearTimeout(button._copyTimer);
      this.copyTimers.delete(button._copyTimer);
      button._copyTimer = null;
    }

    const raw = typeof text === 'string' ? text : '';
    let ok = false;

    try {
      if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        await navigator.clipboard.writeText(raw);
        ok = true;
      } else {
        ok = this.legacyCopy(raw);
      }
    } catch {
      try {
        ok = this.legacyCopy(raw);
      } catch {
        ok = false;
      }
    }

    this.showCopyFeedback(button, ok);
  }

  /** Faithful port of _legacyCopy. */
  private legacyCopy(text: string): boolean {
    const previousActive = document.activeElement as HTMLElement | null;
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.setAttribute('readonly', '');
    ta.style.position = 'fixed';
    ta.style.top = '0';
    ta.style.left = '0';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    let success = false;
    try {
      ta.focus();
      ta.select();
      success = document.execCommand('copy');
    } finally {
      document.body.removeChild(ta);
      if (previousActive && typeof previousActive.focus === 'function') {
        try { previousActive.focus(); } catch { /* ignore */ }
      }
    }
    return success;
  }

  /** Faithful port of _showCopyFeedback. */
  private showCopyFeedback(button: CopyButton, ok: boolean): void {
    if (button._copyTimer) {
      clearTimeout(button._copyTimer);
      button._copyTimer = null;
    }

    button.classList.remove('agent-copy-success', 'agent-copy-fail');
    // Force reflow so the class change reapplies when retriggered quickly.
    void button.offsetWidth;

    if (ok) {
      button.classList.add('agent-copy-success');
      button.innerHTML = AGENT_CHECK_ICON_SVG;
      button.setAttribute('aria-label', '已复制');
      button.title = '已复制';
    } else {
      button.classList.add('agent-copy-fail');
      button.setAttribute('aria-label', '复制失败');
      button.title = '复制失败';
    }

    const timer = setTimeout(() => {
      this.copyTimers.delete(timer);
      button.classList.remove('agent-copy-success', 'agent-copy-fail');
      button.innerHTML = AGENT_COPY_ICON_SVG;
      button.setAttribute('aria-label', '复制');
      button.title = '复制';
      button._copyTimer = null;
    }, 1500);
    button._copyTimer = timer;
    this.copyTimers.add(timer);
  }

  private requestFrame(callback: () => void): void {
    let frame = 0;
    let completed = false;
    frame = requestAnimationFrame(() => {
      completed = true;
      this.animationFrames.delete(frame);
      callback();
    });
    if (!completed) this.animationFrames.add(frame);
  }

  // --- Attachments (timeline arranges, composer builds the tile) -----------

  /** Faithful port of _appendMessageAttachments. */
  private appendMessageAttachments(attachments: RawHostMessage[]): void {
    if (!Array.isArray(attachments) || attachments.length === 0) return;
    const host = this.currentTurn || this.thread;
    if (!host) return;
    const userBodies = host.querySelectorAll<HTMLElement>('.agent-message-user .agent-message-body');
    const body = userBodies[userBodies.length - 1];
    if (!body) return;

    const grid = document.createElement('div');
    grid.className = 'agent-message-attachments';
    attachments.forEach((attachment) => {
      grid.appendChild(this.host.createMessageAttachmentTile(this.workspaceId, attachment));
    });

    const content = body.querySelector('.agent-message-content');
    if (content) {
      body.insertBefore(grid, content);
    } else {
      body.appendChild(grid);
    }
  }

  // --- Thinking ------------------------------------------------------------

  /** Faithful port of _showThinking. */
  private showThinking(): void {
    if (this.thinkingRow) return;
    const row = document.createElement('div');
    row.className = 'agent-thinking';
    row.setAttribute('role', 'status');
    row.setAttribute('aria-live', 'polite');
    row.setAttribute('aria-busy', 'true');
    row.innerHTML = '<span class="agent-spinner"></span><span>Thinking</span>';
    this.thinkingRow = row;
    this.thinkingContent = null;
    this.thinkingHasContent = false;
    this.appendToCurrentHost(row);
    this.scrollToBottom();
  }

  /** Faithful port of _appendThinkingDelta. */
  private appendThinkingDelta(text: string): void {
    if (!text) return;

    if (!this.thinkingRow || !this.thinkingContent) {
      this.upgradeThinkingBlock();
    }
    if (!this.thinkingContent) return;

    this.thinkingHasContent = true;
    this.thinkingContent.textContent = (this.thinkingContent.textContent || '') + text;
    this.scrollToBottom();
  }

  /** Faithful port of _hideThinking. */
  private hideThinking(): void {
    if (!this.thinkingRow) return;
    if (!this.thinkingHasContent) {
      this.thinkingRow.remove();
    } else {
      this.thinkingRow.classList.remove('agent-thinking-running');
      this.thinkingRow.removeAttribute('open');
      this.thinkingRow.setAttribute('aria-busy', 'false');
      const spinner = this.thinkingRow.querySelector('.agent-spinner');
      if (spinner) spinner.remove();
    }
    this.thinkingRow = null;
    this.thinkingContent = null;
    this.thinkingHasContent = false;
  }

  /** Faithful port of _upgradeThinkingBlock. */
  private upgradeThinkingBlock(): void {
    const previous = this.thinkingRow;
    const block = this.createThinkingBlock('', true);
    if (previous && previous.parentNode) {
      previous.replaceWith(block);
    } else {
      this.appendToCurrentHost(block);
    }
    this.thinkingRow = block;
    this.thinkingContent = block.querySelector<HTMLElement>('.agent-thinking-content');
  }

  /** Faithful port of _appendThinkingBlock. */
  private appendThinkingBlock(text: string, running: boolean): HTMLElement {
    const block = this.createThinkingBlock(text, running);
    this.appendToCurrentHost(block);
    this.scrollToBottom();
    return block;
  }

  /** Faithful port of _createThinkingBlock. */
  private createThinkingBlock(text: string, running: boolean): HTMLElement {
    const details = document.createElement('details');
    details.className = 'agent-thinking-block' + (running ? ' agent-thinking-running' : '');
    details.open = !!running;
    details.setAttribute('aria-busy', running ? 'true' : 'false');

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
  }

  // --- Tool run-groups + cards ---------------------------------------------

  /** Faithful port of _createRunGroup. */
  private createRunGroup(runId: string): void {
    const details = document.createElement('details');
    details.className = 'agent-run-group';
    details.open = true;
    details.dataset.runId = runId;

    const header = document.createElement('summary');
    header.className = 'agent-run-group-header';
    const chevron = document.createElement('span');
    chevron.className = 'agent-run-group-chevron';
    const summarySpan = document.createElement('span');
    summarySpan.className = 'agent-run-group-summary';
    summarySpan.textContent = 'Tool activity';
    header.appendChild(chevron);
    header.appendChild(summarySpan);

    const body = document.createElement('div');
    body.className = 'agent-run-group-body';

    details.appendChild(header);
    details.appendChild(body);
    details.style.display = 'none';

    this.appendToCurrentHost(details);
    this.currentRunGroup = details;
    this.currentRunGroupBody = body;
    this.currentRunId = runId;
    this.toolCards = {};
    this.runToolCounts = { total: 0, running: 0 };
    this.scrollToBottom();
  }

  /** Faithful port of _ensureRunGroup. */
  private ensureRunGroup(runId: string): void {
    if (this.currentAssistant && (this.currentAssistant.dataset.raw || '').trim()) {
      this.currentAssistant = null;
    }

    if (!runId) {
      if (!this.currentRunGroup) this.createRunGroup('local-' + Date.now());
      return;
    }

    if (this.currentRunGroup && this.currentRunId === runId) {
      return;
    }

    this.finalizeRunGroup();
    this.createRunGroup(runId);
  }

  /** Faithful port of _updateRunGroupSummary. */
  private updateRunGroupSummary(): void {
    if (!this.currentRunGroup || !this.runToolCounts) return;
    const { total, running } = this.runToolCounts;
    const label = total === 0 ? 'Tool activity'
      : 'Tool activity \u00b7 ' + total + ' call' + (total > 1 ? 's' : '')
        + (running > 0 ? ' \u00b7 ' + running + ' running' : '');
    const summary = this.currentRunGroup.querySelector('.agent-run-group-summary');
    if (summary) summary.textContent = label;
  }

  /** Faithful port of _setToolCardState. */
  private setToolCardState(card: ToolCard | null, state: string): void {
    if (!card?.details) return;
    const raw = String(state || 'done').toLowerCase();
    const normalized = ['done', 'completed', 'complete', 'success', 'succeeded'].includes(raw) ? 'done'
      : ['error', 'failed', 'failure'].includes(raw) ? 'error'
      : ['cancelled', 'canceled'].includes(raw) ? 'cancelled'
      : raw === 'fallback' ? 'fallback'
      : raw === 'running' ? 'running'
      : 'done';
    const labels: Record<string, string> = {
      running: 'Running',
      done: 'Done',
      error: 'Failed',
      cancelled: 'Cancelled',
      fallback: 'Needs terminal'
    };

    Array.from(card.details.classList)
      .filter((name) => name.startsWith('agent-tool-card-'))
      .forEach((name) => card.details.classList.remove(name));
    card.details.classList.add('agent-tool-card-' + normalized);
    card.details.dataset.state = normalized;
    const status = card.details.querySelector('.agent-tool-card-status');
    if (status) {
      status.textContent = labels[normalized];
      status.setAttribute('aria-label', 'Tool status: ' + labels[normalized]);
    }
  }

  /** Faithful port of _createToolCard. */
  private createToolCard(
    toolCallId: string,
    name: string,
    input: string,
    summary: string,
    state: string
  ): ToolCard | undefined {
    if (!this.currentRunGroupBody) return undefined;

    const existing = this.toolCards[toolCallId];
    if (existing) {
      if (input && !existing.pre.textContent) {
        existing.pre.textContent = input;
      }
      return existing;
    }

    const details = document.createElement('details');
    details.className = 'agent-tool-card';
    details.open = true;
    details.dataset.toolId = toolCallId;

    const header = document.createElement('summary');
    header.className = 'agent-tool-card-header';
    const summarySpan = document.createElement('span');
    summarySpan.className = 'agent-tool-card-summary';
    summarySpan.textContent = summary || name;
    const statusSpan = document.createElement('span');
    statusSpan.className = 'agent-tool-card-status';
    header.appendChild(summarySpan);
    header.appendChild(statusSpan);

    const body = document.createElement('div');
    body.className = 'agent-tool-card-body';
    const pre = document.createElement('pre');
    pre.className = 'agent-tool-card-content';
    pre.textContent = input;
    body.appendChild(pre);

    details.appendChild(header);
    details.appendChild(body);
    this.currentRunGroupBody.appendChild(details);

    this.toolCards[toolCallId] = { details, body, pre };
    this.setToolCardState(this.toolCards[toolCallId], state);
    this.currentToolCardId = toolCallId;

    if (this.runToolCounts) {
      this.runToolCounts.total++;
      if (details.dataset.state === 'running') this.runToolCounts.running++;
      this.updateRunGroupSummary();
    }

    if (this.currentRunGroup) {
      this.currentRunGroup.style.display = '';
    }
    this.scrollToBottom();
    return this.toolCards[toolCallId];
  }

  /** Faithful port of _resolveToolCard. */
  private resolveToolCard(event: RawHostMessage): { toolCallId: string; card: ToolCard } | null {
    const requestedId = asString(event.toolCallId) || this.currentToolCardId || '';
    if (requestedId && this.toolCards[requestedId]) {
      return { toolCallId: requestedId, card: this.toolCards[requestedId] };
    }

    this.ensureRunGroup(asString(event.runId));
    const toolCallId = requestedId || ('orphan-' + Date.now());
    const card = this.createToolCard(
      toolCallId,
      asString(event.name) || 'Tool',
      asString(event.input),
      asString(event.summary) || asString(event.name) || 'Tool output',
      'running'
    );

    return card ? { toolCallId, card } : null;
  }

  /** Faithful port of _finalizeRunGroup. */
  private finalizeRunGroup(): void {
    if (!this.currentRunGroup) return;
    if (this.runToolCounts && this.runToolCounts.total === 0) {
      this.currentRunGroup.remove();
    } else if (!this.currentRunGroup.classList.contains('agent-run-group-error')) {
      this.currentRunGroup.removeAttribute('open');
    }
    this.currentRunGroup = null;
    this.currentRunGroupBody = null;
    this.currentRunId = null;
    this.toolCards = {};
    this.currentToolCardId = null;
  }

  /** Faithful port of _createHistoryGroup. */
  private createHistoryGroup(runId: string): void {
    const details = document.createElement('details');
    details.className = 'agent-run-group';
    details.dataset.runId = runId;

    const header = document.createElement('summary');
    header.className = 'agent-run-group-header';
    const chevron = document.createElement('span');
    chevron.className = 'agent-run-group-chevron';
    const summarySpan = document.createElement('span');
    summarySpan.className = 'agent-run-group-summary';
    summarySpan.textContent = 'Tool activity';
    header.appendChild(chevron);
    header.appendChild(summarySpan);

    const body = document.createElement('div');
    body.className = 'agent-run-group-body';

    details.appendChild(header);
    details.appendChild(body);
    this.appendToCurrentHost(details);

    this.historyGroup = details;
    this.historyGroupBody = body;
    this.historyToolCounts = { total: 0 };
  }

  /** Faithful port of _appendHistoryToolCard. */
  private appendHistoryToolCard(msg: RawHostMessage): void {
    if (!this.historyGroupBody) return;

    const details = document.createElement('details');
    details.className = 'agent-tool-card';
    details.dataset.toolId = asString(msg.toolCallId);

    const header = document.createElement('summary');
    header.className = 'agent-tool-card-header';
    const summarySpan = document.createElement('span');
    summarySpan.className = 'agent-tool-card-summary';
    summarySpan.textContent = asString(msg.summary) || asString(msg.name) || 'Tool';
    const statusSpan = document.createElement('span');
    statusSpan.className = 'agent-tool-card-status';
    header.appendChild(summarySpan);
    header.appendChild(statusSpan);

    const body = document.createElement('div');
    body.className = 'agent-tool-card-body';
    const pre = document.createElement('pre');
    pre.className = 'agent-tool-card-content';
    pre.textContent = asString(msg.toolOutput) || asString(msg.text);
    body.appendChild(pre);

    details.appendChild(header);
    details.appendChild(body);
    this.historyGroupBody.appendChild(details);
    this.setToolCardState({ details, body, pre }, asString(msg.toolStatus) || 'done');

    if (this.historyToolCounts && this.historyGroup) {
      this.historyToolCounts.total++;
      const total = this.historyToolCounts.total;
      const label = 'Tool activity \u00b7 ' + total + ' call' + (total > 1 ? 's' : '');
      const summary = this.historyGroup.querySelector('.agent-run-group-summary');
      if (summary) summary.textContent = label;
    }
  }

  /** Faithful port of _finalizeHistoryGroup. */
  private finalizeHistoryGroup(): void {
    this.historyGroup = null;
    this.historyGroupBody = null;
    this.historyToolCounts = null;
  }

  /** Faithful port of _appendTool. */
  private appendTool(name: string, text: string, state: string): HTMLElement {
    const card = document.createElement('section');
    card.className = 'agent-tool agent-tool-' + state;
    card.dataset.state = state;
    const header = document.createElement('div');
    header.className = 'agent-tool-header';
    const title = document.createElement('span');
    title.textContent = name;
    const status = document.createElement('span');
    status.className = 'agent-tool-card-status';
    status.textContent = state === 'error' ? 'Failed' : state === 'fallback' ? 'Needs terminal' : 'Done';
    header.appendChild(title);
    header.appendChild(status);
    const body = document.createElement('pre');
    body.textContent = text;
    card.appendChild(header);
    card.appendChild(body);
    this.appendToCurrentHost(card);
    this.scrollToBottom();
    return body;
  }

  /** Faithful port of _appendToolDelta. */
  private appendToolDelta(name: string, text: string): void {
    if (!this.currentToolBody) {
      this.currentToolBody = this.appendTool(name, '', 'output');
    }
    this.currentToolBody.textContent = (this.currentToolBody.textContent || '') + text;
    this.scrollToBottom();
  }

  /** Faithful port of _finishTool. */
  private finishTool(): void {
    if (!this.currentToolBody) {
      this.appendTool('Tool', 'Finished.', 'done');
      return;
    }

    if (this.currentToolBody.textContent && this.currentToolBody.textContent.trim()) {
      this.currentToolBody.textContent += '\n\nFinished.';
    } else {
      this.currentToolBody.textContent = 'Finished.';
    }

    this.currentToolBody = null;
    this.scrollToBottom();
  }

  // --- Replay / clear ------------------------------------------------------

  /** Faithful port of the timeline slice of _loadThread. */
  private loadThread(event: RawHostMessage): void {
    if (event.clear) {
      // The mode-transition prompt is decisions-owned; the pending attachments
      // are composer-owned; the plan is inspector-owned. Those controllers reset
      // their own regions. Here the timeline resets the thread + its bookkeeping.
      const thread = this.thread;
      if (thread) thread.innerHTML = '';
      this.currentTurn = null;
      this.currentAssistant = null;
      this.currentToolBody = null;
      this.thinkingRow = null;
      this.thinkingContent = null;
      this.thinkingHasContent = false;
      this.currentRunGroup = null;
      this.currentRunGroupBody = null;
      this.currentRunId = null;
      this.toolCards = {};
      this.currentToolCardId = null;
      this.runToolCounts = null;
      this.historyGroup = null;
      this.historyGroupBody = null;
    }

    const messages = asMessageArray(event.messages);
    let lastRunId: string | null = null;

    for (const msg of messages) {
      const role = asString(msg.role);

      if (role === 'user') {
        if (this.historyGroup) this.finalizeHistoryGroup();
        this.startTurn();
        this.appendMessage('user', asString(msg.text));
        this.appendMessageAttachments(asMessageArray(msg.attachments));
        lastRunId = null;
        continue;
      }

      if (role === 'thinking') {
        if (this.historyGroup) this.finalizeHistoryGroup();
        if (!this.currentTurn) this.startTurn();
        this.appendThinkingBlock(asString(msg.text), false);
        lastRunId = null;
        continue;
      }

      if (role === 'plan') {
        // Plan replay is inspector-owned; only the timeline bookkeeping that a
        // plan message triggers is replayed here to keep tool grouping identical.
        if (this.historyGroup) this.finalizeHistoryGroup();
        lastRunId = null;
        continue;
      }

      if (role === 'mode_transition') {
        if (this.historyGroup) this.finalizeHistoryGroup();
        if (!this.currentTurn) this.startTurn();
        this.host.renderHistoricalModeTransition(this.workspaceId, {
          requestId: asString(msg.requestId),
          toolCallId: asString(msg.toolCallId),
          title: asString(msg.name),
          documentText: asString(msg.text),
          options: Array.isArray(msg.decisionOptions) ? msg.decisionOptions : [],
          selectedOptionId: asString(msg.selectedOptionId),
          decisionState: asString(msg.decisionState) || 'interrupted'
        });
        lastRunId = null;
        continue;
      }

      if (role === 'tool' && msg.runId) {
        const runId = asString(msg.runId);
        if (runId !== lastRunId) {
          this.finalizeHistoryGroup();
          this.createHistoryGroup(runId);
          lastRunId = runId;
        }
        this.appendHistoryToolCard(msg);
      } else {
        if (this.historyGroup) this.finalizeHistoryGroup();
        if (role === 'tool') {
          this.appendTool(asString(msg.name) || 'Tool', asString(msg.text), 'done');
        } else if (role === 'system') {
          this.appendSystem(asString(msg.text));
        } else {
          this.appendMessage(role === 'user' ? 'user' : 'assistant', asString(msg.text));
        }
      }
    }
    this.finalizeHistoryGroup();
    this.currentTurn = null;

    if (messages.length === 0) {
      this.appendSystem('Ready. ' + this.assistantName + ' will start on the first message. Working directory and session state are shown above.');
    }
  }

  /** Faithful port of the timeline slice of the agent_cleared handler. */
  private handleCleared(): void {
    const thread = this.thread;
    if (thread) thread.innerHTML = '';
    this.currentTurn = null;
    this.currentAssistant = null;
    this.currentToolBody = null;
    this.thinkingRow = null;
    this.thinkingContent = null;
    this.thinkingHasContent = false;
    this.currentRunGroup = null;
    this.currentRunGroupBody = null;
    this.currentRunId = null;
    this.toolCards = {};
    this.currentToolCardId = null;
    this.runToolCounts = null;
    this.historyGroup = null;
    this.historyGroupBody = null;
    this.historyToolCounts = null;
    this.appendSystem('Thread UI cleared. ' + this.assistantName + ' session context is unchanged.');
  }
}
