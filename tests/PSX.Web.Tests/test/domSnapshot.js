// domSnapshot.js — structural DOM snapshots for legacy/React equivalence
// tests. Captures tag, class, high-signal attributes and children; svg
// subtrees collapse to their tag (icon markup formatting differs between
// innerHTML strings and JSX but is visually identical).

const ATTRS = [
  'data-state', 'data-run-id', 'data-tool-id', 'data-request-id', 'data-option-id',
  'data-option-kind', 'data-raw', 'data-decision-state', 'aria-label', 'aria-live',
  'aria-busy', 'aria-pressed', 'aria-atomic', 'role', 'title', 'type'
];

export function snap(node) {
  if (node.nodeType === 3) {
    return node.textContent;
  }
  const tag = node.tagName.toLowerCase();
  if (tag === 'svg') return { tag };
  const out = { tag };
  const cls = node.getAttribute('class');
  if (cls) out.class = cls;
  for (const name of ATTRS) {
    const value = node.getAttribute(name);
    if (value !== null) out[name] = value;
  }
  if (node.hasAttribute('open')) out.open = true;
  if (node.hasAttribute('hidden')) out.hidden = true;
  if (node.disabled) out.disabled = true;
  if (tag === 'input' || tag === 'textarea') {
    if (node.type === 'checkbox') {
      out.checked = node.checked;
    } else {
      out.value = node.value;
    }
    if (node.getAttribute('type')) out.type = node.getAttribute('type');
    if (node.getAttribute('step')) out.step = node.getAttribute('step');
    if (node.getAttribute('rows')) out.rows = node.getAttribute('rows');
  }
  if (tag === 'a') {
    out.href = node.getAttribute('href');
    out.target = node.getAttribute('target');
    out.rel = node.getAttribute('rel');
  }
  if (node.hasAttribute('aria-checked')) out['aria-checked'] = node.getAttribute('aria-checked');
  const style = node.getAttribute('style');
  if (style) out.style = style.replace(/\s/g, '');
  const children = [];
  for (const child of node.childNodes) {
    if (child.nodeType === 3) {
      if (child.textContent) children.push(child.textContent);
    } else if (child.nodeType === 1) {
      children.push(snap(child));
    }
  }
  if (children.length) out.children = children;
  return out;
}

export function threadSnapshot(container) {
  return [...container.children].map((child) => snap(child));
}
