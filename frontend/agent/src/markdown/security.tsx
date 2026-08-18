import type { ComponentPropsWithoutRef, ReactNode } from 'react';
import { defaultSchema } from 'rehype-sanitize';
import rehypeSanitize from 'rehype-sanitize';
import { BlockPolicy, harden } from 'rehype-harden';
import {
  defaultRehypePlugins,
  defaultRemarkPlugins,
  type Components,
  type MermaidOptions,
  type UrlTransform
} from 'streamdown';
import type { PluggableList } from 'unified';

const ATTACHMENT_ORIGIN = 'https://psx-attachments.local';
const SAFE_RAW_HTML_TAGS = new Set([
  'br', 'b', 'strong', 'i', 'em', 'u', 's', 'code', 'kbd', 'mark',
  'sub', 'sup', 'small', 'details', 'summary'
]);
const SAFE_RAW_TAG = /^<\/?([a-z][a-z0-9]*)\s*\/?>$/i;
const SAFE_FOOTNOTE_TARGET = /^#user-content-fn(?:ref)?-[a-z0-9_-]+$/i;
const IPV4_HOST = /^(?:\d{1,3}\.){3}\d{1,3}$/;
const MAX_MATH_SOURCE_LENGTH = 16 * 1024;
const MAX_MERMAID_SOURCE_LENGTH = 32 * 1024;

interface TreeNode {
  type: string;
  value?: string;
  lang?: string | null;
  children?: TreeNode[];
}

function isIpHost(hostname: string): boolean {
  return IPV4_HOST.test(hostname) || hostname.startsWith('[') || hostname.endsWith(']');
}

