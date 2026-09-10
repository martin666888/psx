// shellTheme.js — project the optional [shellTheme] appearance payload onto
// the shell-chrome CSS custom properties (defaults in css/agent/tokens.css).
// The payload reaches the WebView through two independent paths — the
// always-loaded shell (main.js applyChromeAppearance) and the Agent chunk
// (WorkspaceHost.applyGlobalAppearance) — so both route through this one
// function to stay identical and idempotent. Every field is nullable: a set
// field writes its variable, a null/absent field removes it so the
// tokens.css default (the pre-shellTheme visual) applies again.
import { colorSchemeForBackground } from './colorScheme.js';

// Design-system ink pair (design-tokens.md §4): light plates take the light
// theme's text ink, dark plates the dark theme's foreground.
const LIGHT_PLATE_INK = '#26292e';
const DARK_PLATE_INK = '#fafafa';

/** @param {unknown} value @returns {string} */
function text(value) {
    return typeof value === 'string' && value.trim() ? value.trim() : '';
}

/** @param {HTMLElement} root @param {string} name @param {unknown} value */
function applyVar(root, name, value) {
    const v = text(value);
    if (v) root.style.setProperty(name, v);
    else root.style.removeProperty(name);
}

/**
 * Apply (or clear) the shell chrome variables on `root`. A missing shellTheme
 * object clears every variable, so stale values from a previous theme never
 * survive a theme switch.
 *
 * @param {HTMLElement} root
 * @param {unknown} shellTheme the `shellTheme` object of a settings /
 *   appearance_settings payload (all fields optional).
 */
export function applyShellTheme(root, shellTheme) {
    const theme = shellTheme && typeof shellTheme === 'object'
        ? /** @type {Record<string, unknown>} */ (shellTheme)
        : {};

    // The sidebar plate: a from+to gradient overrides a plain color; a lone
    // gradient endpoint is ignored in favor of the plain color. The gradient
    // is painted viewport-anchored (background-attachment: fixed in CSS) so
    // the rail and the History dock share one continuous wash.
    const gradientFrom = text(theme.sidebarGradientFrom);
    const gradientTo = text(theme.sidebarGradientTo);
    let sidebar = text(theme.sidebar);
    if (gradientFrom && gradientTo) {
        sidebar = `linear-gradient(180deg, ${gradientFrom}, ${gradientTo})`;
    }
    applyVar(root, '--agent-shell-sidebar', sidebar);
    applyVar(root, '--agent-shell-sidebar-selected', theme.sidebarSelected);
    applyVar(root, '--agent-shell-sidebar-input', theme.sidebarInput);
    applyVar(root, '--agent-shell-sidebar-input-border', theme.sidebarInputBorder);
    applyVar(root, '--agent-shell-chrome', theme.chrome);
    applyVar(root, '--agent-shell-workspace', theme.workspace);
    applyVar(root, '--agent-shell-tab-active', theme.tabActive);

    // A terminal-column active tab may flip to its own plate (e.g. a light
    // plate on a dark theme); derive a readable ink for it so title and icon
    // stay legible. Cleared together with the plate.
    const tabActiveTerminal = text(theme.tabActiveTerminal);
    if (tabActiveTerminal) {
        root.style.setProperty('--agent-shell-tab-active-terminal', tabActiveTerminal);
        root.style.setProperty(
            '--agent-shell-tab-active-terminal-ink',
            colorSchemeForBackground(tabActiveTerminal) === 'light' ? LIGHT_PLATE_INK : DARK_PLATE_INK
        );
    } else {
        root.style.removeProperty('--agent-shell-tab-active-terminal');
        root.style.removeProperty('--agent-shell-tab-active-terminal-ink');
    }

    applyVar(root, '--agent-shell-composer-bg', theme.composerBg);
    applyVar(root, '--agent-shell-composer-border', theme.composerBorder);
    // composerShadow carries only the shadow COLOR; offsets/blur/spread are
    // composed in the .agent-composer-card box-shadow rule.
    applyVar(root, '--agent-shell-composer-shadow', theme.composerShadow);
    applyVar(root, '--agent-shell-terminal-bg', theme.terminalBackground);
}
