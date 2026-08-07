// AgentWorkspaceController.ts — owns a single Agent workspace.
//
// Holds the workspace state slice and coordinates the lifecycle of its domain
// feature controllers.

import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';

/** Feature controllers that throttle their render work while the workspace
 * panel is hidden (today: TimelineController). */
export interface PaneVisibilityAware {
  setVisible(visible: boolean): void;
}

function isPaneVisibilityAware(
  feature: FeatureController
): feature is FeatureController & PaneVisibilityAware {
  return typeof (feature as Partial<PaneVisibilityAware>).setVisible === 'function';
}

export class AgentWorkspaceController {
  readonly workspaceId: string;
  private state: AgentWorkspaceState;
  private readonly features: FeatureController[];
  private disposed = false;

  constructor(workspaceId: string, state: AgentWorkspaceState, features: FeatureController[]) {
    this.workspaceId = workspaceId;
    this.state = state;
    this.features = features;
  }

  mount(): void {
    for (const feature of this.features) feature.mount();
  }

  /** Panel visibility from the workspace host: forwarded to render-throttled
   * features; controllers without a visibility seam are unaffected. */
  setVisible(visible: boolean): void {
    if (this.disposed) return;
    for (const feature of this.features) {
      if (isPaneVisibilityAware(feature)) feature.setVisible(visible);
    }
  }

  // The store folds each event into a fresh immutable state; the registry hands
  // it here so feature controllers always render from the current snapshot.
  update(event: AgentWorkspaceEvent, state?: AgentWorkspaceState): void {
    if (this.disposed) return;
    if (state) this.state = state;
    for (const feature of this.features) feature.update(event, this.state);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    for (const feature of this.features) feature.dispose();
  }
}
