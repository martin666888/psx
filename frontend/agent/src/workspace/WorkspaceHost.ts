// WorkspaceHost.ts — native multi-workspace host (replaces the legacy
// WorkspaceViewManager). Owns only the Agent panel shell for each workspace:
// template instantiation, the workspace-scoped Bridge, the unconditional Clear
// button, the global appearance CSS variables, panel visibility + the terminal
// view toggle, and the initial `state` request. Every data-role region inside a
// panel is owned by a feature controller; this host never writes them.
//
// Faithful port of the surviving (non-domain) behaviour of the legacy
// WorkspaceViewManager.createAgent / activate / closeAgent and
// AgentThreadManager.setAgentSettings + setVisible.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { SessionRuntimeHost } from './SessionRuntimeController.js';
import type { PlanHost } from '../plan/PlanController.js';
import { applyShadcnTheme } from '../ui/themeAdapter.js';
import { setPortalContainer } from '../ui/portalContainer.js';

/** The one terminal-view method the host toggles when switching workspaces. */
interface TerminalViewToggle {
  setViewVisible(visible: boolean): void;
}

/** Appearance settings shape (subset of the app 'settings' host event). */
interface AgentAppearanceSettings {
  agentFontSize?: number;
  agentFontFamily?: string;
  agentMonoFontFamily?: string;
  themeColors?: Record<string, string | undefined>;
  agentThemeColors?: Record<string, string | undefined>;
}

interface WorkspaceEntry {
  panel: HTMLElement;
  bridge: AgentBridgePort;
  focusTimer: ReturnType<typeof setTimeout> | null;
  /** keep-alive (CP3a): view state saved on hide, restored on show. The panel
   * DOM itself survives tab switches (hidden, never unmounted), but a
   * display:none subtree loses scroll offsets and focus, so those are saved
   * explicitly. The composer draft lives in the kept-alive textarea. */
  savedScrollTop: number;
  savedAtBottom: boolean;
  savedFocusRole: string | null;
  savedSelection: { start: number; end: number } | null;
}

export class WorkspaceHost implements SessionRuntimeHost, PlanHost {
  private readonly terminalManager: TerminalViewToggle;
  private readonly container: HTMLElement;
  private readonly template: HTMLTemplateElement;
  private readonly workspaces = new Map<string, WorkspaceEntry>();
  private activeWorkspaceId = '';
  private settings: AgentAppearanceSettings | null = null;
  // Once the first workspace_layout snapshot arrives, pane assignment owns
  // panel visibility/rects; activate() keeps only its side effects.
  private layoutDriven = false;

  constructor(
    terminalManager: TerminalViewToggle,
    container: HTMLElement,
    template: HTMLTemplateElement
  ) {
    this.terminalManager = terminalManager;
    this.container = container;
    this.template = template;
    // The shadcn variable boundary must exist from the first mounted frame;
    // applyShadcnTheme re-applies it (idempotent) with themed values once the
    // first settings event arrives.
    this.container.classList.add('agent-ui');
    // Radix portals must mount inside the boundary to see the themed
    // variables (a body-mounted tooltip/popup renders unstyled).
    setPortalContainer(document.getElementById('global-portal-root') ?? this.container);
  }

