// DshWorkspaceHost.js — always-loaded shell host for the single DeepSeek
// Harness web workspace. Phase 1 renders placeholder status cards (the iframe
// and runtime arrive in Phase 2/4); it never touches the Agent chunk and never
// triggers loadAgentApp(). The runtime status is a process-wide singleton, so
// it is independent of workspace_activated ordering.

import { Bridge } from './Bridge.js';

export class DshWorkspaceHost {
    constructor(container) {
        this.container = container;
        this.panels = new Map();   // workspaceId -> panel element
        this.status = { state: 'not_installed' };
    }

    applyLayout(snapshot, rects) {
        if (!snapshot || !Array.isArray(snapshot.columns)) return;
        const assignments = new Map();
        for (const column of snapshot.columns) {
            if (!column.activeTabId) continue;
            const tab = (column.tabs || []).find((item) => item.workspaceId === column.activeTabId);
            if (!tab || tab.kind !== 'dsh_web') continue;
            const rect = rects?.get(column.columnId);
            if (rect) assignments.set(String(tab.workspaceId), rect);
        }
        for (const [id, panel] of this.panels) {
            const rect = assignments.get(id);
            if (!rect) {
                if (!panel.hidden) panel.hidden = true;
                continue;
            }
            this.applyRect(panel, rect);
            panel.hidden = false;
        }
        for (const [id, rect] of assignments) {
            if (!this.panels.has(id)) {
                const panel = document.createElement('section');
                panel.className = 'dsh-panel';
                panel.dataset.workspaceId = id;
                this.applyRect(panel, rect);
                this.renderCard(panel, this.status);
                this.container.appendChild(panel);
                this.panels.set(id, panel);
            }
        }
    }

    activate() {
        // Single-instance: nothing workspace-specific to track in Phase 1.
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
                // Phase 2 mounts the cross-origin iframe at status.readyUrl.
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
