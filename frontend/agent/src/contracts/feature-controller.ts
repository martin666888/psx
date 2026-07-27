// feature-controller.ts — lifecycle contract for a workspace feature domain.
//
// Every controller owns a state slice, a DOM region, its user-input
// listeners, and its own disposables — mount/update/dispose bound that scope.

import type { AgentWorkspaceEvent } from './host-events.js';
import type { AgentWorkspaceState } from './workspace-state.js';

export interface FeatureController {
  /** Wire up DOM listeners and initial render for the owned region. */
  mount(): void;
  /** Apply a decoded workspace event and the current state snapshot. */
  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void;
  /** Remove listeners, timers, RAFs, object URLs, and body classes. */
  dispose(): void;
}
