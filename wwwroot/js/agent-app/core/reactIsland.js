import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
// reactIsland.tsx — shared mounting tool for independent React roots.
//
// Every call creates its own root. Sharing this helper standardizes commit
// diagnostics and failure containment without coupling island lifecycles.
import { Component, useLayoutEffect } from 'react';
import { createRoot } from 'react-dom/client';
class IslandErrorBoundary extends Component {
    state = { failed: false };
    static getDerivedStateFromError() {
        return { failed: true };
    }
    componentDidCatch(error, _info) {
        this.props.reportFailure(error, 'render');
    }
    render() {
        if (this.state.failed)
            return null;
        return this.props.children;
    }
}
function CommitMarker({ host }) {
    useLayoutEffect(() => {
        host.dataset.islandState = 'mounted';
        host.removeAttribute('aria-busy');
    });
    return null;
}
function IslandFrame(props) {
    return (_jsxs(IslandErrorBoundary, { host: props.host, reportFailure: props.reportFailure, children: [_jsx(CommitMarker, { host: props.host }), props.children] }));
}
export function mountReactIsland(name, host, reportFailure, renderElement) {
    let disposed = false;
    let root;
    root = createRoot(host, {
        onUncaughtError(error) {
            reportFailure(error, 'render');
        },
        onRecoverableError(error) {
            console.warn('[agent] React island "' + name + '" recovered from an error.', error);
        }
    });
    return {
        render(props) {
            if (disposed)
                return;
            root.render(_jsx(IslandFrame, { host: host, reportFailure: reportFailure, children: renderElement(props) }));
        },
        dispose() {
            if (disposed)
                return;
            disposed = true;
            root.unmount();
        }
    };
}
