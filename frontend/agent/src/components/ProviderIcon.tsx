// ProviderIcon.tsx — the provider brand marks used by History rows.
//
// Monochrome inline SVGs that follow currentColor, so the icons pick up the
// PSX theme automatically (light, dark, Vercel Dark) with zero extra assets,
// CDNs or network requests. Selection is driven purely by the catalog's
// iconKey — never by provider-name string checks.
//
// Path sources:
// - 'claude' / 'kimi' / 'qwen' / 'qoder' / 'cline' / 'opencode': Lobe Icons static SVG
//   1.94.0, archived under Assets/AgentIcons/lobehub-1.94.0 and declared in
//   licenses/lobe-icons. They are the same marks TabBar renders through
//   Themes/AgentIcons.xaml.
// - 'agent': PSX's original four-point sparkle (the WPF fallback geometry).

import type { JSX } from 'react';
import { getProviderIcon, PROVIDER_ICONS } from '../../../webview/src/ProviderIcons.js';

export interface ProviderIconProps {
  /** Catalog icon key: known brand keys render their mark; anything else → generic. */
  iconKey: string;
  className?: string;
}

/** Decorative brand mark: always aria-hidden because the row text already
 * names the provider; fills with currentColor to follow the theme. */
export function ProviderIcon({ iconKey, className }: ProviderIconProps): JSX.Element {
  const normalizedKey = iconKey.toLowerCase();
  const icon = getProviderIcon(normalizedKey);
  const dataIcon = PROVIDER_ICONS[normalizedKey] ? normalizedKey : 'agent';

  const shared = {
    className,
    fill: 'currentColor',
    'aria-hidden': true,
    focusable: false,
    'data-icon': dataIcon
  } as const;

  return (
    <svg {...shared} viewBox={icon.viewBox} fillRule={icon.fillRule}>
      {icon.paths.map((path) => (
        <path key={path.slice(0, 16)} d={path} />
      ))}
    </svg>
  );
}
