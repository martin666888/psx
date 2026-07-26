// SessionRuntimeController.ts — the first Phase 4 domain owner.
//
// Owns the Session + Runtime DOM regions of one Agent workspace: the toolbar
// status/cwd/session line, the change-cwd control, the Context usage ring, and
// the runtime install card. It renders purely from the reduced
// AgentWorkspaceState (the reducer is the single source of truth) and owns its
// own DOM listeners, so the legacy engine suppresses those same nodes.
//
// Faithful port of the legacy DOM writes:
//   _updateState toolbar block + _setContextUsed (wwwroot/js/agent/thread.js)
//   _updateRuntimeStatus + _wireRuntimeControls (wwwroot/js/agent/runtime.js)
// Rendering is triggered on the same events legacy reacted to.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type { AgentWorkspaceState, WorkspaceRuntimeState } from '../contracts/workspace-state.js';
import { isReactUiEnabled } from '../core/flags.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import { computeContextUsageView, formatStatus, type ContextUsageView } from './sessionFormat.js';
import type { ContextUsageProps, SessionMetaProps } from './SessionToolbar.js';

/** The strangler seam the controller needs from the legacy adapter. */
export interface SessionRuntimeHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
}

const RUNTIME_TITLES: Readonly<Record<string, string>> = {
  missing: 'Agent runtime required',
  installing: 'Installing Agent runtime',
  failed: 'Agent runtime installation failed',
  cancelled: 'Agent runtime installation cancelled'
};

function role(panel: HTMLElement, name: string): HTMLElement | null {
  return panel.querySelector<HTMLElement>('[data-role="' + name + '"]');
}

