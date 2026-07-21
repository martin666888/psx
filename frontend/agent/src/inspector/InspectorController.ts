// InspectorController.ts — the Phase 4 Inspector domain owner.
//
// Owns the whole Agent inspector: the Plan + History tab pair, the resizable
// panel (pointer + keyboard), width persistence, plan-unread badge and the
// History scroll restoration. Plan entries and History rows are rendered purely
// from the reduced AgentWorkspaceState (the reducer folds plan_update /
// agent_threads / agent_history_error / agent_thread_loaded / agent_cleared);
// the transient loading / initial-empty display states and the active-tab /
// width / scroll are controller-local UI concerns.
//
// Faithful port of the legacy DOM writes and listeners:
//   wwwroot/js/agent/inspector.js  (tabs, history, resize, width, unread)
//   wwwroot/js/agent/plan.js       (plan panel rendering)
// so the produced DOM is byte-identical to the pre-refactor engine.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type {
  AgentWorkspaceState,
  WorkspaceHistoryThread,
  WorkspacePlanState
} from '../contracts/workspace-state.js';
import { planStatusClass, planStatusLabel, planStatusMarker } from '../core/plan.js';

/** The strangler seam the controller needs from the legacy adapter. */
export interface InspectorHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
}

const MIN_WIDTH = 260;
const MAX_WIDTH = 380;
const DEFAULT_WIDTH = 300;
const STORAGE_KEY = 'psx.agent.inspectorWidth';
const LEGACY_STORAGE_KEY = 'psx.agent.planPanelWidth';

const EMPTY_PLAN: WorkspacePlanState = { active: false, runId: '', entries: [], fallbackText: '' };

function role(panel: HTMLElement, name: string): HTMLElement | null {
  return panel.querySelector<HTMLElement>('[data-role="' + name + '"]');
}

