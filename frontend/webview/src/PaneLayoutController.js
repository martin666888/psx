// PaneLayoutController.js — the neutral pane layout root shared by Terminal
// and Agent workspaces (split panes).
//
// Owns the geometry engine: it mirrors the C# requested layout
// (workspace_layout snapshots behind a monotonic revision guard), measures
// the stack, accounts the global History dock inset, and projects one rect
// per pane onto the two render systems through registered listeners
// (TerminalManager.applyLayout / AgentApp.setPaneLayout). Content never
// lives inside the .workspace-pane slots — slots are transparent frames that
// draw the focused-pane ring and, by flex-grow, keep the ring geometry equal
// to the projected content rects with zero extra measuring.
//
// This controller is deliberately terminal/agent-agnostic and lives in the
// always-loaded webview layer (not the lazily imported Agent chunk): a
// Terminal-only split must work without ever loading React.

import { Bridge } from './Bridge.js';

const WORKBENCH_GUTTER = 12; // mirrors --agent-workbench-gutter in shell.css

export class PaneLayoutController {
    constructor(root) {
        if (!root) throw new Error('PaneLayoutController requires the #workspace-panes root');
        this.root = root;
        // Monotonic guard for workspace_layout snapshots (requested layout
        // from the C# WorkspaceLayoutService). Late or replayed snapshots are
        // dropped.
        this.lastRevision = -1;
        this.snapshot = null;
        this.dockInset = 0;
        this.rects = new Map();
        this.listeners = new Set();
        this.resizeObserver = null;
        // Divider drag: ratios preview locally and never touch the snapshot;
        // one pane_ratio intent goes out on pointerup.
        this.previewRatios = null;
        this.dragging = null;
        this.areaLeft = 0;
        this.areaWidth = 0;

        if (typeof ResizeObserver === 'function') {
            this.resizeObserver = new ResizeObserver(() => this.recompute());
            this.resizeObserver.observe(root);
        }
    }

    dispose() {
        this.resizeObserver?.disconnect();
        this.resizeObserver = null;
        this.listeners.clear();
    }

    get paneCount() {
        return this.root.querySelectorAll('.workspace-pane').length;
    }

    paneAt(index) {
        return this.root.querySelectorAll('.workspace-pane')[index] ?? null;
    }

    paneById(paneId) {
        for (const pane of this.root.querySelectorAll('.workspace-pane')) {
            if (pane.dataset.paneId === paneId) return pane;
        }
        return null;
    }

