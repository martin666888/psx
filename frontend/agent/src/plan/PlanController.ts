// PlanController.ts — the workspace-local Plan card owner.
//
// The Plan card is the first tenant of the generic context-card column
// (data-role="context-cards") inside every workspace panel; future cards will
// reuse the same host, but there is deliberately no registry abstraction yet.
// Plan entries render purely from the reduced AgentWorkspaceState (the
// reducer folds plan_update / agent_thread_loaded / agent_cleared), and the
// panel DOM survives Tab hide/show untouched.
//
// Card mode is workspace-local runtime state (never persisted; every mount
// starts at 'auto'):
//   auto   — expands while a plan is active, collapses to the entry button
//            otherwise.
//   pinned — always expanded, including the empty state.
//   closed — stays collapsed; arriving plan updates only raise the unread
//            badge on the entry button and never interrupt.
// The width is a GLOBAL preference (psx.agent.planWidth, shared by all
// workspaces like the legacy inspector width); psx.agent.inspectorWidth is
// read once as a migration source and psx.agent.planPanelWidth is retired.

import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type {
  AgentWorkspaceState,
  WorkspacePlanState
} from '../contracts/workspace-state.js';
import { planStatusClass, planStatusLabel, planStatusMarker } from '../core/plan.js';

/** The seam the controller needs from the workspace host. */
export interface PlanHost {
  getPanel(workspaceId: string): HTMLElement | null;
}

export type PlanCardMode = 'auto' | 'pinned' | 'closed';

/** Shell layout seam: the card reports expansion changes so the compact
 * one-drawer-at-a-time rule can close the History drawer (and vice versa). */
export interface PlanControllerCallbacks {
  onExpansionChanged?: () => void;
}

const MIN_WIDTH = 280;
const MAX_WIDTH = 400;
const DEFAULT_WIDTH = 320;
const STORAGE_KEY = 'psx.agent.planWidth';
// One-time migration source; never read again once the new key exists.
const LEGACY_STORAGE_KEY = 'psx.agent.inspectorWidth';

const EMPTY_PLAN: WorkspacePlanState = { active: false, runId: '', entries: [], fallbackText: '' };

function role(panel: HTMLElement, name: string): HTMLElement | null {
  return panel.querySelector<HTMLElement>('[data-role="' + name + '"]');
}

