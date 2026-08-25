// copy.ts — render-time copy resolution for PSX-authored timeline text.
//
// The projection must never store a translated sentence: language switches
// have to re-localize every visible row in place. PSX-generated copy is kept
// as { code, params } pairs and resolved through i18next only inside React
// render. Raw strings stay raw — they are user content, provider payloads or
// technical details, never PSX UI copy.

export interface LocalizedText {
    code: string;
    params?: Record<string, unknown>;
}

export type DisplayText = string | LocalizedText;

/** Build a localized placeholder for PSX-authored copy. */
export function loc(code: string, params?: Record<string, unknown>): LocalizedText {
    return { code, params };
}

type Translate = (key: string, options?: Record<string, unknown>) => string;

/** Resolve a display value at render time; returns '' when unresolvable. */
export function resolveDisplay(value: DisplayText | undefined, t: Translate): string {
    if (!value) return '';
    if (typeof value === 'string') return value;
    const localized = t(value.code, { defaultValue: '', ...(value.params ?? {}) });
    return localized || '';
}

/** Resolve with a localized fallback applied when the primary resolves ''. */
export function resolveDisplayOr(
    value: DisplayText | undefined,
    t: Translate,
    fallbackKey: string
): string {
    const resolved = resolveDisplay(value, t);
    if (resolved) return resolved;
    return t(fallbackKey, { defaultValue: '' }) || '';
}

/** Normalize a backend `args` payload into interpolation params. */
export function asParams(value: unknown): Record<string, unknown> | undefined {
    return value && typeof value === 'object' && !Array.isArray(value)
        ? (value as Record<string, unknown>)
        : undefined;
}
