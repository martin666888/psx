// DshWorkspaceHost.js — always-loaded shell host for the single DeepSeek
// Harness web workspace. It never touches the Agent chunk and never
// triggers loadAgentApp(). The runtime status is a process-wide singleton, so
// it is independent of workspace_activated ordering.

import { Bridge } from './Bridge.js';

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

export class DshWorkspaceHost {
    constructor(container) {
        this.container = container;
        this.panels = new Map();   // workspaceId -> panel element
        this.status = { state: 'not_installed' };
        // The injected DSH frame script posts export handoffs and a
        // pointerdown focus ping here. Validate the message origin against
        // the current DSH ready URL before forwarding so a spoofed
        // cross-origin message cannot reach the host save or focus path.
        window.addEventListener('message', (event) => this.onFrameMessage(event));
    }

    onFrameMessage(event) {
        const data = event?.data;
        if (!data || typeof data !== 'object') return;
        if (data.source === 'psx-dsh-focus') {
            this.onFrameFocus(event);
            return;
        }
        if (data.source !== 'psx-dsh-export') return;
        const expectedOrigin = expectedOriginFromReadyUrl(this.status?.readyUrl);
        if (!expectedOrigin || event.origin !== expectedOrigin) return;
        const url = typeof data.url === 'string' ? data.url : '';
        const filename = typeof data.filename === 'string' && data.filename
            ? data.filename : 'session.zip';
        if (!url) return;
        Bridge.sendDshExport(url, filename);
    }

    onFrameFocus(event) {
        const expectedOrigin = expectedOriginFromReadyUrl(this.status?.readyUrl);
        if (!expectedOrigin || event.origin !== expectedOrigin) return;
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
        const assignments = new Map();
        for (const column of snapshot.columns) {
            if (!column.activeTabId) continue;
            const tab = (column.tabs || []).find((item) => item.workspaceId === column.activeTabId);
            if (!tab || tab.kind !== 'dsh_web') continue;
            const rect = rects?.get(column.columnId);
            if (rect) assignments.set(String(tab.workspaceId), { rect, columnId: column.columnId });
        }
        for (const [id, panel] of this.panels) {
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
                this.panels.set(id, panel);
            }
        }
    }

    activate() {
        // Single-instance: nothing workspace-specific to track.
    }

    applyRuntimeStatus(message) {
        this.status = {
            state: String(message?.state || 'not_installed'),
            readyUrl: message?.readyUrl || null,
            errorClass: message?.errorClass || null
        };
        for (const panel of this.panels.values()) this.renderCard(panel, this.status);
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
            const existing = panel.querySelector('iframe.dsh-frame');
            if (existing && isSameFrameUrl(existing, status.readyUrl)) return;
            const iframe = document.createElement('iframe');
            iframe.className = 'dsh-frame';
            iframe.src = status.readyUrl;
            iframe.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms allow-downloads allow-popups allow-modals');
            iframe.setAttribute('aria-label', 'DeepSeek Harness');
            panel.replaceChildren(iframe);
            return;
        }

        panel.replaceChildren();
        const card = document.createElement('div');
        card.className = 'dsh-card';

        const title = document.createElement('h2');
        title.className = 'dsh-card-title';
        title.textContent = 'DeepSeek Harness';
        card.appendChild(title);

        const body = document.createElement('div');
        body.className = 'dsh-card-body';

        switch (status.state) {
            case 'not_installed': {
                const note = document.createElement('p');
                note.textContent = '运行时尚未安装。安装会执行第三方 npm 原生脚本（node-pty / koffi）。';
                body.appendChild(note);
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'dsh-card-action';
                button.textContent = '安装';
                button.addEventListener('click', () => Bridge.sendDshCommand('install'));
                body.appendChild(button);
                break;
            }
            case 'installing':
            case 'starting': {
                const note = document.createElement('p');
                note.textContent = status.state === 'installing' ? '正在安装运行时…' : '正在启动…';
                body.appendChild(note);
                break;
            }
            case 'failed': {
                const note = document.createElement('p');
                note.textContent = '运行时不可用。';
                body.appendChild(note);
                const retry = document.createElement('button');
                retry.type = 'button';
                retry.className = 'dsh-card-action';
                retry.textContent = '重试';
                retry.addEventListener('click', () => Bridge.sendDshCommand('retry'));
                body.appendChild(retry);
                break;
            }
            case 'exited': {
                const note = document.createElement('p');
                note.textContent = '已停止。';
                body.appendChild(note);
                const retry = document.createElement('button');
                retry.type = 'button';
                retry.className = 'dsh-card-action';
                retry.textContent = '重新启动';
                retry.addEventListener('click', () => Bridge.sendDshCommand('retry'));
                body.appendChild(retry);
                break;
            }
            case 'ready': {
                const note = document.createElement('p');
                note.textContent = '运行时已就绪。';
                body.appendChild(note);
                break;
            }
            default: {
                const note = document.createElement('p');
                note.textContent = '状态未知。';
                body.appendChild(note);
            }
        }
        card.appendChild(body);
        panel.appendChild(card);
    }
}
