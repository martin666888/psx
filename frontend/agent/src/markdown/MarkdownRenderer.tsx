import { cjk } from '@streamdown/cjk';
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type JSX,
  type RefObject
} from 'react';
import {
  CodeBlock,
  CodeBlockCopyButton,
  Streamdown,
  type CustomRendererProps,
  type PluginConfig
} from 'streamdown';
import { cn } from '../lib/utils.js';
import { codeRendererLanguages } from './codeLanguages.js';
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
import {
  getReducedMotionSnapshot,
  subscribeReducedMotion
} from './reducedMotion.js';
import {
  isStreamingTextAnimationEnabled,
  STREAMING_TEXT_ANIMATION
} from './streamingAnimation.js';

const CODE_FILENAME_META = /(?:^|\s)(?:filename|title)=(?:"([^"]*)"|'([^']*)'|([^\s]+))/i;
const CODE_START_LINE_META = /(?:^|\s)startLine=(\d+)/;
const MAX_CODE_FILENAME_CHARACTERS = 260;

function readCodeFilename(meta?: string): string {
  const match = meta?.match(CODE_FILENAME_META);
  return Array.from(match?.[1] ?? match?.[2] ?? match?.[3] ?? '')
    .filter((character) => {
      const codePoint = character.codePointAt(0) ?? 0;
      return codePoint > 0x1f && (codePoint < 0x7f || codePoint > 0x9f);
    })
    .join('')
    .trim()
    .slice(0, MAX_CODE_FILENAME_CHARACTERS);
}

function PsxCodeBlock({ code, isIncomplete, language, meta }: CustomRendererProps): JSX.Element {
  const filename = readCodeFilename(meta);
  const startLineMatch = meta?.match(CODE_START_LINE_META);
  const startLine = startLineMatch ? Number.parseInt(startLineMatch[1], 10) : undefined;
  return (
    <CodeBlock
      code={code}
      isIncomplete={isIncomplete}
      language={language}
      lineNumbers={!/\bnoLineNumbers\b/.test(meta ?? '')}
      startLine={startLine && startLine >= 1 ? startLine : undefined}
    >
      <span data-psx-code-label title={filename || language}>
        {filename || language}
      </span>
      <CodeBlockCopyButton />
    </CodeBlock>
  );
}

const psxCodeRenderers = [{
  component: PsxCodeBlock,
  language: codeRendererLanguages
}];

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
  const reducedMotion = useSyncExternalStore(
    subscribeReducedMotion,
    getReducedMotionSnapshot,
    () => false
  );
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

  const plugins = useMemo<PluginConfig>(
    () => ({ cjk, renderers: psxCodeRenderers, ...heavyPlugins }),
    [heavyPlugins]
  );
  const mermaid = useMemo(() => createPsxMermaidOptions(dark), [dark]);
  // source.length intentionally measures the complete Markdown source in
  // UTF-16 code units. Fences, links, tables and heavy blocks count toward
  // the limit even when Streamdown excludes their rendered nodes from the
  // character animation.
  const animationEnabled = isStreamingTextAnimationEnabled(
    mode,
    source.length,
    reducedMotion,
    !!heavyPlugins.math
  );

  return (
    <div
      ref={containerRef}
      className={cn('psx-markdown size-full', className)}
      data-markdown-mode={mode}
      data-markdown-surface={surface}
    >
      <Streamdown
        animated={animationEnabled ? STREAMING_TEXT_ANIMATION : false}
        components={psxMarkdownComponents}
        controls={{
          code: { copy: true, download: false },
          table: { copy: true, download: false, fullscreen: false },
          mermaid: { copy: true, download: false, fullscreen: true, panZoom: true }
        }}
        isAnimating={mode === 'streaming'}
        // Streamdown 2.5 does not reliably reparse already-mounted math when
        // only plugins changes. Remount its inner renderer once, while the
        // PSX message/Markdown wrapper remains stable. Animation is disabled
        // after math becomes ready so the existing prefix cannot replay.
        key={heavyPlugins.math ? 'math-ready' : 'math-pending'}
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
