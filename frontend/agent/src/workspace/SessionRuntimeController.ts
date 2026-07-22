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
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';

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

/** Mirrors legacy _formatStatus. */
function formatStatus(status: string): string {
  return String(status || 'ready')
    .split('_')
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ');
}

function formatTokens(value: number): string {
  return new Intl.NumberFormat('en-US', {
    notation: 'compact',
    maximumFractionDigits: value >= 100_000 ? 0 : 1
  }).format(value);
}

function formatPercent(value: number): string {
  return (value < 10 ? value.toFixed(1) : Math.round(value).toString()) + '%';
}

function formatCost(amount: number, currency: string): string {
  return amount.toFixed(2).replace(/\.00$/, '') + ' ' + currency;
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
    this.on(this.runtimeInstall, 'click', () => {
      if (this.runtimeState === 'installing' || this.runtimeState === 'ready') return;
      this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('install_runtime');
    });
    this.on(this.runtimeCancel, 'click', () => {
      if (this.runtimeState !== 'installing') return;
      this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('cancel_runtime_install');
    });
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
        this.renderRuntime(state);
        break;
      default:
        break;
    }
  }

  dispose(): void {
    for (const off of this.cleanup.splice(0)) off();
    this.panel = null;
  }

  private renderSession(state: AgentWorkspaceState): void {
    const s = state.session;
    if (this.status) {
      this.status.textContent = formatStatus(s.status);
      this.status.dataset.status = s.status;
    }
    if (this.cwd) this.cwd.textContent = s.cwd || 'cwd not set';
    if (this.session) {
      this.session.textContent = s.sessionId ? 'session ' + s.sessionId.slice(0, 8) : 'no session';
    }
    if (this.changeCwd) {
      this.changeCwd.disabled = !s.isDraft || s.busy || s.isRestoring || s.isTranscriptOnly;
      this.changeCwd.title = s.isDraft
        ? 'Change draft working directory'
        : 'Create a new Agent tab to use another working directory';
    }
    this.renderContextUsage(s.contextUsedTokens, s.contextWindowTokens, s.contextCostAmount, s.contextCostCurrency);
  }

  private renderContextUsage(
    used: number | null,
    size: number | null,
    costAmount: number | null,
    costCurrency: string
  ): void {
    if (!this.contextUsed) return;

    const hasLimit = used !== null && size !== null;
    const percent = hasLimit ? Math.min(100, Math.max(0, (used / size) * 100)) : 0;
    const state = !hasLimit ? 'unknown' : percent >= 90 ? 'error' : percent >= 75 ? 'warning' : 'accent';
    this.contextUsed.dataset.contextState = state;
    this.contextProgress?.setAttribute('stroke-dashoffset', String(100 - percent));

    let summary = 'Agent has not reported context usage';
    let detail = 'Context usage will appear when the Agent reports it.';
    if (hasLimit) {
      summary = formatPercent(percent) + ' · ' + formatTokens(used) + ' / ' + formatTokens(size);
      detail = formatTokens(Math.max(0, size - used)) + ' remaining';
    } else if (used !== null) {
      summary = formatTokens(used) + ' used';
      detail = 'Agent did not report a context limit.';
    } else if (size === null) {
      detail = 'Agent did not report a context limit.';
    }

    if (this.contextTooltipSummary) this.contextTooltipSummary.textContent = summary;
    if (this.contextTooltipDetail) this.contextTooltipDetail.textContent = detail;
    const description = 'Context: ' + summary + '. ' + detail;
    this.contextUsed.setAttribute('aria-label', description);

    if (this.contextTooltipCost) {
      const hasCost = costAmount !== null && !!costCurrency;
      this.contextTooltipCost.hidden = !hasCost;
      this.contextTooltipCost.textContent = hasCost ? 'Cost · ' + formatCost(costAmount!, costCurrency) : '';
    }
  }

  private renderRuntime(state: AgentWorkspaceState): void {
    const r = state.runtime;
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

  private on(node: HTMLElement | null, type: string, handler: () => void): void {
    if (!node) return;
    node.addEventListener(type, handler);
    this.cleanup.push(() => node.removeEventListener(type, handler));
  }
}
