import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { planStatusClass, planStatusLabel, planStatusMarker } from '../core/plan.js';
export function PlanCard({ plan }) {
    if (!plan.active) {
        return _jsx("div", { className: "agent-plan-empty", children: "No active plan" });
    }
    if (plan.entries.length === 0) {
        return _jsx("pre", { className: "agent-plan-fallback", children: plan.fallbackText || 'No plan items.' });
    }
    return (_jsx("ol", { className: "agent-plan-list", children: plan.entries.map((entry, index) => (_jsxs("li", { className: 'agent-plan-item ' + planStatusClass(entry.status), "aria-label": planStatusLabel(entry.status) + ': ' + entry.content, "data-priority": entry.priority || undefined, children: [_jsx("span", { className: "agent-plan-marker", children: planStatusMarker(entry.status) }), _jsx("span", { className: "agent-plan-content", children: entry.content })] }, index))) }));
}
