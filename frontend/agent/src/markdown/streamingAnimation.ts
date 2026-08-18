import type { MarkdownRenderMode } from './MarkdownContent.js';

export const MAX_ANIMATED_SOURCE_CHARACTERS = 2048;

export const STREAMING_TEXT_ANIMATION = {
  animation: 'fadeIn',
  duration: 120,
  easing: 'ease',
  sep: 'char',
  // Streamdown defaults to 40ms. Streaming content must never queue behind
  // an artificial character-by-character delay.
  stagger: 0
} as const;

export function isStreamingTextAnimationEnabled(
  mode: MarkdownRenderMode,
  sourceLength: number,
  reducedMotion: boolean,
  mathPluginReady = false
): boolean {
  return mode === 'streaming' &&
    sourceLength <= MAX_ANIMATED_SOURCE_CHARACTERS &&
    !reducedMotion &&
    !mathPluginReady;
}
