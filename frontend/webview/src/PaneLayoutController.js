// PaneLayoutController.js — neutral, always-loaded split-pane geometry.
// C# owns the requested pane order/focus/ratios. This controller owns the
// effective pixel presentation, including responsive collapse and live drag.

import { Bridge } from './Bridge.js';

const WORKBENCH_GUTTER = 12;
const CHROME_HEIGHT = 40;
const HISTORY_RAIL_WIDTH = 40;

export class PaneLayoutController {
    constructor(root) {
        if (!root) throw new Error('PaneLayoutController requires the #workspace-panes root');
        this.root = root;
        this.lastRevision = -1;
        this.snapshot = null;
        this.dockInset = HISTORY_RAIL_WIDTH;
        this.rects = new Map();
        this.listeners = new Set();
        this.resizeObserver = null;
        this.resizeRafId = null;
        this.lastStackWidth = -1;
        this.lastStackHeight = -1;
        this.previewRatios = null;
        this.dragging = null;
        this.dragPreviewFrameId = null;
        this.pendingDragClientX = null;
        this.areaLeft = 0;
        this.areaWidth = 0;
        this.effectivePanes = [];
        this.zoomed = false;
        this.terminalMinimumWidthResolver = null;
        this.onPanesCollectedCallback = null;
        this.lastCollectedPaneIds = null;

        this.onWindowPointerMove = (event) => this.handlePointerMove(event);
        this.onWindowPointerUp = (event) => this.finishDrag(event);
        this.onWindowPointerCancel = (event) => this.cancelDrag(event);
        this.onWindowBlur = () => this.cancelDrag();
        window.addEventListener('pointermove', this.onWindowPointerMove);
        window.addEventListener('pointerup', this.onWindowPointerUp);
        window.addEventListener('pointercancel', this.onWindowPointerCancel);
        window.addEventListener('blur', this.onWindowBlur);

        if (typeof ResizeObserver === 'function') {
            this.resizeObserver = new ResizeObserver(() => this.scheduleRecompute());
            this.resizeObserver.observe(root.parentElement ?? root);
        }
        this.root.dataset.splitActive = 'false';
        this.root.addEventListener('dragover', (event) => this.onDragOver(event));
        this.root.addEventListener('drop', (event) => this.onDrop(event));
        this.root.addEventListener('dragleave', (event) => this.onDragLeave(event));
    }

    static get workspaceIdPattern() {
        return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    }

    static get gutter() {
        return WORKBENCH_GUTTER;
    }

    static get minPaneWidth() {
        return 400;
    }

    setTerminalMinimumWidthResolver(resolver) {
        this.terminalMinimumWidthResolver = typeof resolver === 'function' ? resolver : null;
    }

    // Edge-triggered responsive-collection notice: the callback receives the
    // paneIds that entered the collected set since the previous recompute and
    // fires only when that set grows. A stable set or a restored pane never
    // re-notifies; zoom (a user action) is not collection and never fires.
    onPanesCollected(callback) {
        this.onPanesCollectedCallback = typeof callback === 'function' ? callback : null;
    }

    dispose() {
        this.cancelDrag(undefined, { recompute: false });
        window.removeEventListener('pointermove', this.onWindowPointerMove);
        window.removeEventListener('pointerup', this.onWindowPointerUp);
        window.removeEventListener('pointercancel', this.onWindowPointerCancel);
        window.removeEventListener('blur', this.onWindowBlur);
        this.resizeObserver?.disconnect();
        this.resizeObserver = null;
        if (this.resizeRafId !== null && typeof cancelAnimationFrame === 'function') {
            cancelAnimationFrame(this.resizeRafId);
        }
        this.resizeRafId = null;
        this.cancelDragPreviewFrame();
        this.listeners.clear();
    }

