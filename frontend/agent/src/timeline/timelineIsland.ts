// timelineIsland.ts — mounts the React Timeline tree (Station 3).
//
// One root owning the whole thread subtree ([data-role="thread"] children).
// Not yet wired into TimelineController: per the migration plan the React
// Timeline path stays unrouted until the tree is feature-complete, then a
// single commit flips react-mode routing over (no mixed ownership ever).

import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { TimelineView, type TimelineViewProps } from './TimelineView.js';

export interface TimelineIslandHandle {
  render(props: TimelineViewProps): void;
  dispose(): void;
}

export function mountTimelineIsland(host: HTMLElement): TimelineIslandHandle {
  const root = createRoot(host);
  return {
    render(props: TimelineViewProps): void {
      root.render(createElement(TimelineView, props));
    },
    dispose(): void {
      root.unmount();
    }
  };
}
