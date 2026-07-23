// HistoryDockController.ts — the single global History dock.
//
// One dock for the whole process, living outside every workspace panel (a
// left column inside #agent-workspace-container). It renders the global
// AgentHistoryStore: grouped/searchable/filterable thread list, loading /
// error / unavailable states, open-thread markers and a search/filter/refresh
// top bar. Open/close lives on the per-workspace toolbar toggle (event
// delegation from the container); all `history` loads go through the
// AgentHistoryRequestBroker and `load_thread` clicks use the broker's channel
// picker without entering the request state machine. Open state and width
// persist in localStorage.
//
// Layered layout keeps History as a left dock at every width. Responsive
// layouts can temporarily collapse it before it would squeeze the reading
// column, but never turn it into a floating drawer.

import type {
  AgentHistoryListener,
  AgentHistoryState
} from '../contracts/agent-history.js';
import {
  buildHistoryGroups,
  filterHistoryGroups,
  formatHistoryTime,
  providerFilterOptions
} from './historyModel.js';

/** Everything the dock needs from the registry (store + broker + the live
 * workspace set). Provided as one seam so the controller stays testable. */
export interface HistoryDockHost {
  getState(): AgentHistoryState;
  subscribe(listener: AgentHistoryListener): () => void;
  requestRefresh(originWorkspaceId: string): void;
  openThread(threadId: string): boolean;
  dismissThreadOpenError(): void;
  hasAgentWorkspaces(): boolean;
  /** The active Agent workspace, or '' when a terminal/nothing is active. */
  activeWorkspaceId(): string;
  /** workspaceId → currentThreadId for every live workspace bound to a thread. */
  openWorkspaceThreadIds(): ReadonlyMap<string, string>;
}

const MIN_WIDTH = 220;
const MAX_WIDTH = 420;
const DEFAULT_WIDTH = 280;
const OPEN_STORAGE_KEY = 'psx.agent.historyDockOpen';
const WIDTH_STORAGE_KEY = 'psx.agent.historyDockWidth';

// Group fold affordance: a folder that is open when the group is expanded and
// closed when folded. Both SVGs live in the header; CSS shows one per state.
const FOLDER_CLOSED_ICON =
  '<svg class="agent-history-folder-closed" viewBox="0 0 16 16" width="13" height="13" aria-hidden="true" focusable="false"><path d="M2.5 4h4l1.5 2h5.5v6.5h-11z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/></svg>';
const FOLDER_OPEN_ICON =
  '<svg class="agent-history-folder-open" viewBox="0 0 16 16" width="13" height="13" aria-hidden="true" focusable="false"><path d="M2.5 4h4l1.5 2h5.5v2h-8.5l-2.5 6.5h-1z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/><path d="M4.5 8.5h9l-2 5h-9z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/></svg>';

export class HistoryDockController {
  private readonly host: HistoryDockHost;

  private dock: HTMLElement | null = null;
  private container: HTMLElement | null = null;
  private content: HTMLElement | null = null;
  private searchInput: HTMLInputElement | null = null;
  private providerSelect: HTMLSelectElement | null = null;

  // preferredOpen is the persisted user choice. responsiveOverride is a
  // temporary responsive value: null means normal layout, boolean means the
  // responsive layout is currently in control.
  private preferredOpen = true;
  private responsiveOverride: boolean | null = null;
  private agentViewActive = true;
  private width = DEFAULT_WIDTH;
  private isResizing = false;
  private providerFilterValue = '';
  private providerOptionsSignature = '';
  private unsubscribeStore: (() => void) | null = null;
  /** Folded groups by cwd key (user clicked the header to collapse). Runtime
   * state, never persisted; shared across workspaces (a project is global). */
  private readonly foldedGroups = new Set<string>();
  /** Groups the user expanded past the 5-item preview limit. */
  private readonly expandedGroups = new Set<string>();
  private readonly openListeners = new Set<(open: boolean) => void>();
  private readonly widthListeners = new Set<(width: number) => void>();