  /**
   * Project the column layout onto agent panels: each workspace that is the
   * active tab of a column shows at that column's rect (with its
   * reading-column geometry scoped to the column width); every other panel —
   * including inactive tabs of visible columns — hides keep-alive. Called by
   * the PaneLayoutController with the latest snapshot and measured rects.
   */
  applyLayout(snapshot: {
    focusedColumnId: string;
    columns: Array<{
      columnId: string;
      tabs?: Array<{ workspaceId?: string | null; kind?: string | null }>;
      activeTabId?: string | null;
      ratio?: number;
    }>;
  }, rects: Map<string, { left: number; top: number; width: number; height: number; dockAdjacent?: boolean }>): void {
    if (!snapshot || !Array.isArray(snapshot.columns)) return;
    this.layoutDriven = true;
    this.container.classList.add('agent-layout-driven');

    const assignments = new Map<string, { columnId: string; rect: { left: number; top: number; width: number; height: number; dockAdjacent?: boolean } }>();
    for (const column of snapshot.columns) {
      if (!column.activeTabId) continue;
      const tab = column.tabs?.find((item) => item.workspaceId === column.activeTabId);
      if (!tab || tab.kind !== 'agent') continue;
      const rect = rects?.get(column.columnId);
      if (rect) assignments.set(String(tab.workspaceId), { columnId: column.columnId, rect });
    }

    for (const [id, entry] of this.workspaces) {
      const assignment = assignments.get(id);
      if (!assignment) {
        if (!entry.panel.hidden) this.setPanelVisible(entry.panel, false);
        continue;
      }
      this.applyPanelRect(entry.panel, assignment.rect);
      entry.panel.dataset.columnId = assignment.columnId;
      if (entry.panel.hidden) this.setPanelVisible(entry.panel, true);
      entry.panel.dataset.paneFocused = assignment.columnId === snapshot.focusedColumnId ? 'true' : 'false';
    }

    // Diagnostic (permanent, [pane-layout]): every projection reports the
    // visible agent panel count and the projected column ids.
    const visiblePanels = [...this.workspaces.values()].filter((entry) => !entry.panel.hidden).length;
    console.debug(
      '[pane-layout] applyLayout: visible panels =', visiblePanels,
      '| columns =', snapshot.columns.map((column) => column.columnId).join(',')
    );

    const anyAgentVisible = assignments.size > 0;
    this.container.classList.toggle('agent-split-active', snapshot.columns.length > 1);
    // The Agent layer spans the whole window above the terminal layer:
    // pointer-events: auto on the blanket container would swallow every click
    // aimed at a visible terminal column (xterm's wrapper focus handler never
    // fires, so the terminal can neither focus nor receive input). Agent
    // panels and the History dock re-enable hit-testing on themselves
    // (pointer-events: auto), so the container only takes events — and paints
    // the workbench backdrop — when no terminal column is visible.
    const anyTerminalVisible = snapshot.columns.some((column) => {
      if (!column.activeTabId) return false;
      const tab = column.tabs?.find((item) => item.workspaceId === column.activeTabId);
      return tab?.kind === 'terminal';
    });
    // A DSH column is a shell-owned surface: while one is visible the Agent
    // blanket must not take the backdrop or swallow its pointer events.
    const anyDshWebVisible = snapshot.columns.some((column) => {
      if (!column.activeTabId) return false;
      const tab = column.tabs?.find((item) => item.workspaceId === column.activeTabId);
      return tab?.kind === 'dsh_web';
    });
    this.container.classList.toggle('agent-workspace-active', anyAgentVisible && !anyTerminalVisible && !anyDshWebVisible);
  }

  /** Width of the focused pane's agent panel, for pane-relative responsive
   * rules; falls back to any visible agent panel. */
  focusedAgentPanelWidth(): number {
    const focused = [...this.workspaces.values()].find(
      entry => !entry.panel.hidden && entry.panel.dataset.paneFocused === 'true'
    );
    const fallback = focused ?? [...this.workspaces.values()].find(entry => !entry.panel.hidden);
    return fallback?.panel.clientWidth ?? 0;
  }

  /** Whether any agent panel is currently visible (split-aware). */
  anyAgentWorkspaceVisible(): boolean {
    for (const entry of this.workspaces.values()) {
      if (!entry.panel.hidden) return true;
    }
    return false;
  }

  /** True once workspace_layout snapshots own panel visibility/rects. */
  isLayoutDriven(): boolean {
    return this.layoutDriven;
  }

  /** Production wiring locks layout-driven mode before the first activation:
   * entry.ts calls this while creating the app (a paneLayout always exists
   * there), so workspace_activated can never reach the legacy fullscreen
   * fallback. Mirrors the container state applyLayout() would produce. */
  markLayoutDriven(): void {
    this.layoutDriven = true;
    this.container.classList.add('agent-layout-driven');
  }

