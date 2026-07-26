// historyIsland.ts — mounts the React history-dock content.
//
// Static React imports are confined to island modules; HistoryDockController
// reaches this file only through a dynamic import so the legacy mode never
// loads React. The host is the dock-owned content element: createRoot takes
// ownership of its children and dispose unmounts before the dock node is
// removed by the controller.

import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { HistoryList, type HistoryListProps } from './HistoryList.js';

export interface HistoryIslandHandle {
  render(props: HistoryListProps): void;
  dispose(): void;
}

export function mountHistoryIsland(host: HTMLElement): HistoryIslandHandle {
  const root = createRoot(host);
  return {
    render(props: HistoryListProps): void {
      root.render(createElement(HistoryList, props));
    },
    dispose(): void {
      root.unmount();
    }
  };
}
