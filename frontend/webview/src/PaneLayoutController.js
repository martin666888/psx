// PaneLayoutController.js — neutral, always-loaded split-column geometry.
// C# owns the requested layout: an ordered list of columns (VS Code
// editor-group model), each with a tab stack, an active tab, a focus and
// normalized ratios. This controller owns the effective pixel presentation:
// a single pure-ratio allocation — columns always display strictly by their
// requested ratio, pixel floors constrain only the drag clamp and sash
// states — plus live divider drag and zoom. Columns are never collected or
// hidden: at extreme narrow widths the ratio allocation squeezes every
// column below its floor rather than dropping one (decision K).

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
        this.dockInsetInteractive = false;
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
        this.effectiveColumns = [];
        this.zoomed = false;
        this.terminalMinimumWidthResolver = null;

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

    // Capacity gate for a brand-new column (decision C): the new-column
    // preview keeps the 400px threshold for Agent columns even though an
    // existing Agent column displays at the 320px floor below.
    static get minPaneWidth() {
        return 400;
    }

    // Display floor for an existing Agent column (decision C).
    static get minAgentColumnWidth() {
        return 320;
    }

    // Display floor for an existing DSH column: aligned with the DeepSeek
    // Harness center column minimum (640px).
    static get minDshColumnWidth() {
        return 640;
    }

    // Capacity threshold for a brand-new DSH column: 720px.
    static get minDshNewPaneWidth() {
        return 720;
    }

    setTerminalMinimumWidthResolver(resolver) {
        this.terminalMinimumWidthResolver = typeof resolver === 'function' ? resolver : null;
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

    get columnCount() {
        return this.root.querySelectorAll('.workspace-pane').length;
    }

    columnAt(index) {
        return this.root.querySelectorAll('.workspace-pane')[index] ?? null;
    }

    columnById(columnId) {
        for (const slot of this.root.querySelectorAll('.workspace-pane')) {
            if (slot.dataset.columnId === columnId) return slot;
        }
        return null;
    }

    onLayoutApplied(listener) {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    /** Applies the History dock inset to the pane geometry. `interactiveResize`
     * marks live preview frames (Terminal fit deferred until the final settle).
     * Settling at the SAME width after an interactive preview still recomputes
     * once non-interactively, so terminals fit exactly once at drag end. */
    setDockInset(px, { interactiveResize = false } = {}) {
        const next = Math.max(HISTORY_RAIL_WIDTH, Math.round(Number(px) || 0));
        const interactive = interactiveResize === true;
        if (next === this.dockInset && this.dockInsetInteractive === interactive) return;
        if (this.dragging) this.cancelDrag(undefined, { recompute: false });
        this.dockInset = next;
        this.dockInsetInteractive = interactive;
        this.recompute({ interactiveResize: interactive });
    }

    toggleZoom() {
        if (this.dragging) this.cancelDrag(undefined, { recompute: false });
        this.zoomed = !this.zoomed;
        this.recompute();
        return this.zoomed;
    }

    /** The requested (C#) column count — never the effective one, which
     * under-reports while zoomed. Drives the create-menu column-cap gate. */
    get requestedColumnCount() {
        return this.snapshot?.columns?.length ?? 1;
    }

    applySnapshot(message) {
        const revision = Number(message?.revision);
        if (!Number.isFinite(revision) || revision <= this.lastRevision) return false;
        if (this.dragging) this.cancelDrag(undefined, { recompute: false });
        this.lastRevision = revision;
        this.previewRatios = null;
        this.snapshot = {
            revision,
            focusedColumnId: String(message.focusedColumnId || ''),
            columns: Array.isArray(message.columns) ? message.columns.map((column) => ({
                columnId: String(column.columnId || ''),
                tabs: Array.isArray(column.tabs) ? column.tabs.map((tab) => ({
                    workspaceId: tab.workspaceId ? String(tab.workspaceId) : null,
                    kind: tab.kind ? String(tab.kind) : null
                })) : [],
                activeTabId: column.activeTabId ? String(column.activeTabId) : null,
                ratio: Number(column.ratio) > 0 ? Number(column.ratio) : 1
            })) : []
        };
        // Diagnostic (permanent, [pane-layout]): accepted snapshots report
        // their revision, column count and focused column.
        console.debug('[pane-layout] applySnapshot: revision =', this.snapshot.revision,
            '| columns =', this.snapshot.columns.length,
            '| focused =', this.snapshot.focusedColumnId);
        this.recompute();
        return true;
    }

    recompute(options = {}) {
        const width = this.root.parentElement?.clientWidth ?? this.root.clientWidth;
        const fullHeight = this.root.parentElement?.clientHeight ?? this.root.clientHeight;
        const height = Math.max(0, fullHeight - CHROME_HEIGHT);
        const requestedColumns = this.snapshot?.columns?.length
            ? this.snapshot.columns
            : [{ columnId: 'column-1', tabs: [], activeTabId: null, ratio: 1 }];
        const requestedFocus = this.snapshot?.focusedColumnId || requestedColumns[0].columnId;
        const areaLeft = Math.min(this.dockInset, Math.max(0, width));
        const areaWidth = Math.max(0, width - areaLeft);
        this.areaLeft = areaLeft;
        this.areaWidth = areaWidth;

        let columns = requestedColumns;
        if (this.zoomed) columns = [requestedColumns.find((column) => column.columnId === requestedFocus) ?? requestedColumns[0]];
        const focusedColumnId = columns.some((column) => column.columnId === requestedFocus)
            ? requestedFocus
            : columns[0].columnId;
        this.effectiveColumns = columns;

        const displayRatios = this.normalizedDisplayRatios(columns);
        const widths = this.allocateWidths(columns, areaWidth, displayRatios);
        this.syncPaneSlots(columns, focusedColumnId, displayRatios);
        this.root.style.left = `${areaLeft}px`;
        this.root.style.top = `${CHROME_HEIGHT}px`;

        const rects = new Map();
        let x = areaLeft;
        columns.forEach((column, index) => {
            const columnWidth = index === columns.length - 1
                ? areaLeft + areaWidth - x
                : Math.round(widths.get(column.columnId) ?? 0);
            rects.set(column.columnId, {
                left: x,
                top: CHROME_HEIGHT,
                width: Math.max(0, columnWidth),
                height,
                // The open History dock already carries the left gutter and
                // the panel gap inside the dock inset; the Agent host drops
                // its own left gutter on this column so the dock edge and the
                // panel edge stay exactly one panel gap apart.
                dockAdjacent: index === 0 && this.dockInset > HISTORY_RAIL_WIDTH
            });
            x += columnWidth;
        });
        this.rects = rects;
        this.updateSashStates(columns, rects);

        const effective = {
            revision: this.snapshot?.revision ?? 0,
            focusedColumnId,
            columns,
            // The full requested layout travels beside the effective
            // presentation: the chrome's workspace list and column-cap gates
            // must reflect the whole open set even while zoomed (decision F).
            requested: this.snapshot
                ? { focusedColumnId: this.snapshot.focusedColumnId, columns: this.snapshot.columns }
                : null
        };
        for (const listener of this.listeners) {
            listener(effective, rects, { interactiveResize: options.interactiveResize === true });
        }
    }

    /** The kind of the workspace visible in a column (its active tab), or
     * null for an empty column. */
    columnKind(column) {
        if (!column.activeTabId) return null;
        const tab = (column.tabs || []).find((item) => item.workspaceId === column.activeTabId);
        return tab?.kind ?? null;
    }

    // Single pure-ratio allocation (decisions C/K). Every requested column is
    // always shown; columns allocate strictly by their requested ratio,
    // whether or not that lands below a pixel floor. When the total width
    // cannot satisfy the sum of floors, the ratio widths squeeze
    // proportionally below their floors (never hidden). Floors constrain only
    // the drag clamp and the sash at-minimum state — never the allocation, so
    // switching focus never changes any column width. Divider slots, the sash
    // and the content rects share this same geometry.
    allocateWidths(columns, areaWidth, displayRatios) {
        const ratioSum = columns.reduce((sum, column) => sum + (displayRatios.get(column.columnId) ?? column.ratio), 0) || 1;
        return new Map(columns.map((column) => [
            column.columnId,
            areaWidth * (displayRatios.get(column.columnId) ?? column.ratio) / ratioSum
        ]));
    }

    // Display floor for an existing column (decision C): Agent columns 320px,
    // DSH columns 640px (DSH's center column minimum), Terminal columns
    // max(400, 60 x measured cell width + padding) or 480px until measured.
    displayFloorForColumn(column) {
        const kind = this.columnKind(column);
        if (kind === 'dsh_web') return PaneLayoutController.minDshColumnWidth;
        if (kind !== 'terminal') return PaneLayoutController.minAgentColumnWidth;
        const measured = this.terminalMinimumWidthResolver?.(column.activeTabId);
        return Number.isFinite(measured) ? Math.max(PaneLayoutController.minPaneWidth, measured) : 480;
    }

    // Capacity helpers — the preventive layer. The chrome disables "new right
    // column" entries when the requested layout plus the new pane's minimum
    // would overflow the available width. This pixel gate is the first
    // defensive line (presentation duty); the C# 3-column count stays the
    // second, so both layers remain in force. The new-column preview keeps
    // the 400px Agent threshold ("容量预演仍用 400 门槛").

    availableWidth() {
        return this.areaWidth;
    }

    // Always measured from the C# REQUESTED layout (snapshot.columns), never
    // from effectiveColumns: the effective list under-reports while zoomed and
    // would wrongly allow yet another column.
    requestedMinimumWidthSum() {
        return this.minimumWidthSum(this.snapshot?.columns ?? []);
    }

    minimumWidthSum(columns) {
        return columns.reduce((sum, column) => sum + this.minimumWidthForPane(column), 0);
    }

    minimumWidthForPane(column) {
        const kind = this.columnKind(column);
        if (kind === 'dsh_web') return PaneLayoutController.minDshColumnWidth;
        if (kind !== 'terminal') return PaneLayoutController.minPaneWidth;
        const measured = this.terminalMinimumWidthResolver?.(column.activeTabId);
        return Number.isFinite(measured) ? Math.max(PaneLayoutController.minPaneWidth, measured) : 480;
    }

    // A brand-new column has no workspace yet: 'terminal' reuses the measured
    // width of an already-open terminal when one exists, otherwise the
    // unmeasured fallback (480); Agent columns always need 400; DSH columns
    // always need 720 (a fresh DSH column must fit the app's usable center).
    minimumWidthForNewPane(kind) {
        if (kind === 'dsh_web') return PaneLayoutController.minDshNewPaneWidth;
        if (kind !== 'terminal') return PaneLayoutController.minPaneWidth;
        const terminalColumn = this.snapshot?.columns?.find(
            (column) => this.columnKind(column) === 'terminal' && column.activeTabId
        );
        if (terminalColumn) {
            const measured = this.terminalMinimumWidthResolver?.(terminalColumn.activeTabId);
            if (Number.isFinite(measured)) return Math.max(PaneLayoutController.minPaneWidth, measured);
        }
        return 480;
    }

    normalizedDisplayRatios(columns) {
        const source = this.previewRatios ?? new Map((this.snapshot?.columns ?? columns).map((column) => [column.columnId, column.ratio]));
        const sum = columns.reduce((total, column) => total + (source.get(column.columnId) ?? column.ratio), 0) || 1;
        return new Map(columns.map((column) => [column.columnId, (source.get(column.columnId) ?? column.ratio) / sum]));
    }

    syncPaneSlots(columns, focusedColumnId, displayRatios) {
        this.root.dataset.splitActive = columns.length > 1 ? 'true' : 'false';
        const seen = new Set();
        for (const column of columns) {
            seen.add(column.columnId);
            let slot = this.columnById(column.columnId);
            if (!slot) {
                slot = document.createElement('div');
                slot.className = 'workspace-pane';
                slot.dataset.columnId = column.columnId;
                this.root.appendChild(slot);
            }
            slot.style.flexGrow = String(displayRatios.get(column.columnId) ?? 1);
            slot.dataset.focused = column.columnId === focusedColumnId ? 'true' : 'false';
        }
        for (const slot of [...this.root.querySelectorAll('.workspace-pane')]) {
            if (!seen.has(slot.dataset.columnId)) slot.remove();
        }

        const desiredDividers = new Set();
        columns.forEach((column, index) => {
            const slot = this.columnById(column.columnId) ?? this.createSlot(column.columnId);
            this.root.appendChild(slot);
            if (index >= columns.length - 1) return;
            const rightColumn = columns[index + 1];
            const key = `${column.columnId}|${rightColumn.columnId}`;
            desiredDividers.add(key);
            let divider = [...this.root.querySelectorAll('.workspace-pane-divider')]
                .find((candidate) => candidate.dataset.dividerKey === key);
            if (!divider) divider = this.createDivider(column.columnId, rightColumn.columnId);
            const leftRatio = displayRatios.get(column.columnId) ?? 0;
            const rightRatio = displayRatios.get(rightColumn.columnId) ?? 0;
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

    createSlot(columnId) {
        const slot = document.createElement('div');
        slot.className = 'workspace-pane';
        slot.dataset.columnId = columnId;
        return slot;
    }

    createDivider(leftColumnId, rightColumnId) {
        const divider = document.createElement('div');
        divider.className = 'workspace-pane-divider';
        divider.dataset.dividerKey = `${leftColumnId}|${rightColumnId}`;
        divider.setAttribute('role', 'separator');
        divider.setAttribute('aria-orientation', 'vertical');
        divider.setAttribute('aria-label', '调整相邻列宽度');
        divider.tabIndex = 0;
        divider.title = '拖动调整宽度，双击均分相邻列';

        divider.addEventListener('pointerdown', (event) => {
            if (event.button !== 0 || !this.snapshot) return;
            // Both sides pinned to their floors: the divider is inert (the
            // preview clamp could not move it anyway).
            if (divider.getAttribute('aria-disabled') === 'true') return;
            const leftRect = this.rects.get(leftColumnId);
            const rightRect = this.rects.get(rightColumnId);
            if (!leftRect || !rightRect) return;
            divider.setPointerCapture?.(event.pointerId);
            divider.dataset.dragging = 'true';
            this.dragging = {
                leftColumnId,
                rightColumnId,
                pointerId: event.pointerId,
                divider,
                startClientX: event.clientX,
                startLeftWidth: leftRect.width,
                pairWidth: leftRect.width + rightRect.width,
                baseRevision: this.snapshot.revision,
                baseRatios: new Map(this.snapshot.columns.map((column) => [column.columnId, column.ratio]))
            };
            this.root.dataset.resizing = 'true';
            event.preventDefault();
            event.stopPropagation();
        });
        divider.addEventListener('dblclick', () => this.equalizePair(leftColumnId, rightColumnId));
        divider.addEventListener('keydown', (event) => this.resizePairByKeyboard(event, leftColumnId, rightColumnId));
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
        if (payload) Bridge.sendPaneRatios(drag.baseRevision, payload);
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

    // Turn a pixel delta into a ratio delta. Display widths are always
    // pure-ratio allocations of the area (no focus expansion), so a pixel
    // move converts linearly over areaWidth; startLeftWidth comes from the
    // same allocation, keeping pixels and ratios on one coordinate system.
    updatePreviewRatios(delta, drag) {
        if (!this.snapshot || this.areaWidth <= 0 || drag.pairWidth <= 0) return;
        const leftColumn = this.snapshot.columns.find((column) => column.columnId === drag.leftColumnId);
        const rightColumn = this.snapshot.columns.find((column) => column.columnId === drag.rightColumnId);
        if (!leftColumn || !rightColumn) return;
        const leftMinimum = this.displayFloorForColumn(leftColumn);
        const rightMinimum = this.displayFloorForColumn(rightColumn);
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
        ratios.set(drag.leftColumnId, (drag.baseRatios.get(drag.leftColumnId) ?? 0) + ratioDelta);
        ratios.set(drag.rightColumnId, (drag.baseRatios.get(drag.rightColumnId) ?? 0) - ratioDelta);
        this.previewRatios = ratios;
    }

    equalizePair(leftColumnId, rightColumnId) {
        if (!this.snapshot) return;
        const ratios = new Map(this.snapshot.columns.map((column) => [column.columnId, column.ratio]));
        const pair = (ratios.get(leftColumnId) ?? 0) + (ratios.get(rightColumnId) ?? 0);
        ratios.set(leftColumnId, pair / 2);
        ratios.set(rightColumnId, pair / 2);
        Bridge.sendPaneRatios(this.snapshot.revision, this.ratioPayload(ratios));
    }

    resizePairByKeyboard(event, leftColumnId, rightColumnId) {
        if (!this.snapshot || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        const leftRect = this.rects.get(leftColumnId);
        const rightRect = this.rects.get(rightColumnId);
        if (!leftRect || !rightRect) return;
        const ratios = new Map(this.snapshot.columns.map((column) => [column.columnId, column.ratio]));
        const drag = {
            leftColumnId,
            rightColumnId,
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
        // The wire keeps the pane_ratios_commit shape (paneId key) with the
        // column id as the id space.
        return (this.snapshot?.columns ?? []).map((column) => ({
            paneId: column.columnId,
            ratio: ratios.get(column.columnId) ?? column.ratio
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
            Bridge.sendPaneMove(workspaceId, slot.dataset.columnId);
        }
    }

    onDragLeave(event) {
        if (event.relatedTarget && this.root.contains(event.relatedTarget)) return;
        this.markDropTarget(null);
    }

    // Sash state (decision C/K): a divider reports at-minimum when either
    // side sits on its pixel floor (the preview clamp cannot shrink it
    // further); both sides at their floors disables the divider entirely.
    // The CSS swaps the cursor; the drag preview already clamps.
    updateSashStates(columns, rects) {
        for (let index = 0; index < columns.length - 1; index++) {
            const left = columns[index];
            const right = columns[index + 1];
            const divider = [...this.root.querySelectorAll('.workspace-pane-divider')]
                .find((candidate) => candidate.dataset.dividerKey === `${left.columnId}|${right.columnId}`);
            if (!divider) continue;
            const leftRect = rects.get(left.columnId);
            const rightRect = rects.get(right.columnId);
            if (!leftRect || !rightRect) continue;
            const leftAtFloor = Math.abs(leftRect.width - this.displayFloorForColumn(left)) < 1;
            const rightAtFloor = Math.abs(rightRect.width - this.displayFloorForColumn(right)) < 1;
            divider.dataset.sashState = leftAtFloor || rightAtFloor ? 'at-minimum' : '';
            const disabled = leftAtFloor && rightAtFloor;
            divider.setAttribute('aria-disabled', disabled ? 'true' : 'false');
            divider.dataset.sashDisabled = disabled ? 'true' : 'false';
        }
    }
}
