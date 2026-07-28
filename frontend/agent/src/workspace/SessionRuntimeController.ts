// SessionRuntimeController.ts — Session and Runtime side-effect coordinator.
//
// React is the single renderer for the toolbar meta line, Context usage and
// runtime card. This controller derives props from AgentWorkspaceState and
// owns the bridge intents emitted by those components. The session meta slice
// is pushed to the WorkspaceToolbarController (the toolbar island's single
// writer); Context usage keeps its own island, portaling into the
// context-usage host that the ComposerView island (3-0) renders inside the
// composer footer — so the host node appears asynchronously and is resolved
// lazily on every render.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type { AgentWorkspaceState, WorkspaceRuntimeState } from '../contracts/workspace-state.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import { computeContextUsageView, formatStatus } from './sessionFormat.js';
import type { ContextUsageProps, SessionMetaProps } from './SessionToolbar.js';
import type { WorkspaceToolbarController } from './WorkspaceToolbarController.js';

export interface SessionRuntimeHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
}

function role(panel: HTMLElement, name: string): HTMLElement | null {
  return panel.querySelector<HTMLElement>('[data-role="' + name + '"]');
}

export class SessionRuntimeController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: SessionRuntimeHost;
  private readonly toolbar: WorkspaceToolbarController;

  private panel: HTMLElement | null = null;
  private contextUsageHost: HTMLElement | null = null;
  private runtimeHost: HTMLElement | null = null;
  private runtimeState = 'missing';
  private runtimeIsland: IslandLoader<WorkspaceRuntimeState> | null = null;
  private contextUsageIsland: IslandLoader<ContextUsageProps> | null = null;
  // The ComposerView island (3-0) renders the context-usage host
  // asynchronously, so the latest props are stashed and replayed once the
  // host node appears (see watchContextUsageHost).
  private contextUsageProps: ContextUsageProps | null = null;
  private contextUsageObserver: MutationObserver | null = null;

  constructor(workspaceId: string, host: SessionRuntimeHost, toolbar: WorkspaceToolbarController) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.toolbar = toolbar;
  }

  mount(): void {
    const panel = this.host.getPanel(this.workspaceId);
    if (!panel) return;
    this.panel = panel;
    this.runtimeHost = role(panel, 'runtime-host');
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    if (!this.panel) return;
    switch (event.type) {
      case 'agent_state':
      case 'agent_usage_update':
      case 'agent_thread_loaded':
        this.renderSession(state);
        break;
      case 'runtime_status':
        this.renderRuntime(state.runtime);
        break;
      default:
        break;
    }
  }

  dispose(): void {
    this.stopWatchingContextUsageHost();
    this.contextUsageProps = null;
    this.runtimeIsland?.dispose();
    this.runtimeIsland = null;
    this.contextUsageIsland?.dispose();
    this.contextUsageIsland = null;
    this.panel = null;
    this.contextUsageHost = null;
    this.runtimeHost = null;
  }

  private renderSession(state: AgentWorkspaceState): void {
    const session = state.session;
    const meta: SessionMetaProps = {
      statusText: formatStatus(session.status),
      status: session.status,
      cwd: session.cwd || 'cwd not set',
      sessionLabel: session.sessionId ? 'session: ' + session.sessionId : 'no session',
      changeCwdDisabled:
        !session.isDraft || session.busy || session.isRestoring || session.isTranscriptOnly,
      changeCwdTitle: session.isDraft
        ? 'Change draft working directory'
        : 'Create a new Agent tab to use another working directory',
      onPickCwd: () => {
        this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('pick_cwd');
      }
    };
    const usage = computeContextUsageView(
      session.contextUsedTokens,
      session.contextWindowTokens,
      session.contextCostAmount,
      session.contextCostCurrency
    );
    this.toolbar.setSessionProps(meta);
    this.renderContextUsage({ view: usage });
  }

  private renderContextUsage(props: ContextUsageProps): void {
    this.contextUsageProps = props;
    // The ComposerView island (3-0) renders the context-usage host, so it may
    // appear after this controller's mount or be replaced by an island retry:
    // resolve it lazily and rebuild the island loader on node identity change.
    const host = this.panel ? role(this.panel, 'context-usage-host') : null;
    if (host !== this.contextUsageHost) {
      this.contextUsageIsland?.dispose();
      this.contextUsageIsland = null;
      this.contextUsageHost = host;
    }
    if (!host) {
      this.watchContextUsageHost();
      return;
    }
    this.stopWatchingContextUsageHost();
    this.contextUsageIsland ??= createIslandLoader<ContextUsageProps>({
      name: 'context-usage',
      load: async () => {
        const mod = await import('./sessionIsland.js');
        return (islandHost, reportFailure) =>
          mod.mountContextUsageIsland(islandHost, reportFailure);
      },
      host
    });
    this.contextUsageIsland.render(props);
  }

  /** The ComposerView island renders the host asynchronously; observe the
   * panel until the node appears, then replay the stashed props so an
   * agent_state that beat the island's first commit is not lost. */
  private watchContextUsageHost(): void {
    if (this.contextUsageObserver || !this.panel) return;
    const panel = this.panel;
    this.contextUsageObserver = new MutationObserver(() => {
      if (this.panel !== panel || !role(panel, 'context-usage-host')) return;
      this.stopWatchingContextUsageHost();
      if (this.contextUsageProps) this.renderContextUsage(this.contextUsageProps);
    });
    this.contextUsageObserver.observe(panel, { childList: true, subtree: true });
  }

  private stopWatchingContextUsageHost(): void {
    this.contextUsageObserver?.disconnect();
    this.contextUsageObserver = null;
  }

  private renderRuntime(runtime: WorkspaceRuntimeState): void {
    const host = this.runtimeHost;
    if (!host) return;
    this.runtimeState = runtime.state;
    this.runtimeIsland ??= createIslandLoader<WorkspaceRuntimeState>({
      name: 'runtime-card',
      load: async () => {
        const mod = await import('./runtimeIsland.js');
        return (islandHost, reportFailure) =>
          mod.mountRuntimeIsland(islandHost, reportFailure, {
            onInstall: () => this.requestInstall(),
            onCancel: () => this.requestCancelInstall()
          });
      },
      host
    });
    this.runtimeIsland.render(runtime);
  }

  private requestInstall(): void {
    if (this.runtimeState === 'installing' || this.runtimeState === 'ready') return;
    this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('install_runtime');
  }

  private requestCancelInstall(): void {
    if (this.runtimeState !== 'installing') return;
    this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('cancel_runtime_install');
  }
}
