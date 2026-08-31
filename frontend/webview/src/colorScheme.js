// colorScheme.js — map a PSX theme background to the CSS color-scheme
// the shell (and the Kimi Web iframe) should advertise. Agent UI also
// uses this so native controls and prefers-color-scheme stay in lockstep
// before the lazy Agent chunk loads.

/** @param {unknown} background Theme `#rgb` / `#rrggbb` background.
 *  @returns {'dark' | 'light' | null} */
export function colorSchemeForBackground(background) {
    if (typeof background !== 'string') return null;
    const match = background.trim().match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
    if (!match) return null;
    let hex = match[1];
    if (hex.length === 3) hex = hex.split('').map((c) => c + c).join('');
    const r = parseInt(hex.slice(0, 2), 16);
    const g = parseInt(hex.slice(2, 4), 16);
    const b = parseInt(hex.slice(4, 6), 16);
    const luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
    return luminance < 0.5 ? 'dark' : 'light';
}
