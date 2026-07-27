// sessionIsland.ts — mounts the React session toolbar islands.
//
// Two independent roots: the toolbar meta line (host: .agent-meta) and the
// Context usage ring (host: .agent-hints). Static React imports are confined
// to island modules; SessionRuntimeController reaches this file only through
// a dynamic import. createRoot takes ownership of each host's children — the
// template-rendered defaults are replaced atomically by the first commit —
// and dispose unmounts while leaving the host nodes to the panel lifecycle.
import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { ContextUsage, SessionMeta } from './SessionToolbar.js';
export function mountSessionMetaIsland(host) {
    const root = createRoot(host);
    return {
        render(props) {
            root.render(createElement(SessionMeta, props));
        },
        dispose() {
            root.unmount();
        }
    };
}
export function mountContextUsageIsland(host) {
    const root = createRoot(host);
    return {
        render(props) {
            root.render(createElement(ContextUsage, props));
        },
        dispose() {
            root.unmount();
        }
    };
}