  private applyPanelRect(
    panel: HTMLElement,
    rect: { left: number; top: number; width: number; height: number; dockAdjacent?: boolean }
  ): void {
    // The panel floats on the workbench backdrop with a full gutter on every
    // side (mirrors --agent-workbench-gutter in shell.css). When the dock is
    // open, the dock inset already carries the left gutter plus the panel
    // gap, so the dock-adjacent first column must not add the gutter a second
    // time — the dock edge and the panel edge stay exactly one panel gap
    // apart (12px), the same spacing a terminal column gets.
    const gutter = 12;
    const gutterLeft = rect.dockAdjacent ? 0 : gutter;
    const left = rect.left + gutterLeft;
    const top = rect.top + gutter;
    const width = Math.max(0, rect.width - gutterLeft - gutter);
    const height = Math.max(0, rect.height - 2 * gutter);
    const key = `${left}:${top}:${width}:${height}`;
    if (panel.dataset.paneRect === key) return;
    panel.dataset.paneRect = key;
    panel.style.left = `${left}px`;
    panel.style.top = `${top}px`;
    panel.style.width = `${width}px`;
    panel.style.height = `${height}px`;
    panel.style.right = 'auto';
    panel.style.bottom = 'auto';
    // Reading-column geometry is pane-relative via the panel's own width.
    panel.style.setProperty('--agent-pane-inline-size', `${width}px`);
  }

  createWorkspace(workspaceId: string): void {
    const id = String(workspaceId || '');
    if (!id || this.workspaces.has(id)) return;

    const fragment = this.template.content.cloneNode(true) as DocumentFragment;
    const panel = fragment.querySelector<HTMLElement>('.agent-panel');
    if (!panel) return;
    panel.dataset.workspaceId = id;
    this.container.appendChild(fragment);

    const bridge = Bridge.createAgentScope(id);
    // Click-to-focus: any interaction with a visible panel focuses its column
    // (split panes). The layout engine stamps the current columnId on the panel.
    panel.addEventListener('mousedown', () => {
      const columnId = panel.dataset.columnId;
      if (columnId) Bridge.sendPaneFocus(columnId);
    });

    if (this.settings) this.applyGlobalAppearance(this.settings);
    this.workspaces.set(id, {
      panel,
      bridge,
      focusTimer: null,
      savedScrollTop: 0,
      savedAtBottom: true,
      savedFocusRole: null,
      savedSelection: null
    });
    this.setPanelVisible(panel, false);
    // Ask the host for the initial workspace state (legacy createAgent tail).
    bridge.sendAgentCommand('state');
  }

  closeWorkspace(workspaceId: string): void {
    const id = String(workspaceId || '');
    const entry = this.workspaces.get(id);
    if (!entry) return;
    this.clearFocusTimer(entry);
    entry.panel.remove();
    this.workspaces.delete(id);
    if (this.activeWorkspaceId === id) this.activeWorkspaceId = '';
  }

  activate(workspaceId: string, kind: 'terminal' | 'agent'): void {
    const id = String(workspaceId || '');
    this.activeWorkspaceId = id;

    if (this.layoutDriven) {
      // applyLayout owns visibility, rects, pointer events and the dock's
      // active state once pane snapshots flow; activation only carries side
      // effects (focus, brokers) there.
      return;
    }

    // Timing lock: production marks layoutDriven inside createAgentApp before
    // any workspace_activated drains (entry.ts, paneLayout always present), so
    // this legacy fallback is dead there — it serves only test harnesses that
    // mount without a paneLayout.

    // The Agent root is an absolute layer above the terminal. It must only
    // receive pointer events while an Agent workspace is actually visible;
    // otherwise its transparent surface prevents blank-area terminal clicks
    // from reaching xterm's wrapper focus handler.
    this.container.classList.toggle('agent-workspace-active', kind === 'agent');
    if (kind === 'terminal') {
      for (const entry of this.workspaces.values()) this.setPanelVisible(entry.panel, false);
      this.terminalManager.setViewVisible(true);
      return;
    }

    this.terminalManager.setViewVisible(false);
    for (const [entryId, entry] of this.workspaces) {
      this.setPanelVisible(entry.panel, entryId === id);
    }
  }

  applySettings(settings: unknown): void {
    if (settings && typeof settings === 'object') {
      this.settings = settings as AgentAppearanceSettings;
      this.applyGlobalAppearance(this.settings);
    }
  }

  getPanel(workspaceId: string): HTMLElement | null {
    return this.workspaces.get(String(workspaceId || ''))?.panel ?? null;
  }

  bridgeFor(workspaceId: string): AgentBridgePort | null {
    return this.workspaces.get(String(workspaceId || ''))?.bridge ?? null;
  }

