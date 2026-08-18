// timelineIsland.ts — mounts the React Timeline tree (Station 3).
//
// One root owning the whole thread subtree ([data-role="thread"] children).
// Not yet wired into TimelineController: per the migration plan the React
// Timeline path stays unrouted until the tree is feature-complete, then a
// single commit flips react-mode routing over (no mixed ownership ever).

import { createElement } from 'react';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { TimelineView, type TimelineViewProps } from './TimelineView.js';

export function mountTimelineIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<TimelineViewProps> {
  return mountReactIsland('timeline', host, reportFailure, (props) =>
    createElement(TimelineView, props)
  );
}