export class PlanController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: PlanHost;
  private readonly callbacks: PlanControllerCallbacks;

  private panel: HTMLElement | null = null;
  private contextCards: HTMLElement | null = null;
  private planCard: HTMLElement | null = null;
  private planPanel: HTMLElement | null = null;
  private planEntry: HTMLElement | null = null;
  private planPin: HTMLElement | null = null;
  private planClose: HTMLElement | null = null;
  private planUnread: HTMLElement | null = null;
  private resizer: HTMLElement | null = null;

  private mode: PlanCardMode = 'auto';
  private unread = false;
  private expanded = false;
  private planWidth = DEFAULT_WIDTH;
  private isResizing = false;
  private lastPlanRef: WorkspacePlanState = EMPTY_PLAN;

  private readonly cleanup: Array<() => void> = [];

  constructor(workspaceId: string, host: PlanHost, callbacks?: PlanControllerCallbacks) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.callbacks = callbacks ?? {};
  }

  mount(): void {
    const panel = this.host.getPanel(this.workspaceId);
    if (!panel) return;
    this.panel = panel;
    this.contextCards = role(panel, 'context-cards');
    this.planCard = role(panel, 'plan-card');
    this.planPanel = role(panel, 'plan-panel');
    this.planEntry = role(panel, 'plan-entry');
    this.planPin = role(panel, 'plan-pin');
    this.planClose = role(panel, 'plan-close');
    this.planUnread = role(panel, 'plan-unread');
    this.resizer = role(panel, 'plan-resizer');

    this.planWidth = this.readPlanWidth();
    this.applyPlanWidth(this.planWidth);
    this.wireCardControls();
    this.wireResizer();
    this.renderPlan(EMPTY_PLAN);
    this.applyMode();
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    if (!this.panel) return;
    switch (event.type) {
      case 'plan_update':
      case 'agent_thread_loaded':
      case 'agent_cleared':
        this.applyPlan(state);
        break;
      default:
        break;
    }
  }

  dispose(): void {
    if (this.isResizing) {
      this.isResizing = false;
      document.body.classList.remove('agent-plan-resizing');
    }
    for (const off of this.cleanup.splice(0)) off();
    this.panel = null;
  }

  // --- Card mode (auto / pinned / closed) --------------------------------------

  private wireCardControls(): void {
    this.on(this.planEntry, 'click', () => this.openFromEntry());
    this.on(this.planPin, 'click', () => {
      this.mode = this.mode === 'pinned' ? 'auto' : 'pinned';
      this.applyMode();
    });
    this.on(this.planClose, 'click', () => {
      this.mode = 'closed';
      this.applyMode();
    });
  }

  /** Entry button: closed wakes back to auto (an active plan expands); an
   * idle auto entry pins the card open so the empty state stays reachable. */
  private openFromEntry(): void {
    if (this.mode === 'closed') this.mode = 'auto';
    else if (this.mode === 'auto') this.mode = 'pinned';
    this.applyMode();
    // User-initiated open: move focus to the first card control (drawer a11y).
    if (this.expanded) this.planPin?.focus();
  }

  /** Shell layout seam for the one-drawer rule and Esc close. */
  isExpanded(): boolean {
    return this.expanded;
  }

  closeCard(): void {
    if (!this.expanded) return;
    this.mode = 'closed';
    this.applyMode();
  }

  focusEntry(): void {
    this.planEntry?.focus();
  }

  private applyMode(): void {
    const expanded = this.mode === 'pinned' || (this.mode === 'auto' && this.lastPlanRef.active);
    // Expanding is the "user saw it" signal that clears the unread badge.
    if (expanded) this.unread = false;
    const changed = expanded !== this.expanded;
    this.expanded = expanded;
    if (this.planCard) this.planCard.hidden = !expanded;
    if (this.planEntry) {
      this.planEntry.hidden = expanded;
      this.planEntry.setAttribute('aria-expanded', String(expanded));
    }
    if (this.planUnread) this.planUnread.hidden = !this.unread;
    if (this.planPin) this.planPin.setAttribute('aria-pressed', String(this.mode === 'pinned'));
    if (changed) this.callbacks.onExpansionChanged?.();
  }

  // --- Plan rendering ---------------------------------------------------------

  /** Renders the reduced plan when it actually changed; in closed mode an
   * active-plan update raises the unread badge instead of expanding. */
  private applyPlan(state: AgentWorkspaceState): void {
    const plan = state.inspector.plan;
    if (plan === this.lastPlanRef) return;
    this.lastPlanRef = plan;
    if (this.mode === 'closed' && plan.active) this.unread = true;
    this.renderPlan(plan);
    this.applyMode();
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

  // --- Resize + width -----------------------------------------------------------

  private wireResizer(): void {
    const resizer = this.resizer;
    if (!resizer || !this.panel) return;

    this.on(resizer, 'pointerdown', (event) => {
      if (event.button !== 0) return;
      this.isResizing = true;
      resizer.setPointerCapture(event.pointerId);
      document.body.classList.add('agent-plan-resizing');
      event.preventDefault();
    });

    this.on(resizer, 'pointermove', (event) => {
      if (!this.isResizing || !this.panel) return;
      // Measure against the overlay column itself: the card floats with a
      // right inset, so the panel edge is no longer the width baseline.
      const rect = (this.contextCards ?? this.panel).getBoundingClientRect();
      this.setPlanWidth(rect.right - event.clientX, true);
    });

    const endResize = (event: PointerEvent): void => {
      if (!this.isResizing) return;
      this.isResizing = false;
      document.body.classList.remove('agent-plan-resizing');
      try {
        resizer.releasePointerCapture(event.pointerId);
      } catch {
        // Pointer capture may already be gone if the window lost focus.
      }
      this.savePlanWidth();
    };

    this.on(resizer, 'pointerup', endResize);
    this.on(resizer, 'pointercancel', endResize);
    this.on(resizer, 'keydown', (event) => {
      const step = event.shiftKey ? 40 : 16;
      if (event.key === 'ArrowLeft') this.setPlanWidth(this.planWidth + step, false);
      else if (event.key === 'ArrowRight') this.setPlanWidth(this.planWidth - step, false);
      else if (event.key === 'Home') this.setPlanWidth(MAX_WIDTH, false);
      else if (event.key === 'End') this.setPlanWidth(MIN_WIDTH, false);
      else return;
      event.preventDefault();
    });
  }

  /** Global width preference with a one-time legacy migration: the new key
   * wins; only when it is absent does the legacy inspector width get read,
   * clamped into the new range and written through. */
  private readPlanWidth(): number {
    try {
      const stored = window.localStorage?.getItem(STORAGE_KEY);
      if (stored != null) return this.clampStoredWidth(stored);
      const legacy = window.localStorage?.getItem(LEGACY_STORAGE_KEY);
      if (legacy != null) {
        const migrated = this.clampStoredWidth(legacy);
        window.localStorage?.setItem(STORAGE_KEY, String(migrated));
        return migrated;
      }
    } catch {
      // localStorage can be unavailable in constrained WebView profiles.
    }
    return DEFAULT_WIDTH;
  }

  private clampStoredWidth(raw: string): number {
    const width = Number(raw);
    if (!Number.isFinite(width) || width <= 0) return DEFAULT_WIDTH;
    return this.clampWidth(width);
  }

  private setPlanWidth(width: number, deferSave: boolean): void {
    this.planWidth = this.clampWidth(width);
    this.applyPlanWidth(this.planWidth);
    if (!deferSave) this.savePlanWidth();
  }

  private applyPlanWidth(width: number): void {
    // Width is a global preference: write the shared workspace container so
    // every mounted overlay inherits it (panels no longer declare their own;
    // the default lives in the shell.css :root block).
    const container = this.panel?.parentElement ?? null;
    container?.style.setProperty('--agent-plan-width', this.clampWidth(width) + 'px');
  }

  private savePlanWidth(): void {
    try {
      window.localStorage?.setItem(STORAGE_KEY, String(this.planWidth));
    } catch {
      // Width persistence is optional; resizing remains available.
    }
  }

  private clampWidth(width: number): number {
    const numeric = Number(width);
    if (!Number.isFinite(numeric)) return DEFAULT_WIDTH;
    return Math.max(MIN_WIDTH, Math.min(MAX_WIDTH, Math.round(numeric)));
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
