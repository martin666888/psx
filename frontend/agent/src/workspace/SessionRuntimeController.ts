// SessionRuntimeController.ts — Session and Runtime side-effect coordinator.
//
// React is the single renderer for the toolbar meta line, Context usage and
// runtime card. This controller derives props from AgentWorkspaceState and
// owns the bridge intents emitted by those components.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type { AgentWorkspaceState, WorkspaceRuntimeState } from '../contracts/workspace-state.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import { computeContextUsageView, formatStatus } from './sessionFormat.js';
import type { ContextUsageProps, SessionMetaProps } from './SessionToolbar.js';

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

  private panel: HTMLElement | null = null;
  private sessionMetaHost: HTMLElement | null = null;
  private contextUsageHost: HTMLElement | null = null;
  private runtimeHost: HTMLElement | null = null;
  private runtimeState = 'missing';
  private runtimeIsland: IslandLoader<WorkspaceRuntimeState> | null = null;
  private sessionMetaIsland: IslandLoader<SessionMetaProps> | null = null;
  private contextUsageIsland: IslandLoader<ContextUsageProps> | null = null;

  constructor(workspaceId: string, host: SessionRuntimeHost) {
    this.workspaceId = workspaceId;
    this.host = host;
  }

  mount(): void {
    const panel = this.host.getPanel(this.workspaceId);
    if (!panel) return;
    this.panel = panel;
    this.sessionMetaHost = role(panel, 'session-meta-host');
    this.contextUsageHost = role(panel, 'context-usage-host');
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
    this.runtimeIsland?.dispose();
    this.runtimeIsland = null;
    this.sessionMetaIsland?.dispose();
    this.sessionMetaIsland = null;
    this.contextUsageIsland?.dispose();
    this.contextUsageIsland = null;
    this.panel = null;
    this.sessionMetaHost = null;
    this.contextUsageHost = null;
    this.runtimeHost = null;
  }

  private renderSession(state: AgentWorkspaceState): void {
    const session = state.session;
    const meta: SessionMetaProps = {
      statusText: formatStatus(session.status),
      status: session.status,
      cwd: session.cwd || 'cwd not set',
      sessionLabel: session.sessionId ? 'session ' + session.sessionId.slice(0, 8) : 'no session',
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
    this.renderSessionMeta(meta);
    this.renderContextUsage({ view: usage });
  }

  private renderSessionMeta(meta: SessionMetaProps): void {
    const host = this.sessionMetaHost;
    if (!host) return;
    this.sessionMetaIsland ??= createIslandLoader<SessionMetaProps>({
      name: 'session-meta',
      load: async () => {
        const mod = await import('./sessionIsland.js');
        return (islandHost, reportFailure) =>
          mod.mountSessionMetaIsland(islandHost, reportFailure);
      },
      host
    });
    this.sessionMetaIsland.render(meta);
  }

  private renderContextUsage(props: ContextUsageProps): void {
    const host = this.contextUsageHost;
    if (!host) return;
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
