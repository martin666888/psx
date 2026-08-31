// DshWorkspaceHost.js — always-loaded shell host for the single DeepSeek
// Harness web workspace. It never touches the Agent chunk and never
// triggers loadAgentApp(). The runtime status is a process-wide singleton, so
// it is independent of workspace_activated ordering.
//
// Ready no longer mounts a cross-origin iframe. DSH 0.1.2+ exchanges a
// one-time ?token= for a SameSite=Strict cookie that a psx.local iframe
// cannot send. The shell keeps an empty column hole and reports its CSS
// pixel rect over dsh_surface_bounds; C# top-level-navigates a second
// WebView2 to the token URL. The token never appears in this file.

import { Bridge } from './Bridge.js';
import { createProviderIconSvg } from './ProviderIcons.js';
import { dshSwitchCta } from './DshRegistryUi.js';
import { t, onLocaleChanged } from './i18n.js';

// Card copy resolves through the shared i18n instance (shell namespace);
// every state's strings live in locales/<lang>/shell.json under dsh.card.*.
function cardCopy(state) {
    const key = {
        not_installed: null,
        installing: 'installing',
        starting: 'starting',
        failed: 'failed',
        exited: 'exited',
        ready: 'ready'
    }[state];
    if (state === 'not_installed') {
        return {
            title: t('dsh.card.notInstalledTitle'),
            message: t('dsh.card.notInstalledMessage'),
            note: t('dsh.card.notInstalledNote'),
            action: t('dsh.card.install'),
            command: 'install'
        };
    }
    if (key == null) {
        return { title: 'DeepSeek Harness', message: t('dsh.card.unknownStateMessage') };
    }
    const suffix = key.charAt(0).toUpperCase() + key.slice(1);
    const copy = { title: t(`dsh.card.${key}Title`), message: t(`dsh.card.${key}Message`) };
    if (state === 'failed') {
        copy.action = t('common.retry');
        copy.command = 'retry';
    } else if (state === 'exited') {
        copy.action = t('dsh.card.restart');
        copy.command = 'retry';
    }
    void suffix;
    return copy;
}

function boundsEqual(a, b) {
    if (!a || !b) return false;
    if (a.visible !== b.visible) return false;
    if (!a.visible) return true;
    return a.left === b.left
        && a.top === b.top
        && a.width === b.width
        && a.height === b.height
        && a.columnId === b.columnId;
}

export class DshWorkspaceHost {
    constructor(container) {
        this.container = container;
        this.panels = new Map();   // workspaceId -> panel element
        this.status = { state: 'not_installed' };
        this.colorScheme = document.documentElement.style.colorScheme || '';
        this.chromeOverlay = false;
        this.settingsOverlay = false;
        this.lastBounds = null;
        this.boundsRaf = 0;
        this.disposeLocaleChanged = onLocaleChanged(() => {
            for (const panel of this.panels.values()) this.renderCard(panel, this.status);
        });
        this.onShellOverlay = (event) => {
            this.chromeOverlay = !!event.detail?.open;
            this.scheduleBounds();
        };
        this.onSettingsState = (event) => {
            this.settingsOverlay = !!event.detail?.open;
            this.scheduleBounds();
        };
        this.onWindowResize = () => this.scheduleBounds();
        document.addEventListener('psx-shell-overlay', this.onShellOverlay);
        document.addEventListener('psx-settings-state', this.onSettingsState);
        window.addEventListener('resize', this.onWindowResize);
        this.resizeObserver = typeof ResizeObserver === 'function'
            ? new ResizeObserver(() => this.scheduleBounds())
            : null;
    }

    applyLayout(snapshot, rects) {
        if (!snapshot || !Array.isArray(snapshot.columns)) return;
        const liveIds = new Set();
        const assignments = new Map();
        for (const column of snapshot.columns) {
            for (const tab of column.tabs || []) {
                if (tab?.kind === 'dsh_web' && tab.workspaceId)
                    liveIds.add(String(tab.workspaceId));
            }
            if (!column.activeTabId) continue;
            const tab = (column.tabs || []).find((item) => item.workspaceId === column.activeTabId);
            if (!tab || tab.kind !== 'dsh_web') continue;
            const rect = rects?.get(column.columnId);
            if (rect) assignments.set(String(tab.workspaceId), { rect, columnId: column.columnId });
        }
        for (const [id, panel] of [...this.panels]) {
            if (!liveIds.has(id)) {
                this.unobservePanel(panel);
                panel.remove();
                this.panels.delete(id);
                continue;
            }
            const assignment = assignments.get(id);
            if (!assignment) {
                if (!panel.hidden) panel.hidden = true;
                continue;
            }
            panel.dataset.columnId = assignment.columnId;
            this.applyRect(panel, assignment.rect);
            panel.hidden = false;
        }
        for (const [id, assignment] of assignments) {
            if (!this.panels.has(id)) {
                const panel = document.createElement('section');
                panel.className = 'dsh-panel';
                panel.dataset.workspaceId = id;
                panel.dataset.columnId = assignment.columnId;
                panel.addEventListener('mousedown', () => {
                    const columnId = panel.dataset.columnId;
                    if (columnId) Bridge.sendPaneFocus(columnId);
                });
                this.applyRect(panel, assignment.rect);
                this.renderCard(panel, this.status);
                this.container.appendChild(panel);
                this.observePanel(panel);
                this.panels.set(id, panel);
            }
        }
        this.scheduleBounds();
    }

