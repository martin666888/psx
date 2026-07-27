// runtimeIsland.ts — thin mount/unmount wrapper around the React runtime-card
// island. This is the ONLY module that statically imports React: the
// controller reaches it exclusively through a dynamic import guarded by the
// experimental flag, so a flag-off session never loads any vendor/react code.
import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { SessionRuntimeCard } from './SessionRuntimeCard.js';
export function mountRuntimeIsland(host, handlers) {
    const root = createRoot(host);
    let disposed = false;
    return {
        render(runtime) {
            if (disposed)
                return;
            root.render(createElement(SessionRuntimeCard, {
                runtime,
                onInstall: handlers.onInstall,
                onCancel: handlers.onCancel,
                onCommitted: handlers.onCommitted
            }));
        },
        dispose() {
            if (disposed)
                return;
            disposed = true;
            root.unmount();
            host.remove();
        }
    };
}
