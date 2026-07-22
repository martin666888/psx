// AgentWorkspaceRegistry.ts — routes decoded host events to controllers.
//
// Every raw host message flows through the HostEventDecoder first. Lifecycle
// events create/activate/destroy controllers; global events update the shared
// History store/broker, provider catalog and notice; workspace content events
// are routed to the matching controller (the decoder already guaranteed one
// exists). Closing removes the controller from the map BEFORE disposing so
// late-arriving events for that workspace are ignored, then tears down the
// panel shell.
//
// The registry also assembles the global History dock seam: entry.ts attaches
// the singleton HistoryDockController, and the registry feeds it workspace
// open-thread state after every event that can change it.

import type { RawHostMessage } from '../contracts/host-events.js';
import type { DecoderDiagnostics } from '../core/HostEventDecoder.js';
import { HostEventDecoder } from '../core/HostEventDecoder.js';
import { WorkspaceHost } from './WorkspaceHost.js';
import { PlanController } from '../plan/PlanController.js';
import { ComposerController } from '../composer/ComposerController.js';
import type { ComposerHost } from '../composer/ComposerController.js';
import { DecisionController } from '../decisions/DecisionController.js';
import type { DecisionHost } from '../decisions/DecisionController.js';
import { TimelineController } from '../timeline/TimelineController.js';
import type { TimelineHost } from '../timeline/TimelineController.js';
import { AgentWorkspaceController } from './AgentWorkspaceController.js';
import { AgentWorkspaceStore } from './AgentWorkspaceStore.js';
import { SessionRuntimeController } from './SessionRuntimeController.js';
import { AgentHistoryStore, normalizeProviderCatalog } from '../history/AgentHistoryStore.js';
import { AgentHistoryRequestBroker } from '../history/AgentHistoryRequestBroker.js';
import type { HistoryDockController, HistoryDockHost } from '../history/HistoryDockController.js';
import type { AgentShellLayoutHost } from '../shell/AgentShellLayoutController.js';

export class AgentWorkspaceRegistry {
  private readonly host: WorkspaceHost;
  private readonly store: AgentWorkspaceStore;
  private readonly decoder: HostEventDecoder;
  private readonly historyStore: AgentHistoryStore;
  private readonly historyBroker: AgentHistoryRequestBroker;
  private historyDock: HistoryDockController | null = null;
  private readonly controllers = new Map<string, AgentWorkspaceController>();
  // Plan controller refs by workspace: the shell layout coordinator needs the
  // ACTIVE workspace's card for the narrow one-at-a-time rule.
  private readonly planControllers = new Map<string, PlanController>();
  // Shell owns responsive layout. Registry retains the narrow result so a Plan
  // created while narrow immediately starts collapsed, and separately tracks
  // whether History must yield to preserve the reading column.
  private narrow = false;
  private historyReadingConstrained = false;
  private planVisibilityListener: ((expanded: boolean) => void) | null = null;
  // Routes an active-workspace notice (agent_workspace_limit_reached) to that
  // workspace's timeline, keeping the thread's single-writer invariant.
  private readonly noticeSinks = new Map<string, (text: string) => void>();

  constructor(host: WorkspaceHost, diagnostics?: DecoderDiagnostics) {
    this.host = host;
    this.store = new AgentWorkspaceStore();
    this.decoder = new HostEventDecoder((id) => this.controllers.has(id), diagnostics);
    // Global History: one store + one broker for the whole process. The broker
    // sees the same live-workspace set as the controllers map and sends every
    // `history` command through the best available workspace channel.
    this.historyStore = new AgentHistoryStore();
    this.historyBroker = new AgentHistoryRequestBroker(
      {
        isAlive: (id) => this.controllers.has(id),
        activeAgentWorkspace: () => this.activeAgentWorkspace(),
        bridgeFor: (id) => this.host.bridgeFor(id)
      },
      this.historyStore
    );
  }

