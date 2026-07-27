// SessionRuntimeController.ts — Session and Runtime side-effect coordinator.
//
// React is the single renderer for the toolbar meta line, Context usage and
// runtime card. This controller derives props from AgentWorkspaceState and
// owns the bridge intents emitted by those components.
import { createIslandLoader } from '../core/islandHost.js';
import { computeContextUsageView, formatStatus } from './sessionFormat.js';
function role(panel, name) {
    return panel.querySelector('[data-role="' + name + '"]');
}
export class SessionRuntimeController {
    workspaceId;
    host;
    panel = null;
    sessionMetaHost = null;
    contextUsageHost = null;
    runtimeHost = null;
    runtimeState = 'missing';
    runtimeIsland = null;
    sessionMetaIsland = null;
    contextUsageIsland = null;
    constructor(workspaceId, host) {
        this.workspaceId = workspaceId;
        this.host = host;
    }
    mount() {
        const panel = this.host.getPanel(this.workspaceId);
        if (!panel)
            return;
        this.panel = panel;
        this.sessionMetaHost = role(panel, 'session-meta-host');
        this.contextUsageHost = role(panel, 'context-usage-host');
        this.runtimeHost = role(panel, 'runtime-host');
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
                this.renderRuntime(state.runtime);
                break;
            default:
                break;
        }
    }
    dispose() {
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
    renderSession(state) {
        const session = state.session;
        const meta = {
            statusText: formatStatus(session.status),
            status: session.status,
            cwd: session.cwd || 'cwd not set',
            sessionLabel: session.sessionId ? 'session ' + session.sessionId.slice(0, 8) : 'no session',
            changeCwdDisabled: !session.isDraft || session.busy || session.isRestoring || session.isTranscriptOnly,
            changeCwdTitle: session.isDraft
                ? 'Change draft working directory'
                : 'Create a new Agent tab to use another working directory',
            onPickCwd: () => {
                this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('pick_cwd');
            }
        };
        const usage = computeContextUsageView(session.contextUsedTokens, session.contextWindowTokens, session.contextCostAmount, session.contextCostCurrency);
        this.renderSessionMeta(meta);
        this.renderContextUsage({ view: usage });
    }
    renderSessionMeta(meta) {
        const host = this.sessionMetaHost;
        if (!host)
            return;
        this.sessionMetaIsland ??= createIslandLoader({
            name: 'session-meta',
            load: async () => {
                const mod = await import('./sessionIsland.js');
                return (islandHost, reportFailure) => mod.mountSessionMetaIsland(islandHost, reportFailure);
            },
            host
        });
        this.sessionMetaIsland.render(meta);
    }
    renderContextUsage(props) {
        const host = this.contextUsageHost;
        if (!host)
            return;
        this.contextUsageIsland ??= createIslandLoader({
            name: 'context-usage',
            load: async () => {
                const mod = await import('./sessionIsland.js');
                return (islandHost, reportFailure) => mod.mountContextUsageIsland(islandHost, reportFailure);
            },
            host
        });
        this.contextUsageIsland.render(props);
    }
    renderRuntime(runtime) {
        const host = this.runtimeHost;
        if (!host)
            return;
        this.runtimeState = runtime.state;
        this.runtimeIsland ??= createIslandLoader({
            name: 'runtime-card',
            load: async () => {
                const mod = await import('./runtimeIsland.js');
                return (islandHost, reportFailure) => mod.mountRuntimeIsland(islandHost, reportFailure, {
                    onInstall: () => this.requestInstall(),
                    onCancel: () => this.requestCancelInstall()
                });
            },
            host
        });
        this.runtimeIsland.render(runtime);
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
}
