// plan.ts — pure plan-entry normalization (no DOM).
//
// Faithful port of the pure helpers in wwwroot/js/agent/plan.js
// (_normalizePlanEntries, _isRawPlanPayload, _readRawPlanEntries and the plan
// status class/marker/label mappers) used by the React Plan presentation.

export interface PlanEntry {
  content: string;
  status: string;
  priority: string;
}

const IN_PROGRESS_STATES = ['in_progress', 'in-progress', 'running', 'current'];

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object';
}

export function normalizePlanEntries(entries: unknown): PlanEntry[] {
  const list = Array.isArray(entries) ? entries : [];
  const result: PlanEntry[] = [];
  for (const raw of list) {
    if (!isRecord(raw)) continue;
    if (typeof raw.content !== 'string' || !raw.content.trim()) continue;
    result.push({
      content: raw.content.trim(),
      status: String(raw.status ?? '').trim(),
      priority: String(raw.priority ?? '').trim()
    });
  }
  return result;
}

export function isRawPlanPayload(text: unknown): boolean {
  const value = String(text ?? '').trim();
  if (!value.startsWith('{') || !value.endsWith('}')) return false;
  try {
    const payload = JSON.parse(value);
    return !!payload && payload.sessionUpdate === 'plan' && Array.isArray(payload.entries);
  } catch {
    return false;
  }
}

export function readRawPlanEntries(text: unknown): PlanEntry[] {
  const value = String(text ?? '').trim();
  if (!value.startsWith('{') || !value.endsWith('}')) return [];
  try {
    const payload = JSON.parse(value);
    if (!payload || payload.sessionUpdate !== 'plan' || !Array.isArray(payload.entries)) {
      return [];
    }
    return normalizePlanEntries(
      payload.entries.map((entry: Record<string, unknown> | null | undefined) => ({
        content: entry?.content || entry?.title || '',
        status: entry?.status || '',
        priority: entry?.priority || ''
      }))
    );
  } catch {
    return [];
  }
}

/** Resolves the effective entries the panel should render for a plan_update. */
export function resolvePlanEntries(rawEntries: unknown, text: unknown): PlanEntry[] {
  const normalized = normalizePlanEntries(rawEntries);
  return normalized.length > 0 ? normalized : readRawPlanEntries(text);
}

export function planStatusClass(status: unknown): string {
  const value = String(status ?? '').toLowerCase();
  if (value === 'completed') return 'agent-plan-item-completed';
  if (IN_PROGRESS_STATES.includes(value)) return 'agent-plan-item-in-progress';
  return 'agent-plan-item-pending';
}

export function planStatusMarker(status: unknown): string {
  const value = String(status ?? '').toLowerCase();
  if (value === 'completed') return '\u2713';
  if (IN_PROGRESS_STATES.includes(value)) return '\u25c9';
  return '\u25cb';
}

/** Fixed locale keys under plan.status.*; resolved at render. */
export function planStatusLabel(status: unknown): string {
  const value = String(status ?? '').toLowerCase();
  if (value === 'completed') return 'plan.status.completed';
  if (IN_PROGRESS_STATES.includes(value)) return 'plan.status.current';
  return 'plan.status.pending';
}
