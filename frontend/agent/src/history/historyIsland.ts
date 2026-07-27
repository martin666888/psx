// historyIsland.ts — mounts the React history-dock content.
//
// Static React imports are confined to island modules; HistoryDockController
// reaches this file only through a dynamic import so the legacy mode never
// loads React. The host is the dock-owned content element: createRoot takes
// ownership of its children and dispose unmounts before the dock node is
// removed by the controller.

import { createElement } from 'react';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { HistoryList, type HistoryListProps } from './HistoryList.js';

export function mountHistoryIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<HistoryListProps> {
  return mountReactIsland('history-list', host, reportFailure, (props) =>
    createElement(HistoryList, props)
  );
}
