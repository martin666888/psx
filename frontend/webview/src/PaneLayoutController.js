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
        this.snapshot = {
            revision,
            focusedPaneId: String(message.focusedPaneId || ''),
            panes: Array.isArray(message.panes) ? message.panes.map((pane) => ({
                paneId: String(pane.paneId || ''),
                workspaceId: pane.workspaceId ? String(pane.workspaceId) : null,
                kind: pane.kind ? String(pane.kind) : null,
                ratio: Number(pane.ratio) > 0 ? Number(pane.ratio) : 1
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
        // The slot row indents as one block; slots then flow inside it.
        this.root.style.left = `${areaLeft}px`;

        const rects = new Map();
        let x = areaLeft;
        panes.forEach((pane, index) => {
            const paneWidth = index === panes.length - 1
                ? areaLeft + areaWidth - x // last pane absorbs rounding
                : Math.round(areaWidth * pane.ratio);
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
     * ring always tracks the projected rect. Slots never take pointer events. */
    syncPaneSlots(panes, focusedPaneId) {
        const seen = new Set();
        panes.forEach((pane, index) => {
            seen.add(pane.paneId);
            let slot = this.paneById(pane.paneId);
            if (!slot) {
                slot = document.createElement('div');
                slot.className = 'workspace-pane';
                slot.dataset.paneId = pane.paneId;
                this.root.appendChild(slot);
            }
            slot.style.flexGrow = String(pane.ratio);
            slot.dataset.focused = pane.paneId === focusedPaneId ? 'true' : 'false';
            // Keep DOM order aligned with pane order for predictable flex layout.
            if (this.root.children[index] !== slot) this.root.insertBefore(slot, this.root.children[index] ?? null);
        });
        for (const slot of [...this.root.children]) {
            if (!seen.has(slot.dataset.paneId)) slot.remove();
        }
    }

    /** Workbench gutter exported so render systems inset their content rect
     * the same way shell.css does (agent panel only; terminals fill the pane). */
    static get gutter() {
        return WORKBENCH_GUTTER;
    }
}
