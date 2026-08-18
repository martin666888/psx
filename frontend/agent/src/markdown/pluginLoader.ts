import type { CodeHighlighterPlugin, DiagramPlugin, MathPlugin } from 'streamdown';

let codePromise: Promise<CodeHighlighterPlugin> | null = null;
let mathPromise: Promise<MathPlugin> | null = null;
let mermaidPromise: Promise<DiagramPlugin> | null = null;

export function loadCodePlugin(): Promise<CodeHighlighterPlugin> {
  codePromise ??= import('./plugins/markdown-code.js').then((module) => module.codePlugin);
  return codePromise;
}

export function loadMathPlugin(): Promise<MathPlugin> {
  mathPromise ??= import('./plugins/markdown-math.js').then((module) => module.mathPlugin);
  return mathPromise;
}

export function loadMermaidPlugin(): Promise<DiagramPlugin> {
  mermaidPromise ??= import('./plugins/markdown-mermaid.js').then((module) => module.mermaidPlugin);
  return mermaidPromise;
}

export function readMarkdownPluginLoadState() {
  return Object.freeze({
    code: codePromise !== null,
    math: mathPromise !== null,
    mermaid: mermaidPromise !== null
  });
}
