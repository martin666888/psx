import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, it } from 'vitest';
import { compareSemVer, parseSemVer } from '../../../tools/dsh-semver.mjs';

const vectorsPath = fileURLToPath(new URL('../../../tools/dsh-semver-vectors.json', import.meta.url));
const vectors = JSON.parse(readFileSync(vectorsPath, 'utf8'));

describe('dsh-semver shared vectors', () => {
  it('walks the ordered chain strictly ascending', () => {
    const chain = vectors.ordered;
    for (let index = 1; index < chain.length; index += 1) {
      assert.equal(compareSemVer(chain[index - 1], chain[index]), -1,
        `${chain[index - 1]} < ${chain[index]}`);
    }
  });

  it('matches every explicit pair', () => {
    for (const { a, b, expect } of vectors.pairs) {
      assert.equal(compareSemVer(a, b), expect, `${a} vs ${b}`);
    }
  });

  it('ignores build metadata for precedence', () => {
    for (const { a, b } of vectors.equalByPrecedence) {
      assert.equal(compareSemVer(a, b), 0, `${a} vs ${b}`);
    }
  });

  it('rejects every invalid string', () => {
    for (const value of vectors.invalid) {
      assert.equal(parseSemVer(value), null, `'${value}' must not parse`);
    }
  });

  it('selects the newest candidate strictly above the seed', () => {
    for (const { seed, candidates, expect } of vectors.latest) {
      let best = null;
      for (const candidate of candidates) {
        if (parseSemVer(candidate) === null) continue;
        if (compareSemVer(seed, candidate) >= 0) continue;
        if (best === null || compareSemVer(best, candidate) < 0) best = candidate;
      }
      assert.equal(best ?? null, expect, `latest > ${seed}`);
    }
  });
});