  activeWorkspace(): string {
    return this.activeWorkspaceId;
  }

  // --- internals -----------------------------------------------------------

  private setPanelVisible(panel: HTMLElement, visible: boolean): void {
    const entry = this.entryForPanel(panel);
    if (!visible && entry && !panel.hidden) this.saveViewState(entry);
    panel.hidden = !visible;
    if (entry) this.clearFocusTimer(entry);
    if (visible && entry) {
      this.restoreViewState(entry);
    }
  }

  /** keep-alive: capture the thread scroll anchor and the focused control
   * before the panel goes display:none (both are lost while hidden). The
   * single scroll node is .agent-thread-scroll inside the Conversation. */
  private saveViewState(entry: WorkspaceEntry): void {
    const thread = entry.panel.querySelector<HTMLElement>('.agent-thread-scroll');
    if (thread) {
      entry.savedScrollTop = thread.scrollTop;
      // Within one pixel of the end counts as pinned (fractional scroll).
      entry.savedAtBottom =
        thread.scrollTop + thread.clientHeight >= thread.scrollHeight - 1;
    }

    const active = entry.panel.ownerDocument.activeElement;
    entry.savedFocusRole = null;
    entry.savedSelection = null;
    if (active instanceof HTMLElement && entry.panel.contains(active)) {
      entry.savedFocusRole = active.dataset.role ?? null;
      if (active instanceof HTMLTextAreaElement || active instanceof HTMLInputElement) {
        const start = active.selectionStart;
        const end = active.selectionEnd;
        if (typeof start === 'number' && typeof end === 'number') {
          entry.savedSelection = { start, end };
        }
      }
    }
  }

  private restoreViewState(entry: WorkspaceEntry): void {
    const panel = entry.panel;
    const thread = panel.querySelector<HTMLElement>('.agent-thread-scroll');
    if (thread) {
      // A workspace pinned to the newest message stays pinned even when new
      // content streamed in while it was hidden; otherwise the exact reading
      // position is restored.
      thread.scrollTop = entry.savedAtBottom
        ? thread.scrollHeight
        : entry.savedScrollTop;
    }

    // Legacy focused the input on show (gated on runtime readiness). The
    // composer disables the input until the runtime is ready / while
    // restoring / for transcripts, so focusing a disabled input is a no-op.
    const focusRole = entry.savedFocusRole ?? 'input';
    const selection = entry.savedSelection;
    entry.focusTimer = setTimeout(() => {
      entry.focusTimer = null;
      if (!panel.isConnected) return;
      const selector = '[data-role="' + focusRole + '"]';
      let target = panel.querySelector<HTMLElement>(selector);
      if (!target || target.hidden) {
        target = panel.querySelector<HTMLElement>('[data-role="input"]');
      }
      if (!target) return;
      if ((target instanceof HTMLTextAreaElement || target instanceof HTMLInputElement) && target.disabled) {
        return;
      }
      target.focus();
      if (
        selection &&
        (target instanceof HTMLTextAreaElement || target instanceof HTMLInputElement)
      ) {
        try {
          target.setSelectionRange(selection.start, selection.end);
        } catch {
          /* input types without selection support */
        }
      }
    }, 0);
  }

  private entryForPanel(panel: HTMLElement): WorkspaceEntry | undefined {
    for (const entry of this.workspaces.values()) {
      if (entry.panel === panel) return entry;
    }
    return undefined;
  }

  private clearFocusTimer(entry: WorkspaceEntry): void {
    if (entry.focusTimer !== null) {
      clearTimeout(entry.focusTimer);
      entry.focusTimer = null;
    }
  }

