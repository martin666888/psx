export interface EditBlock {
  type: 'text' | 'diff';
  text: string;
  path: string;
  displayPath: string;
  external: boolean;
  oldText: string | null;
  newText: string;
}

export function readEditBlocks(value: unknown): EditBlock[] | undefined {
  if (!Array.isArray(value) || !value.length) return undefined;
  const blocks: EditBlock[] = [];
  for (const raw of value) {
    if (!raw || typeof raw !== 'object') return undefined;
    if (raw.type === 'text' && typeof raw.text === 'string') {
      blocks.push({ type: 'text', text: raw.text, path: '', displayPath: '', external: false, oldText: null, newText: '' });
    } else if (raw.type === 'diff' && typeof raw.path === 'string' && raw.path
      && typeof raw.newText === 'string' && (raw.oldText === null || typeof raw.oldText === 'string')) {
      blocks.push({ type: 'diff', text: '', path: raw.path,
        displayPath: typeof raw.displayPath === 'string' && raw.displayPath ? raw.displayPath : raw.path,
        external: raw.external !== false, oldText: raw.oldText, newText: raw.newText });
    } else return undefined;
  }
  return blocks.some(block => block.type === 'diff') ? blocks : undefined;
}

export interface DiffLine {
  kind: 'context' | 'delete' | 'add';
  text: string;
  oldLine?: number;
  newLine?: number;
  noNewline: boolean;
}

// Compare complete lines, including their terminators. Bound the LCS matrix to
// avoid quadratic work on large provider payloads; the fallback is an exact
// replacement of the unmatched middle, never an invented unchanged region.
export function diffFileLines(oldText: string | null, newText: string): DiffLine[] {
  const before = (oldText ?? '').match(/[^\n]*\n|[^\n]+$/g) ?? [];
  const after = newText.match(/[^\n]*\n|[^\n]+$/g) ?? [];
  let prefix = 0;
  while (prefix < before.length && prefix < after.length && before[prefix] === after[prefix]) prefix++;
  let suffix = 0;
  while (suffix < before.length - prefix && suffix < after.length - prefix
    && before[before.length - 1 - suffix] === after[after.length - 1 - suffix]) suffix++;
  const result: DiffLine[] = [];
  let oldLine = 1;
  let newLine = 1;
  const emit = (kind: DiffLine['kind'], text: string) => result.push({ kind,
    text: text.replace(/\r?\n$/, ''), noNewline: !text.endsWith('\n'),
    oldLine: kind === 'add' ? undefined : oldLine++,
    newLine: kind === 'delete' ? undefined : newLine++ });
  for (let i = 0; i < prefix; i++) emit('context', before[i]);
  const n = before.length - prefix - suffix;
  const m = after.length - prefix - suffix;
  if ((n + 1) * (m + 1) <= 1_000_000) {
    const width = m + 1;
    const lcs = new Uint32Array((n + 1) * width);
    for (let i = n - 1; i >= 0; i--) {
      for (let j = m - 1; j >= 0; j--) {
        lcs[i * width + j] = before[prefix + i] === after[prefix + j]
          ? lcs[(i + 1) * width + j + 1] + 1
          : Math.max(lcs[(i + 1) * width + j], lcs[i * width + j + 1]);
      }
    }
    let i = 0;
    let j = 0;
    while (i < n || j < m) {
      if (i < n && j < m && before[prefix + i] === after[prefix + j]) {
        emit('context', before[prefix + i++]); j++;
      } else if (i < n && (j === m || lcs[(i + 1) * width + j] >= lcs[i * width + j + 1])) {
        emit('delete', before[prefix + i++]);
      } else emit('add', after[prefix + j++]);
    }
  } else {
    for (let i = prefix; i < before.length - suffix; i++) emit('delete', before[i]);
    for (let i = prefix; i < after.length - suffix; i++) emit('add', after[i]);
  }
  for (let i = before.length - suffix; i < before.length; i++) emit('context', before[i]);
  return result;
}
