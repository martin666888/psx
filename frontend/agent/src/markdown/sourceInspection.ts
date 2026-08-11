export interface RequiredMarkdownPlugins {
  code: boolean;
  math: boolean;
  mermaid: boolean;
}

const FENCE_LINE = /^[ \t]{0,3}(`{3,}|~{3,})([^\n]*)$/gm;
const INLINE_MATH = /(^|[^\\])\$(?!\$|\s)(?:\\.|[^$\n\\])+\$(?!\$)/m;

export function inspectMarkdownSource(source: string): RequiredMarkdownPlugins {
  let code = false;
  let mermaid = false;
  let openFence: { marker: string; length: number } | null = null;
  for (const match of source.matchAll(FENCE_LINE)) {
    const marker = match[1];
    const remainder = match[2];
    if (openFence) {
      if (
        marker[0] === openFence.marker
        && marker.length >= openFence.length
        && remainder.trim().length === 0
      ) {
        openFence = null;
      }
      continue;
    }
    openFence = { marker: marker[0], length: marker.length };
    const language = remainder.trim().split(/\s+/, 1)[0]?.toLowerCase();
    if (language === 'mermaid') mermaid = true;
    else code = true;
  }
  return {
    code,
    math: source.includes('$$') || INLINE_MATH.test(source),
    mermaid
  };
}