  private readonly cleanup: Array<() => void> = [];

  constructor(host: HistoryDockHost) {
    this.host = host;
  }

  mount(parent: HTMLElement): void {
    if (this.dock || !parent) return;

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
    refresh.setAttribute('aria-label', 'Refresh history');
    refresh.innerHTML =
      '<svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true" focusable="false">' +
      '<path d="M13.5 8a5.5 5.5 0 1 1-1.61-3.89 M13.5 2.5v2.6h-2.6" fill="none" ' +
      'stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/></svg>';

    bar.appendChild(search);
    bar.appendChild(providerSelect);
    bar.appendChild(refresh);

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

    this.dock = dock;
    this.container = parent;
    this.content = content;
    this.searchInput = search;
    this.providerSelect = providerSelect;

    this.width = this.readWidth();
    this.applyWidth(this.width);
    this.preferredOpen = this.readOpen();
    this.unsubscribeStore = this.host.subscribe(() => this.render());
    this.wire(search, providerSelect, refresh, resizer);
    // The open/close control is the history-toggle button in every workspace
    // toolbar (there is no dock-owned strip trigger anymore); one delegated
    // listener on the container also covers panels created later.
    this.on(parent, 'click', (event) => {
      const target = event.target as HTMLElement | null;
      if (!target || !target.closest('[data-role="history-toggle"]')) return;
      if (this.effectiveOpen()) this.requestClose();
      else this.openHistory('');
    });
    this.syncVisibility();
    this.render();
  }

  /** Composer `/history` + toolbar-toggle seam: open the dock and load through the
   * broker. The first open loads via syncVisibility → maybeAutoLoad; an
   * explicit refresh is only sent once data exists, so opening never queues
   * a redundant second wave. */
  openHistory(sourceWorkspaceId: string): void {
    this.requestOpen(sourceWorkspaceId);
  }

  isOpen(): boolean {
    return this.effectiveOpen();
  }

  /** Shell layout seam: fires after every effective visibility change. */
  onOpenChanged(listener: (open: boolean) => void): void {
    this.openListeners.add(listener);
  }

  /** Current persisted dock width, used by Shell to avoid squeezing reading. */
  getWidth(): number {
    return this.width;
  }

  /** Fires when a completed pointer resize or keyboard resize changes width. */
  onWidthChanged(listener: (width: number) => void): void {
    this.widthListeners.add(listener);
  }

  /** Focus entry point for user-initiated History open requests. */
  focusSearch(): void {
    this.searchInput?.focus();
  }

  /** Focus the history toggle of the currently visible workspace panel. */
  focusToggle(): void {
    const panel = this.dock?.parentElement?.querySelector('.agent-panel:not([hidden])');
    panel?.querySelector<HTMLElement>('[data-role="history-toggle"]')?.focus();
  }

  /** WorkspaceHost view toggle: the whole agent area (dock included) hides
   * while a terminal workspace is active. */
  setAgentViewActive(active: boolean): void {
    if (this.agentViewActive === active) return;
    this.agentViewActive = active;
    this.syncVisibility();
  }

  /** Registry hook after workspace events/lifecycle: refresh open-thread
   * markers and kick the first load once a workspace exists. Also syncs
   * visibility + every toolbar toggle so a newly created panel's
   * history-toggle button starts in the correct aria-expanded state. */
  updateOpenState(): void {
    this.syncVisibility();
    this.maybeAutoLoad();
    this.render();
  }

  dispose(): void {
    if (this.isResizing) {
      this.isResizing = false;
      document.body.classList.remove('agent-history-dock-resizing');
    }
    if (this.unsubscribeStore) {
      this.unsubscribeStore();
      this.unsubscribeStore = null;
    }
    for (const off of this.cleanup.splice(0)) off();
    this.dock?.remove();
    this.container?.style.removeProperty('--agent-history-width');
    this.dock = null;
    this.container = null;
    this.content = null;
    this.searchInput = null;
    this.providerSelect = null;
  }