  /** The seam entry.ts hands to the singleton HistoryDockController. */
  createHistoryDockHost(): HistoryDockHost {
    return {
      getState: () => this.historyStore.getState(),
      subscribe: (listener) => this.historyStore.subscribe(listener),
      requestRefresh: (originWorkspaceId) => this.historyBroker.requestRefresh(originWorkspaceId),
      sendCommandOnChannel: (command, value) => this.historyBroker.sendCommandOnChannel(command, value),
      hasAgentWorkspaces: () => this.controllers.size > 0,
      activeWorkspaceId: () => this.activeAgentWorkspace(),
      openWorkspaceThreadIds: () => this.openWorkspaceThreadIds()
    };
  }

  /** Called by entry.ts once the global dock exists (before any workspace is
   * created), so composer `/history` and open-state updates reach it. */
  attachHistoryDock(dock: HistoryDockController): void {
    this.historyDock = dock;
    dock.applyResponsiveCollapse(this.narrow || this.historyReadingConstrained);
  }

  /** The seam entry.ts hands to the AgentShellLayoutController: flat accessors
   * over the global dock plus the ACTIVE workspace's Plan card. */
  createShellLayoutHost(dock: HistoryDockController): AgentShellLayoutHost {
    const activePlan = (): PlanController | undefined =>
      this.planControllers.get(this.activeAgentWorkspace());
    return {
      setResponsiveLayout: (narrow, collapseHistoryForReading) =>
        this.setResponsiveLayout(narrow, collapseHistoryForReading),
      isHistoryOpen: () => dock.isOpen(),
      closeHistory: () => dock.requestClose(),
      onHistoryOpenChanged: (listener) => dock.onOpenChanged(listener),
      historyWidth: () => dock.getWidth(),
      onHistoryWidthChanged: (listener) => dock.onWidthChanged(listener),
      focusHistoryToggle: () => dock.focusToggle(),
      isActivePlanVisible: () => activePlan()?.isVisible() ?? false,
      closeActivePlan: () => activePlan()?.closeCard(),
      focusActivePlanToggle: () => activePlan()?.focusToggle(),
      onActivePlanVisibilityChanged: (listener) => {
        this.planVisibilityListener = listener;
      }
    };
  }

  /** Plan visibility reports arrive per workspace; only the active one drives
   * the narrow one-at-a-time rule. */
  private notifyPlanVisibility(workspaceId: string): void {
    if (workspaceId !== this.activeAgentWorkspace()) return;
    this.planVisibilityListener?.(this.planControllers.get(workspaceId)?.isVisible() ?? false);
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
          if (event.kind === 'agent') {
            this.historyBroker.activateWorkspace(event.workspaceId);
            // The active workspace changed: re-evaluate the narrow rule.
            this.notifyPlanVisibility(event.workspaceId);
          }
          this.historyDock?.updateOpenState();
        } else if (event.type === 'agent_workspace_created') {
          this.createController(event.workspaceId, event.raw);
        } else if (event.type === 'agent_workspace_closed') {
          this.closeController(event.workspaceId);
        }
        return;

      case 'agent-global':
        // History events keep their sender workspaceId but are global data:
        // the broker folds them into the single AgentHistoryStore.
        if (event.type === 'agent_providers') {
          this.historyStore.applyProviders(normalizeProviderCatalog(event.providers ?? []));
        } else if (event.type === 'agent_threads') {
          this.historyBroker.handleThreads(event.workspaceId ?? '', event.raw);
        } else if (event.type === 'agent_history_error') {
          this.historyBroker.handleHistoryError(event.workspaceId ?? '', event.raw);
        } else if (event.type === 'agent_history_invalidated') {
          this.historyBroker.handleInvalidated(event.workspaceId ?? '');
        } else if (event.type === 'agent_workspace_limit_reached') {
          this.showNotice(event.text ?? '');
        }
        return;