    /** Rect-projection listener: fn(snapshot, rects: Map<paneId, rect>). */
    onLayoutApplied(listener) {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    /** Global History dock inset in px (0 while closed). Shifts every pane. */
    setDockInset(px) {
        const next = Math.max(0, Math.round(Number(px) || 0));
        if (next === this.dockInset) return;
        this.dockInset = next;
        this.recompute();
    }

    /** Apply a C# workspace_layout snapshot. Returns true when accepted. */
    applySnapshot(message) {
        const revision = Number(message?.revision);
        if (!Number.isFinite(revision) || revision <= this.lastRevision) return false;
        this.lastRevision = revision;
        this.previewRatios = null; // an authoritative snapshot ends any preview
        this.snapshot = {
            revision,
            focusedPaneId: String(message.focusedPaneId || ''),
            panes: Array.isArray(message.panes) ? message.panes.map((pane) => ({
                paneId: String(pane.paneId || ''),
                workspaceId: pane.workspaceId ? String(pane.workspaceId) : null,
                kind: pane.kind ? String(pane.kind) : null,
                ratio: Number(pane.ratio) > 0 ? Number(pane.ratio) : 1,
                attention: pane.attention === true,
                sharedWorktree: pane.sharedWorktree === true
            })) : []
        };
        this.recompute();
        return true;
    }

    /** Recompute pane rects from the current snapshot + measured stack and
     * fan them out. Safe to call with no snapshot (single default pane). */
    recompute() {
        // Measure the STACK (stable), not this root: the root itself shifts
        // right by the dock inset, so its own width already excludes it.
        const width = this.root.parentElement?.clientWidth ?? this.root.clientWidth;
        const height = this.root.parentElement?.clientHeight ?? this.root.clientHeight;
        const panes = this.snapshot?.panes?.length
            ? this.snapshot.panes
            : [{ paneId: 'pane-1', workspaceId: null, kind: null, ratio: 1 }];
        const focusedPaneId = this.snapshot?.focusedPaneId || 'pane-1';

        this.syncPaneSlots(panes, focusedPaneId);

        const areaLeft = Math.min(this.dockInset, Math.max(0, width));
        const areaWidth = Math.max(0, width - areaLeft);
        this.areaLeft = areaLeft;
        this.areaWidth = areaWidth;
        // The slot row indents as one block; slots then flow inside it.
        this.root.style.left = `${areaLeft}px`;

        const ratioOf = (pane) => this.previewRatios?.get(pane.paneId) ?? pane.ratio;

        const rects = new Map();
        let x = areaLeft;
        panes.forEach((pane, index) => {
            const paneWidth = index === panes.length - 1
                ? areaLeft + areaWidth - x // last pane absorbs rounding
                : Math.round(areaWidth * ratioOf(pane));
            rects.set(pane.paneId, { left: x, top: 0, width: Math.max(0, paneWidth), height });
            x += paneWidth;
        });
        this.rects = rects;

        const effective = {
            revision: this.snapshot?.revision ?? 0,
            focusedPaneId,
            panes
        };
        for (const listener of this.listeners) listener(effective, rects);
    }

    /** Keep one transparent slot per pane; flex-grow mirrors the ratio so the
     * ring always tracks the projected rect. Slots never take pointer events;
     * a divider hit strip sits between adjacent panes. */
    syncPaneSlots(panes, focusedPaneId) {
        const ratioOf = (pane) => this.previewRatios?.get(pane.paneId) ?? pane.ratio;
        const seen = new Set();
        panes.forEach((pane) => {
            seen.add(pane.paneId);
            let slot = this.paneById(pane.paneId);
            if (!slot) {
                slot = document.createElement('div');
                slot.className = 'workspace-pane';
                slot.dataset.paneId = pane.paneId;
                this.root.appendChild(slot);
            }
            slot.style.flexGrow = String(ratioOf(pane));
            slot.dataset.focused = pane.paneId === focusedPaneId ? 'true' : 'false';
            slot.dataset.attention = pane.attention === true ? 'true' : 'false';
            slot.dataset.sharedWorktree = pane.sharedWorktree === true ? 'true' : 'false';
            if (pane.sharedWorktree === true) {
                slot.title = '两个可见 Agent 指向同一 worktree；分屏不等于安全的并行开发';
            } else {
                slot.removeAttribute('title');
            }
        });
        for (const slot of [...this.root.querySelectorAll('.workspace-pane')]) {
            if (!seen.has(slot.dataset.paneId)) slot.remove();
        }

        // Rebuild dividers between adjacent panes (none for a single pane).
        for (const divider of [...this.root.querySelectorAll('.workspace-pane-divider')]) divider.remove();
        const slots = panes.map((pane) => this.paneById(pane.paneId)).filter(Boolean);
        slots.forEach((slot, index) => {
            this.root.appendChild(slot);
            if (index < slots.length - 1) {
                this.root.appendChild(this.createDivider(panes[index].paneId));
            }
        });
    }

    createDivider(leftPaneId) {
        const divider = document.createElement('div');
        divider.className = 'workspace-pane-divider';
        divider.setAttribute('role', 'separator');
        divider.setAttribute('aria-orientation', 'vertical');
        divider.title = '拖动调整宽度，双击均分';

        divider.addEventListener('pointerdown', (event) => {
            if (event.button !== 0) return;
            divider.setPointerCapture(event.pointerId);
            divider.dataset.dragging = 'true';
            this.dragging = { leftPaneId, pointerId: event.pointerId };
            event.preventDefault();
        });
        divider.addEventListener('pointermove', (event) => {
            if (!this.dragging || this.dragging.pointerId !== event.pointerId) return;
            if (this.areaWidth <= 0) return;
            const ratio = Math.min(0.8, Math.max(0.2,
                (event.clientX - this.areaLeft) / this.areaWidth));
            this.previewRatios = new Map([[this.dragging.leftPaneId, ratio]]);
            this.recompute();
        });
        const endDrag = (event) => {
            if (!this.dragging || this.dragging.pointerId !== event.pointerId) return;
            const ratio = this.previewRatios?.get(this.dragging.leftPaneId);
            const target = this.dragging.leftPaneId;
            this.dragging = null;
            divider.dataset.dragging = 'false';
            // One intent per drag end; the C# snapshot is authoritative.
            if (typeof ratio === 'number') Bridge.sendPaneRatio(target, ratio);
        };
        divider.addEventListener('pointerup', endDrag);
        divider.addEventListener('pointercancel', (event) => {
            if (!this.dragging || this.dragging.pointerId !== event.pointerId) return;
            this.dragging = null;
            this.previewRatios = null;
            divider.dataset.dragging = 'false';
            this.recompute();
        });
        divider.addEventListener('dblclick', () => {
            this.previewRatios = null;
            Bridge.sendPaneRatio(leftPaneId, 0.5);
        });
        return divider;
    }

    /** Workbench gutter exported so render systems inset their content rect
     * the same way shell.css does (agent panel only; terminals fill the pane). */
    static get gutter() {
        return WORKBENCH_GUTTER;
    }
}
