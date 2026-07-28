// planIsland.ts — mounts the React plan card.
//
// Static React imports are confined to island modules; PlanController reaches
// this file only through a dynamic import so the legacy mode never loads
// React. The host is the template-owned plan-card section itself: the island
// loader clears its children on mount (dropping the legacy static header and
// panel markup while the template still carries it) and React renders the
// whole card shell. Dispose unmounts but leaves the section node in place —
// the workspace panel lifecycle owns the node.

import { createElement } from 'react';
import type { WorkspacePlanState } from '../contracts/workspace-state.js';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { PlanCard } from './PlanCard.js';

export function mountPlanIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<WorkspacePlanState> {
  return mountReactIsland('plan-card', host, reportFailure, (plan) =>
    createElement(PlanCard, { plan })
  );
}