export class SessionRuntimeController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: SessionRuntimeHost;

  private panel: HTMLElement | null = null;
  private status: HTMLElement | null = null;
  private cwd: HTMLElement | null = null;
  private session: HTMLElement | null = null;
  private changeCwd: HTMLButtonElement | null = null;
  private contextUsed: HTMLElement | null = null;
  private contextProgress: SVGCircleElement | null = null;
  private contextTooltipSummary: HTMLElement | null = null;
  private contextTooltipDetail: HTMLElement | null = null;
  private contextTooltipCost: HTMLElement | null = null;
  private runtimeCard: HTMLElement | null = null;
  private runtimeTitle: HTMLElement | null = null;
  private runtimeMessage: HTMLElement | null = null;
  private runtimeInstall: HTMLButtonElement | null = null;
  private runtimeCancel: HTMLButtonElement | null = null;

  // Tracked so the install/cancel guards match legacy exactly.
  private runtimeState = 'missing';
  private readonly cleanup: Array<() => void> = [];

  // React island state (flag: psx.agent.experimental.react, default on).
  private reactUiEnabled = false;
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
    this.status = role(panel, 'status');
    this.cwd = role(panel, 'cwd');
    this.session = role(panel, 'session');
    this.changeCwd = role(panel, 'change-cwd') as HTMLButtonElement | null;
    this.contextUsed = role(panel, 'context-used');
    this.contextProgress = role(panel, 'context-ring-progress') as unknown as SVGCircleElement | null;
    this.contextTooltipSummary = role(panel, 'context-tooltip-summary');
    this.contextTooltipDetail = role(panel, 'context-tooltip-detail');
    this.contextTooltipCost = role(panel, 'context-tooltip-cost');
    this.runtimeCard = role(panel, 'runtime-card');
    this.runtimeTitle = role(panel, 'runtime-title');
    this.runtimeMessage = role(panel, 'runtime-message');
    this.runtimeInstall = role(panel, 'runtime-install') as HTMLButtonElement | null;
    this.runtimeCancel = role(panel, 'runtime-cancel') as HTMLButtonElement | null;

    // Listeners the controller now owns (legacy skips wiring these). We leave
    // the template defaults untouched until the first event, matching legacy.
    this.on(this.changeCwd, 'click', () => {
      this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('pick_cwd');
    });
    this.on(this.runtimeInstall, 'click', () => this.requestInstall());
    this.on(this.runtimeCancel, 'click', () => this.requestCancelInstall());

    this.reactUiEnabled = isReactUiEnabled();
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
        if (this.reactUiEnabled && !this.runtimeIsland?.hasFailed()) {
          this.renderRuntimeReact(state.runtime);
        } else {
          this.renderRuntime(state.runtime);
        }
        break;
      default:
        break;
    }
  }

  dispose(): void {
    for (const off of this.cleanup.splice(0)) off();
    // Unmount the React root and drop its host node; a pending island import
    // resolving after this point sees the loader disposed and discards the
    // mount.
    this.runtimeIsland?.dispose();
    this.runtimeIsland = null;
    this.sessionMetaIsland?.dispose();
    this.sessionMetaIsland = null;
    this.contextUsageIsland?.dispose();
    this.contextUsageIsland = null;
    this.panel = null;
  }

  private renderSession(state: AgentWorkspaceState): void {
    const s = state.session;
    const meta: SessionMetaProps = {
      statusText: formatStatus(s.status),
      status: s.status,
      cwd: s.cwd || 'cwd not set',
      sessionLabel: s.sessionId ? 'session ' + s.sessionId.slice(0, 8) : 'no session',
      changeCwdDisabled: !s.isDraft || s.busy || s.isRestoring || s.isTranscriptOnly,
      changeCwdTitle: s.isDraft
        ? 'Change draft working directory'
        : 'Create a new Agent tab to use another working directory',
      onPickCwd: () => {
        this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('pick_cwd');
      }
    };
    const usage = computeContextUsageView(
      s.contextUsedTokens,
      s.contextWindowTokens,
      s.contextCostAmount,
      s.contextCostCurrency
    );
    if (this.reactUiEnabled && !this.sessionMetaIsland?.hasFailed()) {
      this.renderSessionMetaReact(meta);
    } else {
      this.renderSessionLegacy(meta);
    }
    if (this.reactUiEnabled && !this.contextUsageIsland?.hasFailed()) {
      this.renderContextUsageReact(usage);
    } else {
      this.renderContextUsageLegacy(usage);
    }
  }

  private renderSessionLegacy(meta: SessionMetaProps): void {
    if (this.status) {
      this.status.textContent = meta.statusText;
      this.status.dataset.status = meta.status;
    }
    if (this.cwd) this.cwd.textContent = meta.cwd;
    if (this.session) this.session.textContent = meta.sessionLabel;
    if (this.changeCwd) {
      this.changeCwd.disabled = meta.changeCwdDisabled;
      this.changeCwd.title = meta.changeCwdTitle;
    }
  }

  private renderContextUsageLegacy(view: ContextUsageView): void {
    if (!this.contextUsed) return;
    this.contextUsed.dataset.contextState = view.state;
    this.contextProgress?.setAttribute('stroke-dashoffset', String(100 - view.percent));
    if (this.contextTooltipSummary) this.contextTooltipSummary.textContent = view.summary;
    if (this.contextTooltipDetail) this.contextTooltipDetail.textContent = view.detail;
    this.contextUsed.setAttribute('aria-label', view.ariaLabel);
    if (this.contextTooltipCost) {
      this.contextTooltipCost.hidden = !view.cost;
      this.contextTooltipCost.textContent = view.cost;
    }
  }

  // ----- React session toolbar islands ---------------------------------------
  //
  // Two independent roots (single-owner rule): the toolbar meta line replaces
  // the children of .agent-meta, the Context ring replaces the children of
  // .agent-hints. Both are props-driven from the reduced state; a load failure
  // falls back permanently to the legacy writes via the shared island loader.

  private renderSessionMetaReact(meta: SessionMetaProps): void {
    this.sessionMetaIsland ??= createIslandLoader<SessionMetaProps>({
      name: 'session-meta',
      load: async () => {
        const mod = await import('./sessionIsland.js');
        return (host) => mod.mountSessionMetaIsland(host);
      },
      createHost: () => this.panel?.querySelector<HTMLElement>('.agent-meta') ?? null,
      onLoadFailed: (props) => {
        if (this.panel) this.renderSessionLegacy(props);
      }
    });
    this.sessionMetaIsland.render(meta);
  }

  private renderContextUsageReact(view: ContextUsageView): void {
    this.contextUsageIsland ??= createIslandLoader<ContextUsageProps>({
      name: 'context-usage',
      load: async () => {
        const mod = await import('./sessionIsland.js');
        return (host) => mod.mountContextUsageIsland(host);
      },
      createHost: () => this.panel?.querySelector<HTMLElement>('.agent-hints') ?? null,
      onLoadFailed: (props) => {
        if (this.panel) this.renderContextUsageLegacy(props.view);
      }
    });
    this.contextUsageIsland.render({ view });
  }

  private renderRuntime(r: WorkspaceRuntimeState): void {
    this.runtimeState = r.state;
    if (this.runtimeCard) {
      this.runtimeCard.hidden = r.state === 'ready';
      this.runtimeCard.dataset.state = r.state;
      this.runtimeCard.setAttribute('aria-busy', r.state === 'installing' ? 'true' : 'false');
    }
    if (this.runtimeTitle) this.runtimeTitle.textContent = RUNTIME_TITLES[r.state] || 'Agent runtime';
    if (this.runtimeMessage) this.runtimeMessage.textContent = r.message;
    if (this.runtimeInstall) {
      this.runtimeInstall.hidden = !r.canInstall;
      this.runtimeInstall.disabled = !r.canInstall;
      this.runtimeInstall.textContent = r.state === 'missing' ? 'Install runtime' : 'Retry installation';
    }
    if (this.runtimeCancel) {
      this.runtimeCancel.hidden = !r.canCancel;
      this.runtimeCancel.disabled = !r.canCancel;
    }
  }

  // ----- Experimental React runtime-card island -----------------------------
  //
  // Props-driven: every runtime_status re-renders the island from the reduced
  // state. Until the island's first commit reaches the DOM the legacy card
  // stays untouched, then retireLegacyRuntimeCard swaps ownership atomically
  // (before paint, via useLayoutEffect in SessionRuntimeCard). Loading,
  // buffering and the permanent legacy fallback live in the shared island
  // loader (core/islandHost.ts).

  private renderRuntimeReact(r: WorkspaceRuntimeState): void {
    this.runtimeState = r.state; // keep the install/cancel guards in sync
    this.runtimeIsland ??= createIslandLoader<WorkspaceRuntimeState>({
      name: 'runtime-card',
      load: async () => {
        const mod = await import('./runtimeIsland.js');
        return (host) =>
          mod.mountRuntimeIsland(host, {
            onInstall: () => this.requestInstall(),
            onCancel: () => this.requestCancelInstall(),
            onCommitted: () => this.retireLegacyRuntimeCard()
          });
      },
      createHost: () => this.createRuntimeIslandHost(),
      onLoadFailed: (props) => {
        if (this.panel) this.renderRuntime(props);
      }
    });
    this.runtimeIsland.render(r);
  }

  private createRuntimeIslandHost(): HTMLElement | null {
    if (!this.panel) return null;
    const host = document.createElement('div');
    host.className = 'agent-runtime-react-host';
    const legacy = this.runtimeCard;
    if (legacy && legacy.parentElement) {
      legacy.insertAdjacentElement('afterend', host);
    } else {
      this.panel.appendChild(host);
    }
    return host;
  }

  /**
   * First React commit is in the DOM: hide the legacy section and rename its
   * data-role so the React section is the only `[data-role="runtime-card"]`.
   */
  private retireLegacyRuntimeCard(): void {
    const legacy = this.runtimeCard;
    if (!legacy || legacy.dataset.role !== 'runtime-card') return;
    legacy.hidden = true;
    legacy.dataset.role = 'runtime-card-legacy';
  }

  private requestInstall(): void {
    if (this.runtimeState === 'installing' || this.runtimeState === 'ready') return;
    this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('install_runtime');
  }

  private requestCancelInstall(): void {
    if (this.runtimeState !== 'installing') return;
    this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('cancel_runtime_install');
  }

  private on(node: HTMLElement | null, type: string, handler: () => void): void {
    if (!node) return;
    node.addEventListener(type, handler);
    this.cleanup.push(() => node.removeEventListener(type, handler));
  }
}
