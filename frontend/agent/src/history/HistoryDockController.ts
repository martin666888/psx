// HistoryDockController.ts — the single global History dock.
//
// One dock for the whole process, living outside every workspace panel (a
// left column inside #agent-workspace-container). It renders the global
// AgentHistoryStore: grouped/searchable/filterable thread list, loading /
// error / unavailable states, open-thread markers and a search/filter/refresh
// top bar. Open/close is driven by the permanent activity-rail button; all
// `history` loads go through AgentHistoryRequestBroker and `load_thread`
// clicks use the broker's workspace-or-global channel picker without entering
// the request state machine. Open state and width persist in localStorage.
//
// The dock chrome (aside frame, top bar, scroll node, resizer) renders
// through the history-dock React island; this controller owns the business
// logic only — the open-state machine, width clamp/persistence/broadcast,
// toolbar-toggle aria and the HistoryList interaction state — and pushes it
// as HistoryDockViewProps. Pointer/keyboard resizing lives in the view; the
// controller only clamps, persists and broadcasts the committed width.
//
// Layered layout keeps History as a left dock at every width. Responsive
// layouts can temporarily collapse it before it would squeeze the reading
// column, but never turn it into a floating drawer.

import type {
  AgentHistoryListener,
  AgentHistoryState
} from '../contracts/agent-history.js';
import {
  HISTORY_DOCK_DEFAULT_WIDTH,
  HISTORY_DOCK_MAX_WIDTH,
  HISTORY_DOCK_MIN_WIDTH,
  providerFilterOptions
} from './historyModel.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import type { HistoryDockViewProps } from './HistoryDockView.js';

/** Everything the dock needs from the registry (store + broker + open-thread
 * markers). Provided as one seam so the controller stays testable. */
export interface HistoryDockHost {
  getState(): AgentHistoryState;
  subscribe(listener: AgentHistoryListener): () => void;
  requestRefresh(originWorkspaceId: string): void;
  openThread(threadId: string): boolean;
  dismissThreadOpenError(): void;
  /** The active Agent workspace, or '' when a terminal/nothing is active. */
  activeWorkspaceId(): string;
  /** workspaceId → currentThreadId for every live workspace bound to a thread. */
  openWorkspaceThreadIds(): ReadonlyMap<string, string>;
}

const OPEN_STORAGE_KEY = 'psx.agent.historyDockOpen';
const WIDTH_STORAGE_KEY = 'psx.agent.historyDockWidth';

export class HistoryDockController {
  private readonly host: HistoryDockHost;

  /** Controller-owned portal host for the dock island. display:contents
   * keeps the React-rendered <aside> a layout child of the container, so the
   * absolute frame and stacking in history.css/shell.css apply unchanged. */
  private dockHost: HTMLElement | null = null;
  private container: HTMLElement | null = null;

  // preferredOpen is the persisted user choice. responsiveOverride is a
  // temporary responsive value: null means normal layout, boolean means the
  // responsive layout is currently in control.
  private preferredOpen = true;
  private responsiveOverride: boolean | null = null;
  private width = HISTORY_DOCK_DEFAULT_WIDTH;
  private query = '';
  private providerFilterValue = '';
  /** Bumped when a search/filter change must zero the list viewport; the
   * island's scroll node watches it. In-place React updates preserve the
   * scroll position on every other render without help. */
  private resetScrollToken = 0;
  private unsubscribeStore: (() => void) | null = null;
  /** Folded groups by cwd key (user clicked the header to collapse). Runtime
   * state, never persisted; shared across workspaces (a project is global). */
  private readonly foldedGroups = new Set<string>();
  /** Groups the user expanded past the 5-item preview limit. */
  private readonly expandedGroups = new Set<string>();
  private readonly openListeners = new Set<(open: boolean) => void>();
  private readonly widthListeners = new Set<(width: number) => void>();
  private readonly previewListeners = new Set<(width: number) => void>();

