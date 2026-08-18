import { lazy, memo, Suspense, type JSX } from 'react';
import { cn } from '../lib/utils.js';

export { inspectMarkdownSource } from './sourceInspection.js';

export type MarkdownRenderMode = 'streaming' | 'static';
export type MarkdownSurface = 'message' | 'plan' | 'decision';

export interface MarkdownContentProps {
  source: string;
  mode: MarkdownRenderMode;
  surface: MarkdownSurface;
  className?: string;
}

const LazyMarkdownRenderer = lazy(async () => {
  const module = await import('./MarkdownRenderer.js');
  return { default: module.MarkdownRenderer };
});

function MarkdownFallback({ source, mode, surface, className }: MarkdownContentProps): JSX.Element {
  return (
    <div
      className={cn('psx-markdown size-full whitespace-pre-wrap', className)}
      data-markdown-mode={mode}
      data-markdown-surface={surface}
      data-streamdown="loading"
    >
      {source}
    </div>
  );
}

function MarkdownContentComponent(props: MarkdownContentProps): JSX.Element {
  return (
    <Suspense fallback={<MarkdownFallback {...props} />}>
      <LazyMarkdownRenderer {...props} />
    </Suspense>
  );
}

export const MarkdownContent = memo(MarkdownContentComponent);
