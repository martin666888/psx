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
import {
  ModeTransitionPromptController,
  type ModeTransitionPromptHost
} from '../decisions/ModeTransitionPromptController.js';
import { TimelineController } from '../timeline/TimelineController.js';
import type { TimelineHost } from '../timeline/TimelineController.js';
import { AgentWorkspaceController } from './AgentWorkspaceController.js';
import { AgentWorkspaceStore } from './AgentWorkspaceStore.js';
import { SessionRuntimeController } from './SessionRuntimeController.js';
import { WorkspaceToolbarController } from './WorkspaceToolbarController.js';
import { AgentHistoryStore, normalizeProviderCatalog } from '../history/AgentHistoryStore.js';
import { AgentHistoryRequestBroker } from '../history/AgentHistoryRequestBroker.js';
import { UsageStore } from '../usage/UsageStore.js';
import { UsageRequestBroker } from '../usage/UsageRequestBroker.js';
import type { UsagePanelHost } from '../usage/UsagePanelController.js';
import type { SettingsSection } from '../contracts/agent-usage.js';
import type { HistoryDockController, HistoryDockHost } from '../history/HistoryDockController.js';
import type { AgentShellLayoutHost } from '../shell/AgentShellLayoutController.js';

export class AgentWorkspaceRegistry {
  private readonly host: WorkspaceHost;
  private readonly store: AgentWorkspaceStore;
  private readonly decoder: HostEventDecoder;
  private readonly historyStore: AgentHistoryStore;
  private readonly historyBroker: AgentHistoryRequestBroker;
  private readonly usageStore: UsageStore;
  private readonly usageBroker: UsageRequestBroker;
  private historyDock: HistoryDockController | null = null;
  private readonly controllers = new Map<string, AgentWorkspaceController>();
  // Plan controller refs by workspace: the shell layout coordinator needs the
  // ACTIVE workspace's card for the narrow one-at-a-time rule.
  private readonly planControllers = new Map<string, PlanController>();
  private readonly toolbarControllers = new Map<string, WorkspaceToolbarController>();
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
    // sees the same live-workspace set as the controllers map, prefers the
    // best workspace channel and falls back to the process-wide bridge.
    this.historyStore = new AgentHistoryStore();
    this.historyBroker = new AgentHistoryRequestBroker(
      {
        isAlive: (id) => this.controllers.has(id),
        activeAgentWorkspace: () => this.activeAgentWorkspace(),
        bridgeFor: (id) => this.host.bridgeFor(id),
        sendGlobalCommand: (command, value, requestId) =>
          Bridge.sendAgentGlobalCommand(command, value, requestId)
      },
      this.historyStore
    );
    // Global Usage + profile: one store + one root-bridge broker for the whole
    // process, independent of Agent workspace lifecycle.
    this.usageStore = new UsageStore();
    this.usageBroker = new UsageRequestBroker(
      {
        sendGlobalCommand: (command, value, requestId) =>
          Bridge.sendAgentGlobalCommand(command, value, requestId),
        sendAppSettingsCommand: (action, requestId, registry) =>
          Bridge.sendAppSettingsCommand(action, requestId, registry)
      },
      this.usageStore
    );
  }

  /** The seam entry.ts hands to the singleton HistoryDockController. */
  createHistoryDockHost(): HistoryDockHost {
    return {
      getState: () => this.historyStore.getState(),
      subscribe: (listener) => this.historyStore.subscribe(listener),
      requestRefresh: (originWorkspaceId) => this.historyBroker.requestRefresh(originWorkspaceId),
      openThread: (threadId) => {
        const sent = this.historyBroker.sendCommandOnChannel('load_thread', threadId);
        if (sent) this.historyStore.clearThreadOpenError();
        return sent;
      },
      dismissThreadOpenError: () => this.historyStore.clearThreadOpenError(),
      activeWorkspaceId: () => this.activeAgentWorkspace(),
      openWorkspaceThreadIds: () => this.openWorkspaceThreadIds()
    };
  }

  /** The seam entry.ts hands to the singleton UsagePanelController. */
  createUsagePanelHost(): UsagePanelHost {
    return {
      getState: () => this.usageStore.getState(),
      subscribe: (listener) => this.usageStore.subscribe(() => listener()),
      requestUsage: (force) => this.usageBroker.requestUsage(force),
      requestConfig: (force) => this.usageBroker.requestConfig(force),
      setDisplayName: (name) => this.usageBroker.setDisplayName(name),
      setAvatar: (base64Png) => this.usageBroker.setAvatar(base64Png),
      setActiveTab: (section) => this.selectSettingsSection(section),
      setSettingsDraft: (registry) => this.usageStore.setSettingsDraft(registry),
      applyRegistry: (registry) => this.usageBroker.setDshRegistry(registry),
      setLocale: (mode: 'system' | 'zh-Hans' | 'zh-Hant' | 'en' | 'ja') => this.usageBroker.setLocale(mode),
      close: () => this.usageStore.setPanelOpen(false)
    };
  }

  /** Settings rail: one dialog with a left nav (profile / usage / config / registry). */
  openSettingsPanel(section: SettingsSection = 'profile'): void {
    this.usageStore.setActiveTab(section);
    this.usageStore.setPanelOpen(true);
    this.ensureSettingsSectionData(section);
  }

  toggleSettingsPanel(): void {
    if (this.usageStore.getState().panelOpen) {
      this.usageStore.setPanelOpen(false);
      return;
    }
    this.openSettingsPanel('profile');
  }

  /** Kept for tests and the pending-open path that still names a section. */
  openUsagePanel(tab: SettingsSection = 'usage'): void {
    this.openSettingsPanel(tab);
  }

  private selectSettingsSection(section: SettingsSection): void {
    this.usageStore.setActiveTab(section);
    this.ensureSettingsSectionData(section);
  }

  private ensureSettingsSectionData(section: SettingsSection): void {
    if (section === 'usage') this.usageBroker.requestUsage(false);
    else if (section === 'config') this.usageBroker.requestConfig(false);
    else if (section === 'registry' || section === 'language')
      this.usageBroker.requestSettings();
  }

  /** Called by entry.ts once the global dock exists (before any workspace is
   * created), so composer `/history` and open-state updates reach it. */
  attachHistoryDock(dock: HistoryDockController): void {
    this.historyDock = dock;
    dock.applyResponsiveCollapse(this.narrow || this.historyReadingConstrained);
    // The global dock's open state feeds every workspace toolbar toggle.
    dock.onOpenChanged((open) => {
      for (const toolbar of this.toolbarControllers.values()) toolbar.setHistoryOpen(open);
    });
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
      onHistoryWidthPreview: (listener) => dock.onWidthPreview(listener),
      focusHistoryToggle: () =>
        this.toolbarControllers.get(this.activeAgentWorkspace())?.focusHistoryToggle(),
      isActivePlanVisible: () => activePlan()?.isVisible() ?? false,
      closeActivePlan: () => activePlan()?.closeCard(),
      focusActivePlanToggle: () => activePlan()?.focusToggle(),
      onActivePlanVisibilityChanged: (listener) => {
        this.planVisibilityListener = listener;
      },
      measurePaneWidth: () => this.host.focusedAgentPanelWidth()
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
          // Render throttling follows panel visibility. When pane snapshots
          // drive the layout, visibility comes from applyLayoutVisibility;
          // the legacy path shows exactly the activated workspace.
          if (!this.host.isLayoutDriven()) {
            for (const [id, controller] of this.controllers) {
              controller.setVisible(event.kind === 'agent' && id === event.workspaceId);
              controller.setPaneFocused(event.kind === 'agent' && id === event.workspaceId);
            }
          }
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
        } else if (event.type === 'agent_thread_open_error') {
          // A failed load_thread reports straight to the global dock: it must
          // not enter the source conversation, and it stays visible even when
          // the source workspace (or its in-flight channel) is already gone.
          this.historyStore.applyThreadOpenError(event.threadId ?? '', event.text ?? '');
        } else if (event.type === 'agent_profile') {
          // Profile events are global: requester replies and broadcasts both
          // fold into the single UsageStore (revision-guarded).
          this.usageBroker.handleProfile(event.raw);
        } else if (event.type === 'agent_usage_report') {
          this.usageBroker.handleUsageReport(event.raw);
        } else if (event.type === 'agent_config_report') {
          this.usageBroker.handleConfigReport(event.raw);
        } else if (event.type === 'app_settings_snapshot') {
          this.usageBroker.handleAppSettings(event.raw);
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

  /** Column-snapshot driven render throttling and live-region gating: a
   * workspace renders while it is the active tab of any column, but only the
   * focused column's workspace may announce. Inactive tabs of visible columns
   * stay hidden keep-alive (same mechanism as the retired background set). */
  applyLayoutVisibility(snapshot: {
    focusedColumnId: string;
    columns: Array<{
      columnId: string;
      tabs?: Array<{ workspaceId?: string | null; kind?: string | null }>;
      activeTabId?: string | null;
      ratio?: number;
    }>;
  }): void {
    const focusedColumn = snapshot.columns.find((column) => column.columnId === snapshot.focusedColumnId);
    const focusedWorkspaceId = focusedColumn?.activeTabId ?? null;
    for (const [id, controller] of this.controllers) {
      controller.setVisible(
        snapshot.columns.some((column) =>
          column.activeTabId === id
          && (column.tabs?.some((tab) => tab.workspaceId === id && tab.kind === 'agent') ?? false)
        )
      );
      controller.setPaneFocused(id === focusedWorkspaceId);
    }
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
    // Notices belong to the active Agent workspace's timeline. If the active
    // workspace is a terminal (with no Agent controller), this is a no-op.
    this.noticeSinks.get(this.host.activeWorkspace())?.(text || 'Unable to create Agent workspace.');
  }

  private setResponsiveLayout(narrow: boolean, collapseHistoryForReading: boolean): void {
    const narrowChanged = this.narrow !== narrow;
    const historyChanged = this.historyReadingConstrained !== collapseHistoryForReading;
    if (!narrowChanged && !historyChanged) return;
    const wasCollapsed = this.historyReadingConstrained;
    const willCollapse = collapseHistoryForReading;
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
    // The toolbar island has exactly one writer; the Session/Plan controllers
    // and the global dock push their slices through it.
    const toolbar = new WorkspaceToolbarController(workspaceId, this.host);
    this.toolbarControllers.set(workspaceId, toolbar);
    const plan = new PlanController(workspaceId, this.host, {
      onVisibilityChanged: () => this.notifyPlanVisibility(workspaceId)
    }, toolbar);
    this.planControllers.set(workspaceId, plan);
    plan.applyNarrow(this.narrow);
    toolbar.setToggleHandlers(
      () => this.historyDock?.toggleFromToolbar(),
      () => plan.requestVisible(!plan.isVisible())
    );
    toolbar.setHistoryOpen(this.historyDock?.isOpen() ?? false);
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
    const promptHost: ModeTransitionPromptHost = {
      getPanel: (id) => this.host.getPanel(id),
      bridgeFor: (id) => this.host.bridgeFor(id),
      setComposerPrompt: (_id, prompt) => composer.setModeTransitionPrompt(prompt),
      focusComposerInput: () => composer.focusInput()
    };
    const promptController = new ModeTransitionPromptController(workspaceId, promptHost);
    const timelineHost: TimelineHost = {
      getPanel: (id) => this.host.getPanel(id),
      bridgeFor: (id) => this.host.bridgeFor(id),
      createMessageAttachmentTile: (_id, attachment) =>
        composer.createMessageAttachmentTile(attachment)
    };
    const timeline = new TimelineController(workspaceId, timelineHost);
    const controller = new AgentWorkspaceController(workspaceId, state, [
      toolbar,
      new SessionRuntimeController(workspaceId, this.host, toolbar),
      plan,
      composer,
      promptController,
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
    this.toolbarControllers.delete(workspaceId);
    this.noticeSinks.delete(workspaceId);
    this.store.delete(workspaceId);
    this.historyBroker.unregisterWorkspace(workspaceId);
    controller.dispose();
    this.host.closeWorkspace(workspaceId);
    this.historyDock?.updateOpenState();
  }

  /** Tears down every live controller and both global brokers (clearing their
   * pending timeout timers). entry.ts owns the dock/panel/shell singletons and
   * disposes those separately. Used by app teardown and test afterEach so a
   * closed app leaves no pending timer or subscription behind. */
  dispose(): void {
    for (const workspaceId of [...this.controllers.keys()]) {
      this.closeController(workspaceId);
    }
    this.historyBroker.dispose();
    this.usageBroker.dispose();
    this.historyDock = null;
    this.planVisibilityListener = null;
  }
}
