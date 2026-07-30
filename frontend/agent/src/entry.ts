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
import type { RawHostMessage } from './contracts/host-events.js';

export interface AgentAppOptions {
  terminalManager: { setViewVisible(visible: boolean): void };
  container: HTMLElement;
  template: HTMLTemplateElement;
}

export interface AgentApp {
  handle(message: RawHostMessage): void;
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
  host.setHistoryDockView(historyDock);
  historyDock.mount(options.container);
  // The global Usage panel dialog is a second singleton beside the dock: the
  // footer opens it through the registry, the store's panelOpen drives it.
  const usagePanel = new UsagePanelController(registry.createUsagePanelHost());
  usagePanel.mount(options.container);
  // Shell layout: responsive narrow collapse + one-visible-panel rule.
  const shellLayout = new AgentShellLayoutController(
    options.container,
    registry.createShellLayoutHost(historyDock)
  );
  shellLayout.mount();

  let disposed = false;

  return {
    handle(message: RawHostMessage): void {
      if (!disposed) registry.handle(message);
    },
    dispose(): void {
      if (disposed) return;
      disposed = true;
      // Order matters: drop the window/document listeners first (shell), then
      // tear workspaces + global brokers down (registry still reports open
      // state to the live dock), and finally unmount the global React islands.
      shellLayout.dispose();
      registry.dispose();
      usagePanel.dispose();
      historyDock.dispose();
    }
  };
}
