// TimelineController.ts — React Timeline projection and side-effect owner.
//
// TimelineProjection is the single source of render state and one React root
// is the single writer for [data-role="thread"] children.

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
  private autoScrollPinned = true;
  private scrollListener: (() => void) | null = null;

  constructor(workspaceId: string, host: TimelineHost) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.state = createInitialWorkspaceState(workspaceId);
  }

  mount(): void {
    const thread = this.thread;
    if (!thread) return;
    this.scrollListener = () => {
      this.autoScrollPinned = this.isNearBottom();
    };
    thread.addEventListener('scroll', this.scrollListener);
    this.render();
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    this.state = state;
    if (this.projection.apply(event.type, event.raw, this.assistantName)) {
      this.render();
    }
  }

  dispose(): void {
    const thread = this.thread;
    if (thread && this.scrollListener) {
      thread.removeEventListener('scroll', this.scrollListener);
    }
    this.scrollListener = null;
    this.timelineIsland?.dispose();
    this.timelineIsland = null;
  }

  appendSystemMessage(text: string): void {
    this.projection.appendSystemMessage(text);
    this.render();
  }

  private render(): void {
    const host = this.thread;
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
    this.timelineIsland.render({
      rows: this.projection.snapshot().rows,
      assistantName: this.assistantName,
      callbacks: this.callbacks(),
      onCommitted: () => this.scrollToBottom()
    });
  }

  private callbacks(): TimelineCallbacks {
    return {
      copyText: (text) => this.copyText(text),
      onOpenTerminal: () => this.bridge()?.sendAgentCommand('terminal'),
      onDecisionOption: (item, option) => this.resolveDecision(item, option),
      onElicitationAction: (item, payload, statusText) => {
        if (!item.requestId) return;
        this.bridge()?.sendAgentElicitationResponse(item.requestId, payload);
        this.projection.disableDecision(item.requestId, statusText);
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
    if (item.kind === 'permission') {
      this.bridge()?.sendAgentPermissionResponse(item.requestId, option.optionId);
    } else if (item.kind === 'question') {
      this.bridge()?.sendAgentQuestionResponse(item.requestId, option.optionId);
    } else {
      return;
    }
    this.projection.selectDecisionOption(item.requestId, option.optionId, option.name);
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

  private scrollToBottom(): void {
    const thread = this.thread;
    if (!thread || !this.autoScrollPinned) return;
    thread.scrollTop = thread.scrollHeight;
    this.autoScrollPinned = true;
  }

  private isNearBottom(): boolean {
    const thread = this.thread;
    if (!thread) return true;
    return thread.scrollHeight - thread.scrollTop - thread.clientHeight <= 48;
  }

  private get assistantName(): string {
    return this.state.identity.assistantName;
  }

  private bridge(): AgentBridgePort | null {
    return this.host.bridgeFor(this.workspaceId);
  }

  private get thread(): HTMLElement | null {
    return this.host
      .getPanel(this.workspaceId)
      ?.querySelector<HTMLElement>('[data-role="thread"]') ?? null;
  }
}
