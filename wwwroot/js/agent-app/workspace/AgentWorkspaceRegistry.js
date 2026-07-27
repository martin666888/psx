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
import { HostEventDecoder } from '../core/HostEventDecoder.js';
import { PlanController } from '../plan/PlanController.js';
import { ComposerController } from '../composer/ComposerController.js';
import { DecisionController } from '../decisions/DecisionController.js';
import { TimelineController } from '../timeline/TimelineController.js';
import { AgentWorkspaceController } from './AgentWorkspaceController.js';
import { AgentWorkspaceStore } from './AgentWorkspaceStore.js';
import { SessionRuntimeController } from './SessionRuntimeController.js';
import { AgentHistoryStore, normalizeProviderCatalog } from '../history/AgentHistoryStore.js';
import { AgentHistoryRequestBroker } from '../history/AgentHistoryRequestBroker.js';
export class AgentWorkspaceRegistry {
    host;
    store;
    decoder;
    historyStore;
    historyBroker;
    historyDock = null;
    controllers = new Map();
    // Plan controller refs by workspace: the shell layout coordinator needs the
    // ACTIVE workspace's card for the narrow one-at-a-time rule.
    planControllers = new Map();
    // Shell owns responsive layout. Registry retains the narrow result so a Plan
    // created while narrow immediately starts collapsed, and separately tracks
    // whether History must yield to preserve the reading column.
    narrow = false;
    historyReadingConstrained = false;
    planVisibilityListener = null;
    // Routes an active-workspace notice (agent_workspace_limit_reached) to that
    // workspace's timeline, keeping the thread's single-writer invariant.
    noticeSinks = new Map();
    constructor(host, diagnostics) {
        this.host = host;
        this.store = new AgentWorkspaceStore();
        this.decoder = new HostEventDecoder((id) => this.controllers.has(id), diagnostics);
        // Global History: one store + one broker for the whole process. The broker
        // sees the same live-workspace set as the controllers map and sends every
        // `history` command through the best available workspace channel.
        this.historyStore = new AgentHistoryStore();
        this.historyBroker = new AgentHistoryRequestBroker({
            isAlive: (id) => this.controllers.has(id),
            activeAgentWorkspace: () => this.activeAgentWorkspace(),
            bridgeFor: (id) => this.host.bridgeFor(id)
        }, this.historyStore);
    }
    /** The seam entry.ts hands to the singleton HistoryDockController. */
    createHistoryDockHost() {
        return {
            getState: () => this.historyStore.getState(),
            subscribe: (listener) => this.historyStore.subscribe(listener),
            requestRefresh: (originWorkspaceId) => this.historyBroker.requestRefresh(originWorkspaceId),
            openThread: (threadId) => {
                const sent = this.historyBroker.sendCommandOnChannel('load_thread', threadId);
                if (sent)
                    this.historyStore.clearThreadOpenError();
                return sent;
            },
            dismissThreadOpenError: () => this.historyStore.clearThreadOpenError(),
            hasAgentWorkspaces: () => this.controllers.size > 0,
            activeWorkspaceId: () => this.activeAgentWorkspace(),
            openWorkspaceThreadIds: () => this.openWorkspaceThreadIds()
        };
    }
    /** Called by entry.ts once the global dock exists (before any workspace is
     * created), so composer `/history` and open-state updates reach it. */
    attachHistoryDock(dock) {
        this.historyDock = dock;
        dock.applyResponsiveCollapse(this.narrow || this.historyReadingConstrained);
    }
    /** The seam entry.ts hands to the AgentShellLayoutController: flat accessors
     * over the global dock plus the ACTIVE workspace's Plan card. */
    createShellLayoutHost(dock) {
        const activePlan = () => this.planControllers.get(this.activeAgentWorkspace());
        return {
            setResponsiveLayout: (narrow, collapseHistoryForReading) => this.setResponsiveLayout(narrow, collapseHistoryForReading),
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
    notifyPlanVisibility(workspaceId) {
        if (workspaceId !== this.activeAgentWorkspace())
            return;
        this.planVisibilityListener?.(this.planControllers.get(workspaceId)?.isVisible() ?? false);
    }
    handle(message) {
        const event = this.decoder.decode(message);
        if (!event)
            return;
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
                }
                else if (event.type === 'agent_workspace_created') {
                    this.createController(event.workspaceId, event.raw);
                }
                else if (event.type === 'agent_workspace_closed') {
                    this.closeController(event.workspaceId);
                }
                return;
            case 'agent-global':
                // History events keep their sender workspaceId but are global data:
                // the broker folds them into the single AgentHistoryStore.
                if (event.type === 'agent_providers') {
                    this.historyStore.applyProviders(normalizeProviderCatalog(event.providers ?? []));
                }
                else if (event.type === 'agent_threads') {
                    this.historyBroker.handleThreads(event.workspaceId ?? '', event.raw);
                }
                else if (event.type === 'agent_history_error') {
                    this.historyBroker.handleHistoryError(event.workspaceId ?? '', event.raw);
                }
                else if (event.type === 'agent_history_invalidated') {
                    this.historyBroker.handleInvalidated(event.workspaceId ?? '');
                }
                else if (event.type === 'agent_thread_open_error') {
                    // A failed load_thread reports straight to the global dock: it must
                    // not enter the source conversation, and it stays visible even when
                    // the source workspace (or its in-flight channel) is already gone.
                    this.historyStore.applyThreadOpenError(event.threadId ?? '', event.text ?? '');
                }
                else if (event.type === 'agent_workspace_limit_reached') {
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
    hasController(workspaceId) {
        return this.controllers.has(workspaceId);
    }
    activeAgentWorkspace() {
        const id = this.host.activeWorkspace();
        return this.controllers.has(id) ? id : '';
    }
    openWorkspaceThreadIds() {
        const map = new Map();
        for (const workspaceId of this.controllers.keys()) {
            const threadId = this.store.get(workspaceId)?.session.currentThreadId ?? '';
            if (threadId)
                map.set(workspaceId, threadId);
        }
        return map;
    }
    showNotice(text) {
        // Legacy routed the notice to the active workspace's timeline; if the
        // active workspace is a terminal (no agent controller), it is a no-op.
        this.noticeSinks.get(this.host.activeWorkspace())?.(text || 'Unable to create Agent workspace.');
    }
    setResponsiveLayout(narrow, collapseHistoryForReading) {
        const narrowChanged = this.narrow !== narrow;
        const historyChanged = this.historyReadingConstrained !== collapseHistoryForReading;
        if (!narrowChanged && !historyChanged)
            return;
        const wasCollapsed = this.narrow || this.historyReadingConstrained;
        const willCollapse = narrow || collapseHistoryForReading;
        this.narrow = narrow;
        this.historyReadingConstrained = collapseHistoryForReading;
        // A user's temporary open survives repeated resizes inside the same mode,
        // but crossing between the reading-constrained and narrow modes returns to
        // the mode's deliberate collapsed default.
        this.historyDock?.applyResponsiveCollapse(willCollapse, wasCollapsed && willCollapse);
        if (narrowChanged) {
            for (const plan of this.planControllers.values())
                plan.applyNarrow(narrow);
        }
    }
    createController(workspaceId, createdRaw) {
        if (this.controllers.has(workspaceId))
            return;
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
        const composerHost = {
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
        const decisionHost = {
            getPanel: (id) => this.host.getPanel(id),
            bridgeFor: (id) => this.host.bridgeFor(id),
            appendToTimeline: (_id, element) => timeline.appendToTimeline(element),
            scrollTimelineToBottom: (_id) => timeline.scrollTimelineToBottom(),
            renderMarkdown: (_id, text) => timeline.renderMarkdown(text),
            createCopyButton: (_id, getText) => timeline.createCopyButton(getText),
            safeHref: (_id, url) => timeline.safeHref(url),
            removeToolCardForModeTransition: (_id, toolCallId) => timeline.removeToolCardForModeTransition(toolCallId),
            autoScrollPinned: (_id) => timeline.autoScrollPinned(),
            scrollModeTransitionToStart: (_id, card) => timeline.scrollModeTransitionToStart(card),
            setComposerPromptActive: (_id, active) => composer.setModeTransitionPromptActive(active),
            focusComposerInput: () => composer.focusInput(),
            timelineReactFailed: (_id) => timeline.hasReactFailed()
        };
        const decisionController = new DecisionController(workspaceId, decisionHost);
        // The Timeline controller is the single writer for the thread. Two pieces it
        // arranges but does not own reach their domains through the host seam:
        // historical mode-transition cards go to the decision controller and message
        // attachment tiles are built by the composer controller.
        const timelineHost = {
            getPanel: (id) => this.host.getPanel(id),
            bridgeFor: (id) => this.host.bridgeFor(id),
            renderHistoricalModeTransition: (_id, msg) => decisionController.renderHistoricalModeTransition(msg),
            createMessageAttachmentTile: (_id, attachment) => composer.createMessageAttachmentTile(attachment),
            replayDecisionEvent: (_id, type, raw) => decisionController.replayLegacyEvent(type, raw)
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
    closeController(workspaceId) {
        const controller = this.controllers.get(workspaceId);
        if (!controller)
            return;
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
