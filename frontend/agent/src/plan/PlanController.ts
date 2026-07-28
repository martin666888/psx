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
// that is never persisted. The preference defaults to visible; Shell applies a
// temporary narrow override so an empty card never covers the conversation
// unprompted. The user hides/shows it through the toolbar
// toggle. While hidden, an arriving active plan raises the unread dot on the
// toggle instead of interrupting; showing the card clears it. The width is a
// fixed CSS token; there is no resizer.

import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type {
  AgentWorkspaceState,
  WorkspacePlanState
} from '../contracts/workspace-state.js';
import { createIslandLoader, type IslandLoader } from '../core/islandHost.js';
import type { WorkspaceToolbarController } from '../workspace/WorkspaceToolbarController.js';

/** The seam the controller needs from the workspace host. */
export interface PlanHost {
  getPanel(workspaceId: string): HTMLElement | null;
}

/** Shell layout seam: the card reports visibility changes so narrow-mode
 * mutual exclusion can close History (and vice versa). */
export interface PlanControllerCallbacks {
  onVisibilityChanged?: () => void;
}

const EMPTY_PLAN: WorkspacePlanState = { active: false, runId: '', entries: [], fallbackText: '' };

function role(panel: HTMLElement, name: string): HTMLElement | null {
  return panel.querySelector<HTMLElement>('[data-role="' + name + '"]');
}

export class PlanController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: PlanHost;
  private readonly callbacks: PlanControllerCallbacks;
  private readonly toolbar: WorkspaceToolbarController;

  private panel: HTMLElement | null = null;
  private planCard: HTMLElement | null = null;

  // preferredVisible is workspace-local user intent. narrowOverride is a
  // temporary responsive value: null means wide mode, boolean means narrow.
  private preferredVisible = true;
  private narrowOverride: boolean | null = null;
  private unread = false;
  private lastPlanRef: WorkspacePlanState = EMPTY_PLAN;

  // React owns every plan-card child (the island renders the whole card
  // shell). Visibility and responsive state stay with this controller because
  // they affect the surrounding shell.
  private planIsland: IslandLoader<WorkspacePlanState> | null = null;

  constructor(
    workspaceId: string,
    host: PlanHost,
    callbacks: PlanControllerCallbacks | undefined,
    toolbar: WorkspaceToolbarController
  ) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.callbacks = callbacks ?? {};
    this.toolbar = toolbar;
  }

  mount(): void {
    const panel = this.host.getPanel(this.workspaceId);
    if (!panel) return;
    this.panel = panel;
    this.planCard = role(panel, 'plan-card');

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
    this.planIsland?.dispose();
    this.planIsland = null;
    this.panel = null;
  }

  // --- Visibility (visible / hidden) -------------------------------------------

  /** Shell layout seam for the narrow mutual-exclusion rule and Esc close. */
  isVisible(): boolean {
    return this.effectiveVisible();
  }

  /** User or Shell request: wide changes the workspace preference, narrow
   * changes only the temporary responsive override. */
  requestVisible(visible: boolean): void {
    if (this.narrowOverride === null) this.setPreferredVisible(visible);
    else this.setNarrowOverride(visible);
  }

  closeCard(): void {
    this.requestVisible(false);
  }

  /** Registry fans Shell's one responsive mode out to every workspace. */
  applyNarrow(narrow: boolean): void {
    if (narrow) {
      // A repeated broadcast must preserve a user's temporary choice.
      if (this.narrowOverride !== null) return;
      this.setNarrowOverride(false);
      return;
    }
    if (this.narrowOverride === null) return;
    this.setNarrowOverride(null);
  }

  focusToggle(): void {
    this.toolbar.focusPlanToggle();
  }

  private applyVisibility(): void {
    // Becoming visible is the "user saw it" signal that clears the unread dot.
    const visible = this.effectiveVisible();
    if (visible) this.unread = false;
    if (this.planCard) this.planCard.hidden = !visible;
    // The toolbar renders aria-expanded/aria-label/the unread dot from this
    // single push; no DOM toggle lookup remains here.
    this.toolbar.setPlanState(visible, this.unread);
  }

  // --- Plan rendering ---------------------------------------------------------

  /** Renders the reduced plan when it actually changed; while hidden an
   * active-plan update raises the unread dot on the toolbar toggle. */
  private applyPlan(state: AgentWorkspaceState): void {
    const plan = state.inspector.plan;
    if (plan === this.lastPlanRef) return;
    this.lastPlanRef = plan;
    if (!this.effectiveVisible() && plan.active) this.unread = true;
    this.renderPlan(plan);
    this.applyVisibility();
  }

  private renderPlan(plan: WorkspacePlanState): void {
    const host = this.planCard;
    if (!host) return;
    this.planIsland ??= createIslandLoader<WorkspacePlanState>({
      name: 'plan-card',
      load: async () => {
        const mod = await import('./planIsland.js');
        return (islandHost, reportFailure) =>
          mod.mountPlanIsland(islandHost, reportFailure);
      },
      host
    });
    this.planIsland.render(plan);
  }

  private effectiveVisible(): boolean {
    return this.narrowOverride ?? this.preferredVisible;
  }

  private setPreferredVisible(visible: boolean): void {
    if (this.preferredVisible === visible) return;
    const previous = this.effectiveVisible();
    this.preferredVisible = visible;
    this.syncEffectiveVisibility(previous);
  }

  private setNarrowOverride(visible: boolean | null): void {
    if (this.narrowOverride === visible) return;
    const previous = this.effectiveVisible();
    this.narrowOverride = visible;
    this.syncEffectiveVisibility(previous);
  }

  private syncEffectiveVisibility(previous: boolean): void {
    this.applyVisibility();
    if (this.effectiveVisible() !== previous) this.callbacks.onVisibilityChanged?.();
  }
}