  // --- Visibility + persistence ---------------------------------------------

  /** Applies Shell's temporary responsive collapse without persistence. */
  applyResponsiveCollapse(collapsed: boolean, resetTemporaryOpen = false): void {
    if (collapsed) {
      // A repeated broadcast must not undo a user's temporary open.
      if (!resetTemporaryOpen && this.responsiveOverride !== null) return;
      this.setResponsiveOverride(false);
      return;
    }
    if (this.responsiveOverride === null) return;
    this.setResponsiveOverride(null);
  }

  /** User request: updates the persistent preference in normal layout and
   * only the temporary override while responsive collapse is active. */
  requestOpen(sourceWorkspaceId = ''): void {
    if (this.responsiveOverride === null) this.setPreferredOpen(true);
    else this.setResponsiveOverride(true);
    // Deliberate History navigation moves focus; responsive restoration does not.
    this.focusSearch();
    if (this.host.getState().loaded) this.host.requestRefresh(sourceWorkspaceId);
  }

  /** User or Shell close request; responsive closes stay temporary. */
  requestClose(): void {
    if (this.responsiveOverride === null) this.setPreferredOpen(false);
    else this.setResponsiveOverride(false);
  }

  private effectiveOpen(): boolean {
    return this.responsiveOverride ?? this.preferredOpen;
  }

  private setPreferredOpen(open: boolean): void {
    if (this.preferredOpen === open) return;
    const previous = this.effectiveOpen();
    this.preferredOpen = open;
    try {
      window.localStorage?.setItem(OPEN_STORAGE_KEY, open ? '1' : '0');
    } catch {
      // Persistence is optional; the dock still works for the session.
    }
    this.syncEffectiveOpen(previous);
  }

  private setResponsiveOverride(open: boolean | null): void {
    if (this.responsiveOverride === open) return;
    const previous = this.effectiveOpen();
    this.responsiveOverride = open;
    this.syncEffectiveOpen(previous);
  }

  private syncEffectiveOpen(previous: boolean): void {
    this.syncVisibility();
    const current = this.effectiveOpen();
    if (current === previous) return;
    for (const listener of this.openListeners) listener(current);
  }

  private readOpen(): boolean {
    try {
      const stored = window.localStorage?.getItem(OPEN_STORAGE_KEY);
      if (stored === '1') return true;
      if (stored === '0') return false;
    } catch {
      // Fall through to the first-run default.
    }
    // First-run preference is desktop-like. Shell applies a temporary
    // responsive override whenever the current layout cannot host the dock.
    return true;
  }

  private syncVisibility(): void {
    if (!this.dock) return;
    const open = this.effectiveOpen();
    const visible = open && this.agentViewActive;
    this.dock.hidden = !visible;
    this.dock.parentElement?.classList.toggle('agent-history-dock-open', visible);
    // Every workspace toolbar carries a history-toggle; keep them all in sync.
    this.dock.parentElement
      ?.querySelectorAll('[data-role="history-toggle"]')
      .forEach((toggle) => toggle.setAttribute('aria-expanded', String(open)));
    if (visible) this.maybeAutoLoad();
  }

  private maybeAutoLoad(): void {
    if (!this.dock || this.dock.hidden) return;
    if (!this.host.hasAgentWorkspaces()) return;
    const state = this.host.getState();
    if (state.loaded || state.status !== 'idle' || state.inFlightWorkspaceId) return;
    this.host.requestRefresh('');
  }

  private readWidth(): number {
    try {
      const width = Number(window.localStorage?.getItem(WIDTH_STORAGE_KEY));
      if (Number.isFinite(width) && width > 0) return this.clampWidth(width);
    } catch {
      // localStorage can be unavailable in constrained WebView profiles.
    }
    return DEFAULT_WIDTH;
  }