    activate() {
        // Single-instance: nothing workspace-specific to track.
    }

    applyRuntimeStatus(message) {
        this.status = {
            state: String(message?.state || 'not_installed'),
            readyUrl: message?.readyUrl || null,
            errorClass: message?.errorClass || null,
            currentVersion: message?.currentVersion || null,
            updateState: message?.updateState || 'idle',
            updatePhase: message?.updatePhase || null,
            availableVersion: message?.availableVersion || null,
            updateErrorCode: message?.updateErrorCode || null
        };
        for (const panel of this.panels.values()) this.renderCard(panel, this.status);
        this.scheduleBounds();
    }

    applyColorScheme(scheme) {
        this.colorScheme = scheme === 'dark' || scheme === 'light' ? scheme : '';
    }

    applyRect(panel, rect) {
        const gutter = 12;
        panel.style.left = `${rect.left + gutter}px`;
        panel.style.top = `${rect.top + gutter}px`;
        panel.style.width = `${Math.max(0, rect.width - 2 * gutter)}px`;
        panel.style.height = `${Math.max(0, rect.height - 2 * gutter)}px`;
    }

    observePanel(panel) {
        try { this.resizeObserver?.observe(panel); }
        catch { /* jsdom ResizeObserver may reject detached nodes */ }
    }

    unobservePanel(panel) {
        try { this.resizeObserver?.unobserve(panel); }
        catch { /* ignore */ }
    }

    shellOverlayOpen() {
        return this.chromeOverlay || this.settingsOverlay
            || document.documentElement.hasAttribute('data-psx-overlay');
    }

    scheduleBounds() {
        if (this.boundsRaf) return;
        this.boundsRaf = window.requestAnimationFrame(() => {
            this.boundsRaf = 0;
            this.publishBounds();
        });
    }

    publishBounds() {
        const visiblePanel = [...this.panels.values()].find((panel) =>
            !panel.hidden && this.status.state === 'ready' && this.status.readyUrl);
        let payload;
        if (!visiblePanel || this.shellOverlayOpen()) {
            payload = { visible: false };
        } else {
            const rect = visiblePanel.getBoundingClientRect();
            payload = {
                visible: true,
                left: rect.left,
                top: rect.top,
                width: rect.width,
                height: rect.height,
                columnId: visiblePanel.dataset.columnId || null
            };
        }
        if (boundsEqual(this.lastBounds, payload)) return;
        this.lastBounds = payload;
        Bridge.sendDshSurfaceBounds(payload);
    }

    renderCard(panel, status) {
        if (status.state === 'ready' && status.readyUrl) {
            panel.classList.remove('dsh-panel--status');
            panel.classList.add('dsh-panel--surface');
            panel.replaceChildren();
            panel.setAttribute('aria-label', 'DeepSeek Harness');
            return;
        }

        panel.classList.add('dsh-panel--status');
        panel.classList.remove('dsh-panel--surface');
        panel.removeAttribute('aria-label');
        panel.replaceChildren();
        const copy = cardCopy(status.state);
        const card = document.createElement('section');
        card.className = 'dsh-card';
        card.dataset.role = 'dsh-runtime-card';
        card.dataset.state = status.state;
        card.setAttribute('aria-live', 'polite');
        if (status.state === 'installing' || status.state === 'starting')
            card.setAttribute('aria-busy', 'true');

        const mark = document.createElement('span');
        mark.className = 'dsh-card-mark';
        mark.setAttribute('aria-hidden', 'true');
        if (status.state === 'installing' || status.state === 'starting') {
            const spinner = document.createElement('span');
            spinner.className = 'dsh-card-spinner';
            mark.appendChild(spinner);
        } else {
            mark.appendChild(createProviderIconSvg('dsh'));
        }

        const heading = document.createElement('div');
        heading.className = 'dsh-card-copy';
        const title = document.createElement('h2');
        title.className = 'dsh-card-title';
        title.dataset.role = 'dsh-runtime-title';
        title.textContent = copy.title;
        heading.appendChild(title);

        const message = document.createElement('p');
        message.className = 'dsh-card-message';
        message.dataset.role = 'dsh-runtime-message';
        message.textContent = status.state === 'failed' && status.errorClass
            ? (t(`dsh.runtimeErrorCopy.${status.errorClass}`, { ns: 'shell' }) || copy.message)
            : copy.message;
        heading.appendChild(message);
        if (copy.note) {
            const note = document.createElement('p');
            note.className = 'dsh-card-note';
            note.textContent = copy.note;
            heading.appendChild(note);
        }

        card.appendChild(mark);
        card.appendChild(heading);

        if (copy.action && copy.command) {
            const actions = document.createElement('div');
            actions.className = 'dsh-card-actions';
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'dsh-card-action';
            button.dataset.role = copy.command === 'install' ? 'dsh-install' : 'dsh-retry';
            button.textContent = copy.action;
            button.addEventListener('click', () => Bridge.sendDshCommand(copy.command));
            actions.appendChild(button);
            const switchCta = dshSwitchCta(status);
            if (switchCta) {
                const alternate = document.createElement('button');
                alternate.type = 'button';
                alternate.className = 'dsh-card-action';
                alternate.dataset.role = 'dsh-switch-registry';
                alternate.textContent = switchCta.label;
                alternate.addEventListener('click', () =>
                    Bridge.sendDshCommand(switchCta.command, undefined, switchCta.registry));
                actions.appendChild(alternate);
            }
            card.appendChild(actions);
        }

        panel.appendChild(card);
    }
}
