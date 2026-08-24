// DshWorkspaceHost.js — always-loaded shell host for the single DeepSeek
// Harness web workspace. It never touches the Agent chunk and never
// triggers loadAgentApp(). The runtime status is a process-wide singleton, so
// it is independent of workspace_activated ordering.

import { Bridge } from './Bridge.js';
import { createProviderIconSvg } from './ProviderIcons.js';
import { dshSwitchCta, DSH_RUNTIME_ERROR_COPY } from './DshRegistryUi.js';

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

const CARD_COPY = {
    not_installed: {
        title: '需要安装 DeepSeek Harness',
        message: '安装会把锁定版本写入本机 PSX 目录，之后启动可直接复用。',
        note: '安装会执行第三方 npm 原生脚本（node-pty / koffi）。',
        action: '安装',
        command: 'install'
    },
    installing: {
        title: '正在安装运行时',
        message: '正在写入本机 PSX 目录，请保持窗口打开。'
    },
    starting: {
        title: '正在启动',
        message: '正在拉起本地 DeepSeek Harness 服务。'
    },
    failed: {
        title: '运行时不可用',
        message: '安装或启动没有完成。可重试，不会改动 DSH 自己的会话和配置。',
        action: '重试',
        command: 'retry'
    },
    exited: {
        title: '已停止',
        message: '本地服务已退出。重新启动不会新建工作区。',
        action: '重新启动',
        command: 'retry'
    },
    ready: {
        title: '运行时已就绪',
        message: '正在打开 DeepSeek Harness。'
    }
};

export class DshWorkspaceHost {
    constructor(container) {
        this.container = container;
        this.panels = new Map();   // workspaceId -> panel element
        this.status = { state: 'not_installed' };
        this.colorScheme = document.documentElement.style.colorScheme || '';
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
            errorClass: message?.errorClass || null,
            currentVersion: message?.currentVersion || null,
            updateState: message?.updateState || 'idle',
            updatePhase: message?.updatePhase || null,
            availableVersion: message?.availableVersion || null,
            updateErrorCode: message?.updateErrorCode || null
        };
        for (const panel of this.panels.values()) this.renderCard(panel, this.status);
    }

    applyColorScheme(scheme) {
        this.colorScheme = scheme === 'dark' || scheme === 'light' ? scheme : '';
        for (const panel of this.panels.values()) {
            const iframe = panel.querySelector('iframe.dsh-frame');
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
            panel.classList.remove('dsh-panel--status');
            const existing = panel.querySelector('iframe.dsh-frame');
            if (existing && isSameFrameUrl(existing, status.readyUrl)) return;
            const iframe = document.createElement('iframe');
            iframe.className = 'dsh-frame';
            iframe.src = status.readyUrl;
            iframe.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms allow-downloads allow-popups allow-modals');
            iframe.setAttribute('aria-label', 'DeepSeek Harness');
            if (this.colorScheme) iframe.style.colorScheme = this.colorScheme;
            panel.replaceChildren(iframe);
            return;
        }

        panel.classList.add('dsh-panel--status');
        panel.replaceChildren();
        const copy = CARD_COPY[status.state] || {
            title: 'DeepSeek Harness',
            message: '状态未知。'
        };
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
            ? (DSH_RUNTIME_ERROR_COPY[status.errorClass] || copy.message)
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