  // React owns the entire dock subtree (chrome + list) inside dockHost.
  private historyIsland: IslandLoader<HistoryDockViewProps> | null = null;

  constructor(host: HistoryDockHost) {
    this.host = host;
  }

  mount(parent: HTMLElement): void {
    if (this.dockHost || !parent) return;

    const dockHost = document.createElement('div');
    dockHost.dataset.role = 'history-dock-host';
    dockHost.style.display = 'contents';
    parent.appendChild(dockHost);

    this.dockHost = dockHost;
    this.container = parent;

    this.width = this.readWidth();
    this.applyWidth(this.width);
    this.preferredOpen = this.readOpen();
    this.unsubscribeStore = this.host.subscribe(() => this.render());
    // The open/close control is the history-toggle rendered by every
    // workspace's toolbar island; clicks arrive through toggleFromToolbar.
    this.syncVisibility();
  }

  /** Composer `/history` + toolbar-toggle seam: open the dock and load through the
   * broker. The first open loads via syncVisibility → maybeAutoLoad; an
   * explicit refresh is only sent once data exists, so opening never queues
   * a redundant second wave. */
  openHistory(sourceWorkspaceId: string): void {
    this.requestOpen(sourceWorkspaceId);
  }

  /** Toolbar history-toggle seam (routed through the workspace toolbar
   * island's onToggle): one click toggles the dock. */
  toggleFromToolbar(): void {
    if (this.effectiveOpen()) this.requestClose();
    else this.openHistory('');
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

  /** Live preview frames from a resizer drag. A preview is never a commit —
   * the final width still arrives through onWidthChanged. */
  onWidthPreview(listener: (width: number) => void): void {
    this.previewListeners.add(listener);
  }

  /** Focus entry point for user-initiated History open requests. */
  focusSearch(): void {
    this.dockHost?.querySelector<HTMLElement>('[data-role="history-search"]')?.focus();
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
    // A dispose mid-drag must not leave the body stuck in col-resize mode.
    document.body.classList.remove('agent-history-dock-resizing');
    if (this.unsubscribeStore) {
      this.unsubscribeStore();
      this.unsubscribeStore = null;
    }
    this.previewListeners.clear();
    this.historyIsland?.dispose();
    this.historyIsland = null;
    this.dockHost?.remove();
    this.container?.style.removeProperty('--agent-history-width');
    this.dockHost = null;
    this.container = null;
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

  private isVisible(): boolean {
    return this.effectiveOpen();
  }

  private syncVisibility(): void {
    if (!this.dockHost) return;
    const visible = this.isVisible();
    // The dock frame renders through the island, but visibility applies
    // synchronously: requestOpen focuses the search box right after unhiding
    // and Shell reads `hidden` in the same task. The island render below
    // carries the same `open` value, so React's deferred commit of the
    // hidden attribute is a no-op.
    const dock = this.dockHost.querySelector<HTMLElement>('[data-role="history-dock"]');
    if (dock) dock.hidden = !visible;
    this.container?.classList.toggle('agent-history-dock-open', visible);
    if (visible) this.maybeAutoLoad();
    this.render();
  }

  private maybeAutoLoad(): void {
    if (!this.dockHost || !this.isVisible()) return;
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
    return HISTORY_DOCK_DEFAULT_WIDTH;
  }

  private applyWidth(width: number): void {
    // The canvas offset rules in shell.css inherit --agent-history-width too
    // (dock and .agent-panel are siblings), so the variable must live on the
    // shared container, not on the dock element itself.
    this.container?.style.setProperty('--agent-history-width', this.clampWidth(width) + 'px');
  }

  /** Resizer drag preview from the island: live width, no persistence and no
   * committed-width broadcast until the pointer is released. Preview
   * listeners track the drag live; the committed width still arrives through
   * onWidthChanged. */
  private previewWidth(width: number): void {
    const clamped = this.clampWidth(width);
    this.applyWidth(clamped);
    for (const listener of this.previewListeners) listener(clamped);
  }

  /** Completed pointer drag or keyboard resize: clamp, persist, broadcast
   * and push the committed width back to the island (resizer aria). */
  private commitWidth(width: number): void {
    this.width = this.clampWidth(width);
    this.applyWidth(this.width);
    try {
      window.localStorage?.setItem(WIDTH_STORAGE_KEY, String(this.width));
    } catch {
      // Width persistence is optional; resizing remains available.
    }
    for (const listener of this.widthListeners) listener(this.width);
    this.render();
  }

  private clampWidth(width: number): number {
    const numeric = Number(width);
    if (!Number.isFinite(numeric)) return HISTORY_DOCK_DEFAULT_WIDTH;
    return Math.max(HISTORY_DOCK_MIN_WIDTH, Math.min(HISTORY_DOCK_MAX_WIDTH, Math.round(numeric)));
  }

  // --- Rendering ------------------------------------------------------------------

  private render(options?: { resetScroll?: boolean }): void {
    if (!this.dockHost) return;
    if (options?.resetScroll) this.resetScrollToken += 1;
    this.renderReact();
  }

  private renderReact(): void {
    const dockHost = this.dockHost;
    if (!dockHost) return;
    const state = this.host.getState();

    this.historyIsland ??= createIslandLoader<HistoryDockViewProps>({
      name: 'history-dock',
      load: async () => {
        const mod = await import('./historyIsland.js');
        return (host, reportFailure) => mod.mountHistoryIsland(host, reportFailure);
      },
      host: dockHost
    });

    const activeWorkspaceId = this.host.activeWorkspaceId();
    const openThreads = this.host.openWorkspaceThreadIds();
    const activeThreadId = activeWorkspaceId ? openThreads.get(activeWorkspaceId) ?? '' : '';
    const openedElsewhere = new Set<string>();
    for (const [workspaceId, threadId] of openThreads) {
      if (threadId && workspaceId !== activeWorkspaceId) openedElsewhere.add(threadId);
    }

    this.historyIsland.render({
      open: this.isVisible(),
      query: this.query,
      providerFilter: this.providerFilterValue,
      providers: this.providerFilterEntries(state),
      width: this.width,
      onQueryChange: (query) => {
        this.query = query;
        this.render({ resetScroll: true });
      },
      onProviderChange: (key) => {
        this.providerFilterValue = key;
        this.render({ resetScroll: true });
      },
      onRefresh: () => this.host.requestRefresh(''),
      onWidthPreview: (width) => this.previewWidth(width),
      onWidthCommit: (width) => this.commitWidth(width),
      resetScrollToken: this.resetScrollToken,
      list: {
        state,
        query: this.query,
        providerFilter: this.providerFilterValue,
        activeThreadId,
        openedElsewhere,
        foldedGroups: this.foldedGroups,
        expandedGroups: this.expandedGroups,
        onToggleFold: (key) => {
          if (this.foldedGroups.has(key)) this.foldedGroups.delete(key);
          else this.foldedGroups.add(key);
          this.render();
        },
        onExpandGroup: (key) => {
          this.expandedGroups.add(key);
          this.render();
        },
        onOpenThread: (threadId) => {
          this.host.openThread(threadId);
        },
        onDismissOpenError: () => this.host.dismissThreadOpenError(),
        onRetryRefresh: () => this.host.requestRefresh('')
      }
    });
  }

  /** Provider filter entries for the island's Select; drops a selection
   * whose option disappeared from the data. */
  private providerFilterEntries(state: AgentHistoryState): Array<{ key: string; label: string }> {
    const options = providerFilterOptions(state.threads, state.providers);
    if (this.providerFilterValue && !options.some((option) => option.value === this.providerFilterValue)) {
      this.providerFilterValue = '';
    }
    return options.map((option) => ({ key: option.value, label: option.label }));
  }
}
