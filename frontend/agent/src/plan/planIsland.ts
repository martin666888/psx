// planIsland.ts — mounts the React plan panel content.
//
// Static React imports are confined to island modules; PlanController reaches
// this file only through a dynamic import so the legacy mode never loads
// React. The host is the template-owned plan panel itself: createRoot takes
// ownership of its children (React clears the legacy-rendered content on the
// first commit) and dispose unmounts but leaves the panel node in place —
// the workspace panel lifecycle owns the node.

import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import type { WorkspacePlanState } from '../contracts/workspace-state.js';
import { PlanCard } from './PlanCard.js';

export interface PlanIslandHandle {
  render(plan: WorkspacePlanState): void;
  dispose(): void;
}

export function mountPlanIsland(host: HTMLElement): PlanIslandHandle {
  const root = createRoot(host);
  return {
    render(plan: WorkspacePlanState): void {
      root.render(createElement(PlanCard, { plan }));
    },
    dispose(): void {
      root.unmount();
    }
  };
}