function hasExplicitPort(value: string): boolean {
  const authorityMatch = /^[a-z][a-z0-9+.-]*:\/\/([^/?#]*)/i.exec(value);
  if (!authorityMatch) return false;
  const authority = authorityMatch[1];
  if (authority.startsWith('[')) return /\]:\d+$/.test(authority);
  return /:\d+$/.test(authority);
}

function hasControlSpaceOrBackslash(value: string): boolean {
  return Array.from(value).some((character) => {
    const codePoint = character.codePointAt(0) ?? 0;
    return codePoint <= 0x20 || codePoint === 0x7f || character === '\\';
  });
}

/** The one URL policy used by Markdown and decision-card links. */
export function normalizePsxHref(href: unknown): string {
  const value = String(href ?? '').replace(/&amp;/g, '&').replace(/&quot;/g, '"').trim();
  if (!value || hasControlSpaceOrBackslash(value) || hasExplicitPort(value)) return '';
  if (SAFE_FOOTNOTE_TARGET.test(value)) return value;

  try {
    const url = new URL(value);
    if (url.username || url.password || url.port) return '';
    if (url.protocol === 'mailto:') return url.href;
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return '';
    if (!url.hostname || isIpHost(url.hostname)) return '';
    return url.href;
  } catch {
    return '';
  }
}

export function normalizePsxImageSource(source: unknown): string {
  const value = String(source ?? '').trim();
  if (!value || hasControlSpaceOrBackslash(value) || hasExplicitPort(value)) return '';
  try {
    const url = new URL(value);
    if (url.username || url.password || url.port) return '';
    if (url.protocol !== 'https:' || url.origin !== ATTACHMENT_ORIGIN) return '';
    return url.href;
  } catch {
    return '';
  }
}

function rehypeRestrictRawHtml() {
  return (tree: TreeNode): void => {
    const visit = (node: TreeNode): void => {
      if (!node.children) return;
      for (let index = 0; index < node.children.length; index += 1) {
        const child = node.children[index];
        if (child.type === 'raw') {
          child.value = (child.value ?? '').replace(/<!--[\s\S]*?-->|<![^>]*>|<[^>]*>/g, (tag) => {
            const match = SAFE_RAW_TAG.exec(tag);
            if (match && SAFE_RAW_HTML_TAGS.has(match[1].toLowerCase())) return tag;
            return tag.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
          });
          continue;
        }
        visit(child);
      }
    };
    visit(tree);
  };
}

function remarkLimitAdvancedContent() {
  return (tree: TreeNode): void => {
    const visit = (node: TreeNode): void => {
      if (!node.children) return;
      for (let index = 0; index < node.children.length; index += 1) {
        const child = node.children[index];
        if (
          child.type === 'code'
          && child.lang?.toLowerCase() === 'mermaid'
          && (child.value?.length ?? 0) > MAX_MERMAID_SOURCE_LENGTH
        ) {
          child.lang = 'text';
        } else if (
          (child.type === 'math' || child.type === 'inlineMath')
          && (child.value?.length ?? 0) > MAX_MATH_SOURCE_LENGTH
        ) {
          node.children[index] = child.type === 'inlineMath'
            ? { type: 'inlineCode', value: `$$${child.value ?? ''}$$` }
            : { type: 'code', lang: 'text', value: `$$\n${child.value ?? ''}\n$$` };
          continue;
        }
        visit(child);
      }
    };
    visit(tree);
  };
}

const codeAttributes = defaultSchema.attributes?.code ?? [];
const psxSanitizeSchema = {
  ...defaultSchema,
  tagNames: [...new Set([...(defaultSchema.tagNames ?? []), ...SAFE_RAW_HTML_TAGS])],
  attributes: {
    ...defaultSchema.attributes,
    code: [...codeAttributes, 'metastring']
  }
};

export const psxRehypePlugins: PluggableList = [
  rehypeRestrictRawHtml,
  defaultRehypePlugins.raw,
  [rehypeSanitize, psxSanitizeSchema],
  [
    harden,
    {
      allowedImagePrefixes: [`${ATTACHMENT_ORIGIN}/`],
      allowedLinkPrefixes: ['*'],
      allowedProtocols: ['http', 'https', 'mailto'],
      defaultOrigin: 'https://psx.local/',
      allowDataImages: false,
      linkBlockPolicy: BlockPolicy.textOnly,
      imageBlockPolicy: BlockPolicy.textOnly
    }
  ]
];

export const psxRemarkPlugins: PluggableList = [
  ...Object.values(defaultRemarkPlugins),
  remarkLimitAdvancedContent
];

export const psxUrlTransform: UrlTransform = (url, key, node) => {
  if (node.tagName === 'a' && key === 'href') return normalizePsxHref(url) || null;
  if (node.tagName === 'img' && key === 'src') return normalizePsxImageSource(url) || null;
  return null;
};

type MarkdownAnchorProps = ComponentPropsWithoutRef<'a'> & { node?: unknown };
type MarkdownImageProps = ComponentPropsWithoutRef<'img'> & { node?: unknown };
type MarkdownStrongProps = ComponentPropsWithoutRef<'strong'> & { node?: unknown };

function PsxMarkdownAnchor({ children, href, node: _node, ...props }: MarkdownAnchorProps) {
  const safeHref = normalizePsxHref(href);
  if (!safeHref) return <>{children}</>;
  return (
    <a {...props} href={safeHref} rel="noreferrer" target="_blank">
      {children}
    </a>
  );
}

function PsxMarkdownImage({ alt, src, node: _node, ...props }: MarkdownImageProps) {
  const safeSource = normalizePsxImageSource(src);
  if (!safeSource) return <span data-streamdown="blocked-image">{alt || 'Image unavailable'}</span>;
  return <img {...props} alt={alt || ''} loading="lazy" src={safeSource} />;
}

function PsxMarkdownStrong({ children, node: _node, ...props }: MarkdownStrongProps) {
  return <strong {...props} data-streamdown="strong">{children}</strong>;
}

export const psxMarkdownComponents: Components = {
  a: PsxMarkdownAnchor,
  img: PsxMarkdownImage,
  strong: PsxMarkdownStrong
};

export function MermaidErrorNotice(_props: { chart: string; error: string; retry: () => void }): ReactNode {
  return <div className="psx-markdown-error" role="status">Diagram could not be rendered.</div>;
}

/** Caller-independent Mermaid policy; document content cannot override these options. */
export function createPsxMermaidOptions(dark: boolean): MermaidOptions {
  return {
    config: {
      securityLevel: 'strict',
      startOnLoad: false,
      suppressErrorRendering: true,
      theme: dark ? 'dark' : 'neutral',
      fontFamily: 'var(--agent-font-mono)'
    },
    errorComponent: MermaidErrorNotice
  };
}

export const markdownLimits = Object.freeze({
  mathSourceCharacters: MAX_MATH_SOURCE_LENGTH,
  mermaidSourceCharacters: MAX_MERMAID_SOURCE_LENGTH
});
