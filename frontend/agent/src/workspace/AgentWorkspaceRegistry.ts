// AgentWorkspaceRegistry.ts — routes decoded host events to controllers.
//
// Every raw host message flows through the HostEventDecoder first. Lifecycle
// events create/activate/destroy controllers; global events update the shared
// catalog/notice; workspace content events are routed to the matching
// controller (the decoder already guaranteed one exists). Closing removes the
// controller from the map BEFORE disposing so late-arriving events for that
// workspace are ignored, then tears down the panel shell.

import type { RawHostMessage } from '../contracts/host-events.js';
import type { DecoderDiagnostics } from '../core/HostEventDecoder.js';
import { HostEventDecoder } from '../core/HostEventDecoder.js';
import { WorkspaceHost } from './WorkspaceHost.js';
import { InspectorController } from '../inspector/InspectorController.js';
import { ComposerController } from '../composer/ComposerController.js';
import type { ComposerHost } from '../composer/ComposerController.js';
import { DecisionController } from '../decisions/DecisionController.js';
import type { DecisionHost } from '../decisions/DecisionController.js';
import { TimelineController } from '../timeline/TimelineController.js';
import type { TimelineHost } from '../timeline/TimelineController.js';
import { AgentWorkspaceController } from './AgentWorkspaceController.js';
import { AgentWorkspaceStore } from './AgentWorkspaceStore.js';
import { SessionRuntimeController } from './SessionRuntimeController.js';

export class AgentWorkspaceRegistry {
  private readonly host: WorkspaceHost;
  private readonly store: AgentWorkspaceStore;
  private readonly decoder: HostEventDecoder;
  private readonly controllers = new Map<string, AgentWorkspaceController>();
  // Routes an active-workspace notice (agent_workspace_limit_reached) to that
  // workspace's timeline, keeping the thread's single-writer invariant.
  private readonly noticeSinks = new Map<string, (text: string) => void>();

  constructor(host: WorkspaceHost, diagnostics?: DecoderDiagnostics) {
    this.host = host;
    this.store = new AgentWorkspaceStore();
    this.decoder = new HostEventDecoder((id) => this.controllers.has(id), diagnostics);
  }

  handle(message: RawHostMessage): void {
    const event = this.decoder.decode(message);
    if (!event) return;

    switch (event.scope) {
      case 'app':
        this.host.applySettings(event.settings ?? {});
        return;

      case 'lifecycle':
        if (event.type === 'workspace_activated') {
          this.host.activate(event.workspaceId, event.kind === 'agent' ? 'agent' : 'terminal');
        } else if (event.type === 'agent_workspace_created') {
          this.createController(event.workspaceId, event.raw);
        } else if (event.type === 'agent_workspace_closed') {
          this.closeController(event.workspaceId);
        }
        return;

      case 'agent-global':
        if (event.type === 'agent_providers') {
          this.host.setProviders(event.providers ?? []);
        } else if (event.type === 'agent_workspace_limit_reached') {
          this.showNotice(event.text ?? '');
        }
        return;

      case 'agent-workspace': {
        const state = this.store.reduce(event.workspaceId, event);
        this.controllers.get(event.workspaceId)?.update(event, state);
        return;
      }
    }
  }

  hasController(workspaceId: string): boolean {
    return this.controllers.has(workspaceId);
  }

  private showNotice(text: string): void {
    // Legacy routed the notice to the active workspace's timeline; if the
    // active workspace is a terminal (no agent controller), it is a no-op.
    this.noticeSinks.get(this.host.activeWorkspace())?.(text || 'Unable to create Agent workspace.');
  }

  private createController(workspaceId: string, createdRaw?: RawHostMessage): void {
    if (this.controllers.has(workspaceId)) return;
    this.host.createWorkspace(workspaceId);
    const state = this.store.create(workspaceId, createdRaw);
    const inspector = new InspectorController(workspaceId, this.host);
    // The composer renders its own system messages (upload validation, "still
    // uploading", read errors) through the timeline seam so the thread keeps a
    // single writer, and drives the optimistic `/history` load through the
    // inspector instance that owns the History tab.
    const composerHost: ComposerHost = {
      getPanel: (id) => this.host.getPanel(id),
      bridgeFor: (id) => this.host.bridgeFor(id),
      appendSystemMessage: (_id, text) => timeline.appendSystemMessage(text),
      beginHistoryLoading: () => inspector.beginHistoryLoading()
    };
    const composer = new ComposerController(workspaceId, composerHost);
    // The Decision controller places its cards into the timeline and reads
    // timeline internals through the TimelineController seam (single writer =
    // the timeline engine), and drives the composer-region prompt coordination
    // through the composer controller instance that owns those nodes.
    const decisionHost: DecisionHost = {
      getPanel: (id) => this.host.getPanel(id),
      bridgeFor: (id) => this.host.bridgeFor(id),
      appendToTimeline: (_id, element) => timeline.appendToTimeline(element),
      scrollTimelineToBottom: (_id) => timeline.scrollTimelineToBottom(),
      renderMarkdown: (_id, text) => timeline.renderMarkdown(text),
      createCopyButton: (_id, getText) => timeline.createCopyButton(getText),
      safeHref: (_id, url) => timeline.safeHref(url),
      removeToolCardForModeTransition: (_id, toolCallId) =>
        timeline.removeToolCardForModeTransition(toolCallId),
      autoScrollPinned: (_id) => timeline.autoScrollPinned(),
      scrollModeTransitionToStart: (_id, card) => timeline.scrollModeTransitionToStart(card),
      setComposerPromptActive: (_id, active) => composer.setModeTransitionPromptActive(active),
      focusComposerInput: () => composer.focusInput()
    };
    const decisionController = new DecisionController(workspaceId, decisionHost);
    // The Timeline controller is the single writer for the thread. Two pieces it
    // arranges but does not own reach their domains through the host seam:
    // historical mode-transition cards go to the decision controller and message
    // attachment tiles are built by the composer controller.
    const timelineHost: TimelineHost = {
      getPanel: (id) => this.host.getPanel(id),
      bridgeFor: (id) => this.host.bridgeFor(id),
      renderHistoricalModeTransition: (_id, msg) =>
        decisionController.renderHistoricalModeTransition(msg),
      createMessageAttachmentTile: (_id, attachment) =>
        composer.createMessageAttachmentTile(attachment)
    };
    const timeline = new TimelineController(workspaceId, timelineHost);
    const controller = new AgentWorkspaceController(workspaceId, state, [
      new SessionRuntimeController(workspaceId, this.host),
      inspector,
      composer,
      decisionController,
      timeline
    ]);
    this.controllers.set(workspaceId, controller);
    this.noticeSinks.set(workspaceId, (text) => timeline.appendSystemMessage(text));
    controller.mount();
  }

  private closeController(workspaceId: string): void {
    const controller = this.controllers.get(workspaceId);
    if (!controller) return;
    this.controllers.delete(workspaceId);
    this.noticeSinks.delete(workspaceId);
    this.store.delete(workspaceId);
    controller.dispose();
    this.host.closeWorkspace(workspaceId);
  }
}
