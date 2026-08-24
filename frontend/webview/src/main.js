// main.js — WebView entry point.
//
// Startup timing (plan Phase 2 + CP3b lazy loading): synchronously create the
// TerminalManager, register the host listener (terminal events handled
// inline, Agent events staged), and send `ready` IMMEDIATELY. The Agent chunk
// is imported only when the FIRST `agent_workspace_created` event arrives —
// a terminal-only session never loads React or the Agent app. The C# host
// calls PublishStateAsync before it sees `ready` (MainWindow.xaml.cs), so
// app-scope events such as agent_providers arrive before any Agent workspace
// exists — the staging store is mandatory, not a fallback. If the Agent
// module fails to load, the terminal is already live and only the Agent
// panel is disabled.

import './css/tailwind.css';
import './css/terminal.css';
import './css/panes.css';
import './css/workspace-chrome.css';
import './css/dsh.css';
import './css/kimi-web.css';
import './css/agent/index.css';
import './css/runtime-diagnostics.css';
import { Bridge } from './Bridge.js';
import { BridgeEventType } from './BridgeMessages.js';
import { workspaceNoticeLabel } from './WorkspaceNoticeCopy.js';
import { colorSchemeForBackground } from './colorScheme.js';
import { PaneLayoutController } from './PaneLayoutController.js';
import { installRuntimeDiagnostics } from './RuntimeDiagnostics.js';
import { TerminalManager } from './TerminalManager.js';
import { WorkspaceChromeController } from './WorkspaceChromeController.js';
import { DshWorkspaceHost } from './DshWorkspaceHost.js';
import { KimiWebWorkspaceHost } from './KimiWebWorkspaceHost.js';

installRuntimeDiagnostics();

