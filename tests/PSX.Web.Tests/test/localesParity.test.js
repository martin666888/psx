// localesParity.test.js — every locale file must carry the exact same key
// set with no empty values, and interpolation variable names must match the
// canonical English resources. Extends to zh-Hant/ja as they land (Phase 2).

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..', '..');

const LOCALE_NAMESPACES = [
  ['frontend/webview/src/locales', 'shell.json'],
  ['frontend/agent/src/locales', 'agent.json'],
  ['frontend/agent/src/usage/locales', 'settings.json']
];
const LOCALES = ['zh-Hans', 'zh-Hant', 'en', 'ja'];
const LOCALE_FILES = LOCALE_NAMESPACES.flatMap(([dir, file]) =>
  LOCALES.map((locale) => `${dir}/${locale}/${file}`)
);

const CANONICAL = {
  shell: 'frontend/webview/src/locales/en/shell.json',
  agent: 'frontend/agent/src/locales/en/agent.json',
  settings: 'frontend/agent/src/usage/locales/en/settings.json'
};

function flatten(value, prefix = '') {
  const keys = [];
  for (const [key, entry] of Object.entries(value)) {
    const dotted = prefix ? `${prefix}.${key}` : key;
    if (entry !== null && typeof entry === 'object') {
      keys.push(...flatten(entry, dotted));
    } else {
      keys.push(dotted);
    }
  }
  return keys.sort();
}

function leaves(value, prefix = '') {
  const out = {};
  for (const [key, entry] of Object.entries(value)) {
    const dotted = prefix ? `${prefix}.${key}` : key;
    if (entry !== null && typeof entry === 'object') {
      Object.assign(out, leaves(entry, dotted));
    } else {
      out[dotted] = String(entry);
    }
  }
  return out;
}

function variables(text) {
  return [...text.matchAll(/\{\{(\w+)\}\}/g)].map((match) => match[1]).sort();
}

test('every shipped locale resource exists and parses', () => {
  for (const relative of LOCALE_FILES) {
    const raw = readFileSync(path.join(repoRoot, relative), 'utf8');
    const parsed = JSON.parse(raw);
    assert.equal(typeof parsed, 'object', relative);
  }
});

for (const [namespace, canonicalPath] of Object.entries(CANONICAL)) {
  const locales = LOCALES;
  const canonicalDoc = JSON.parse(readFileSync(path.join(repoRoot, canonicalPath), 'utf8'));
  const canonicalKeys = flatten(canonicalDoc);
  const canonicalLeaves = leaves(canonicalDoc);

  for (const locale of locales) {
    const candidates = LOCALE_FILES.filter(
      (file) => file.includes(`/${locale}/`) && file.endsWith(`${namespace}.json`)
    );
    assert.equal(candidates.length, 1, `one ${locale} ${namespace} resource expected`);

    const doc = JSON.parse(readFileSync(path.join(repoRoot, candidates[0]), 'utf8'));
    const docLeaves = leaves(doc);

    test(`${locale}/${namespace}.json key set matches en exactly`, () => {
      assert.deepEqual(flatten(doc), canonicalKeys);
    });

    test(`${locale}/${namespace}.json has no empty values`, () => {
      for (const [key, value] of Object.entries(docLeaves)) {
        assert.notEqual(value.trim(), '', `${locale}/${namespace}: ${key} is empty`);
      }
    });

    test(`${locale}/${namespace}.json interpolation variables match en`, () => {
      for (const [key, value] of Object.entries(docLeaves)) {
        if (!canonicalLeaves[key]) continue;
        assert.deepEqual(
          variables(value),
          variables(canonicalLeaves[key]),
          `${locale}/${namespace}: variable mismatch at ${key}`
        );
      }
    });
  }
}
