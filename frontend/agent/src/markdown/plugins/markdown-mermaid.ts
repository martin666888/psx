import { createMermaidPlugin } from '@streamdown/mermaid';

export const mermaidPlugin = createMermaidPlugin({
  config: {
    securityLevel: 'strict',
    startOnLoad: false,
    suppressErrorRendering: true,
    fontFamily: 'var(--agent-font-mono)'
  }
});
