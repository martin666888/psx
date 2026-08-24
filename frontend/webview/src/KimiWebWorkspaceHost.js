// KimiWebWorkspaceHost.js — always-loaded shell host for the single Kimi
// Code Web workspace. It never touches the Agent chunk and never triggers
// loadAgentApp(). The runtime status is a process-wide singleton, so it is
// independent of workspace_activated ordering. Mirror of DshWorkspaceHost:
// same panel/iframe/status-card/focus-ping/export-forwarding shape, but the
// token-bearing readyUrl is consumed here only as the iframe src and the
// message-origin check — the export handoff never carries the token.
//
// Frame contract: the injected kimi frame script posts export handoffs
// ({ source: 'psx-kimi-web-export', url, path, sessionId } — no token) and a
// pointerdown focus ping ({ source: 'psx-kimi-web-focus' }). Both are
// validated against the current ready URL's origin before any action, so a
// spoofed cross-origin message cannot reach the host export or focus path.

import { Bridge } from './Bridge.js';
import { createProviderIconSvg } from './ProviderIcons.js';
import { t, onLocaleChanged } from './i18n.js';

function isSameFrameUrl(iframe, readyUrl) {
    try {
        return new URL(iframe.src).href === new URL(readyUrl).href;
    } catch {
        return iframe.getAttribute('src') === readyUrl;
    }
}

function expectedOriginFromReadyUrl(readyUrl) {
    if (!readyUrl) return null;
    try { return new URL(readyUrl).origin; }
    catch { return null; }
}

// All Kimi Web card strings resolve through the shared i18n instance
// (shell namespace); see locales/<lang>/shell.json under kimi.*.
function unavailableReason(reason) {
    return t(`kimi.unavailableReasonCopy.${reason}`);
}

function failedError(errorClass) {
    return t(`kimi.failedErrorCopy.${errorClass}`);
}

function cardCopy(state) {
    switch (state) {
        case 'starting':
            return { title: t('kimi.card.startingTitle'), message: t('kimi.card.startingMessage') };
        case 'unavailable':
            return {
                title: t('kimi.card.unavailableTitle'),
                message: t('kimi.card.unavailableMessage'),
                note: t('kimi.card.unavailableNote'),
                action: t('kimi.card.retry'),
                command: 'retry'
            };
        case 'failed':
            return {
                title: t('kimi.card.failedTitle'),
                message: t('kimi.card.failedMessage'),
                action: t('kimi.card.retry'),
                command: 'retry'
            };
        case 'stopped':
            return {
                title: t('kimi.card.stoppedTitle'),
                message: t('kimi.card.stoppedMessage'),
                action: t('kimi.card.restart'),
                command: 'retry'
            };
        case 'stopping':
            return { title: t('kimi.card.stoppingTitle'), message: t('kimi.card.stoppingMessage') };
        case 'exited':
            return {
                title: t('kimi.card.exitedTitle'),
                message: t('kimi.card.exitedMessage'),
                action: t('kimi.card.restart'),
                command: 'retry'
            };
        case 'ready':
            return { title: t('kimi.card.readyTitle'), message: t('kimi.card.readyMessage') };
        default:
            return { title: 'Kimi Code Web', message: t('dsh.card.unknownStateMessage') };
    }
}

export class KimiWebWorkspaceHost {
    constructor(container) {
        this.container = container;
        this.panels = new Map();   // workspaceId -> panel element
        this.status = { state: 'stopped' };
        this.colorScheme = document.documentElement.style.colorScheme || '';
        this.disposeLocaleChanged = onLocaleChanged(() => {
            for (const panel of this.panels.values()) this.renderCard(panel, this.status);
        });
        window.addEventListener('message', (event) => this.onFrameMessage(event));
    }

    onFrameMessage(event) {
        const data = event?.data;
        if (!data || typeof data !== 'object') return;
        if (data.source === 'psx-kimi-web-focus') {
            this.onFrameFocus(event);
            return;
        }
        if (data.source !== 'psx-kimi-web-export') return;
        const expectedOrigin = expectedOriginFromReadyUrl(this.status?.readyUrl);
        if (!expectedOrigin || event.origin !== expectedOrigin) return;
        const url = typeof data.url === 'string' ? data.url : '';
        const path = typeof data.path === 'string' ? data.path : '';
        const sessionId = typeof data.sessionId === 'string' ? data.sessionId : '';
        if (!url) return;
        Bridge.sendKimiWebExport(url, path, sessionId);
    }

    onFrameFocus(event) {
        const expectedOrigin = expectedOriginFromReadyUrl(this.status?.readyUrl);
        if (!expectedOrigin || event.origin !== expectedOrigin) return;
        document.dispatchEvent(new window.CustomEvent('psx-embedded-frame-pointerdown'));
        for (const panel of this.panels.values()) {
            if (panel.hidden) continue;
            const columnId = panel.dataset.columnId;
            if (columnId) {
                Bridge.sendPaneFocus(columnId);
                return;
            }
        }
    }