  /** Faithful port of AgentThreadManager.setAgentSettings — writes the global
   * appearance CSS variables onto document.documentElement. */
  private applyGlobalAppearance(settings: AgentAppearanceSettings): void {
    const root = document.documentElement;
    // shadcn variable layer, scoped to the Agent UI container (CP2).
    applyShadcnTheme(this.container, settings);
    const globalPortal = document.getElementById('global-portal-root');
    if (globalPortal) applyShadcnTheme(globalPortal, settings);

    const fontSize = settings.agentFontSize;
    if (typeof fontSize === 'number' && Number.isInteger(fontSize) && fontSize >= 6 && fontSize <= 72) {
      root.style.setProperty('--agent-font-size', fontSize + 'px');
    }
    if (typeof settings.agentFontFamily === 'string' && settings.agentFontFamily.trim()) {
      root.style.setProperty('--agent-font-ui', settings.agentFontFamily.trim());
    }
    if (typeof settings.agentMonoFontFamily === 'string' && settings.agentMonoFontFamily.trim()) {
      root.style.setProperty('--agent-font-mono', settings.agentMonoFontFamily.trim());
    }

    const t = settings.themeColors;
    if (t && typeof t === 'object') {
      this.setVar(root, '--agent-bg', t.background);
      this.setVar(root, '--agent-surface', t.surface);
      this.setVar(root, '--agent-surface-raised', t.surfaceRaised);
      this.setVar(root, '--agent-surface-muted', t.surfaceMuted);
      this.setVar(root, '--agent-hover', t.hover);
      this.setVar(root, '--agent-border', t.border);
      this.setVar(root, '--agent-border-strong', t.borderStrong);
      this.setVar(root, '--agent-text', t.text);
      this.setVar(root, '--agent-text-muted', t.textMuted);
      this.setVar(root, '--agent-text-dim', t.textDim);
      this.setVar(root, '--agent-accent', t.accent);
      this.setVar(root, '--agent-accent-hover', t.accentHover);
      this.setVar(root, '--agent-error', t.error);
      this.setVar(root, '--agent-error-bg', t.errorBg);
      this.setVar(root, '--agent-warning', t.warning);
      this.setVar(root, '--agent-warning-bg', t.warningBg);
      this.setVar(root, '--agent-scrollbar', t.scrollbar);
      this.setVar(root, '--agent-scrollbar-hover', t.scrollbarHover);
      // Native form controls and scrollbars follow the active theme's scheme.
      const scheme = colorSchemeForBackground(t.background);
      if (scheme) root.style.colorScheme = scheme;
    }

    const a = settings.agentThemeColors;
    if (a && typeof a === 'object') {
      this.setVar(root, '--agent-code-block-bg', a.codeBlockBg);
      this.setVar(root, '--agent-code-block-text', a.codeBlockText);
      this.setVar(root, '--agent-code-block-border', a.codeBlockBorder);
      this.setVar(root, '--agent-decision-primary', a.decisionPrimary);
      this.setVar(root, '--agent-decision-primary-hover', a.decisionPrimaryHover);
      this.setVar(root, '--agent-decision-primary-text', a.decisionPrimaryText);
      this.setVar(root, '--agent-stop-btn', a.stopBtn);
      this.setVar(root, '--agent-stop-btn-hover', a.stopBtnHover);
      this.setVar(root, '--agent-permission-bg', a.permissionBg);
      this.setVar(root, '--agent-permission-border', a.permissionBorder);
      this.setVar(root, '--agent-elicitation-bg', a.elicitationBg);
      this.setVar(root, '--agent-elicitation-border', a.elicitationBorder);
      this.setVar(root, '--agent-overlay', a.overlay);
      this.setVar(root, '--agent-shadow', a.shadow);
      this.setVar(root, '--agent-focus-ring', a.focusRing);
      this.setVar(root, '--agent-workbench-tint', a.workbenchTint);
    }
  }

  private setVar(el: HTMLElement, name: string, value: unknown): void {
    if (typeof value === 'string' && value.trim()) {
      el.style.setProperty(name, value.trim());
    }
  }
}

/** Maps a #rgb/#rrggbb theme background to the matching color-scheme so
 * native controls (form fields, scrollbars) render in the theme's palette. */
function colorSchemeForBackground(background: unknown): 'dark' | 'light' | null {
  if (typeof background !== 'string') return null;
  const match = background.trim().match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
  if (!match) return null;
  let hex = match[1];
  if (hex.length === 3) hex = hex.split('').map((c) => c + c).join('');
  const r = parseInt(hex.slice(0, 2), 16);
  const g = parseInt(hex.slice(2, 4), 16);
  const b = parseInt(hex.slice(4, 6), 16);
  const luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
  return luminance < 0.5 ? 'dark' : 'light';
}
