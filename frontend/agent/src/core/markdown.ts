// markdown.ts — the Agent's safe Markdown renderer (pure string -> HTML).
//
// A faithful TypeScript port of the legacy AgentThreadManager._renderMarkdown
// family (wwwroot/js/agent/markdown.js). No DOM, no `this`: the same input must
// produce byte-identical HTML so the Phase 3 equivalence tests stay green while
// the legacy engine still renders.

const SAFE_INLINE_TAGS = [
  'br', 'b', 'strong', 'i', 'em', 'u', 's', 'code', 'kbd', 'mark',
  'sub', 'sup', 'small', 'details', 'summary'
];

export function escapeHtml(value: unknown): string {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

export function safeHref(href: unknown): string {
  const value = String(href).replace(/&amp;/g, '&').replace(/&quot;/g, '"');
  try {
    const url = new URL(value);
    if (url.username || url.password || url.port) return '';
    if ((url.protocol === 'http:' || url.protocol === 'https:') && url.hostname) return url.href;
    if (url.protocol === 'mailto:') return url.href;
  } catch {
    // Not a valid absolute URL — treated as unsafe.
  }
  return '';
}

function restoreSafeHtml(html: string): string {
  let result = html;
  for (const tag of SAFE_INLINE_TAGS) {
    const open = new RegExp('&lt;' + tag + '\\s*/?&gt;', 'gi');
    const close = new RegExp('&lt;/' + tag + '&gt;', 'gi');
    result = result.replace(open, (match) => match.replace(/&lt;/g, '<').replace(/&gt;/g, '>'));
    result = result.replace(close, '</' + tag + '>');
  }
  return result;
}

export function renderInline(text: unknown): string {
  const codeSpans: string[] = [];
  const withCodeTokens = String(text).replace(/`([^`]+)`/g, (_match, code: string) => {
    const token = '\u0000CODE' + codeSpans.length + '\u0000';
    codeSpans.push('<code>' + escapeHtml(code) + '</code>');
    return token;
  });

  let html = escapeHtml(withCodeTokens);
  html = restoreSafeHtml(html);
  html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  html = html.replace(/__([^_]+)__/g, '<strong>$1</strong>');
  html = html.replace(/\*([^*\n]+)\*/g, '<em>$1</em>');
  html = html.replace(/_([^_\n]+)_/g, '<em>$1</em>');
  html = html.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_match, label: string, href: string) => {
    const href2 = safeHref(href);
    if (!href2) return label;
    return '<a href="' + escapeHtml(href2) + '" target="_blank" rel="noreferrer">' + label + '</a>';
  });
  html = html.replace(/\n/g, '<br>');

  codeSpans.forEach((code, index) => {
    html = html.replace('\u0000CODE' + index + '\u0000', code);
  });
  return html;
}

function splitTableRow(row: string): string[] {
  return row.replace(/^\|/, '').replace(/\|$/, '').split('|').map((cell) => cell.trim());
}

function tryReadTable(lines: string[], startIndex: number): { html: string; endIndex: number } | null {
  if (startIndex + 1 >= lines.length) return null;

  const header = lines[startIndex].trim();
  const divider = lines[startIndex + 1].trim();
  if (!header.includes('|') || !/^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$/.test(divider)) {
    return null;
  }

  const rows = [splitTableRow(header)];
  let endIndex = startIndex + 1;
  for (let index = startIndex + 2; index < lines.length; index += 1) {
    const row = lines[index].trim();
    if (!row.includes('|')) break;
    rows.push(splitTableRow(row));
    endIndex = index;
  }

  const columnCount = rows[0].length;
  const head = rows[0].map((cell) => '<th>' + renderInline(cell) + '</th>').join('');
  const body = rows.slice(1).map((row) => {
    const cells = row.slice(0, columnCount).map((cell) => '<td>' + renderInline(cell) + '</td>').join('');
    return '<tr>' + cells + '</tr>';
  }).join('');

  return {
    html: '<div class="agent-table-scroll"><table><thead><tr>' + head + '</tr></thead><tbody>' + body + '</tbody></table></div>',
    endIndex
  };
}

interface PendingList {
  type: 'ul' | 'ol';
  items: string[];
}

function renderMarkdownBlocks(markdown: string): string {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const html: string[] = [];
  let paragraph: string[] = [];
  let list: PendingList | null = null;

  const flushParagraph = (): void => {
    if (paragraph.length === 0) return;
    html.push('<p>' + renderInline(paragraph.join('\n').trim()) + '</p>');
    paragraph = [];
  };

  const flushList = (): void => {
    if (!list) return;
    html.push('<' + list.type + '>' + list.items.map((item) => '<li>' + renderInline(item) + '</li>').join('') + '</' + list.type + '>');
    list = null;
  };

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const trimmed = line.trim();

    if (!trimmed) {
      flushParagraph();
      flushList();
      continue;
    }

    const table = tryReadTable(lines, index);
    if (table) {
      flushParagraph();
      flushList();
      html.push(table.html);
      index = table.endIndex;
      continue;
    }

    const heading = trimmed.match(/^(#{1,6})\s+(.+)$/);
    if (heading) {
      flushParagraph();
      flushList();
      const level = heading[1].length;
      html.push('<h' + level + '>' + renderInline(heading[2]) + '</h' + level + '>');
      continue;
    }

    if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)) {
      flushParagraph();
      flushList();
      html.push('<hr>');
      continue;
    }

    const quote = trimmed.match(/^>\s?(.*)$/);
    if (quote) {
      flushParagraph();
      flushList();
      html.push('<blockquote>' + renderInline(quote[1]) + '</blockquote>');
      continue;
    }

    const unordered = trimmed.match(/^[-*+]\s+(.+)$/);
    if (unordered) {
      flushParagraph();
      if (!list || list.type !== 'ul') {
        flushList();
        list = { type: 'ul', items: [] };
      }
      list.items.push(unordered[1]);
      continue;
    }

    const ordered = trimmed.match(/^\d+[.)]\s+(.+)$/);
    if (ordered) {
      flushParagraph();
      if (!list || list.type !== 'ol') {
        flushList();
        list = { type: 'ol', items: [] };
      }
      list.items.push(ordered[1]);
      continue;
    }

    flushList();
    paragraph.push(line);
  }

  flushParagraph();
  flushList();
  return html.join('');
}

function renderCodeBlock(segment: string): string {
  const lines = segment.replace(/\r\n/g, '\n').split('\n');
  if (lines.length > 1 && /^[A-Za-z0-9_+#.-]{1,32}$/.test(lines[0].trim())) {
    lines.shift();
  }
  const normalized = lines.join('\n');
  return '<pre><code>' + escapeHtml(normalized.trimEnd()) + '</code></pre>';
}

/** Renders Agent Markdown to the same safe HTML the legacy engine produced. */
export function renderMarkdown(text: unknown): string {
  const segments = String(text).split(/```/g);
  return segments.map((segment, index) => {
    if (index % 2 === 1) return renderCodeBlock(segment);
    return renderMarkdownBlocks(segment);
  }).join('');
}
