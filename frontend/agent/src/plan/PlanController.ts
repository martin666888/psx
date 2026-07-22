// PlanController.ts — the workspace-local Plan card owner.
//
// The Plan card is the first tenant of the generic context-card column
// (data-role="context-cards") inside every workspace panel; future cards will
// reuse the same host, but there is deliberately no registry abstraction yet.
// Plan entries render purely from the reduced AgentWorkspaceState (the
// reducer folds plan_update / agent_thread_loaded / agent_cleared), and the
// panel DOM survives Tab hide/show untouched.
//
// Visibility is two-state (visible / hidden), workspace-local runtime state
// that is never persisted. Wide mode (>=1080px) shows the card by default — a
// small, content-height card below the toolbar, empty state included — while
// compact mode starts with the drawer closed so an empty card never covers
// the conversation unprompted. The user hides/shows it through the toolbar
// toggle. While hidden, an arriving active plan raises the unread dot on the
// toggle instead of interrupting; showing the card clears it. The width is a
// fixed CSS token; there is no resizer.

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

/** Shell layout seam: the card reports visibility changes so the compact
 * one-drawer-at-a-time rule can close the History drawer (and vice versa). */
export interface PlanControllerCallbacks {
  onVisibilityChanged?: () => void;
}

const EMPTY_PLAN: WorkspacePlanState = { active: false, runId: '', entries: [], fallbackText: '' };

// Same breakpoint as AgentShellLayoutController: wide shows the card by
// default, compact keeps the drawer closed until the user asks for it.
const WIDE_QUERY = '(min-width: 1080px)';

function role(panel: HTMLElement, name: string): HTMLElement | null {
  return panel.querySelector<HTMLElement>('[data-role="' + name + '"]');
}

export class PlanController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: PlanHost;
  private readonly callbacks: PlanControllerCallbacks;

  private panel: HTMLElement | null = null;
  private planCard: HTMLElement | null = null;
  private planPanel: HTMLElement | null = null;
  private planToggle: HTMLElement | null = null;
  private planToggleUnread: HTMLElement | null = null;

  private visible = true;
  private unread = false;
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
    this.planCard = role(panel, 'plan-card');
    this.planPanel = role(panel, 'plan-panel');
    this.planToggle = role(panel, 'plan-toggle');
    this.planToggleUnread = role(panel, 'plan-toggle-unread');

    this.visible = this.readDefaultVisible();
    this.on(this.planToggle, 'click', () => this.setVisible(!this.visible));
    this.renderPlan(EMPTY_PLAN);
    this.applyVisibility();
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
    for (const off of this.cleanup.splice(0)) off();
    this.panel = null;
  }

  // --- Visibility (visible / hidden) -------------------------------------------

  private readDefaultVisible(): boolean {
    try {
      return window.matchMedia(WIDE_QUERY).matches;
    } catch {
      // matchMedia can be unavailable in constrained WebView profiles.
      return true;
    }
  }

  /** Shell layout seam for the compact one-drawer rule and Esc close. */
  isVisible(): boolean {
    return this.visible;
  }

  setVisible(visible: boolean): void {
    if (this.visible === visible) return;
    this.visible = visible;
    this.applyVisibility();
    this.callbacks.onVisibilityChanged?.();
  }

  closeCard(): void {
    this.setVisible(false);
  }

  focusToggle(): void {
    this.planToggle?.focus();
  }

  private applyVisibility(): void {
    // Becoming visible is the "user saw it" signal that clears the unread dot.
    if (this.visible) this.unread = false;
    if (this.planCard) this.planCard.hidden = !this.visible;
    if (this.planToggle) {
      this.planToggle.setAttribute('aria-expanded', String(this.visible));
      // The explicit aria-label overrides any inner span's label, so the
      // unread state must be part of the button's own accessible name.
      this.planToggle.setAttribute('aria-label',
        this.unread ? 'Toggle Plan card, plan updated' : 'Toggle Plan card');
    }
    if (this.planToggleUnread) this.planToggleUnread.hidden = !this.unread;
  }

  // --- Plan rendering ---------------------------------------------------------

  /** Renders the reduced plan when it actually changed; while hidden an
   * active-plan update raises the unread dot on the toolbar toggle. */
  private applyPlan(state: AgentWorkspaceState): void {
    const plan = state.inspector.plan;
    if (plan === this.lastPlanRef) return;
    this.lastPlanRef = plan;
    if (!this.visible && plan.active) this.unread = true;
    this.renderPlan(plan);
    this.applyVisibility();
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
