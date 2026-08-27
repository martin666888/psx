// TimelineController.ts — React Timeline projection and side-effect owner.
//
// TimelineProjection is the single source of render state and the timeline
// island (mounted on [data-role="conversation-host"]) is the single writer
// for the thread subtree. Visible-time stick-to-bottom scrolling is owned by
// the AI Elements Conversation inside TimelineView; hide/show scroll
// snapshots stay with WorkspaceHost.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';
import { createInitialWorkspaceState } from '../contracts/workspace-state.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import type { TimelineCallbacks, TimelineViewProps } from './TimelineView.js';
import { TimelineProjection, type DecisionItem, type DecisionOptionVM } from './timelineViewModel.js';

export interface TimelineHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
  /** Message attachment tiles remain composer-owned widgets. */
  createMessageAttachmentTile(workspaceId: string, attachment: RawHostMessage): HTMLElement;
}

export class TimelineController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: TimelineHost;
  private state: AgentWorkspaceState;
  private readonly projection = new TimelineProjection();
  private timelineIsland: IslandLoader<TimelineViewProps> | null = null;
  private renderFrame: number | null = null;
  // Visibility throttling: hidden workspaces keep folding ACP events into the
  // projection but defer the React render; showing re-projects one snapshot.
  private visible = true;
  private renderPending = false;
  // Live regions only announce while this workspace's pane is focused.
  private paneFocused = true;

  constructor(workspaceId: string, host: TimelineHost) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.state = createInitialWorkspaceState(workspaceId);
  }

  setPaneFocused(focused: boolean): void {
    if (this.paneFocused === focused) return;
    this.paneFocused = focused;
    this.render();
  }

  setVisible(visible: boolean): void {
    if (this.visible === visible) return;
    this.visible = visible;
    if (!visible) {
      if (this.renderFrame !== null) {
        cancelAnimationFrame(this.renderFrame);
        this.renderFrame = null;
        this.renderPending = true;
      }
      return;
    }
    if (this.renderPending) {
      this.renderPending = false;
      this.render();
    }
  }

  mount(): void {
    if (!this.conversationHost) return;
    this.render();
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    this.state = state;
    if (this.projection.apply(event.type, event.raw, this.assistantName)) {
      // Streaming deltas arrive many times per frame; coalesce to one render.
      this.scheduleRender();
    } else if (event.type === 'runtime_status') {
      // Runtime is independent of the timeline projection, but it owns the
      // empty-conversation copy. Render immediately on its first snapshot and
      // on every lifecycle transition.
      this.render();
    }
  }

  dispose(): void {
    if (this.renderFrame !== null) {
      cancelAnimationFrame(this.renderFrame);
      this.renderFrame = null;
    }
    this.timelineIsland?.dispose();
    this.timelineIsland = null;
  }

  appendSystemMessage(text: string): void {
    this.projection.appendSystemMessage(text);
    this.render();
  }

  /** Fixed-locale-key system rows from composer/registry seams. */
  appendSystemCodeRow(key: string, params?: Record<string, unknown>): void {
    this.projection.appendSystemCodeRow(key, params);
    this.render();
  }

  private scheduleRender(): void {
    if (!this.visible) {
      this.renderPending = true;
      return;
    }
    if (this.renderFrame !== null) return;
    this.renderFrame = requestAnimationFrame(() => {
      this.renderFrame = null;
      this.render();
    });
  }

  private render(): void {
    if (!this.visible) {
      this.renderPending = true;
      return;
    }
    const host = this.conversationHost;
    if (!host) return;
    this.timelineIsland ??= createIslandLoader<TimelineViewProps>({
      name: 'timeline',
      load: async () => {
        const mod = await import('./timelineIsland.js');
        return (islandHost, reportFailure) =>
          mod.mountTimelineIsland(islandHost, reportFailure);
      },
      host
    });
    const snapshot = this.projection.snapshot();
    const runtimeState = this.state.runtime.state;
    const emptyKind = runtimeState === 'ready'
      ? 'ready'
      : runtimeState === 'installing'
        ? 'installing'
        : runtimeState === 'failed' || runtimeState === 'cancelled'
          ? 'unavailable'
          : 'missing';
    this.timelineIsland.render({
      rows: snapshot.rows,
      assistantName: this.assistantName,
      emptyState: snapshot.rows.length === 0 && this.state.runtime.statusKnown
        ? { kind: emptyKind, agentName: this.assistantName }
        : null,
      announce: this.visible && this.paneFocused,
      callbacks: this.callbacks()
    });
  }

  private callbacks(): TimelineCallbacks {
    return {
      copyText: (text) => this.copyText(text),
      onOpenTerminal: () => this.bridge()?.sendAgentCommand('terminal'),
      onDecisionOption: (item, option) => this.resolveDecision(item, option),
      onElicitationAction: (item, payload, statusCode) => {
        if (!item.requestId) return;
        if (item.kind === 'permission' && item.schema) {
          this.resolvePermissionForm(item, payload, statusCode);
          return;
        }
        this.bridge()?.sendAgentElicitationResponse(item.requestId, payload);
        this.projection.disableDecision(item.id, { code: `timeline.${statusCode}` });
        this.render();
      },
      createAttachmentTile: (attachment) =>
        this.host.createMessageAttachmentTile(
          this.workspaceId,
          attachment as unknown as RawHostMessage
        )
    };
  }

  private resolveDecision(item: DecisionItem, option: DecisionOptionVM): void {
    if (!item.requestId) return;
    if (
      item.kind === 'permission' ||
      item.kind === 'mode_transition' ||
      item.kind === 'document_permission'
    ) {
      // Document decisions are ACP permission requests presented as Markdown;
      // the backend awaits the same agent_permission_response.
      this.bridge()?.sendAgentPermissionResponse(item.requestId, option.optionId);
    } else if (item.kind === 'question') {
      this.bridge()?.sendAgentQuestionResponse(item.requestId, option.optionId);
    } else {
      return;
    }
    // Update the clicked card by stable timeline id. ACP requestId alone is
    // not unique across historical replay + a restarted agent process.
    this.projection.selectDecisionOption(item.id, option.optionId, option.name);
    this.render();
  }
  /**
   * Ask-user permission form: stay on agent_permission_response. Accept sends
   * JSON `{ optionId, content }`; decline/cancel send an offered reject option
   * or the shared `__cancelled__` sentinel.
   */
  private resolvePermissionForm(item: DecisionItem, payload: string, statusCode: string): void {
    if (!item.requestId) return;
    let action = 'cancel';
    let content: Record<string, unknown> = {};
    try {
      const parsed = JSON.parse(payload) as { action?: string; content?: Record<string, unknown> };
      action = typeof parsed.action === 'string' ? parsed.action : 'cancel';
      if (parsed.content && typeof parsed.content === 'object') content = parsed.content;
    } catch {
      action = 'cancel';
    }

    if (action === 'accept') {
      const optionId = item.formSubmitOptionId || resolveProceedOptionId(item.options);
      if (!optionId) return;
      this.bridge()?.sendAgentPermissionResponse(
        item.requestId,
        JSON.stringify({ optionId, content })
      );
      // No raw option label crosses into state; the card falls back to the
      // localized "selection recorded" wording.
      this.projection.selectDecisionOption(item.id, optionId, '');
      this.render();
      return;
    }

    const cancelId = resolveCancelOptionId(item.options);
    this.bridge()?.sendAgentPermissionResponse(item.requestId, cancelId || '__cancelled__');
    this.projection.disableDecision(item.id, { code: `timeline.${statusCode}` });
    this.render();
  }

  private async copyText(text: string): Promise<boolean> {
    const raw = typeof text === 'string' ? text : '';
    try {
      if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        await navigator.clipboard.writeText(raw);
        return true;
      }
      return this.copyViaExecCommand(raw);
    } catch {
      try {
        return this.copyViaExecCommand(raw);
      } catch {
        return false;
      }
    }
  }

  private copyViaExecCommand(text: string): boolean {
    const previousActive = document.activeElement as HTMLElement | null;
    const textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.setAttribute('readonly', '');
    textarea.style.position = 'fixed';
    textarea.style.opacity = '0';
    document.body.appendChild(textarea);
    try {
      textarea.focus();
      textarea.select();
      return document.execCommand('copy');
    } finally {
      textarea.remove();
      previousActive?.focus();
    }
  }

  private get assistantName(): string {
    return this.state.identity.assistantName;
  }

  private bridge(): AgentBridgePort | null {
    return this.host.bridgeFor(this.workspaceId);
  }

  private get conversationHost(): HTMLElement | null {
    return this.host
      .getPanel(this.workspaceId)
      ?.querySelector<HTMLElement>('[data-role="conversation-host"]') ?? null;
  }
}

function resolveProceedOptionId(options: DecisionOptionVM[]): string {
  const exact = options.find(
    (option) =>
      option.optionId === 'proceed_once' ||
      option.optionId === 'allow_once' ||
      option.optionId === 'allow'
  );
  if (exact) return exact.optionId;
  const byKind = options.find(
    (option) =>
      option.kind === 'allow_once' || option.kind === 'allow_always' || option.kind === 'allow'
  );
  if (byKind) return byKind.optionId;
  const fallback = options.find(
    (option) =>
      option.kind !== 'reject_once' &&
      option.kind !== 'reject_always' &&
      option.optionId !== 'cancel' &&
      !/reject|cancel/i.test(option.optionId)
  );
  return fallback?.optionId || '';
}

function resolveCancelOptionId(options: DecisionOptionVM[]): string {
  const exact = options.find(
    (option) =>
      option.optionId === 'cancel' ||
      option.optionId === 'reject' ||
      option.optionId === 'reject_once'
  );
  if (exact) return exact.optionId;
  const byKind = options.find(
    (option) => option.kind === 'reject_once' || option.kind === 'reject_always'
  );
  return byKind?.optionId || '';
}
