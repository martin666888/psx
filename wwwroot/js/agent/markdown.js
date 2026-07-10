AgentThreadManager.prototype._renderMarkdown = function(text) {
        const segments = String(text).split(/```/g);
        return segments.map((segment, index) => {
            if (index % 2 === 1) {
                return this._renderCodeBlock(segment);
            }

            return this._renderMarkdownBlocks(segment);
        }).join('');
};

AgentThreadManager.prototype._renderCodeBlock = function(segment) {
        const lines = segment.replace(/\r\n/g, '\n').split('\n');
        if (lines.length > 1 && /^[A-Za-z0-9_+#.-]{1,32}$/.test(lines[0].trim())) {
            lines.shift();
        }

        const normalized = lines.join('\n');
        return '<pre><code>' + this._escape(normalized.trimEnd()) + '</code></pre>';
};

AgentThreadManager.prototype._renderMarkdownBlocks = function(markdown) {
        const lines = markdown.replace(/\r\n/g, '\n').split('\n');
        const html = [];
        let paragraph = [];
        let list = null;

        const flushParagraph = () => {
            if (paragraph.length === 0) return;
            html.push('<p>' + this._renderInline(paragraph.join('\n').trim()) + '</p>');
            paragraph = [];
        };

        const flushList = () => {
            if (!list) return;
            html.push('<' + list.type + '>' + list.items.map((item) => '<li>' + this._renderInline(item) + '</li>').join('') + '</' + list.type + '>');
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

            const table = this._tryReadTable(lines, index);
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
                html.push('<h' + level + '>' + this._renderInline(heading[2]) + '</h' + level + '>');
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
                html.push('<blockquote>' + this._renderInline(quote[1]) + '</blockquote>');
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
};

AgentThreadManager.prototype._tryReadTable = function(lines, startIndex) {
        if (startIndex + 1 >= lines.length) return null;

        const header = lines[startIndex].trim();
        const divider = lines[startIndex + 1].trim();
        if (!header.includes('|') || !/^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$/.test(divider)) {
            return null;
        }

        const rows = [this._splitTableRow(header)];
        let endIndex = startIndex + 1;
        for (let index = startIndex + 2; index < lines.length; index += 1) {
            const row = lines[index].trim();
            if (!row.includes('|')) break;
            rows.push(this._splitTableRow(row));
            endIndex = index;
        }

        const columnCount = rows[0].length;
        const head = rows[0].map((cell) => '<th>' + this._renderInline(cell) + '</th>').join('');
        const body = rows.slice(1).map((row) => {
            const cells = row.slice(0, columnCount).map((cell) => '<td>' + this._renderInline(cell) + '</td>').join('');
            return '<tr>' + cells + '</tr>';
        }).join('');

        return {
            html: '<div class="agent-table-scroll"><table><thead><tr>' + head + '</tr></thead><tbody>' + body + '</tbody></table></div>',
            endIndex
        };
};

AgentThreadManager.prototype._splitTableRow = function(row) {
        return row.replace(/^\|/, '').replace(/\|$/, '').split('|').map((cell) => cell.trim());
};

AgentThreadManager.prototype._renderInline = function(text) {
        const codeSpans = [];
        const withCodeTokens = String(text).replace(/`([^`]+)`/g, (_, code) => {
            const token = '\u0000CODE' + codeSpans.length + '\u0000';
            codeSpans.push('<code>' + this._escape(code) + '</code>');
            return token;
        });

        let html = this._escape(withCodeTokens);
        html = this._restoreSafeHtml(html);
        html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
        html = html.replace(/__([^_]+)__/g, '<strong>$1</strong>');
        html = html.replace(/\*([^*\n]+)\*/g, '<em>$1</em>');
        html = html.replace(/_([^_\n]+)_/g, '<em>$1</em>');
        html = html.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, label, href) => {
            const safeHref = this._safeHref(href);
            if (!safeHref) return label;
            return '<a href="' + safeHref + '" target="_blank" rel="noreferrer">' + label + '</a>';
        });
        html = html.replace(/\n/g, '<br>');

        codeSpans.forEach((code, index) => {
            html = html.replace('\u0000CODE' + index + '\u0000', code);
        });
        return html;
};

AgentThreadManager.prototype._restoreSafeHtml = function(html) {
        const tags = ['br', 'b', 'strong', 'i', 'em', 'u', 's', 'code', 'kbd', 'mark', 'sub', 'sup', 'small', 'details', 'summary'];
        tags.forEach((tag) => {
            const open = new RegExp('&lt;' + tag + '\\s*/?&gt;', 'gi');
            const close = new RegExp('&lt;/' + tag + '&gt;', 'gi');
            html = html.replace(open, (match) => match.replace(/&lt;/g, '<').replace(/&gt;/g, '>'));
            html = html.replace(close, '</' + tag + '>');
        });
        return html;
};

AgentThreadManager.prototype._safeHref = function(href) {
        const value = String(href).replace(/&amp;/g, '&').replace(/&quot;/g, '"');
        if (/^(https?:|mailto:)/i.test(value)) {
            return this._escape(value);
        }

        return '';
};

AgentThreadManager.prototype._escape = function(value) {
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#039;');
};