    scheduleRecompute() {
        if (this.resizeRafId !== null || typeof requestAnimationFrame !== 'function') {
            if (typeof requestAnimationFrame !== 'function') this.recompute();
            return;
        }
        this.resizeRafId = requestAnimationFrame(() => {
            this.resizeRafId = null;
            const width = this.root.parentElement?.clientWidth ?? 0;
            const height = this.root.parentElement?.clientHeight ?? 0;
            if (width === this.lastStackWidth && height === this.lastStackHeight) return;
            if (this.dragging) this.cancelDrag(undefined, { recompute: false });
            this.lastStackWidth = width;
            this.lastStackHeight = height;
            this.recompute();
        });
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

    onLayoutApplied(listener) {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    setDockInset(px) {
        const next = Math.max(HISTORY_RAIL_WIDTH, Math.round(Number(px) || 0));
        if (next === this.dockInset) return;
        if (this.dragging) this.cancelDrag(undefined, { recompute: false });
        this.dockInset = next;
        this.recompute();
    }

    toggleZoom() {
        if (this.dragging) this.cancelDrag(undefined, { recompute: false });
        this.zoomed = !this.zoomed;
        this.recompute();
        return this.zoomed;
    }

    applySnapshot(message) {
        const revision = Number(message?.revision);
        if (!Number.isFinite(revision) || revision <= this.lastRevision) return false;
        if (this.dragging) this.cancelDrag(undefined, { recompute: false });
        this.lastRevision = revision;
        this.previewRatios = null;
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
        // Diagnostic (permanent, [pane-layout]): accepted snapshots report
        // their revision, pane count and focused pane.
        console.debug('[pane-layout] applySnapshot: revision =', this.snapshot.revision,
            '| panes =', this.snapshot.panes.length,
            '| focused =', this.snapshot.focusedPaneId);
        this.recompute();
        return true;
    }

    recompute(options = {}) {
        const width = this.root.parentElement?.clientWidth ?? this.root.clientWidth;
        const fullHeight = this.root.parentElement?.clientHeight ?? this.root.clientHeight;
        const height = Math.max(0, fullHeight - CHROME_HEIGHT);
        const requestedPanes = this.snapshot?.panes?.length
            ? this.snapshot.panes
            : [{ paneId: 'pane-1', workspaceId: null, kind: null, ratio: 1 }];
        const requestedFocus = this.snapshot?.focusedPaneId || requestedPanes[0].paneId;
        const areaLeft = Math.min(this.dockInset, Math.max(0, width));
        const areaWidth = Math.max(0, width - areaLeft);
        this.areaLeft = areaLeft;
        this.areaWidth = areaWidth;

        let panes = this.selectEffectivePanes(requestedPanes, requestedFocus, areaWidth);
        if (this.zoomed) panes = [requestedPanes.find((pane) => pane.paneId === requestedFocus) ?? requestedPanes[0]];
        const focusedPaneId = panes.some((pane) => pane.paneId === requestedFocus)
            ? requestedFocus
            : panes[0].paneId;
        this.effectivePanes = panes;
        this.updateCollectedPanes(requestedPanes, panes);

        const displayRatios = this.normalizedDisplayRatios(panes);
        this.syncPaneSlots(panes, focusedPaneId, displayRatios);
        this.root.style.left = `${areaLeft}px`;
        this.root.style.top = `${CHROME_HEIGHT}px`;

        const rects = new Map();
        let x = areaLeft;
        panes.forEach((pane, index) => {
            const paneWidth = index === panes.length - 1
                ? areaLeft + areaWidth - x
                : Math.round(areaWidth * (displayRatios.get(pane.paneId) ?? 0));
            rects.set(pane.paneId, { left: x, top: CHROME_HEIGHT, width: Math.max(0, paneWidth), height });
            x += paneWidth;
        });
        this.rects = rects;

        const effective = {
            revision: this.snapshot?.revision ?? 0,
            focusedPaneId,
            panes
        };
        for (const listener of this.listeners) {
            listener(effective, rects, { interactiveResize: options.interactiveResize === true });
        }
    }

    selectEffectivePanes(requestedPanes, focusedPaneId, areaWidth) {
        if (requestedPanes.length <= 1 || areaWidth <= 0) return [...requestedPanes];
        const visible = [...requestedPanes];
        const focusedIndex = requestedPanes.findIndex((pane) => pane.paneId === focusedPaneId);
        while (visible.length > 1 && this.minimumWidthSum(visible) > areaWidth) {
            const candidates = visible
                .filter((pane) => pane.paneId !== focusedPaneId)
                .map((pane) => ({ pane, index: requestedPanes.indexOf(pane) }))
                .sort((a, b) => {
                    const distance = Math.abs(b.index - focusedIndex) - Math.abs(a.index - focusedIndex);
                    return distance !== 0 ? distance : b.index - a.index;
                });
            if (!candidates.length) break;
            const collected = candidates[0].pane;
            // Diagnostic (permanent, [pane-layout]): every responsive
            // collection reports the collected pane and the width pressure
            // that caused it (available vs required).
            console.debug('[pane-layout] selectEffectivePanes: collected', collected.paneId,
                '| available =', areaWidth, 'required =', this.minimumWidthSum(visible));
            visible.splice(visible.indexOf(collected), 1);
        }
        return visible;
    }

    minimumWidthSum(panes) {
        return panes.reduce((sum, pane) => sum + this.minimumWidthForPane(pane), 0);
    }

    minimumWidthForPane(pane) {
        if (pane.kind !== 'terminal') return PaneLayoutController.minPaneWidth;
        const measured = this.terminalMinimumWidthResolver?.(pane.workspaceId);
        return Number.isFinite(measured) ? Math.max(400, measured) : 480;
    }

    // Capacity helpers — the preventive layer. The chrome disables "new right
    // column" entries when the requested layout plus the new pane's minimum
    // would overflow the available width. This pixel gate is the first
    // defensive line (presentation duty); the C# 4-column count stays the
    // second, so both layers remain in force.

    availableWidth() {
        return this.areaWidth;
    }

    // Always measured from the C# REQUESTED layout (snapshot.panes), never
    // from effectivePanes: after responsive collection the effective list
    // undercounts and would wrongly allow yet another column.
    requestedMinimumWidthSum() {
        return this.minimumWidthSum(this.snapshot?.panes ?? []);
    }

    // A brand-new pane has no workspaceId yet: 'terminal' reuses the measured
    // width of an already-open terminal when one exists, otherwise the
    // unmeasured fallback (480); Agent panes always need 400.
    minimumWidthForNewPane(kind) {
        if (kind !== 'terminal') return PaneLayoutController.minPaneWidth;
        const terminalPane = this.snapshot?.panes?.find((pane) => pane.kind === 'terminal');
        if (terminalPane) {
            const measured = this.terminalMinimumWidthResolver?.(terminalPane.workspaceId);
            if (Number.isFinite(measured)) return Math.max(400, measured);
        }
        return 480;
    }

    // Edge detection for the collection notice. Zoom is a user-initiated
    // fullscreen — its hidden panes are not "collected" — so the previous
    // collected set is preserved untouched while zoomed.
    updateCollectedPanes(requestedPanes, panes) {
        if (this.zoomed) return;
        const effectiveIds = new Set(panes.map((pane) => pane.paneId));
        const collected = requestedPanes
            .filter((pane) => !effectiveIds.has(pane.paneId))
            .map((pane) => pane.paneId);
        if (this.lastCollectedPaneIds === null) {
            // First recompute establishes the baseline without notifying.
            this.lastCollectedPaneIds = collected;
            return;
        }
        const newlyCollected = collected.filter((paneId) => !this.lastCollectedPaneIds.includes(paneId));
        this.lastCollectedPaneIds = collected;
        if (newlyCollected.length && this.onPanesCollectedCallback) {
            this.onPanesCollectedCallback(newlyCollected);
        }
    }

    normalizedDisplayRatios(panes) {
        const source = this.previewRatios ?? new Map((this.snapshot?.panes ?? panes).map((pane) => [pane.paneId, pane.ratio]));
        const sum = panes.reduce((total, pane) => total + (source.get(pane.paneId) ?? pane.ratio), 0) || 1;
        return new Map(panes.map((pane) => [pane.paneId, (source.get(pane.paneId) ?? pane.ratio) / sum]));
    }

    syncPaneSlots(panes, focusedPaneId, displayRatios) {
        this.root.dataset.splitActive = panes.length > 1 ? 'true' : 'false';
        const seen = new Set();
        for (const pane of panes) {
            seen.add(pane.paneId);
            let slot = this.paneById(pane.paneId);
            if (!slot) {
                slot = document.createElement('div');
                slot.className = 'workspace-pane';
                slot.dataset.paneId = pane.paneId;
                this.root.appendChild(slot);
            }
            slot.style.flexGrow = String(displayRatios.get(pane.paneId) ?? 1);
            slot.dataset.focused = pane.paneId === focusedPaneId ? 'true' : 'false';
        }
        for (const slot of [...this.root.querySelectorAll('.workspace-pane')]) {
            if (!seen.has(slot.dataset.paneId)) slot.remove();
        }

        const desiredDividers = new Set();
        panes.forEach((pane, index) => {
            const slot = this.paneById(pane.paneId) ?? this.createSlot(pane.paneId);
            this.root.appendChild(slot);
            if (index >= panes.length - 1) return;
            const rightPane = panes[index + 1];
            const key = `${pane.paneId}|${rightPane.paneId}`;
            desiredDividers.add(key);
            let divider = [...this.root.querySelectorAll('.workspace-pane-divider')]
                .find((candidate) => candidate.dataset.dividerKey === key);
            if (!divider) divider = this.createDivider(pane.paneId, rightPane.paneId);
            const leftRatio = displayRatios.get(pane.paneId) ?? 0;
            const rightRatio = displayRatios.get(rightPane.paneId) ?? 0;
            const pairRatio = leftRatio + rightRatio;
            divider.setAttribute('aria-valuemin', '0');
            divider.setAttribute('aria-valuemax', '100');
            divider.setAttribute('aria-valuenow', String(Math.round(pairRatio > 0 ? leftRatio / pairRatio * 100 : 50)));
            this.root.appendChild(divider);
        });
        for (const divider of [...this.root.querySelectorAll('.workspace-pane-divider')]) {
            if (!desiredDividers.has(divider.dataset.dividerKey)) divider.remove();
        }
    }

    createSlot(paneId) {
        const slot = document.createElement('div');
        slot.className = 'workspace-pane';
        slot.dataset.paneId = paneId;
        return slot;
    }

    createDivider(leftPaneId, rightPaneId) {
        const divider = document.createElement('div');
        divider.className = 'workspace-pane-divider';
        divider.dataset.dividerKey = `${leftPaneId}|${rightPaneId}`;
        divider.setAttribute('role', 'separator');
        divider.setAttribute('aria-orientation', 'vertical');
        divider.setAttribute('aria-label', '调整相邻工作区宽度');
        divider.tabIndex = 0;
        divider.title = '拖动调整宽度，双击均分相邻列';

        divider.addEventListener('pointerdown', (event) => {
            if (event.button !== 0 || !this.snapshot) return;
            const leftRect = this.rects.get(leftPaneId);
            const rightRect = this.rects.get(rightPaneId);
            if (!leftRect || !rightRect) return;
            divider.setPointerCapture?.(event.pointerId);
            divider.dataset.dragging = 'true';
            this.dragging = {
                leftPaneId,
                rightPaneId,
                pointerId: event.pointerId,
                divider,
                startClientX: event.clientX,
                startLeftWidth: leftRect.width,
                pairWidth: leftRect.width + rightRect.width,
                baseRevision: this.snapshot.revision,
                baseRatios: new Map(this.snapshot.panes.map((pane) => [pane.paneId, pane.ratio])),
                commitAllowed: this.effectivePanes.length === this.snapshot.panes.length
            };
            this.root.dataset.resizing = 'true';
            event.preventDefault();
            event.stopPropagation();
        });
        divider.addEventListener('dblclick', () => this.equalizePair(leftPaneId, rightPaneId));
        divider.addEventListener('keydown', (event) => this.resizePairByKeyboard(event, leftPaneId, rightPaneId));
        return divider;
    }

    handlePointerMove(event) {
        if (!this.dragging || this.dragging.pointerId !== event.pointerId || this.areaWidth <= 0) return;
        this.pendingDragClientX = event.clientX;
        this.scheduleDragPreview();
        event.preventDefault?.();
    }

    finishDrag(event) {
        if (!this.dragging || this.dragging.pointerId !== event.pointerId) return;
        const drag = this.dragging;
        this.cancelDragPreviewFrame();
        this.updatePreviewRatios(event.clientX - drag.startClientX, drag);
        const payload = this.previewRatios ? this.ratioPayload(this.previewRatios) : null;
        this.releaseDragCapture(drag);
        this.dragging = null;
        this.pendingDragClientX = null;
        delete this.root.dataset.resizing;
        this.recompute();
        if (drag.commitAllowed && payload) Bridge.sendPaneRatios(drag.baseRevision, payload);
    }

    cancelDrag(event, { recompute = true } = {}) {
        if (!this.dragging) return;
        if (event?.pointerId !== undefined && this.dragging.pointerId !== event.pointerId) return;
        const drag = this.dragging;
        this.cancelDragPreviewFrame();
        this.releaseDragCapture(drag);
        this.dragging = null;
        this.pendingDragClientX = null;
        this.previewRatios = null;
        delete this.root.dataset.resizing;
        if (recompute) this.recompute();
    }

    releaseDragCapture(drag) {
        drag.divider.dataset.dragging = 'false';
        try {
            if (drag.divider.hasPointerCapture?.(drag.pointerId)) {
                drag.divider.releasePointerCapture(drag.pointerId);
            }
        } catch {
            // Chromium may release capture before pointercancel reaches us.
        }
    }

    cancelDragPreviewFrame() {
        if (this.dragPreviewFrameId !== null && typeof cancelAnimationFrame === 'function') {
            cancelAnimationFrame(this.dragPreviewFrameId);
        }
        this.dragPreviewFrameId = null;
    }

    scheduleDragPreview() {
        if (this.dragPreviewFrameId !== null) return;
        const preview = () => {
            this.dragPreviewFrameId = null;
            if (!this.dragging || this.pendingDragClientX === null) return;
            this.updatePreviewRatios(this.pendingDragClientX - this.dragging.startClientX, this.dragging);
            this.recompute({ interactiveResize: true });
        };
        if (typeof requestAnimationFrame === 'function') this.dragPreviewFrameId = requestAnimationFrame(preview);
        else preview();
    }

    updatePreviewRatios(delta, drag) {
        if (!this.snapshot || this.areaWidth <= 0 || drag.pairWidth <= 0) return;
        const leftPane = this.snapshot.panes.find((pane) => pane.paneId === drag.leftPaneId);
        const rightPane = this.snapshot.panes.find((pane) => pane.paneId === drag.rightPaneId);
        if (!leftPane || !rightPane) return;
        const leftMinimum = this.minimumWidthForPane(leftPane);
        const rightMinimum = this.minimumWidthForPane(rightPane);
        const minimumTotal = leftMinimum + rightMinimum;
        const effectiveLeftMinimum = drag.pairWidth >= minimumTotal
            ? leftMinimum
            : drag.pairWidth * leftMinimum / minimumTotal;
        const effectiveRightMinimum = drag.pairWidth >= minimumTotal
            ? rightMinimum
            : drag.pairWidth * rightMinimum / minimumTotal;
        const nextLeftWidth = Math.min(
            drag.pairWidth - effectiveRightMinimum,
            Math.max(effectiveLeftMinimum, drag.startLeftWidth + delta)
        );
        const ratioDelta = (nextLeftWidth - drag.startLeftWidth) / this.areaWidth;
        const ratios = new Map(drag.baseRatios);
        ratios.set(drag.leftPaneId, (drag.baseRatios.get(drag.leftPaneId) ?? 0) + ratioDelta);
        ratios.set(drag.rightPaneId, (drag.baseRatios.get(drag.rightPaneId) ?? 0) - ratioDelta);
        this.previewRatios = ratios;
    }

    equalizePair(leftPaneId, rightPaneId) {
        if (!this.snapshot || this.effectivePanes.length !== this.snapshot.panes.length) return;
        const ratios = new Map(this.snapshot.panes.map((pane) => [pane.paneId, pane.ratio]));
        const pair = (ratios.get(leftPaneId) ?? 0) + (ratios.get(rightPaneId) ?? 0);
        ratios.set(leftPaneId, pair / 2);
        ratios.set(rightPaneId, pair / 2);
        Bridge.sendPaneRatios(this.snapshot.revision, this.ratioPayload(ratios));
    }

    resizePairByKeyboard(event, leftPaneId, rightPaneId) {
        if (!this.snapshot || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        const leftRect = this.rects.get(leftPaneId);
        const rightRect = this.rects.get(rightPaneId);
        if (!leftRect || !rightRect || this.effectivePanes.length !== this.snapshot.panes.length) return;
        const ratios = new Map(this.snapshot.panes.map((pane) => [pane.paneId, pane.ratio]));
        const drag = {
            leftPaneId,
            rightPaneId,
            startLeftWidth: leftRect.width,
            pairWidth: leftRect.width + rightRect.width,
            baseRatios: ratios
        };
        const step = event.shiftKey ? 48 : 16;
        this.updatePreviewRatios((event.key === 'ArrowLeft' ? -1 : 1) * step, drag);
        if (this.previewRatios) Bridge.sendPaneRatios(this.snapshot.revision, this.ratioPayload(this.previewRatios));
        event.preventDefault();
    }

    ratioPayload(ratios) {
        return (this.snapshot?.panes ?? []).map((pane) => ({
            paneId: pane.paneId,
            ratio: ratios.get(pane.paneId) ?? pane.ratio
        }));
    }

    slotAt(clientX) {
        for (const slot of this.root.querySelectorAll('.workspace-pane')) {
            const rect = slot.getBoundingClientRect();
            if (clientX >= rect.left && clientX < rect.right) return slot;
        }
        return null;
    }

    markDropTarget(slot) {
        for (const candidate of this.root.querySelectorAll('.workspace-pane')) {
            candidate.dataset.dropTarget = candidate === slot ? 'true' : 'false';
        }
    }

    onDragOver(event) {
        const slot = this.slotAt(event.clientX);
        if (!slot) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = 'move';
        this.markDropTarget(slot);
    }

    onDrop(event) {
        const slot = this.slotAt(event.clientX);
        this.markDropTarget(null);
        if (!slot) return;
        event.preventDefault();
        const workspaceId = event.dataTransfer.getData('text/plain');
        if (PaneLayoutController.workspaceIdPattern.test(workspaceId)) {
            Bridge.sendPaneMove(workspaceId, slot.dataset.paneId);
        }
    }

    onDragLeave(event) {
        if (event.relatedTarget && this.root.contains(event.relatedTarget)) return;
        this.markDropTarget(null);
    }
}
