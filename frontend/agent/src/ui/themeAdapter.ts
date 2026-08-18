// themeAdapter.ts — maps the existing appearance_settings payload onto the
// shadcn CSS variables, scoped to the .agent-ui boundary (CP2 contract: the
// payload does not change; only the Agent UI container receives the shadcn
// variable layer, so the Terminal and everything outside .agent-ui is
// untouched). The legacy --agent-* variables keep flowing through
// WorkspaceHost.applyGlobalAppearance unchanged; this adapter adds the
// parallel shadcn vocabulary that the shadcn/AI Elements components consume.

interface ThemeColorTables {
  themeColors?: Record<string, string | undefined>;
  agentThemeColors?: Record<string, string | undefined>;
}

function setVar(el: HTMLElement, name: string, value: string | undefined): void {
  if (typeof value === 'string' && value.trim()) {
    el.style.setProperty(name, value.trim());
  }
}

/** Perceived luminance of a #rrggbb / #rgb hex color (0..1), or null. */
function hexLuminance(hex: string | undefined): number | null {
  if (typeof hex !== 'string') return null;
  const match = /^#([0-9a-f]{3}|[0-9a-f]{6})/i.exec(hex.trim());
  if (!match) return null;
  let value = match[1];
  if (value.length === 3) value = value.replace(/./g, (c) => c + c);
  const r = parseInt(value.slice(0, 2), 16) / 255;
  const g = parseInt(value.slice(2, 4), 16) / 255;
  const b = parseInt(value.slice(4, 6), 16) / 255;
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/**
 * Apply the shadcn variable layer onto the Agent UI container. Idempotent;
 * called on every settings/appearance_settings event with the same payload
 * WorkspaceHost already receives.
 */
export function applyShadcnTheme(container: HTMLElement, settings: ThemeColorTables): void {
  container.classList.add('agent-ui');

  const t = settings.themeColors;
  const a = settings.agentThemeColors;

  const luminance = hexLuminance(t?.background);
  if (luminance !== null) {
    container.classList.toggle('agent-ui-dark', luminance < 0.5);
  }

  if (t && typeof t === 'object') {
    setVar(container, '--background', t.background);
    setVar(container, '--foreground', t.text);
    setVar(container, '--card', t.surfaceRaised);
    setVar(container, '--card-foreground', t.text);
    setVar(container, '--popover', t.surfaceRaised);
    setVar(container, '--popover-foreground', t.text);
    setVar(container, '--secondary', t.surfaceMuted);
    setVar(container, '--secondary-foreground', t.text);
    setVar(container, '--muted', t.surfaceMuted);
    setVar(container, '--muted-foreground', t.textMuted);
    // shadcn "accent" is the hover/selected surface, not the brand color.
    setVar(container, '--accent', t.hover);
    setVar(container, '--accent-foreground', t.text);
    setVar(container, '--destructive', t.error);
    setVar(container, '--border', t.border);
    setVar(container, '--input', t.borderStrong ?? t.border);
    // Brand color: the primary action surface follows the send button when
    // themed, falling back to the accent color.
    setVar(container, '--primary', a?.sendBtn ?? t.accent);
    setVar(container, '--primary-foreground', a?.sendBtnText);
    setVar(container, '--ring', a?.focusRing ?? t.accent);
  }
}
