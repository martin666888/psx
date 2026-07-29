// ProviderIcon.tsx — the provider brand marks used by History rows.
//
// Monochrome inline SVGs that follow currentColor, so the icons pick up the
// PSX theme automatically (light, dark, Vercel Dark) with zero extra assets,
// CDNs or network requests. Selection is driven purely by the catalog's
// iconKey — never by provider-name string checks.
//
// Path sources:
// - 'claude' / 'kimi': Lobe Icons static SVG 1.94.0 (MIT), archived under
//   Assets/AgentIcons/lobehub-1.94.0 and declared in licenses/lobe-icons.
//   They are the same marks TabBar renders through Themes/AgentIcons.xaml.
// - 'agent': PSX's original four-point sparkle (the WPF fallback geometry).

import type { JSX } from 'react';

const CLAUDE_PATH =
  'M4.709 15.955l4.72-2.647.08-.23-.08-.128H9.2l-.79-.048-2.698-.073-2.339-.097-2.266-.122-.571-.121L0 11.784l.055-.352.48-.321.686.06 1.52.103 2.278.158 1.652.097 2.449.255h.389l.055-.157-.134-.098-.103-.097-2.358-1.596-2.552-1.688-1.336-.972-.724-.491-.364-.462-.158-1.008.656-.722.881.06.225.061.893.686 1.908 1.476 2.491 1.833.365.304.145-.103.019-.073-.164-.274-1.355-2.446-1.446-2.49-.644-1.032-.17-.619a2.97 2.97 0 01-.104-.729L6.283.134 6.696 0l.996.134.42.364.62 1.414 1.002 2.229 1.555 3.03.456.898.243.832.091.255h.158V9.01l.128-1.706.237-2.095.23-2.695.08-.76.376-.91.747-.492.584.28.48.685-.067.444-.286 1.851-.559 2.903-.364 1.942h.212l.243-.242.985-1.306 1.652-2.064.73-.82.85-.904.547-.431h1.033l.76 1.129-.34 1.166-1.064 1.347-.881 1.142-1.264 1.7-.79 1.36.073.11.188-.02 2.856-.606 1.543-.28 1.841-.315.833.388.091.395-.328.807-1.969.486-2.309.462-3.439.813-.042.03.049.061 1.549.146.662.036h1.622l3.02.225.79.522.474.638-.079.485-1.215.62-1.64-.389-3.829-.91-1.312-.329h-.182v.11l1.093 1.068 2.006 1.81 2.509 2.33.127.578-.322.455-.34-.049-2.205-1.657-.851-.747-1.926-1.62h-.128v.17l.444.649 2.345 3.521.122 1.08-.17.353-.608.213-.668-.122-1.374-1.925-1.415-2.167-1.143-1.943-.14.08-.674 7.254-.316.37-.729.28-.607-.461-.322-.747.322-1.476.389-1.924.315-1.53.286-1.9.17-.632-.012-.042-.14.018-1.434 1.967-2.18 2.945-1.726 1.845-.414.164-.717-.37.067-.662.401-.589 2.388-3.036 1.44-1.882.93-1.086-.006-.158h-.055L4.132 18.56l-1.13.146-.487-.456.061-.746.231-.243 1.908-1.312-.006.006z';

const KIMI_PATHS = [
  'M21.846 0a1.923 1.923 0 110 3.846H20.15a.226.226 0 01-.227-.226V1.923C19.923.861 20.784 0 21.846 0z',
  'M11.065 11.199l7.257-7.2c.137-.136.06-.41-.116-.41H14.3a.164.164 0 00-.117.051l-7.82 7.756c-.122.12-.302.013-.302-.179V3.82c0-.127-.083-.23-.185-.23H3.186c-.103 0-.186.103-.186.23V19.77c0 .128.083.23.186.23h2.69c.103 0 .186-.102.186-.23v-3.25c0-.069.025-.135.069-.178l2.424-2.406a.158.158 0 01.205-.023l6.484 4.772a7.677 7.677 0 003.453 1.283c.108.012.2-.095.2-.23v-3.06c0-.117-.07-.212-.164-.227a5.028 5.028 0 01-2.027-.807l-5.613-4.064c-.117-.078-.132-.279-.028-.381z'
];

// The WPF unknown-Agent fallback sparkle (Themes/AgentIcons.xaml) in its
// original 12x12 coordinate space.
const AGENT_SPARKLE_PATH =
  'M6 0.8L7.3 4.7 11.2 6 7.3 7.3 6 11.2 4.7 7.3 0.8 6 4.7 4.7Z';

export interface ProviderIconProps {
  /** Catalog icon key: 'claude' | 'kimi' | anything else → generic mark. */
  iconKey: string;
  className?: string;
}

/** Decorative brand mark: always aria-hidden because the row text already
 * names the provider; fills with currentColor to follow the theme. */
export function ProviderIcon({ iconKey, className }: ProviderIconProps): JSX.Element {
  const shared = {
    className,
    fill: 'currentColor',
    'aria-hidden': true,
    focusable: false,
    'data-icon': iconKey === 'claude' || iconKey === 'kimi' ? iconKey : 'agent'
  } as const;
  if (iconKey === 'claude') {
    return (
      <svg {...shared} viewBox="0 0 24 24" fillRule="evenodd">
        <path d={CLAUDE_PATH} />
      </svg>
    );
  }
  if (iconKey === 'kimi') {
    return (
      <svg {...shared} viewBox="0 0 24 24" fillRule="evenodd">
        {KIMI_PATHS.map((path) => (
          <path key={path.slice(0, 16)} d={path} />
        ))}
      </svg>
    );
  }
  return (
    <svg {...shared} viewBox="0 0 12 12">
      <path d={AGENT_SPARKLE_PATH} />
    </svg>
  );
}
