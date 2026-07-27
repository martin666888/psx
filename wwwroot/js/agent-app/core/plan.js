// plan.ts — pure plan-entry normalization (no DOM).
//
// Faithful port of the pure helpers in wwwroot/js/agent/plan.js
// (_normalizePlanEntries, _isRawPlanPayload, _readRawPlanEntries and the plan
// status class/marker/label mappers) used by the React Plan presentation.
const IN_PROGRESS_STATES = ['in_progress', 'in-progress', 'running', 'current'];
function isRecord(value) {
    return !!value && typeof value === 'object';
}
export function normalizePlanEntries(entries) {
    const list = Array.isArray(entries) ? entries : [];
    const result = [];
    for (const raw of list) {
        if (!isRecord(raw))
            continue;
        if (typeof raw.content !== 'string' || !raw.content.trim())
            continue;
        result.push({
            content: raw.content.trim(),
            status: String(raw.status ?? '').trim(),
            priority: String(raw.priority ?? '').trim()
        });
    }
    return result;
}
export function isRawPlanPayload(text) {
    const value = String(text ?? '').trim();
    if (!value.startsWith('{') || !value.endsWith('}'))
        return false;
    try {
        const payload = JSON.parse(value);
        return !!payload && payload.sessionUpdate === 'plan' && Array.isArray(payload.entries);
    }
    catch {
        return false;
    }
}
export function readRawPlanEntries(text) {
    const value = String(text ?? '').trim();
    if (!value.startsWith('{') || !value.endsWith('}'))
        return [];
    try {
        const payload = JSON.parse(value);
        if (!payload || payload.sessionUpdate !== 'plan' || !Array.isArray(payload.entries)) {
            return [];
        }
        return normalizePlanEntries(payload.entries.map((entry) => ({
            content: entry?.content || entry?.title || '',
            status: entry?.status || '',
            priority: entry?.priority || ''
        })));
    }
    catch {
        return [];
    }
}
/** Resolves the effective entries the panel should render for a plan_update. */
export function resolvePlanEntries(rawEntries, text) {
    const normalized = normalizePlanEntries(rawEntries);
    return normalized.length > 0 ? normalized : readRawPlanEntries(text);
}
export function planStatusClass(status) {
    const value = String(status ?? '').toLowerCase();
    if (value === 'completed')
        return 'agent-plan-item-completed';
    if (IN_PROGRESS_STATES.includes(value))
        return 'agent-plan-item-in-progress';
    return 'agent-plan-item-pending';
}
export function planStatusMarker(status) {
    const value = String(status ?? '').toLowerCase();
    if (value === 'completed')
        return '\u2713';
    if (IN_PROGRESS_STATES.includes(value))
        return '\u25c9';
    return '\u25cb';
}
export function planStatusLabel(status) {
    const value = String(status ?? '').toLowerCase();
    if (value === 'completed')
        return 'Completed';
    if (IN_PROGRESS_STATES.includes(value))
        return 'Current';
    return 'Pending';
}