    applyLayout(snapshot, rects) {
        if (!snapshot || !Array.isArray(snapshot.columns)) return;
        const liveIds = new Set();
        const assignments = new Map();
        for (const column of snapshot.columns) {
            for (const tab of column.tabs || []) {
                if (tab?.kind === 'kimi_web' && tab.workspaceId)
                    liveIds.add(String(tab.workspaceId));
            }
            if (!column.activeTabId) continue;
            const tab = (column.tabs || []).find((item) => item.workspaceId === column.activeTabId);
            if (!tab || tab.kind !== 'kimi_web') continue;
            const rect = rects?.get(column.columnId);
            if (rect) assignments.set(String(tab.workspaceId), { rect, columnId: column.columnId });
        }
        for (const [id, panel] of [...this.panels]) {
            if (!liveIds.has(id)) {
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
                panel.className = 'kimi-web-panel';
                panel.dataset.workspaceId = id;
                panel.dataset.columnId = assignment.columnId;
                panel.addEventListener('mousedown', () => {
                    const columnId = panel.dataset.columnId;
                    if (columnId) Bridge.sendPaneFocus(columnId);
                });
                this.applyRect(panel, assignment.rect);
                this.renderCard(panel, this.status);
                this.container.appendChild(panel);
                this.panels.set(id, panel);
            }
        }
    }

    activate() {
        // Single-instance: nothing workspace-specific to track.
    }

    applyRuntimeStatus(message) {
        this.status = {
            state: String(message?.state || 'stopped'),
            readyUrl: message?.readyUrl || null,
            errorClass: message?.errorClass || null,
            reason: message?.reason || null
        };
        for (const panel of this.panels.values()) this.renderCard(panel, this.status);
    }

    applyColorScheme(scheme) {
        this.colorScheme = scheme === 'dark' || scheme === 'light' ? scheme : '';
        for (const panel of this.panels.values()) {
            const iframe = panel.querySelector('iframe.kimi-web-frame');
            if (iframe) iframe.style.colorScheme = this.colorScheme;
        }
    }

    applyRect(panel, rect) {
        const gutter = 12;
        panel.style.left = `${rect.left + gutter}px`;
        panel.style.top = `${rect.top + gutter}px`;
        panel.style.width = `${Math.max(0, rect.width - 2 * gutter)}px`;
        panel.style.height = `${Math.max(0, rect.height - 2 * gutter)}px`;
    }

    renderCard(panel, status) {
        if (status.state === 'ready' && status.readyUrl) {
            panel.classList.remove('kimi-web-panel--status');
            const existing = panel.querySelector('iframe.kimi-web-frame');
            if (existing && isSameFrameUrl(existing, status.readyUrl)) return;
            const iframe = document.createElement('iframe');
            iframe.className = 'kimi-web-frame';
            iframe.src = status.readyUrl;
            iframe.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms allow-downloads allow-popups allow-modals');
            iframe.setAttribute('aria-label', 'Kimi Code Web');
            if (this.colorScheme) iframe.style.colorScheme = this.colorScheme;
            panel.replaceChildren(iframe);
            return;
        }

        panel.classList.add('kimi-web-panel--status');
        panel.replaceChildren();
        const copy = cardCopy(status.state);
        const card = document.createElement('section');
        card.className = 'kimi-web-card';
        card.dataset.role = 'kimi-web-runtime-card';
        card.dataset.state = status.state;
        card.setAttribute('aria-live', 'polite');
        if (status.state === 'starting' || status.state === 'stopping')
            card.setAttribute('aria-busy', 'true');

        const mark = document.createElement('span');
        mark.className = 'kimi-web-card-mark';
        mark.setAttribute('aria-hidden', 'true');
        if (status.state === 'starting' || status.state === 'stopping') {
            const spinner = document.createElement('span');
            spinner.className = 'kimi-web-card-spinner';
            mark.appendChild(spinner);
        } else {
            mark.appendChild(createProviderIconSvg('kimi'));
        }

        const heading = document.createElement('div');
        heading.className = 'kimi-web-card-copy';
        const title = document.createElement('h2');
        title.className = 'kimi-web-card-title';
        title.dataset.role = 'kimi-web-runtime-title';
        title.textContent = copy.title;
        heading.appendChild(title);

        const message = document.createElement('p');
        message.className = 'kimi-web-card-message';
        message.dataset.role = 'kimi-web-runtime-message';
        message.textContent = status.state === 'unavailable' && status.reason
            ? (unavailableReason(status.reason) || copy.message)
            : status.state === 'failed' && status.errorClass
                ? (failedError(status.errorClass) || copy.message)
                : copy.message;
        heading.appendChild(message);
        if (copy.note) {
            const note = document.createElement('p');
            note.className = 'kimi-web-card-note';
            note.textContent = copy.note;
            heading.appendChild(note);
        }

        card.appendChild(mark);
        card.appendChild(heading);

        if (copy.action && copy.command) {
            const actions = document.createElement('div');
            actions.className = 'kimi-web-card-actions';
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'kimi-web-card-action';
            button.dataset.role = 'kimi-web-retry';
            button.textContent = copy.action;
            button.addEventListener('click', () => Bridge.sendKimiWebCommand(copy.command));
            actions.appendChild(button);
            card.appendChild(actions);
        }

        panel.appendChild(card);
    }
}
