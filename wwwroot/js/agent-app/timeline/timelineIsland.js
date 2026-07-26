// timelineIsland.ts — mounts the React Timeline tree (Station 3).
//
// One root owning the whole thread subtree ([data-role="thread"] children).
// Not yet wired into TimelineController: per the migration plan the React
// Timeline path stays unrouted until the tree is feature-complete, then a
// single commit flips react-mode routing over (no mixed ownership ever).
import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { TimelineView } from './TimelineView.js';
export function mountTimelineIsland(host) {
    const root = createRoot(host);
    return {
        render(props) {
            root.render(createElement(TimelineView, props));
        },
        dispose() {
            root.unmount();
        }
    };
}
