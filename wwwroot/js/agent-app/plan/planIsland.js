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
import { PlanCard } from './PlanCard.js';
export function mountPlanIsland(host) {
    const root = createRoot(host);
    return {
        render(plan) {
            root.render(createElement(PlanCard, { plan }));
        },
        dispose() {
            root.unmount();
        }
    };
}