      case 'agent-workspace': {
        const state = this.store.reduce(event.workspaceId, event);
        this.controllers.get(event.workspaceId)?.update(event, state);
        // Thread binding changes the dock's Current/Open markers.
        if (event.type === 'agent_state' || event.type === 'agent_thread_loaded') {
          this.historyDock?.updateOpenState();
        }
        return;
      }
    }
  }

  hasController(workspaceId: string): boolean {
    return this.controllers.has(workspaceId);
  }

  private activeAgentWorkspace(): string {
    const id = this.host.activeWorkspace();
    return this.controllers.has(id) ? id : '';
  }

  private openWorkspaceThreadIds(): ReadonlyMap<string, string> {
    const map = new Map<string, string>();
    for (const workspaceId of this.controllers.keys()) {
      const threadId = this.store.get(workspaceId)?.session.currentThreadId ?? '';
      if (threadId) map.set(workspaceId, threadId);
    }
    return map;
  }

  private showNotice(text: string): void {
    // Legacy routed the notice to the active workspace's timeline; if the
    // active workspace is a terminal (no agent controller), it is a no-op.
    this.noticeSinks.get(this.host.activeWorkspace())?.(text || 'Unable to create Agent workspace.');
  }

  private setResponsiveLayout(narrow: boolean, collapseHistoryForReading: boolean): void {
    const narrowChanged = this.narrow !== narrow;
    const historyChanged = this.historyReadingConstrained !== collapseHistoryForReading;
    if (!narrowChanged && !historyChanged) return;
    const wasCollapsed = this.narrow || this.historyReadingConstrained;
    const willCollapse = narrow || collapseHistoryForReading;
    this.narrow = narrow;
    this.historyReadingConstrained = collapseHistoryForReading;
    // A user's temporary open survives repeated resizes inside the same mode,
    // but crossing between the reading-constrained and narrow modes returns to
    // the mode's deliberate collapsed default.
    this.historyDock?.applyResponsiveCollapse(willCollapse, wasCollapsed && willCollapse);
    if (narrowChanged) {
      for (const plan of this.planControllers.values()) plan.applyNarrow(narrow);
    }
  }

  private createController(workspaceId: string, createdRaw?: RawHostMessage): void {
    if (this.controllers.has(workspaceId)) return;
    this.host.createWorkspace(workspaceId);
    const state = this.store.create(workspaceId, createdRaw);
    const plan = new PlanController(workspaceId, this.host, {
      onVisibilityChanged: () => this.notifyPlanVisibility(workspaceId)
    });
    this.planControllers.set(workspaceId, plan);
    plan.applyNarrow(this.narrow);
    // The composer renders its own system messages (upload validation, "still
    // uploading", read errors) through the timeline seam so the thread keeps a
    // single writer, and opens the global History dock through the dock seam.
    const composerHost: ComposerHost = {
      getPanel: (id) => this.host.getPanel(id),
      bridgeFor: (id) => this.host.bridgeFor(id),
      appendSystemMessage: (_id, text) => timeline.appendSystemMessage(text),
      openHistory: (id) => this.historyDock?.openHistory(id)
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
      plan,
      composer,
      decisionController,
      timeline
    ]);
    this.controllers.set(workspaceId, controller);
    this.noticeSinks.set(workspaceId, (text) => timeline.appendSystemMessage(text));
    this.historyBroker.registerWorkspace(workspaceId);
    controller.mount();
    this.historyDock?.updateOpenState();
  }

  private closeController(workspaceId: string): void {
    const controller = this.controllers.get(workspaceId);
    if (!controller) return;
    this.controllers.delete(workspaceId);
    this.planControllers.delete(workspaceId);
    this.noticeSinks.delete(workspaceId);
    this.store.delete(workspaceId);
    this.historyBroker.unregisterWorkspace(workspaceId);
    controller.dispose();
    this.host.closeWorkspace(workspaceId);
    this.historyDock?.updateOpenState();
  }
}