(function () {
    // Neutral pane layout root (split-pane Phase 0 seam, always loaded — a
    // Terminal-only session must be able to split without the Agent chunk).
    // Phase 0 holds exactly one pane; Phase 1 drives slots from the C#
    // WorkspaceLayoutService snapshots.
    const paneLayout = new PaneLayoutController(document.getElementById('workspace-panes'));
    const terminalManager = new TerminalManager(document.getElementById('terminal-container'));
    paneLayout.setTerminalMinimumWidthResolver((workspaceId) => terminalManager.minimumPaneWidth(workspaceId));
    const workspaceChrome = new WorkspaceChromeController(
        document.getElementById('workspace-chrome'),
        document.getElementById('workspace-popover-root')
    );
    const dshWorkspaceHost = new DshWorkspaceHost(document.getElementById('dsh-workspace-container'));
    const kimiWebWorkspaceHost = new KimiWebWorkspaceHost(document.getElementById('kimi-web-workspace-container'));
    // Pixel-capacity gate (prevention): the chrome disables "new column"
    // entries when the requested layout plus one more column of that kind
    // would overflow the current width. The C# 3-column count remains the
    // second defensive line; both layers stay in force. The requested
    // column count rides along so the chrome's cap gate survives zoom
    // (which presents only the focused column).
    workspaceChrome.setCapacityChecker(() => {
        const available = paneLayout.availableWidth();
        return {
            fitsAgent: paneLayout.requestedMinimumWidthSum() + paneLayout.minimumWidthForNewPane('agent') <= available,
            fitsTerminal: paneLayout.requestedMinimumWidthSum() + paneLayout.minimumWidthForNewPane('terminal') <= available,
            fitsDsh: paneLayout.requestedMinimumWidthSum() + paneLayout.minimumWidthForNewPane('dsh_web') <= available,
            fitsKimiWeb: paneLayout.requestedMinimumWidthSum() + paneLayout.minimumWidthForNewPane('kimi_web') <= available,
            requestedColumnCount: paneLayout.requestedColumnCount
        };
    });

    let agentApp = null;
    let agentDisabled = false;
    let agentLoadStarted = false;

    // The geometry engine projects column rects onto both render systems;
    // the Agent app joins lazily once its chunk loads.
    paneLayout.onLayoutApplied((snapshot, rects, options) => {
        terminalManager.applyLayout(snapshot, rects, options);
        workspaceChrome.applyLayout(snapshot, rects);
        dshWorkspaceHost.applyLayout(snapshot, rects);
        kimiWebWorkspaceHost.applyLayout(snapshot, rects);
        if (agentApp) agentApp.setPaneLayout(snapshot, rects);
    });

    function applyChromeAppearance(settings) {
        if (!settings || typeof settings !== 'object') return;
        const root = document.documentElement;
        const set = (name, value) => {
            if (typeof value === 'string' && value.trim()) root.style.setProperty(name, value.trim());
        };
        const theme = settings.themeColors;
        if (theme && typeof theme === 'object') {
            for (const [key, variable] of Object.entries({
                background: '--agent-bg', surface: '--agent-surface', surfaceRaised: '--agent-surface-raised',
                surfaceMuted: '--agent-surface-muted', hover: '--agent-hover', border: '--agent-border',
                borderStrong: '--agent-border-strong', text: '--agent-text', textMuted: '--agent-text-muted',
                textDim: '--agent-text-dim', accent: '--agent-accent', accentHover: '--agent-accent-hover',
                error: '--agent-error', warning: '--agent-warning', scrollbar: '--agent-scrollbar',
                scrollbarHover: '--agent-scrollbar-hover'
            })) set(variable, theme[key]);
            // Cross-origin DSH iframes read prefers-color-scheme from the
            // embedder's used color-scheme. This used to live only in the
            // lazy Agent chunk, so a terminal/DSH-only session stayed light
            // until History loaded Agent UI.
            const scheme = colorSchemeForBackground(theme.background);
            if (scheme) {
                root.style.colorScheme = scheme;
                dshWorkspaceHost.applyColorScheme(scheme);
                kimiWebWorkspaceHost.applyColorScheme(scheme);
            }
        }
        const agent = settings.agentThemeColors;
        if (agent && typeof agent === 'object') {
            set('--agent-focus-ring', agent.focusRing);
            set('--agent-shadow', agent.shadow);
        }
        if (typeof settings.agentFontFamily === 'string') set('--agent-font-ui', settings.agentFontFamily);
        if (typeof settings.agentMonoFontFamily === 'string') set('--agent-font-mono', settings.agentMonoFontFamily);
    }

    // Pre-load staging (CP3b). Config/state events only need their most
    // recent value, so they collapse instead of queueing; History
    // invalidations merge into one dirty marker; every workspace lifecycle /
    // content event is buffered losslessly in arrival order. The queue cap is
    // a diagnostic tripwire, not a drop threshold — content is never
    // silently discarded.
    const latestByType = new Map();
    let latestActivation = null;
    let historyInvalidation = null;
    const agentQueue = [];
    const AGENT_QUEUE_DIAGNOSTIC_LIMIT = 4096;
    let queueLimitReported = false;

    function stageAgentEvent(message) {
        switch (message.type) {
            case BridgeEventType.Settings:
            case BridgeEventType.AppearanceSettings:
            case BridgeEventType.AgentProviders:
                latestByType.set(message.type, message);
                return;
            case BridgeEventType.WorkspaceActivated:
                // Only the final activation describes the current state; no
                // Agent workspace events can precede agent_workspace_created,
                // so collapsing never reorders a workspace lifecycle.
                latestActivation = message;
                return;
            case BridgeEventType.AgentHistoryInvalidated:
                historyInvalidation = message;
                return;
            default:
                agentQueue.push(message);
                if (agentQueue.length > AGENT_QUEUE_DIAGNOSTIC_LIMIT && !queueLimitReported) {
                    queueLimitReported = true;
                    console.error(
                        '[agent] staged event queue exceeded ' + AGENT_QUEUE_DIAGNOSTIC_LIMIT
                        + ' entries while the Agent module was loading; buffering continues losslessly.'
                    );
                }
                return;
        }
    }

    function drainStagedEvents(deliver) {
        // Config first, then the ordered lifecycle/content stream (which
        // starts with the triggering agent_workspace_created), then the
        // current activation, then the merged History dirty marker.
        for (const type of [BridgeEventType.Settings, BridgeEventType.AppearanceSettings, BridgeEventType.AgentProviders]) {
            const staged = latestByType.get(type);
            if (staged) deliver(staged);
        }
        latestByType.clear();
        for (const queued of agentQueue.splice(0)) deliver(queued);
        if (latestActivation) {
            deliver(latestActivation);
            latestActivation = null;
        }
        if (historyInvalidation) {
            deliver(historyInvalidation);
            historyInvalidation = null;
        }
    }

    function showAgentLoadFailure() {
        const workspace = document.getElementById('agent-workspace-container');
        if (!workspace) return null;

        let error = workspace.querySelector('.agent-load-error');
        if (!error) {
            error = document.createElement('section');
            error.className = 'agent-load-error';
            error.setAttribute('data-agent-font-surface', '');
            error.setAttribute('role', 'alert');
            error.setAttribute('aria-live', 'assertive');

            const card = document.createElement('div');
            card.className = 'agent-load-error-card';
            const title = document.createElement('h2');
            title.textContent = 'Agent UI unavailable';
            const message = document.createElement('p');
            message.textContent = 'The Agent panel could not load. Terminal workspaces remain available; restart PSX to retry the Agent panel.';
            card.append(title, message);
            error.appendChild(card);
            error.hidden = true;
            workspace.appendChild(error);
        }

        workspace.setAttribute('data-agent-load-error', 'true');
        return error;
    }

    function handleAgentFallback(message) {
        let activation = message;
        if (message.type === BridgeEventType.ViewMode) {
            activation = {
                type: BridgeEventType.WorkspaceActivated,
                workspaceId: message.workspaceId || '',
                kind: message.mode === 'agent' ? 'agent' : 'terminal'
            };
        }

        if (activation.type !== BridgeEventType.WorkspaceActivated) return;
        const showingAgent = activation.kind === 'agent';
        terminalManager.setViewVisible(!showingAgent);
        const error = showAgentLoadFailure();
        if (error) error.hidden = !showingAgent;
    }

    function forwardToAgent(message) {
        if (agentDisabled) {
            handleAgentFallback(message);
            return;
        }
        if (agentApp) {
            agentApp.handle(message);
            return;
        }
        stageAgentEvent(message);
        // Lazy trigger (CP3b): the first Agent workspace pulls in the Agent
        // chunk. A terminal-only session still avoids it until the user asks
        // to open the global History dock.
        if (message.type === BridgeEventType.AgentWorkspaceCreated) loadAgentApp();
    }

    // Load the Agent ESM app on demand. Success: drain the staged events and
    // route every later Agent event through it. Failure: disable the Agent
    // panel; the terminal is already live and unaffected, and the error card
    // becomes visible only while an Agent workspace is active.
    function loadAgentApp() {
        if (agentLoadStarted) return;
        agentLoadStarted = true;
        import('../../agent/src/entry.ts')
            .then((module) => {
                const app = module.createAgentApp({
                    terminalManager,
                    container: document.getElementById('agent-workspace-container'),
                    template: document.getElementById('agent-workspace-template'),
                    paneLayout,
                    // A settings-triggered lazy load must not restore the
                    // History dock's persisted open preference; an explicit
                    // History entry (or a workspace-driven load) still does.
                    restoreHistoryDock: !pendingSettingsOpen || pendingHistoryOpen
                });
                agentApp = app;
                // Ordering contract: createAgentApp has already called
                // markLayoutDriven (paneLayout exists in production), so the
                // staged workspace_activated below early-returns without the
                // fullscreen fallback; the recompute() that follows is the
                // first real projection and must never be removed.
                drainStagedEvents((staged) => app.handle(staged));
                // The app may have missed earlier rect projections while its
                // chunk loaded; replay the current layout once.
                paneLayout.recompute();
                if (pendingHistoryOpen) {
                    pendingHistoryOpen = false;
                    app.openHistory();
                }
                if (pendingSettingsOpen) {
                    const section = pendingSettingsOpen;
                    pendingSettingsOpen = null;
                    app.openSettings(section);
                }
            })
            .catch((error) => {
                agentDisabled = true;
                console.error('[agent] failed to load Agent app; terminal remains available.', error);
                showAgentLoadFailure();
                drainStagedEvents((staged) => handleAgentFallback(staged));
            });
    }

    let pendingHistoryOpen = false;
    let pendingSettingsOpen = null;
    document.addEventListener('psx-history-toggle', () => {
        if (agentApp || agentDisabled) return;
        pendingHistoryOpen = true;
        loadAgentApp();
    });
    document.addEventListener('psx-open-settings', () => {
        if (agentApp || agentDisabled) return;
        pendingSettingsOpen = 'profile';
        loadAgentApp();
    });

    Bridge.onHostMessage((message) => {
        switch (message.type) {
            // Terminal + shared settings: handled inline so they are never
            // blocked by the Agent module import. Settings also feed the Agent
            // app (as an 'app'-scope host event).
            case BridgeEventType.Settings:
            case BridgeEventType.AppearanceSettings:
                terminalManager.setSettings(message.settings);
                applyChromeAppearance(message.settings);
                forwardToAgent(message);
                return;
            case BridgeEventType.Create:
                terminalManager.createTerminal(message.sessionId);
                return;
            case BridgeEventType.Switch:
                terminalManager.switchTerminal(message.sessionId);
                return;
            case BridgeEventType.Output:
                terminalManager.writeOutput(message.sessionId, message.data);
                return;
            case BridgeEventType.Resize:
                terminalManager.resizeTerminal(message.sessionId, message.cols, message.rows);
                return;
            case BridgeEventType.PasteResponse:
                terminalManager.handlePasteResponse(message);
                return;
            case BridgeEventType.Close:
                terminalManager.closeTerminal(message.sessionId);
                return;
            case BridgeEventType.ViewMode:
                // Compatibility shim: older host callers send view_mode; the
                // Agent app only understands workspace_activated, so translate
                // before forwarding.
                forwardToAgent({
                    type: BridgeEventType.WorkspaceActivated,
                    workspaceId: message.workspaceId || '',
                    kind: message.mode === 'agent' ? 'agent' : 'terminal'
                });
                return;
            case BridgeEventType.WorkspaceLayout:
                // Requested-layout snapshots are pane-neutral: handled in the
                // always-loaded layer, never staged for the Agent chunk.
                paneLayout.applySnapshot(message);
                return;
            case BridgeEventType.WorkspaceCatalog:
                workspaceChrome.applyCatalog(message);
                return;
            case BridgeEventType.ThemeCatalog:
                workspaceChrome.applyThemeCatalog(message);
                return;
            case BridgeEventType.WorkspaceNotice:
                workspaceChrome.showNotice(workspaceNoticeLabel(message.code));
                return;
            case BridgeEventType.PaneZoomToggle:
                paneLayout.toggleZoom();
                return;
            case BridgeEventType.DshRuntimeStatus:
                // DSH runtime state is shell-owned: never staged into the
                // Agent chunk.
                dshWorkspaceHost.applyRuntimeStatus(message);
                workspaceChrome.applyDshRuntimeStatus(message);
                return;
            case BridgeEventType.AppSettingsSnapshot:
                workspaceChrome.applyAppSettingsSnapshot(message);
                forwardToAgent(message);
                return;
            case BridgeEventType.KimiWebRuntimeStatus:
                // Kimi Web runtime state is shell-owned: never staged into
                // the Agent chunk.
                kimiWebWorkspaceHost.applyRuntimeStatus(message);
                workspaceChrome.applyKimiWebRuntimeStatus(message);
                return;
            case BridgeEventType.WorkspaceActivated:
                // DSH and Kimi Web activations are shell-owned; every other
                // activation is Agent-owned and must keep flowing to the Agent app.
                if (message.kind === 'kimi_web') {
                    kimiWebWorkspaceHost.activate(message);
                    return;
                }
                if (message.kind === 'dsh_web') {
                    dshWorkspaceHost.activate(message);
                    return;
                }
                forwardToAgent(message);
                return;
            default:
                // Every other event is Agent-owned (lifecycle, providers,
                // content). The decoder inside the Agent app ignores any wire
                // type it does not own.
                forwardToAgent(message);
                return;
        }
    });

    // Send ready IMMEDIATELY so the host creates the initial terminal without
    // waiting for anything Agent-related; the Agent chunk is imported only
    // when the host announces the first Agent workspace or the user opens
    // process-wide History (loadAgentApp above).
    Bridge.sendReady();
})();
