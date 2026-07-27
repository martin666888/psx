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

/** The one terminal-view method the host toggles when switching workspaces. */
interface TerminalViewToggle {
  setViewVisible(visible: boolean): void;
}

/** The global History dock view: the whole agent area (dock included) hides
 * while a terminal workspace is active. */
export interface AgentViewVisibilityListener {
  setAgentViewActive(active: boolean): void;
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
}

export class WorkspaceHost implements SessionRuntimeHost, PlanHost {
  private readonly terminalManager: TerminalViewToggle;
  private readonly container: HTMLElement;
  private readonly template: HTMLTemplateElement;
  private readonly workspaces = new Map<string, WorkspaceEntry>();
  private activeWorkspaceId = '';
  private settings: AgentAppearanceSettings | null = null;
  private historyDockView: AgentViewVisibilityListener | null = null;

  constructor(
    terminalManager: TerminalViewToggle,
    container: HTMLElement,
    template: HTMLTemplateElement
  ) {
    this.terminalManager = terminalManager;
    this.container = container;
    this.template = template;
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
    // Clear is a PSX built-in with no feature domain, so the shell wires it.
    const clear = panel.querySelector<HTMLElement>('[data-role="clear"]');
    clear?.addEventListener('click', () => bridge.sendAgentCommand('clear'));

    if (this.settings) this.applyGlobalAppearance(this.settings);
    this.workspaces.set(id, { panel, bridge, focusTimer: null });
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

  /** entry.ts attaches the global History dock after construction. */
  setHistoryDockView(view: AgentViewVisibilityListener): void {
    this.historyDockView = view;
  }

  activate(workspaceId: string, kind: 'terminal' | 'agent'): void {
    const id = String(workspaceId || '');
    this.activeWorkspaceId = id;
    // The Agent root is an absolute layer above the terminal. It must only
    // receive pointer events while an Agent workspace is actually visible;
    // otherwise its transparent surface prevents blank-area terminal clicks
    // from reaching xterm's wrapper focus handler.
    this.container.classList.toggle('agent-workspace-active', kind === 'agent');
    this.historyDockView?.setAgentViewActive(kind === 'agent');

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
    panel.hidden = !visible;
    const entry = this.entryForPanel(panel);
    if (entry) this.clearFocusTimer(entry);
    if (visible) {
      // Legacy focused the input on show (gated on runtime readiness). The
      // composer disables the input until the runtime is ready / while
      // restoring / for transcripts, so focusing a disabled input is a no-op.
      const input = panel.querySelector<HTMLTextAreaElement>('[data-role="input"]');
      if (entry) {
        entry.focusTimer = setTimeout(() => {
          entry.focusTimer = null;
          if (panel.isConnected && input && !input.disabled) input.focus();
        }, 0);
      }
    }
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
    }

    const a = settings.agentThemeColors;
    if (a && typeof a === 'object') {
      this.setVar(root, '--agent-code-block-bg', a.codeBlockBg);
      this.setVar(root, '--agent-code-block-text', a.codeBlockText);
      this.setVar(root, '--agent-code-block-border', a.codeBlockBorder);
      this.setVar(root, '--agent-send-btn', a.sendBtn);
      this.setVar(root, '--agent-send-btn-hover', a.sendBtnHover);
      this.setVar(root, '--agent-send-btn-text', a.sendBtnText);
      this.setVar(root, '--agent-decision-primary', a.decisionPrimary);
      this.setVar(root, '--agent-decision-primary-hover', a.decisionPrimaryHover);
      this.setVar(root, '--agent-decision-primary-text', a.decisionPrimaryText);
      this.setVar(root, '--agent-stop-btn', a.stopBtn);
      this.setVar(root, '--agent-stop-btn-hover', a.stopBtnHover);
      this.setVar(root, '--agent-allow-color', a.allowColor);
      this.setVar(root, '--agent-deny-color', a.denyColor);
      this.setVar(root, '--agent-caution-color', a.cautionColor);
      this.setVar(root, '--agent-permission-bg', a.permissionBg);
      this.setVar(root, '--agent-permission-border', a.permissionBorder);
      this.setVar(root, '--agent-elicitation-bg', a.elicitationBg);
      this.setVar(root, '--agent-elicitation-border', a.elicitationBorder);
      this.setVar(root, '--agent-overlay', a.overlay);
      this.setVar(root, '--agent-shadow', a.shadow);
      this.setVar(root, '--agent-focus-ring', a.focusRing);
    }
  }

  private setVar(el: HTMLElement, name: string, value: unknown): void {
    if (typeof value === 'string' && value.trim()) {
      el.style.setProperty(name, value.trim());
    }
  }
}
