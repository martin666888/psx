// historyModel.ts — pure view-model helpers for the global History dock.
//
// Groups the flat AgentHistoryThread list by normalized working directory
// (Windows rules: '/' and '\' are equivalent, comparison ignores case, and
// trailing separators are stripped without ever eating a drive or UNC root),
// maps provider keys to display names through the global catalog, and applies
// the search box / provider filter. No DOM here — HistoryDockController
// renders whatever these functions return.
export const UNKNOWN_WORKSPACE_GROUP = 'Unknown workspace';
/** Windows-aware cwd grouping key: unified separators, case-insensitive, no
 * trailing separator unless the path IS a root (C:\ or \\server\share). */
export function normalizeCwdKey(cwd) {
    let value = String(cwd || '').trim().replace(/\//g, '\\');
    if (!value)
        return '';
    while (value.length > 1 && value.endsWith('\\')) {
        const stripped = value.slice(0, -1);
        if (/^[A-Za-z]:$/.test(stripped))
            break; // C:\ is already the drive root
        if (/^\\\\[^\\]+$/.test(stripped))
            break; // \\server\ keeps its separator
        value = stripped;
        if (/^\\\\[^\\]+\\[^\\]+$/.test(value))
            break; // \\server\share is the UNC root
    }
    return value.toLowerCase();
}
/** Display name for a group: the last segment of the original path. */
export function historyGroupName(displayPath) {
    const trimmed = String(displayPath || '').trim().replace(/[\\/]+$/, '');
    if (!trimmed)
        return UNKNOWN_WORKSPACE_GROUP;
    const segments = trimmed.split(/[\\/]/).filter((segment) => segment.length > 0);
    return segments.length > 0 ? segments[segments.length - 1] : trimmed;
}
/** Provider key → display name via the catalog; unknown keys stay raw. */
export function providerDisplayName(providers, key) {
    const match = providers.find((provider) => provider.key === key);
    return match && match.displayName ? match.displayName : key;
}
/** Groups threads by normalized cwd. Groups sort by latest activity desc,
 * threads inside a group sort by updatedAt desc (the C# "u" timestamp format
 * is lexicographically sortable), and the group's display path comes from its
 * most recent thread. */
export function buildHistoryGroups(threads, providers) {
    const byKey = new Map();
    for (const thread of threads) {
        const key = normalizeCwdKey(thread.cwd);
        const bucket = byKey.get(key);
        if (bucket)
            bucket.push(thread);
        else
            byKey.set(key, [thread]);
    }
    const groups = [];
    for (const [key, bucket] of byKey) {
        const sorted = bucket.slice().sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
        const displayPath = sorted[0] ? sorted[0].cwd.trim() : '';
        groups.push({
            key,
            name: key ? historyGroupName(displayPath) : UNKNOWN_WORKSPACE_GROUP,
            path: displayPath,
            threads: sorted.map((thread) => ({
                ...thread,
                providerDisplay: providerDisplayName(providers, thread.providerKey)
            }))
        });
    }
    groups.sort((a, b) => {
        const aLatest = a.threads[0]?.updatedAt ?? '';
        const bLatest = b.threads[0]?.updatedAt ?? '';
        return bLatest.localeCompare(aLatest);
    });
    return groups;
}
/** Applies the search box (title / path / provider name or key) and the
 * provider filter dropdown; groups left empty drop out. */
export function filterHistoryGroups(groups, query, providerKey) {
    const needle = String(query || '').trim().toLowerCase();
    return groups
        .map((group) => {
        const threads = group.threads.filter((thread) => {
            if (providerKey && thread.providerKey !== providerKey)
                return false;
            if (!needle)
                return true;
            return (thread.title.toLowerCase().includes(needle) ||
                group.path.toLowerCase().includes(needle) ||
                thread.providerDisplay.toLowerCase().includes(needle) ||
                thread.providerKey.toLowerCase().includes(needle));
        });
        return { ...group, threads };
    })
        .filter((group) => group.threads.length > 0);
}
/** Provider filter options: the catalog first (display names), then any
 * unknown keys that actually appear in the data (raw key as label). */
export function providerFilterOptions(threads, providers) {
    const options = [];
    const seen = new Set();
    for (const provider of providers) {
        if (!provider.key || seen.has(provider.key))
            continue;
        seen.add(provider.key);
        options.push({ value: provider.key, label: provider.displayName || provider.key });
    }
    for (const thread of threads) {
        if (!thread.providerKey || seen.has(thread.providerKey))
            continue;
        seen.add(thread.providerKey);
        options.push({ value: thread.providerKey, label: thread.providerKey });
    }
    return options;
}
/** Formats a C# "u"-format UTC timestamp ("2026-07-21 14:28:28Z") for the
 * history row meta: local HH:mm for same-day, MM-dd within the same year,
 * yyyy-MM-dd otherwise. Unparseable input comes back as-is ('' stays ''). */
export function formatHistoryTime(updatedAt, now = new Date()) {
    const raw = String(updatedAt || '').trim();
    if (!raw)
        return '';
    const parsed = new Date(raw.replace(' ', 'T'));
    if (Number.isNaN(parsed.getTime()))
        return raw;
    const pad = (value) => String(value).padStart(2, '0');
    const sameDay = parsed.getFullYear() === now.getFullYear() &&
        parsed.getMonth() === now.getMonth() &&
        parsed.getDate() === now.getDate();
    if (sameDay)
        return pad(parsed.getHours()) + ':' + pad(parsed.getMinutes());
    if (parsed.getFullYear() === now.getFullYear()) {
        return pad(parsed.getMonth() + 1) + '-' + pad(parsed.getDate());
    }
    return parsed.getFullYear() + '-' + pad(parsed.getMonth() + 1) + '-' + pad(parsed.getDate());
}
