// AgentWorkspaceController.ts — owns a single Agent workspace.
//
// Phase 2 delegates every content event to the LegacyAgentAdapter, which drives
// the legacy AgentThreadManager for this workspace. The controller holds the
// workspace's state slice and its FeatureController set (a single legacy
// feature today; Phase 4 splits it into Session/Inspector/Composer/Decision/
// Timeline controllers).

import type { AgentWorkspaceEvent } from '../contracts/host-events.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';

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
