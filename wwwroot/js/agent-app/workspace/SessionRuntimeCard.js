import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
// SessionRuntimeCard.tsx — the React pilot component (dev-react validation).
//
// Renders the runtime install card as a faithful DOM equivalent of
// SessionRuntimeController.renderRuntime + the wwwroot/index.html template
// markup: same element structure, classes, data attributes, aria attributes,
// button labels and hidden/disabled behavior. Pure props-driven — the island
// re-renders it from the reduced state on every runtime_status event, and the
// install/cancel intents flow back through props callbacks to the existing
// bridge path.
import { useLayoutEffect } from 'react';
import { PsxButton, PsxCard } from '../ui/Psx.js';
const RUNTIME_TITLES = {
    missing: 'Agent runtime required',
    installing: 'Installing Agent runtime',
    failed: 'Agent runtime installation failed',
    cancelled: 'Agent runtime installation cancelled'
};
export function SessionRuntimeCard({ runtime, onInstall, onCancel, onCommitted }) {
    useLayoutEffect(() => {
        onCommitted();
    }, [onCommitted]);
    return (_jsxs(PsxCard, { "data-role": "runtime-card", className: "agent-runtime-card", "aria-live": "polite", "data-state": runtime.state, "aria-busy": runtime.state === 'installing', hidden: runtime.state === 'ready', children: [_jsx("div", { className: "agent-runtime-indicator", "aria-hidden": "true" }), _jsxs("div", { className: "agent-runtime-copy", children: [_jsx("h2", { "data-role": "runtime-title", children: RUNTIME_TITLES[runtime.state] || 'Agent runtime' }), _jsx("p", { "data-role": "runtime-message", children: runtime.message }), _jsx("p", { className: "agent-runtime-note", children: "Downloads pinned components from the official npm registry into this PSX folder and reuses them on later launches." })] }), _jsxs("div", { className: "agent-runtime-actions", children: [_jsx(PsxButton, { "data-role": "runtime-install", hidden: !runtime.canInstall, disabled: !runtime.canInstall, onClick: onInstall, children: runtime.state === 'missing' ? 'Install runtime' : 'Retry installation' }), _jsx(PsxButton, { "data-role": "runtime-cancel", hidden: !runtime.canCancel, disabled: !runtime.canCancel, onClick: onCancel, children: "Cancel" })] })] }));
}
