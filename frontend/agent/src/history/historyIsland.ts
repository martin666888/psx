// historyIsland.ts — mounts the React history dock (chrome + list).
//
// Static React imports are confined to island modules; HistoryDockController
// reaches this file only through a dynamic import so the terminal-only
// startup path never loads React. The host is the controller-owned dock host
// element (display:contents): the portal takes ownership of its children and
// dispose unmounts before the host node is removed by the controller.

import { createElement } from 'react';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { HistoryDockView, type HistoryDockViewProps } from './HistoryDockView.js';

export function mountHistoryIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<HistoryDockViewProps> {
  return mountReactIsland('history-dock', host, reportFailure, (props) =>
    createElement(HistoryDockView, props)
  );
}
