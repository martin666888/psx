// flags.ts — feature flag readers for in-progress experiments.
//
// These are temporary development switches, not part of the AGENTS.md bridge
// contract. Reads are wrapped in try/catch because localStorage can throw in
// restricted WebView2 contexts; any failure means the flag is off.

/**
 * Experimental React runtime-card island (dev-react branch validation).
 * Off by default; enable with:
 *   localStorage.setItem('psx.agent.experimental.react', '1')
 */
export function isReactRuntimeCardEnabled(): boolean {
  try {
    const value = globalThis.localStorage?.getItem('psx.agent.experimental.react');
    return value === '1' || value === 'true';
  } catch {
    return false;
  }
}
