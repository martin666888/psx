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
import { isReactRuntimeCardEnabled } from '../core/flags.js';
import { createIslandLoader } from '../core/islandHost.js';
const RUNTIME_TITLES = {
    missing: 'Agent runtime required',
    installing: 'Installing Agent runtime',
    failed: 'Agent runtime installation failed',
    cancelled: 'Agent runtime installation cancelled'
};
function role(panel, name) {
    return panel.querySelector('[data-role="' + name + '"]');
}
/** Mirrors legacy _formatStatus. */
function formatStatus(status) {
    return String(status || 'ready')
        .split('_')
        .filter(Boolean)
        .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
        .join(' ');
}
function formatTokens(value) {
    return new Intl.NumberFormat('en-US', {
        notation: 'compact',
        maximumFractionDigits: value >= 100_000 ? 0 : 1
    }).format(value);
}
function formatPercent(value) {
    return (value < 10 ? value.toFixed(1) : Math.round(value).toString()) + '%';
}
function formatCost(amount, currency) {
    return amount.toFixed(2).replace(/\.00$/, '') + ' ' + currency;
}
export class SessionRuntimeController {
    workspaceId;
    host;
    panel = null;
    status = null;
    cwd = null;
    session = null;
    changeCwd = null;
    contextUsed = null;
    contextProgress = null;
    contextTooltipSummary = null;
    contextTooltipDetail = null;
    contextTooltipCost = null;
    runtimeCard = null;
    runtimeTitle = null;
    runtimeMessage = null;
    runtimeInstall = null;
    runtimeCancel = null;
    // Tracked so the install/cancel guards match legacy exactly.
    runtimeState = 'missing';
    cleanup = [];
    // Experimental React island state (flag: psx.agent.experimental.react).
    reactRuntimeEnabled = false;
    runtimeIsland = null;
    constructor(workspaceId, host) {
        this.workspaceId = workspaceId;
        this.host = host;
    }
    mount() {
        const panel = this.host.getPanel(this.workspaceId);
        if (!panel)
            return;
        this.panel = panel;
        this.status = role(panel, 'status');
        this.cwd = role(panel, 'cwd');
        this.session = role(panel, 'session');
        this.changeCwd = role(panel, 'change-cwd');
        this.contextUsed = role(panel, 'context-used');
        this.contextProgress = role(panel, 'context-ring-progress');
        this.contextTooltipSummary = role(panel, 'context-tooltip-summary');
        this.contextTooltipDetail = role(panel, 'context-tooltip-detail');
        this.contextTooltipCost = role(panel, 'context-tooltip-cost');
        this.runtimeCard = role(panel, 'runtime-card');
        this.runtimeTitle = role(panel, 'runtime-title');
        this.runtimeMessage = role(panel, 'runtime-message');
        this.runtimeInstall = role(panel, 'runtime-install');
        this.runtimeCancel = role(panel, 'runtime-cancel');
        // Listeners the controller now owns (legacy skips wiring these). We leave
        // the template defaults untouched until the first event, matching legacy.
        this.on(this.changeCwd, 'click', () => {
            this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('pick_cwd');
        });
        this.on(this.runtimeInstall, 'click', () => this.requestInstall());
        this.on(this.runtimeCancel, 'click', () => this.requestCancelInstall());
        this.reactRuntimeEnabled = isReactRuntimeCardEnabled();
    }
    update(event, state) {
        if (!this.panel)
            return;
        switch (event.type) {
            case 'agent_state':
            case 'agent_usage_update':
            case 'agent_thread_loaded':
                this.renderSession(state);
                break;
            case 'runtime_status':
                if (this.reactRuntimeEnabled && !this.runtimeIsland?.hasFailed()) {
                    this.renderRuntimeReact(state.runtime);
                }
                else {
                    this.renderRuntime(state.runtime);
                }
                break;
            default:
                break;
        }
    }
    dispose() {
        for (const off of this.cleanup.splice(0))
            off();
        // Unmount the React root and drop its host node; a pending island import
        // resolving after this point sees the loader disposed and discards the
        // mount.
        this.runtimeIsland?.dispose();
        this.runtimeIsland = null;
        this.panel = null;
    }
    renderSession(state) {
        const s = state.session;
        if (this.status) {
            this.status.textContent = formatStatus(s.status);
            this.status.dataset.status = s.status;
        }
        if (this.cwd)
            this.cwd.textContent = s.cwd || 'cwd not set';
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
    renderContextUsage(used, size, costAmount, costCurrency) {
        if (!this.contextUsed)
            return;
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
        }
        else if (used !== null) {
            summary = formatTokens(used) + ' used';
            detail = 'Agent did not report a context limit.';
        }
        else if (size === null) {
            detail = 'Agent did not report a context limit.';
        }
        if (this.contextTooltipSummary)
            this.contextTooltipSummary.textContent = summary;
        if (this.contextTooltipDetail)
            this.contextTooltipDetail.textContent = detail;
        const description = 'Context: ' + summary + '. ' + detail;
        this.contextUsed.setAttribute('aria-label', description);
        if (this.contextTooltipCost) {
            const hasCost = costAmount !== null && !!costCurrency;
            this.contextTooltipCost.hidden = !hasCost;
            this.contextTooltipCost.textContent = hasCost ? 'Cost · ' + formatCost(costAmount, costCurrency) : '';
        }
    }
    renderRuntime(r) {
        this.runtimeState = r.state;
        if (this.runtimeCard) {
            this.runtimeCard.hidden = r.state === 'ready';
            this.runtimeCard.dataset.state = r.state;
            this.runtimeCard.setAttribute('aria-busy', r.state === 'installing' ? 'true' : 'false');
        }
        if (this.runtimeTitle)
            this.runtimeTitle.textContent = RUNTIME_TITLES[r.state] || 'Agent runtime';
        if (this.runtimeMessage)
            this.runtimeMessage.textContent = r.message;
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
    renderRuntimeReact(r) {
        this.runtimeState = r.state; // keep the install/cancel guards in sync
        this.runtimeIsland ??= createIslandLoader({
            name: 'runtime-card',
            load: async () => {
                const mod = await import('./runtimeIsland.js');
                return (host) => mod.mountRuntimeIsland(host, {
                    onInstall: () => this.requestInstall(),
                    onCancel: () => this.requestCancelInstall(),
                    onCommitted: () => this.retireLegacyRuntimeCard()
                });
            },
            createHost: () => this.createRuntimeIslandHost(),
            onLoadFailed: (props) => {
                if (this.panel)
                    this.renderRuntime(props);
            }
        });
        this.runtimeIsland.render(r);
    }
    createRuntimeIslandHost() {
        if (!this.panel)
            return null;
        const host = document.createElement('div');
        host.className = 'agent-runtime-react-host';
        const legacy = this.runtimeCard;
        if (legacy && legacy.parentElement) {
            legacy.insertAdjacentElement('afterend', host);
        }
        else {
            this.panel.appendChild(host);
        }
        return host;
    }
    /**
     * First React commit is in the DOM: hide the legacy section and rename its
     * data-role so the React section is the only `[data-role="runtime-card"]`.
     */
    retireLegacyRuntimeCard() {
        const legacy = this.runtimeCard;
        if (!legacy || legacy.dataset.role !== 'runtime-card')
            return;
        legacy.hidden = true;
        legacy.dataset.role = 'runtime-card-legacy';
    }
    requestInstall() {
        if (this.runtimeState === 'installing' || this.runtimeState === 'ready')
            return;
        this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('install_runtime');
    }
    requestCancelInstall() {
        if (this.runtimeState !== 'installing')
            return;
        this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('cancel_runtime_install');
    }
    on(node, type, handler) {
        if (!node)
            return;
        node.addEventListener(type, handler);
        this.cleanup.push(() => node.removeEventListener(type, handler));
    }
}
