// SessionRuntimeController.ts — the first Phase 4 domain owner.
//
// Owns the Session + Runtime DOM regions of one Agent workspace: the toolbar
// status/cwd/session line, the change-cwd control, the context-used hint, and
// the runtime install card. It renders purely from the reduced
// AgentWorkspaceState (the reducer is the single source of truth) and owns its
// own DOM listeners, so the legacy engine suppresses those same nodes.
//
// Faithful port of the legacy DOM writes:
//   _updateState toolbar block + _setContextUsed (wwwroot/js/agent/thread.js)
//   _updateRuntimeStatus + _wireRuntimeControls (wwwroot/js/agent/runtime.js)
// Rendering is triggered on the same events legacy reacted to, so the produced
// DOM is byte-identical to the pre-refactor engine.
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
/** Mirrors legacy _formatContextUsed. */
function formatContextUsed(value) {
    return value === null ? 'Context used --k' : 'Context used ' + (value / 1000).toFixed(2) + 'k';
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
    runtimeCard = null;
    runtimeTitle = null;
    runtimeMessage = null;
    runtimeInstall = null;
    runtimeCancel = null;
    // Tracked so the install/cancel guards match legacy exactly.
    runtimeState = 'missing';
    cleanup = [];
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
        this.on(this.runtimeInstall, 'click', () => {
            if (this.runtimeState === 'installing' || this.runtimeState === 'ready')
                return;
            this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('install_runtime');
        });
        this.on(this.runtimeCancel, 'click', () => {
            if (this.runtimeState !== 'installing')
                return;
            this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('cancel_runtime_install');
        });
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
                this.renderRuntime(state);
                break;
            default:
                break;
        }
    }
    dispose() {
        for (const off of this.cleanup.splice(0))
            off();
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
        if (this.contextUsed)
            this.contextUsed.textContent = formatContextUsed(s.contextUsedTokens);
    }
    renderRuntime(state) {
        const r = state.runtime;
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
    on(node, type, handler) {
        if (!node)
            return;
        node.addEventListener(type, handler);
        this.cleanup.push(() => node.removeEventListener(type, handler));
    }
}
