// flags.ts — UI mode switch for the React migration (dev-react branch).
//
// React rendering is the DEFAULT on this branch. The localStorage key below
// is an emergency fallback: set it to '0' / 'false' / 'off' to force the
// legacy renderers for a session. Reads are wrapped in try/catch because
// localStorage can throw in restricted WebView2 contexts; any failure means
// the default (React) applies. This switch is temporary and not part of the
// AGENTS.md bridge contract; it is deleted once the migration stabilizes.

const REACT_UI_KEY = 'psx.agent.experimental.react';

/** True unless the emergency fallback explicitly disables React. */
export function isReactUiEnabled(): boolean {
  try {
    const value = globalThis.localStorage?.getItem(REACT_UI_KEY);
    return !(value === '0' || value === 'false' || value === 'off');
  } catch {
    return true;
  }
}

/** Diagnostic label surfaced at startup and on <body data-agent-ui-mode>. */
export function reactUiMode(): 'react' | 'legacy' {
  return isReactUiEnabled() ? 'react' : 'legacy';
}