export class InspectorController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: InspectorHost;

  private panel: HTMLElement | null = null;
  private inspector: HTMLElement | null = null;
  private inspectorResizer: HTMLElement | null = null;
  private planTab: HTMLElement | null = null;
  private planUnread: HTMLElement | null = null;
  private planPanel: HTMLElement | null = null;
  private historyTab: HTMLElement | null = null;
  private historyPanel: HTMLElement | null = null;

  private activeInspectorTab: 'plan' | 'history' = 'plan';
  private inspectorWidth = DEFAULT_WIDTH;
  private isResizingInspector = false;
  private historyScrollTop = 0;
  private lastPlanRef: WorkspacePlanState | undefined = undefined;

  private readonly cleanup: Array<() => void> = [];
  private readonly animationFrames = new Set<number>();

  constructor(workspaceId: string, host: InspectorHost) {
    this.workspaceId = workspaceId;
    this.host = host;
  }

  mount(): void {
    const panel = this.host.getPanel(this.workspaceId);
    if (!panel) return;
    this.panel = panel;
    this.inspector = role(panel, 'inspector');
    this.inspectorResizer = role(panel, 'inspector-resizer');
    this.planTab = role(panel, 'plan-tab');
    this.planUnread = role(panel, 'plan-unread');
    this.planPanel = role(panel, 'plan-panel');
    this.historyTab = role(panel, 'history-tab');
    this.historyPanel = role(panel, 'history-panel');

    this.inspectorWidth = this.readInspectorWidth();
    this.applyInspectorWidth(this.inspectorWidth);
    this.wireInspectorTabs();
    this.wireInspectorResizer();
    this.clearPlanUnread();
    this.renderPlan(EMPTY_PLAN);
    this.showHistoryState('empty', 'Open History to load saved Agent threads.');
    this.selectInspectorTab('plan', false);
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    if (!this.panel) return;
    const raw = event.raw;
    switch (event.type) {
      case 'plan_update':
        this.applyPlan(state);
        break;
      case 'agent_thread_loaded':
        if (raw.selectPlan === true) this.selectInspectorTab('plan', false);
        this.applyPlan(state);
        this.syncHistoryCurrentState(state.session.currentThreadId);
        break;
      case 'agent_state':
        if (typeof raw.threadId === 'string' && raw.threadId) {
          this.syncHistoryCurrentState(state.session.currentThreadId);
        }
        break;
      case 'agent_threads':
        this.selectInspectorTab('history', false);
        this.renderHistory(state);
        break;
      case 'agent_history_error':
        this.selectInspectorTab('history', false);
        this.renderHistoryError(state.inspector.history.errorText);
        break;
      case 'agent_history_invalidated':
        if (this.activeInspectorTab === 'history') this.refreshHistory();
        break;
      case 'agent_cleared':
        this.applyPlan(state);
        break;
      default:
        break;
    }
  }

  dispose(): void {
    if (this.isResizingInspector) {
      this.isResizingInspector = false;
      document.body.classList.remove('agent-inspector-resizing');
    }
    for (const frame of this.animationFrames) cancelAnimationFrame(frame);
    this.animationFrames.clear();
    for (const off of this.cleanup.splice(0)) off();
    this.panel = null;
  }

  // --- Plan ----------------------------------------------------------------

  /** Renders the reduced plan when it actually changed, mirroring the legacy
   * _upsertPlan / _resetPlanState mark/clear-unread behaviour. */
  private applyPlan(state: AgentWorkspaceState): void {
    const plan = state.inspector.plan;
    if (plan === this.lastPlanRef) return;
    this.lastPlanRef = plan;
    this.renderPlan(plan);
    if (plan.active) this.markPlanUnread();
    else this.clearPlanUnread();
  }

  private renderPlan(plan: WorkspacePlanState): void {
    if (!this.planPanel) return;
    this.planPanel.innerHTML = '';

    if (!plan.active) {
      const empty = document.createElement('div');
      empty.className = 'agent-plan-empty';
      empty.textContent = 'No active plan';
      this.planPanel.appendChild(empty);
      return;
    }

    if (plan.entries.length === 0) {
      const fallback = document.createElement('pre');
      fallback.className = 'agent-plan-fallback';
      fallback.textContent = plan.fallbackText || 'No plan items.';
      this.planPanel.appendChild(fallback);
      return;
    }

    const list = document.createElement('ol');
    list.className = 'agent-plan-list';
    plan.entries.forEach((entry) => {
      const item = document.createElement('li');
      item.className = 'agent-plan-item ' + planStatusClass(entry.status);
      item.setAttribute('aria-label', planStatusLabel(entry.status) + ': ' + entry.content);
      if (entry.priority) item.dataset.priority = entry.priority;

      const marker = document.createElement('span');
      marker.className = 'agent-plan-marker';
      marker.textContent = planStatusMarker(entry.status);

      const content = document.createElement('span');
      content.className = 'agent-plan-content';
      content.textContent = entry.content;

      item.appendChild(marker);
      item.appendChild(content);
      list.appendChild(item);
    });
    this.planPanel.appendChild(list);
  }

  private markPlanUnread(): void {
    if (this.activeInspectorTab === 'plan' || !this.planUnread) return;
    this.planUnread.hidden = false;
  }

  private clearPlanUnread(): void {
    if (this.planUnread) this.planUnread.hidden = true;
  }

  // --- History -------------------------------------------------------------

  private renderHistory(state: AgentWorkspaceState): void {
    if (!this.historyPanel) return;
    const threads = state.inspector.history.threads;
    const currentThreadId = state.session.currentThreadId;
    const scrollTop = this.historyScrollTop || this.historyPanel.scrollTop || 0;
    this.historyPanel.innerHTML = '';

    if (threads.length === 0) {
      this.showHistoryState('empty', 'No saved Agent threads.');
      return;
    }

    const list = document.createElement('div');
    list.className = 'agent-history-list';
    threads.forEach((thread) => {
      const row = document.createElement('button');
      row.type = 'button';
      row.className = 'agent-history-item';
      row.dataset.threadId = thread.threadId || '';
      row.setAttribute('aria-current', thread.threadId === currentThreadId ? 'true' : 'false');

      const heading = document.createElement('span');
      heading.className = 'agent-history-heading';

      const title = document.createElement('strong');
      title.textContent = thread.title || 'Agent Chat';
      heading.appendChild(title);

      const current = document.createElement('span');
      current.className = 'agent-history-current';
      current.textContent = 'Current';
      current.hidden = thread.threadId !== currentThreadId;
      heading.appendChild(current);

      const cwd = document.createElement('span');
      cwd.className = 'agent-history-cwd';
      cwd.textContent = thread.cwd || '';
      cwd.title = thread.cwd || '';

      const meta = document.createElement('small');
      meta.textContent = this.historyMeta(thread);

      row.appendChild(heading);
      row.appendChild(cwd);
      row.appendChild(meta);
      row.addEventListener('click', () => {
        if (this.historyPanel) this.historyScrollTop = this.historyPanel.scrollTop;
        this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('load_thread', thread.threadId || '');
      });
      list.appendChild(row);
    });

    this.historyPanel.appendChild(list);
    this.requestFrame(() => {
      if (this.historyPanel) this.historyPanel.scrollTop = scrollTop;
    });
  }

  private renderHistoryError(text: string): void {
    if (!this.historyPanel) return;
    this.historyPanel.innerHTML = '';

    const state = document.createElement('div');
    state.className = 'agent-history-state agent-history-error';
    state.setAttribute('role', 'alert');

    const message = document.createElement('p');
    message.textContent = text || 'Unable to load Agent thread history.';

    const retry = document.createElement('button');
    retry.type = 'button';
    retry.className = 'agent-history-retry';
    retry.textContent = 'Retry';
    retry.addEventListener('click', () => this.refreshHistory());

    state.appendChild(message);
    state.appendChild(retry);
    this.historyPanel.appendChild(state);
  }

  private showHistoryState(stateName: string, text: string): void {
    if (!this.historyPanel) return;
    this.historyPanel.innerHTML = '';
    const state = document.createElement('div');
    state.className = 'agent-history-state agent-history-' + stateName;
    if (stateName === 'loading') {
      state.setAttribute('role', 'status');
      state.setAttribute('aria-live', 'polite');
    }
    state.textContent = text;
    this.historyPanel.appendChild(state);
  }

  private syncHistoryCurrentState(currentThreadId: string): void {
    if (!this.historyPanel) return;
    this.historyPanel.querySelectorAll<HTMLElement>('.agent-history-item').forEach((row) => {
      const isCurrent = row.dataset.threadId === currentThreadId;
      row.setAttribute('aria-current', isCurrent ? 'true' : 'false');
      const badge = row.querySelector<HTMLElement>('.agent-history-current');
      if (badge) badge.hidden = !isCurrent;
    });
  }

  private refreshHistory(): void {
    if (this.historyPanel) this.historyScrollTop = this.historyPanel.scrollTop;
    this.showHistoryState('loading', 'Loading history...');
    this.host.bridgeFor(this.workspaceId)?.sendAgentCommand('history');
  }

  private historyMeta(thread: WorkspaceHistoryThread): string {
    const parts: string[] = [];
    if (thread.updatedAt) parts.push(thread.updatedAt);
    if (thread.sessionId) parts.push('session ' + thread.sessionId.slice(0, 8));
    return parts.join(' | ');
  }

  // --- Composer seam -------------------------------------------------------

  /** Optimistic History load triggered by the composer's `/history` command
   * (faithful port of the legacy commands.js `_applyCommand` history branch):
   * switch to the History tab without a second refresh and show the loading
   * state, then let the pending `history` bridge command resolve it. */
  beginHistoryLoading(): void {
    if (!this.panel) return;
    this.selectInspectorTab('history', false);
    this.showHistoryState('loading', 'Loading history...');
  }

  // --- Tabs ----------------------------------------------------------------

  private selectInspectorTab(name: string, refreshHistory: boolean): void {
    const next = name === 'history' ? 'history' : 'plan';
    const shouldRefresh = next === 'history' && refreshHistory !== false;
    this.activeInspectorTab = next;

    const isPlan = next === 'plan';
    if (this.planTab) {
      this.planTab.setAttribute('aria-selected', String(isPlan));
      this.planTab.tabIndex = isPlan ? 0 : -1;
    }
    if (this.historyTab) {
      this.historyTab.setAttribute('aria-selected', String(!isPlan));
      this.historyTab.tabIndex = isPlan ? -1 : 0;
    }
    if (this.planPanel) this.planPanel.hidden = !isPlan;
    if (this.historyPanel) this.historyPanel.hidden = isPlan;

    if (isPlan) this.clearPlanUnread();
    else if (shouldRefresh) this.refreshHistory();
  }

  private wireInspectorTabs(): void {
    const tabs = [this.planTab, this.historyTab].filter((tab): tab is HTMLElement => !!tab);
    if (tabs.length === 0) return;

    this.on(this.planTab, 'click', () => this.selectInspectorTab('plan', false));
    this.on(this.historyTab, 'click', () => this.selectInspectorTab('history', true));

    tabs.forEach((tab, index) => {
      this.on(tab, 'keydown', (event) => {
        let targetIndex = index;
        if (event.key === 'ArrowLeft') targetIndex = (index - 1 + tabs.length) % tabs.length;
        else if (event.key === 'ArrowRight') targetIndex = (index + 1) % tabs.length;
        else if (event.key === 'Home') targetIndex = 0;
        else if (event.key === 'End') targetIndex = tabs.length - 1;
        else return;

        event.preventDefault();
        const target = tabs[targetIndex];
        const targetName = target === this.historyTab ? 'history' : 'plan';
        this.selectInspectorTab(targetName, targetName === 'history');
        target.focus();
      });
    });
  }

  // --- Resize + width ------------------------------------------------------

  private wireInspectorResizer(): void {
    const resizer = this.inspectorResizer;
    if (!resizer || !this.inspector) return;

    this.on(resizer, 'pointerdown', (event) => {
      if (event.button !== 0) return;
      this.isResizingInspector = true;
      resizer.setPointerCapture(event.pointerId);
      document.body.classList.add('agent-inspector-resizing');
      event.preventDefault();
    });

    this.on(resizer, 'pointermove', (event) => {
      if (!this.isResizingInspector || !this.panel) return;
      const rect = this.panel.getBoundingClientRect();
      const width = rect.right - event.clientX - resizer.offsetWidth;
      this.setInspectorWidth(width, true);
    });

    const endResize = (event: PointerEvent): void => {
      if (!this.isResizingInspector) return;
      this.isResizingInspector = false;
      document.body.classList.remove('agent-inspector-resizing');
      try {
        resizer.releasePointerCapture(event.pointerId);
      } catch {
        // Pointer capture may already be gone if the window lost focus.
      }
      this.saveInspectorWidth();
    };

    this.on(resizer, 'pointerup', endResize);
    this.on(resizer, 'pointercancel', endResize);
    this.on(resizer, 'keydown', (event) => {
      const step = event.shiftKey ? 40 : 16;
      if (event.key === 'ArrowLeft') this.setInspectorWidth(this.inspectorWidth + step, false);
      else if (event.key === 'ArrowRight') this.setInspectorWidth(this.inspectorWidth - step, false);
      else if (event.key === 'Home') this.setInspectorWidth(MAX_WIDTH, false);
      else if (event.key === 'End') this.setInspectorWidth(MIN_WIDTH, false);
      else return;
      event.preventDefault();
    });
  }

  private readInspectorWidth(): number {
    try {
      const stored = window.localStorage?.getItem(STORAGE_KEY)
        ?? window.localStorage?.getItem(LEGACY_STORAGE_KEY);
      const width = Number(stored);
      if (Number.isFinite(width)) return this.clampInspectorWidth(width);
    } catch {
      // localStorage can be unavailable in constrained WebView profiles.
    }
    return DEFAULT_WIDTH;
  }

  private setInspectorWidth(width: number, deferSave: boolean): void {
    this.inspectorWidth = this.clampInspectorWidth(width);
    this.applyInspectorWidth(this.inspectorWidth);
    if (!deferSave) this.saveInspectorWidth();
  }

  private applyInspectorWidth(width: number): void {
    if (this.panel) {
      this.panel.style.setProperty('--agent-inspector-width', this.clampInspectorWidth(width) + 'px');
    }
  }

  private saveInspectorWidth(): void {
    try {
      window.localStorage?.setItem(STORAGE_KEY, String(this.inspectorWidth));
    } catch {
      // Width persistence is optional; resizing remains available.
    }
  }

  private clampInspectorWidth(width: number): number {
    const numeric = Number(width);
    if (!Number.isFinite(numeric)) return DEFAULT_WIDTH;
    return Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, Math.round(numeric)));
  }

  private requestFrame(callback: () => void): void {
    let frame = 0;
    let completed = false;
    frame = requestAnimationFrame(() => {
      completed = true;
      this.animationFrames.delete(frame);
      callback();
    });
    if (!completed) this.animationFrames.add(frame);
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
