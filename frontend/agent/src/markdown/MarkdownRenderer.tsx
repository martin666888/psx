import { cjk } from '@streamdown/cjk';
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type JSX,
  type RefObject
} from 'react';
import { Streamdown, type PluginConfig } from 'streamdown';
import { cn } from '../lib/utils.js';
import type { MarkdownContentProps, MarkdownRenderMode } from './MarkdownContent.js';
import {
  loadCodePlugin,
  loadMathPlugin,
  loadMermaidPlugin
} from './pluginLoader.js';
import {
  createPsxMermaidOptions,
  psxMarkdownComponents,
  psxRehypePlugins,
  psxRemarkPlugins,
  psxUrlTransform
} from './security.js';
import { inspectMarkdownSource, type RequiredMarkdownPlugins } from './sourceInspection.js';

function useNearViewport(mode: MarkdownRenderMode, required: RequiredMarkdownPlugins) {
  const containerRef = useRef<HTMLDivElement>(null);
  const needsHeavyPlugin = required.code || required.math || required.mermaid;
  const [nearViewport, setNearViewport] = useState(mode === 'streaming' && needsHeavyPlugin);

  useEffect(() => {
    if (!needsHeavyPlugin || nearViewport) return;
    if (mode === 'streaming' || typeof IntersectionObserver === 'undefined') {
      setNearViewport(true);
      return;
    }
    const element = containerRef.current;
    if (!element) return;
    const scrollRoot = element.closest('.agent-thread-scroll');
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          setNearViewport(true);
          observer.disconnect();
        }
      },
      {
        root: scrollRoot instanceof Element ? scrollRoot : null,
        rootMargin: '800px 0px',
        threshold: 0
      }
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [mode, nearViewport, needsHeavyPlugin]);

  return { containerRef, nearViewport };
}

function useDarkTheme(containerRef: RefObject<HTMLDivElement | null>): boolean {
  const [dark, setDark] = useState(false);
  useEffect(() => {
    const element = containerRef.current;
    const themeRoot = element?.closest('.agent-ui');
    if (!(themeRoot instanceof HTMLElement)) return;
    const update = () => setDark(themeRoot.classList.contains('agent-ui-dark'));
    update();
    const observer = new MutationObserver(update);
    observer.observe(themeRoot, { attributes: true, attributeFilter: ['class', 'style'] });
    return () => observer.disconnect();
  }, [containerRef]);
  return dark;
}

export function MarkdownRenderer({ source, mode, surface, className }: MarkdownContentProps): JSX.Element {
  const required = useMemo(() => inspectMarkdownSource(source), [source]);
  const { containerRef, nearViewport } = useNearViewport(mode, required);
  const dark = useDarkTheme(containerRef);
  const [heavyPlugins, setHeavyPlugins] = useState<PluginConfig>({});

  useEffect(() => {
    if (!nearViewport) return;
    let active = true;
    const load = async () => {
      const [code, math, mermaid] = await Promise.all([
        required.code ? loadCodePlugin() : Promise.resolve(undefined),
        required.math ? loadMathPlugin() : Promise.resolve(undefined),
        required.mermaid ? loadMermaidPlugin() : Promise.resolve(undefined)
      ]);
      if (active) setHeavyPlugins((current) => ({ ...current, code, math, mermaid }));
    };
    void load();
    return () => {
      active = false;
    };
  }, [nearViewport, required.code, required.math, required.mermaid]);

  const plugins = useMemo<PluginConfig>(() => ({ cjk, ...heavyPlugins }), [heavyPlugins]);
  const mermaid = useMemo(() => createPsxMermaidOptions(dark), [dark]);

  return (
    <div
      ref={containerRef}
      className={cn('psx-markdown size-full', className)}
      data-markdown-mode={mode}
      data-markdown-surface={surface}
    >
      <Streamdown
        animated={false}
        components={psxMarkdownComponents}
        controls={{
          code: { copy: true, download: false },
          table: { copy: true, download: false, fullscreen: false },
          mermaid: { copy: true, download: false, fullscreen: true, panZoom: true }
        }}
        isAnimating={mode === 'streaming'}
        lineNumbers
        linkSafety={{ enabled: false }}
        mermaid={mermaid}
        mode={mode}
        parseIncompleteMarkdown={mode === 'streaming'}
        plugins={plugins}
        rehypePlugins={psxRehypePlugins}
        remarkPlugins={psxRemarkPlugins}
        shikiTheme={['github-light', 'github-dark']}
        urlTransform={psxUrlTransform}
      >
        {source}
      </Streamdown>
    </div>
  );
}
