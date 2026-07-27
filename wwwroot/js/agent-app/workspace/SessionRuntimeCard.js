import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { PsxButton, PsxCard } from '../ui/Psx.js';
const RUNTIME_TITLES = {
    missing: 'Agent runtime required',
    installing: 'Installing Agent runtime',
    failed: 'Agent runtime installation failed',
    cancelled: 'Agent runtime installation cancelled'
};
export function SessionRuntimeCard({ runtime, onInstall, onCancel }) {
    return (_jsxs(PsxCard, { "data-role": "runtime-card", className: "agent-runtime-card", "aria-live": "polite", "data-state": runtime.state, "aria-busy": runtime.state === 'installing', hidden: runtime.state === 'ready', children: [_jsx("div", { className: "agent-runtime-indicator", "aria-hidden": "true" }), _jsxs("div", { className: "agent-runtime-copy", children: [_jsx("h2", { "data-role": "runtime-title", children: RUNTIME_TITLES[runtime.state] || 'Agent runtime' }), _jsx("p", { "data-role": "runtime-message", children: runtime.message }), _jsx("p", { className: "agent-runtime-note", children: "Downloads pinned components from the official npm registry into this PSX folder and reuses them on later launches." })] }), _jsxs("div", { className: "agent-runtime-actions", children: [_jsx(PsxButton, { "data-role": "runtime-install", hidden: !runtime.canInstall, disabled: !runtime.canInstall, onClick: onInstall, children: runtime.state === 'missing' ? 'Install runtime' : 'Retry installation' }), _jsx(PsxButton, { "data-role": "runtime-cancel", hidden: !runtime.canCancel, disabled: !runtime.canCancel, onClick: onCancel, children: "Cancel" })] })] }));
}
