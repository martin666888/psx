// entry.ts — composition root for the Agent ESM app.
//
// main.js dynamically imports this module (a Vite dynamic chunk) AFTER it has
// already created the TerminalManager and sent `ready`. createAgentApp wires the
// native WorkspaceHost (panel shells + workspace-scoped bridges) to the registry
// and returns a single `handle(message)` sink. main.js drains its staging queue
// through this sink and forwards every later Agent host event to it.

import { WorkspaceHost } from './workspace/WorkspaceHost.js';
import { AgentWorkspaceRegistry } from './workspace/AgentWorkspaceRegistry.js';
import { HistoryDockController } from './history/HistoryDockController.js';
import { UsagePanelController } from './usage/UsagePanelController.js';
import { AgentShellLayoutController } from './shell/AgentShellLayoutController.js';
import { registerNamespace } from '../../webview/src/i18n.js';
import agentZhHans from './locales/zh-Hans/agent.json';
import agentEn from './locales/en/agent.json';
import type { RawHostMessage } from './contracts/host-events.js';
import type { SettingsSection } from './contracts/agent-usage.js';

// The Agent namespace ships with this lazy chunk for every released language,
// so a language switch never awaits resources (atomic single-frame switch).
registerNamespace('agent', {
  'zh-Hans': agentZhHans,
  en: agentEn
});

export interface AgentAppOptions {
  terminalManager: { setViewVisible(visible: boolean): void };
  container: HTMLElement;
  template: HTMLTemplateElement;
  /** Neutral pane geometry engine (webview layer); the dock inset feeds back
   * so panes shift when the global History dock opens or resizes. */
  paneLayout?: {
    setDockInset(px: number, options?: { interactiveResize?: boolean }): void;
  };
  /** Pass false when the app is lazily loaded for a non-History surface
   * (the rail settings dialog): mounting then must not restore the dock's
   * persisted open preference as a side effect. Defaults to true. */
  restoreHistoryDock?: boolean;
}

export interface AgentApp {
  handle(message: RawHostMessage): void;
  /** Pane snapshot + measured rects from the PaneLayoutController: projects
   * visibility/rects onto panels and drives render throttling. The columns
   * array is the EFFECTIVE presentation (only the focused column while
   * zoomed); active-tab workspaces own their column's rect. */
  setPaneLayout(
    snapshot: {
      focusedColumnId: string;
      columns: Array<{
        columnId: string;
        tabs?: Array<{ workspaceId?: string | null; kind?: string | null }>;
        activeTabId?: string | null;
        ratio?: number;
      }>;
    },
    rects: Map<string, { left: number; top: number; width: number; height: number; dockAdjacent?: boolean }>
  ): void;
  /** Opens the process-wide History dock. Used by the always-loaded rail when
   * it is the action that lazily starts the Agent app. */
  openHistory(): void;
  /** Opens the settings dialog on a section. Used by tests and pending-open. */
  openUsage(tab?: SettingsSection): void;
  openSettings(section?: SettingsSection): void;
  toggleSettings(): void;
  /** Tears down every controller, global broker, island and layout listener.
   * The shipped app runs for the process lifetime; tests call this in
   * afterEach so no app instance, subscription or pending timer survives. */
  dispose(): void;
}

export function createAgentApp(options: AgentAppOptions): AgentApp {
  // Surface the UI mode once per app start: visible in DevTools and on the
  // <body> for tests and diagnostics ("React" unless the emergency fallback
  // key disables it).
  const host = new WorkspaceHost(options.terminalManager, options.container, options.template);
  const registry = new AgentWorkspaceRegistry(host, {
    onIgnored(reason: string): void {
      console.debug('[agent] ignored host event:', reason);
    }
  });
  // The global History dock is a singleton living outside every workspace
  // panel; the registry owns its data seam, the host its view visibility.
  const historyDock = new HistoryDockController(registry.createHistoryDockHost());
  registry.attachHistoryDock(historyDock);
  historyDock.mount(options.container, options.restoreHistoryDock !== false);
  // The global Usage panel dialog is a second singleton beside the dock: the
  // rail settings menu opens it through the registry, the store's panelOpen drives it.
  const usagePanel = new UsagePanelController(registry.createUsagePanelHost());
  usagePanel.mount(options.container);
  // Shell layout: responsive narrow collapse + one-visible-panel rule.
  const shellLayoutHost = registry.createShellLayoutHost(historyDock);
  const shellLayout = new AgentShellLayoutController(options.container, shellLayoutHost);
  shellLayout.mount();

  // The global History dock indents every pane: report its width (plus the
  // workbench gutter and panel gap) to the neutral geometry engine.
  if (options.paneLayout) {
    // Ordering lock: markLayoutDriven must run before main.js drains the
    // staged events (createAgentApp returns only after this), so the
    // workspace_activated below early-returns in activate() without painting
    // the legacy fullscreen fallback; the compensating paneLayout.recompute()
    // at the tail of main.js' loadAgentApp must not be removed.
    host.markLayoutDriven();
    const paneLayout = options.paneLayout;
    const reportDockInset = (): void => {
      paneLayout.setDockInset(shellLayoutHost.isHistoryOpen() ? 40 + shellLayoutHost.historyWidth() + 24 : 40, {
        interactiveResize: false
      });
    };
    // Live dock drag frames move the columns on every preview; the final
    // settle still arrives through onHistoryWidthChanged (reportDockInset).
    shellLayoutHost.onHistoryWidthPreview((width) => {
      paneLayout.setDockInset(shellLayoutHost.isHistoryOpen() ? 40 + width + 24 : 40, { interactiveResize: true });
    });
    shellLayoutHost.onHistoryOpenChanged(reportDockInset);
    shellLayoutHost.onHistoryOpenChanged((open) => {
      document.dispatchEvent(new CustomEvent('psx-history-state', { detail: { open } }));
    });
    shellLayoutHost.onHistoryWidthChanged(reportDockInset);
    reportDockInset();
    document.dispatchEvent(new CustomEvent('psx-history-state', {
      detail: { open: shellLayoutHost.isHistoryOpen() }
    }));
  }

  const onGlobalHistoryToggle = (): void => historyDock.toggleFromToolbar();
  const onOpenSettings = (): void => {
    if (!disposed) registry.toggleSettingsPanel();
  };
  let disposed = false;
  document.addEventListener('psx-history-toggle', onGlobalHistoryToggle);
  document.addEventListener('psx-open-settings', onOpenSettings);

  return {
    handle(message: RawHostMessage): void {
      if (!disposed) registry.handle(message);
    },
    setPaneLayout(snapshot, rects): void {
      if (disposed) return;
      host.applyLayout(snapshot, rects);
      registry.applyLayoutVisibility(snapshot);
    },
    openHistory(): void {
      if (!disposed) historyDock.requestOpen('');
    },
    openUsage(tab = 'usage'): void {
      if (!disposed) registry.openSettingsPanel(tab);
    },
    openSettings(section = 'profile'): void {
      if (!disposed) registry.openSettingsPanel(section);
    },
    toggleSettings(): void {
      if (!disposed) registry.toggleSettingsPanel();
    },
    dispose(): void {
      if (disposed) return;
      disposed = true;
      // Order matters: drop the window/document listeners first (shell), then
      // tear workspaces + global brokers down (registry still reports open
      // state to the live dock), and finally unmount the global React islands.
      shellLayout.dispose();
      document.removeEventListener('psx-history-toggle', onGlobalHistoryToggle);
      document.removeEventListener('psx-open-settings', onOpenSettings);
      registry.dispose();
      usagePanel.dispose();
      historyDock.dispose();
    }
  };
}
