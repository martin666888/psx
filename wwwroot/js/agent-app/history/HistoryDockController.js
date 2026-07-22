// HistoryDockController.ts — the single global History dock.
//
// One dock for the whole process, living outside every workspace panel (a
// left column inside #agent-workspace-container). It renders the global
// AgentHistoryStore: grouped/searchable/filterable thread list, loading /
// error / unavailable states, open-thread markers and a collapse/refresh
// top bar. All `history` loads go through the AgentHistoryRequestBroker;
// `load_thread` clicks use the broker's channel picker without entering the
// request state machine. Open state and width persist in localStorage.
//
// Layered layout (canvas/overlay), theming and the responsive drawer are
// later steps; this step is a plain block-level dock.
import { buildHistoryGroups, filterHistoryGroups, providerFilterOptions } from './historyModel.js';
const MIN_WIDTH = 220;
const MAX_WIDTH = 420;
const DEFAULT_WIDTH = 280;
const OPEN_STORAGE_KEY = 'psx.agent.historyDockOpen';
const WIDTH_STORAGE_KEY = 'psx.agent.historyDockWidth';
const FIRST_RUN_MIN_WIDTH = 1080;
export class HistoryDockController {
    host;
    dock = null;
    trigger = null;
    container = null;
    content = null;
    searchInput = null;
    providerSelect = null;
    open = false;
    agentViewActive = true;
    width = DEFAULT_WIDTH;
    isResizing = false;
    providerFilterValue = '';
    providerOptionsSignature = '';
    unsubscribeStore = null;
    openListeners = new Set();
    cleanup = [];
    constructor(host) {
        this.host = host;
    }
    mount(parent) {
        if (this.dock || !parent)
            return;
        const dock = document.createElement('aside');
        dock.className = 'agent-history-dock';
        dock.dataset.role = 'history-dock';
        dock.setAttribute('aria-label', 'Agent history');
        dock.hidden = true;
        const bar = document.createElement('div');
        bar.className = 'agent-history-dock-bar';
        const search = document.createElement('input');
        search.type = 'search';
        search.className = 'agent-history-search';
        search.dataset.role = 'history-search';
        search.placeholder = 'Search threads';
        search.setAttribute('aria-label', 'Search history');
        const providerSelect = document.createElement('select');
        providerSelect.className = 'agent-history-provider-filter';
        providerSelect.dataset.role = 'history-provider-filter';
        providerSelect.setAttribute('aria-label', 'Filter by provider');
        const refresh = document.createElement('button');
        refresh.type = 'button';
        refresh.className = 'agent-history-refresh';
        refresh.dataset.role = 'history-refresh';
        refresh.title = 'Refresh history';
        refresh.textContent = 'Refresh';
        const collapse = document.createElement('button');
        collapse.type = 'button';
        collapse.className = 'agent-history-collapse';
        collapse.dataset.role = 'history-collapse';
        collapse.title = 'Collapse history panel';
        collapse.textContent = 'Collapse';
        bar.appendChild(search);
        bar.appendChild(providerSelect);
        bar.appendChild(refresh);
        bar.appendChild(collapse);
        const content = document.createElement('div');
        content.className = 'agent-history-dock-content';
        content.dataset.role = 'history-content';
        const resizer = document.createElement('div');
        resizer.className = 'agent-history-dock-resizer';
        resizer.dataset.role = 'history-dock-resizer';
        resizer.setAttribute('role', 'separator');
        resizer.setAttribute('aria-label', 'Resize Agent history');
        resizer.setAttribute('aria-orientation', 'vertical');
        resizer.tabIndex = 0;
        dock.appendChild(bar);
        dock.appendChild(content);
        dock.appendChild(resizer);
        parent.appendChild(dock);
        // The trigger is the only always-available History entry (the composer
        // `/history` command is the other); the shell layout controller reads its
        // aria-expanded state and returns focus to it when a drawer closes.
        const trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'agent-history-trigger';
        trigger.dataset.role = 'history-trigger';
        trigger.textContent = 'History';
        trigger.setAttribute('aria-expanded', 'false');
        trigger.setAttribute('aria-label', 'Open Agent history');
        parent.appendChild(trigger);
        this.dock = dock;
        this.trigger = trigger;
        this.container = parent;
        this.content = content;
        this.searchInput = search;
        this.providerSelect = providerSelect;
        this.width = this.readWidth();
        this.applyWidth(this.width);
        this.open = this.readOpen();
        this.unsubscribeStore = this.host.subscribe(() => this.render());
        this.wire(search, providerSelect, refresh, collapse, resizer);
        this.on(trigger, 'click', () => this.openHistory(''));
        this.syncVisibility();
        this.render();
    }
    /** Composer `/history` + trigger seam: open the dock and load through the
     * broker. The first open loads via syncVisibility → maybeAutoLoad; an
     * explicit refresh is only sent once data exists, so opening never queues
     * a redundant second wave. */
    openHistory(sourceWorkspaceId) {
        this.setOpen(true);
        if (this.host.getState().loaded)
            this.host.requestRefresh(sourceWorkspaceId);
    }
    isOpen() {
        return this.open;
    }
    /** Shell layout seam: fires after every open-state change (drawer
     * exclusivity + trigger aria-expanded sync live there). */
    onOpenChanged(listener) {
        this.openListeners.add(listener);
    }
    /** Focus entry points for the drawer accessibility flow. */
    focusSearch() {
        this.searchInput?.focus();
    }
    focusTrigger() {
        this.trigger?.focus();
    }
    /** WorkspaceHost view toggle: the whole agent area (dock included) hides
     * while a terminal workspace is active. */
    setAgentViewActive(active) {
        if (this.agentViewActive === active)
            return;
        this.agentViewActive = active;
        this.syncVisibility();
    }
    /** Registry hook after workspace events/lifecycle: refresh open-thread
     * markers and kick the first load once a workspace exists. */
    updateOpenState() {
        this.maybeAutoLoad();
        this.render();
    }
    dispose() {
        if (this.isResizing) {
            this.isResizing = false;
            document.body.classList.remove('agent-history-dock-resizing');
        }
        if (this.unsubscribeStore) {
            this.unsubscribeStore();
            this.unsubscribeStore = null;
        }
        for (const off of this.cleanup.splice(0))
            off();
        this.dock?.remove();
        this.trigger?.remove();
        this.container?.style.removeProperty('--agent-history-width');
        this.dock = null;
        this.trigger = null;
        this.container = null;
        this.content = null;
        this.searchInput = null;
        this.providerSelect = null;
    }
    // --- Visibility + persistence ---------------------------------------------
    setOpen(open) {
        if (this.open === open) {
            this.syncVisibility();
            return;
        }
        this.open = open;
        try {
            window.localStorage?.setItem(OPEN_STORAGE_KEY, open ? '1' : '0');
        }
        catch {
            // Persistence is optional; the dock still works for the session.
        }
        this.syncVisibility();
        for (const listener of this.openListeners)
            listener(open);
    }
    readOpen() {
        try {
            const stored = window.localStorage?.getItem(OPEN_STORAGE_KEY);
            if (stored === '1')
                return true;
            if (stored === '0')
                return false;
        }
        catch {
            // Fall through to the first-run default.
        }
        // First run: expanded on desktop widths, collapsed on compact widths
        // (the full responsive behaviour is a later step).
        return window.matchMedia('(min-width: ' + FIRST_RUN_MIN_WIDTH + 'px)').matches;
    }
    syncVisibility() {
        if (!this.dock)
            return;
        const visible = this.open && this.agentViewActive;
        this.dock.hidden = !visible;
        this.dock.parentElement?.classList.toggle('agent-history-dock-open', visible);
        if (this.trigger) {
            this.trigger.hidden = this.open || !this.agentViewActive;
            this.trigger.setAttribute('aria-expanded', String(this.open));
        }
        if (visible)
            this.maybeAutoLoad();
    }
    maybeAutoLoad() {
        if (!this.dock || this.dock.hidden)
            return;
        if (!this.host.hasAgentWorkspaces())
            return;
        const state = this.host.getState();
        if (state.loaded || state.status !== 'idle' || state.inFlightWorkspaceId)
            return;
        this.host.requestRefresh('');
    }
    readWidth() {
        try {
            const width = Number(window.localStorage?.getItem(WIDTH_STORAGE_KEY));
            if (Number.isFinite(width) && width > 0)
                return this.clampWidth(width);
        }
        catch {
            // localStorage can be unavailable in constrained WebView profiles.
        }
        return DEFAULT_WIDTH;
    }
    applyWidth(width) {
        // The canvas offset rules in shell.css inherit --agent-history-width too
        // (dock and .agent-panel are siblings), so the variable must live on the
        // shared container, not on the dock element itself.
        this.container?.style.setProperty('--agent-history-width', this.clampWidth(width) + 'px');
    }
    saveWidth() {
        try {
            window.localStorage?.setItem(WIDTH_STORAGE_KEY, String(this.width));
        }
        catch {
            // Width persistence is optional; resizing remains available.
        }
    }
    clampWidth(width) {
        const numeric = Number(width);
        if (!Number.isFinite(numeric))
            return DEFAULT_WIDTH;
        return Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, Math.round(numeric)));
    }
    // --- Wiring -------------------------------------------------------------------
    wire(search, providerSelect, refresh, collapse, resizer) {
        this.on(search, 'input', () => this.render({ resetScroll: true }));
        this.on(providerSelect, 'change', () => {
            this.providerFilterValue = providerSelect.value;
            this.render({ resetScroll: true });
        });
        this.on(refresh, 'click', () => this.host.requestRefresh(''));
        this.on(collapse, 'click', () => this.setOpen(false));
        this.on(resizer, 'pointerdown', (event) => {
            if (event.button !== 0)
                return;
            this.isResizing = true;
            resizer.setPointerCapture(event.pointerId);
            document.body.classList.add('agent-history-dock-resizing');
            event.preventDefault();
        });
        this.on(resizer, 'pointermove', (event) => {
            if (!this.isResizing || !this.dock)
                return;
            const rect = this.dock.getBoundingClientRect();
            this.width = this.clampWidth(event.clientX - rect.left);
            this.applyWidth(this.width);
        });
        const endResize = (event) => {
            if (!this.isResizing)
                return;
            this.isResizing = false;
            document.body.classList.remove('agent-history-dock-resizing');
            try {
                resizer.releasePointerCapture(event.pointerId);
            }
            catch {
                // Pointer capture may already be gone if the window lost focus.
            }
            this.saveWidth();
        };
        this.on(resizer, 'pointerup', endResize);
        this.on(resizer, 'pointercancel', endResize);
        this.on(resizer, 'keydown', (event) => {
            const step = event.shiftKey ? 40 : 16;
            if (event.key === 'ArrowLeft')
                this.setWidthFromKeyboard(this.width - step);
            else if (event.key === 'ArrowRight')
                this.setWidthFromKeyboard(this.width + step);
            else if (event.key === 'Home')
                this.setWidthFromKeyboard(MAX_WIDTH);
            else if (event.key === 'End')
                this.setWidthFromKeyboard(MIN_WIDTH);
            else
                return;
            event.preventDefault();
        });
    }
    setWidthFromKeyboard(width) {
        this.width = this.clampWidth(width);
        this.applyWidth(this.width);
        this.saveWidth();
    }
    // --- Rendering ------------------------------------------------------------------
    render(options) {
        if (!this.content)
            return;
        // Rebuilds wipe the list; keep the user's place across store-driven
        // renders (open-marker refreshes, invalidation waves) and only reset when
        // the search box or provider filter changed the result set.
        const scrollTop = options?.resetScroll ? 0 : this.content.scrollTop;
        this.renderContent();
        this.content.scrollTop = scrollTop;
    }
    renderContent() {
        if (!this.content)
            return;
        const state = this.host.getState();
        this.syncProviderOptions(state);
        this.content.innerHTML = '';
        if (!this.host.hasAgentWorkspaces()) {
            this.showState('empty', 'Open an Agent workspace to load history.');
            return;
        }
        if (state.status === 'initial-loading') {
            this.showState('loading', 'Loading history...');
            return;
        }
        if (state.status === 'unavailable') {
            this.showState('empty', 'Open an Agent workspace to load history.');
            return;
        }
        if (state.status === 'error') {
            this.renderError(state.errorText);
            return;
        }
        const query = this.searchInput?.value ?? '';
        const groups = filterHistoryGroups(buildHistoryGroups(state.threads, state.providers), query, this.providerFilterValue);
        if (state.threads.length === 0) {
            this.showState('empty', state.loaded ? 'No saved Agent threads.' : 'Loading history...');
            return;
        }
        if (groups.length === 0) {
            this.showState('empty', 'No threads match the current search or filter.');
            return;
        }
        const activeWorkspaceId = this.host.activeWorkspaceId();
        const openThreads = this.host.openWorkspaceThreadIds();
        const activeThreadId = activeWorkspaceId ? openThreads.get(activeWorkspaceId) ?? '' : '';
        const openedElsewhere = new Set();
        for (const [workspaceId, threadId] of openThreads) {
            if (threadId && workspaceId !== activeWorkspaceId)
                openedElsewhere.add(threadId);
        }
        const list = document.createElement('div');
        list.className = 'agent-history-list';
        for (const group of groups) {
            const heading = document.createElement('div');
            heading.className = 'agent-history-group-heading';
            const name = document.createElement('strong');
            name.textContent = group.name;
            heading.appendChild(name);
            if (group.path) {
                const path = document.createElement('small');
                path.textContent = group.path;
                path.title = group.path;
                heading.appendChild(path);
            }
            list.appendChild(heading);
            for (const thread of group.threads) {
                list.appendChild(this.renderThread(thread, activeThreadId, openedElsewhere));
            }
        }
        this.content.appendChild(list);
    }
    renderThread(thread, activeThreadId, openedElsewhere) {
        const row = document.createElement('button');
        row.type = 'button';
        row.className = 'agent-history-item';
        row.dataset.threadId = thread.threadId || '';
        row.setAttribute('aria-current', thread.threadId === activeThreadId ? 'true' : 'false');
        const heading = document.createElement('span');
        heading.className = 'agent-history-heading';
        const title = document.createElement('strong');
        title.textContent = thread.title || 'Agent Chat';
        heading.appendChild(title);
        if (thread.threadId === activeThreadId) {
            const current = document.createElement('span');
            current.className = 'agent-history-current';
            current.textContent = 'Current';
            heading.appendChild(current);
        }
        else if (thread.threadId && openedElsewhere.has(thread.threadId)) {
            const opened = document.createElement('span');
            opened.className = 'agent-history-open';
            opened.textContent = 'Open';
            heading.appendChild(opened);
        }
        const meta = document.createElement('small');
        const parts = [];
        if (thread.providerDisplay)
            parts.push(thread.providerDisplay);
        if (thread.updatedAt)
            parts.push(thread.updatedAt);
        meta.textContent = parts.join(' | ');
        row.appendChild(heading);
        row.appendChild(meta);
        row.addEventListener('click', () => {
            if (thread.threadId)
                this.host.sendCommandOnChannel('load_thread', thread.threadId);
        });
        return row;
    }
    renderError(text) {
        if (!this.content)
            return;
        const state = document.createElement('div');
        state.className = 'agent-history-state agent-history-error';
        state.setAttribute('role', 'alert');
        const message = document.createElement('p');
        message.textContent = text || 'Unable to load Agent thread history.';
        const retry = document.createElement('button');
        retry.type = 'button';
        retry.className = 'agent-history-retry';
        retry.textContent = 'Retry';
        retry.addEventListener('click', () => this.host.requestRefresh(''));
        state.appendChild(message);
        state.appendChild(retry);
        this.content.appendChild(state);
    }
    showState(stateName, text) {
        if (!this.content)
            return;
        const state = document.createElement('div');
        state.className = 'agent-history-state agent-history-' + stateName;
        if (stateName === 'loading') {
            state.setAttribute('role', 'status');
            state.setAttribute('aria-live', 'polite');
        }
        state.textContent = text;
        this.content.appendChild(state);
    }
    syncProviderOptions(state) {
        if (!this.providerSelect)
            return;
        const options = providerFilterOptions(state.threads, state.providers);
        const signature = options.map((option) => option.value + '="' + option.label).join('');
        if (signature === this.providerOptionsSignature)
            return;
        this.providerOptionsSignature = signature;
        const selected = this.providerFilterValue;
        this.providerSelect.innerHTML = '';
        const all = document.createElement('option');
        all.value = '';
        all.textContent = 'All providers';
        this.providerSelect.appendChild(all);
        for (const option of options) {
            const item = document.createElement('option');
            item.value = option.value;
            item.textContent = option.label;
            this.providerSelect.appendChild(item);
        }
        // Drop a selection whose option disappeared from the data.
        this.providerFilterValue = options.some((option) => option.value === selected) ? selected : '';
        this.providerSelect.value = this.providerFilterValue;
    }
    on(node, type, handler) {
        if (!node)
            return;
        node.addEventListener(type, handler);
        this.cleanup.push(() => node.removeEventListener(type, handler));
    }
}
