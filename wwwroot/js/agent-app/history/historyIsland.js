// historyIsland.ts — mounts the React history-dock content.
//
// Static React imports are confined to island modules; HistoryDockController
// reaches this file only through a dynamic import so the legacy mode never
// loads React. The host is the dock-owned content element: createRoot takes
// ownership of its children and dispose unmounts before the dock node is
// removed by the controller.
import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { HistoryList } from './HistoryList.js';
export function mountHistoryIsland(host) {
    const root = createRoot(host);
    return {
        render(props) {
            root.render(createElement(HistoryList, props));
        },
        dispose() {
            root.unmount();
        }
    };
}