  private applyWidth(width: number): void {
    // The canvas offset rules in shell.css inherit --agent-history-width too
    // (dock and .agent-panel are siblings), so the variable must live on the
    // shared container, not on the dock element itself.
    this.container?.style.setProperty('--agent-history-width', this.clampWidth(width) + 'px');
  }

  private saveWidth(): void {
    try {
      window.localStorage?.setItem(WIDTH_STORAGE_KEY, String(this.width));
    } catch {
      // Width persistence is optional; resizing remains available.
    }
    for (const listener of this.widthListeners) listener(this.width);
  }

  private clampWidth(width: number): number {
    const numeric = Number(width);
    if (!Number.isFinite(numeric)) return DEFAULT_WIDTH;
    return Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, Math.round(numeric)));
  }

  // --- Wiring -------------------------------------------------------------------

  private wire(
    search: HTMLInputElement,
    providerSelect: HTMLSelectElement,
    refresh: HTMLButtonElement,
    resizer: HTMLElement
  ): void {
    this.on(search, 'input', () => this.render({ resetScroll: true }));
    this.on(providerSelect, 'change', () => {
      this.providerFilterValue = providerSelect.value;
      this.render({ resetScroll: true });
    });
    this.on(refresh, 'click', () => this.host.requestRefresh(''));

    this.on(resizer, 'pointerdown', (event) => {
      if (event.button !== 0) return;
      this.isResizing = true;
      resizer.setPointerCapture(event.pointerId);
      document.body.classList.add('agent-history-dock-resizing');
      event.preventDefault();
    });
    this.on(resizer, 'pointermove', (event) => {
      if (!this.isResizing || !this.dock) return;
      const rect = this.dock.getBoundingClientRect();
      this.width = this.clampWidth(event.clientX - rect.left);
      this.applyWidth(this.width);
    });
    const endResize = (event: PointerEvent): void => {
      if (!this.isResizing) return;
      this.isResizing = false;
      document.body.classList.remove('agent-history-dock-resizing');
      try {
        resizer.releasePointerCapture(event.pointerId);
      } catch {
        // Pointer capture may already be gone if the window lost focus.
      }
      this.saveWidth();
    };
    this.on(resizer, 'pointerup', endResize);
    this.on(resizer, 'pointercancel', endResize);
    this.on(resizer, 'keydown', (event) => {
      const step = event.shiftKey ? 40 : 16;
      if (event.key === 'ArrowLeft') this.setWidthFromKeyboard(this.width - step);
      else if (event.key === 'ArrowRight') this.setWidthFromKeyboard(this.width + step);
      else if (event.key === 'Home') this.setWidthFromKeyboard(MAX_WIDTH);
      else if (event.key === 'End') this.setWidthFromKeyboard(MIN_WIDTH);
      else return;
      event.preventDefault();
    });
  }

  private setWidthFromKeyboard(width: number): void {
    this.width = this.clampWidth(width);
    this.applyWidth(this.width);
    this.saveWidth();
  }

  // --- Rendering ------------------------------------------------------------------

  private render(options?: { resetScroll?: boolean }): void {
    if (!this.content) return;
    // Rebuilds wipe the list; keep the user's place across store-driven
    // renders (open-marker refreshes, invalidation waves) and only reset when
    // the search box or provider filter changed the result set.
    const scrollTop = options?.resetScroll ? 0 : this.content.scrollTop;
    this.renderContent();
    this.content.scrollTop = scrollTop;
  }

  private renderContent(): void {
    if (!this.content) return;
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
    const groups = filterHistoryGroups(
      buildHistoryGroups(state.threads, state.providers),
      query,
      this.providerFilterValue
    );

    if (state.threadOpenError) {
      this.renderThreadOpenError(
        state.threadOpenError.threadId,
        state.threadOpenError.text
      );
    }

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
    const openedElsewhere = new Set<string>();
    for (const [workspaceId, threadId] of openThreads) {
      if (threadId && workspaceId !== activeWorkspaceId) openedElsewhere.add(threadId);
    }

    const list = document.createElement('div');
    list.className = 'agent-history-list';
    const isSearching = !!query || !!this.providerFilterValue;
    for (const group of groups) {
      list.appendChild(this.renderGroup(
        group,
        activeThreadId,
        openedElsewhere,
        isSearching
      ));
    }
    this.content.appendChild(list);
  }

  private renderGroup(
    group: { key: string; name: string; path: string; threads: Array<{ threadId: string; title: string; updatedAt: string; providerDisplay: string }> },
    activeThreadId: string,
    openedElsewhere: ReadonlySet<string>,
    isSearching: boolean
  ): HTMLElement {
    const PREVIEW_LIMIT = 5;
    const allThreads = group.threads;
    const showAll = isSearching || this.expandedGroups.has(group.key);
    const visibleThreads = showAll ? allThreads : allThreads.slice(0, PREVIEW_LIMIT);
    const hiddenCount = allThreads.length - visibleThreads.length;

    // Searching forces every group open so results stay visible.
    const folded = !isSearching && this.foldedGroups.has(group.key);
    const hasActive = allThreads.some((thread) => thread.threadId === activeThreadId);

    const groupEl = document.createElement('div');
    groupEl.className = 'agent-history-group';
    groupEl.dataset.folded = String(folded);

    // Header (click toggles fold)
    const header = document.createElement('button');
    header.type = 'button';
    header.className = 'agent-history-group-header';
    header.setAttribute('aria-expanded', String(!folded));

    const folder = document.createElement('span');
    folder.className = 'agent-history-group-folder';
    folder.setAttribute('aria-hidden', 'true');
    folder.innerHTML = FOLDER_CLOSED_ICON + FOLDER_OPEN_ICON;
    header.appendChild(folder);

    const label = document.createElement('span');
    label.className = 'agent-history-group-label';
    const name = document.createElement('strong');
    name.textContent = group.name;
    label.appendChild(name);
    if (group.path) {
      const path = document.createElement('small');
      path.textContent = group.path;
      path.title = group.path;
      label.appendChild(path);
    }
    header.appendChild(label);

    const count = document.createElement('span');
    count.className = 'agent-history-group-count';
    count.textContent = String(allThreads.length);
    header.appendChild(count);

    if (hasActive) {
      const dot = document.createElement('span');
      dot.className = 'agent-history-group-active';
      dot.setAttribute('aria-label', 'Contains the active session');
      header.appendChild(dot);
    }

    header.addEventListener('click', () => {
      if (this.foldedGroups.has(group.key)) this.foldedGroups.delete(group.key);
      else this.foldedGroups.add(group.key);
      this.render();
    });
    groupEl.appendChild(header);

    // Threads container (hidden when folded)
    const threadsEl = document.createElement('div');
    threadsEl.className = 'agent-history-group-threads';
    threadsEl.hidden = folded;
    for (const thread of visibleThreads) {
      threadsEl.appendChild(this.renderThread(thread, activeThreadId, openedElsewhere));
    }
    if (hiddenCount > 0) {
      const more = document.createElement('button');
      more.type = 'button';
      more.className = 'agent-history-show-more';
      more.textContent = 'Show all ' + allThreads.length + ' threads';
      more.addEventListener('click', () => {
        this.expandedGroups.add(group.key);
        this.render();
      });
      threadsEl.appendChild(more);
    }
    groupEl.appendChild(threadsEl);

    return groupEl;
  }

  private renderThread(
    thread: { threadId: string; title: string; updatedAt: string; providerDisplay: string },
    activeThreadId: string,
    openedElsewhere: ReadonlySet<string>
  ): HTMLElement {
    const row = document.createElement('button');
    row.type = 'button';
    row.className = 'agent-history-item';
    row.dataset.threadId = thread.threadId || '';
    row.setAttribute('aria-current', thread.threadId === activeThreadId ? 'true' : 'false');

    const heading = document.createElement('span');
    heading.className = 'agent-history-heading';

    const titleText = thread.title || 'Agent Chat';
    const title = document.createElement('strong');
    title.textContent = titleText;
    heading.appendChild(title);

    let badgeText = '';
    if (thread.threadId === activeThreadId) {
      badgeText = 'Current';
      const current = document.createElement('span');
      current.className = 'agent-history-current';
      current.textContent = badgeText;
      heading.appendChild(current);
    } else if (thread.threadId && openedElsewhere.has(thread.threadId)) {
      badgeText = 'Open';
      const opened = document.createElement('span');
      opened.className = 'agent-history-open';
      opened.textContent = badgeText;
      heading.appendChild(opened);
    }

    const meta = document.createElement('small');
    const parts: string[] = [];
    if (thread.providerDisplay) parts.push(thread.providerDisplay);
    const timeLabel = formatHistoryTime(thread.updatedAt);
    if (timeLabel) parts.push(timeLabel);
    // Single-line row: only the short time stays visible at the row's trailing
    // edge. The full "provider | time" string is the row's tooltip and part of
    // its accessible name — a tooltip on the trailing <small> is unreachable
    // for keyboard and screen-reader users because focus lands on the row.
    meta.textContent = timeLabel || thread.providerDisplay || '';
    const fullDescription = parts.join(' | ');
    if (fullDescription) row.title = fullDescription;
    row.setAttribute(
      'aria-label',
      [titleText, badgeText, fullDescription].filter((part) => part).join(', '));
    heading.appendChild(meta);

    row.appendChild(heading);
    row.addEventListener('click', () => {
      if (thread.threadId) this.host.openThread(thread.threadId);
    });
    return row;
  }

  private renderThreadOpenError(threadId: string, text: string): void {
    if (!this.content) return;
    const notice = document.createElement('div');
    notice.className = 'agent-history-open-error';
    notice.setAttribute('role', 'alert');

    const message = document.createElement('p');
    message.textContent = text || 'PSX could not open the selected Agent thread. Try again.';

    const actions = document.createElement('div');
    actions.className = 'agent-history-open-error-actions';

    const retry = document.createElement('button');
    retry.type = 'button';
    retry.className = 'agent-history-retry';
    retry.textContent = 'Retry';
    retry.addEventListener('click', () => this.host.openThread(threadId));

    const dismiss = document.createElement('button');
    dismiss.type = 'button';
    dismiss.className = 'agent-history-dismiss';
    dismiss.textContent = 'Dismiss';
    dismiss.addEventListener('click', () => this.host.dismissThreadOpenError());

    actions.append(retry, dismiss);
    notice.append(message, actions);
    this.content.appendChild(notice);
  }

  private renderError(text: string): void {
    if (!this.content) return;
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

  private showState(stateName: string, text: string): void {
    if (!this.content) return;
    const state = document.createElement('div');
    state.className = 'agent-history-state agent-history-' + stateName;
    if (stateName === 'loading') {
      state.setAttribute('role', 'status');
      state.setAttribute('aria-live', 'polite');
    }
    state.textContent = text;
    this.content.appendChild(state);
  }

  private syncProviderOptions(state: AgentHistoryState): void {
    if (!this.providerSelect) return;
    const options = providerFilterOptions(state.threads, state.providers);
    const signature = options.map((option) => option.value + '="' + option.label).join('');
    if (signature === this.providerOptionsSignature) return;
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

  private on<K extends keyof HTMLElementEventMap>(
    node: HTMLElement | null,
    type: K,
    handler: (event: HTMLElementEventMap[K]) => void
  ): void {
    if (!node) return;
    node.addEventListener(type, handler as EventListener);
    this.cleanup.push(() => node.removeEventListener(type, handler as EventListener));
  }
}
