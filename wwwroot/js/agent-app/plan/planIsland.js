// planIsland.ts — mounts the React plan panel content.
//
// Static React imports are confined to island modules; PlanController reaches
// this file only through a dynamic import so the legacy mode never loads
// React. The host is the template-owned plan panel itself: createRoot takes
// ownership of its children (React clears the legacy-rendered content on the
// first commit) and dispose unmounts but leaves the panel node in place —
// the workspace panel lifecycle owns the node.
import { createElement } from 'react';
import { mountReactIsland } from '../core/reactIsland.js';
import { PlanCard } from './PlanCard.js';
export function mountPlanIsland(host, reportFailure) {
    return mountReactIsland('plan-card', host, reportFailure, (plan) => createElement(PlanCard, { plan }));
}
