import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { describe, it } from 'node:test';
import { repositoryRoot, scriptPaths } from './productionBundle.js';

// The jsdom tests execute a concatenation of the production scripts, so the
// bundle must cover exactly the production Agent script set in the same order
// index.html uses in the real WebView. TerminalManager.js and main.js are the
// only intentionally unbundled js/ scripts — anything else added to
// index.html without updating scriptPaths (or vice versa) fails here.
const intentionallyNotBundled = new Set([
  'wwwroot/js/TerminalManager.js',
  'wwwroot/js/main.js'
]);

const indexHtml = fs.readFileSync(path.join(repositoryRoot, 'wwwroot', 'index.html'), 'utf8');
const indexScripts = [...indexHtml.matchAll(/<script src="(js\/[^"]+)"><\/script>/g)]
  .map((match) => 'wwwroot/' + match[1]);

describe('Production script order', () => {
  it('has no duplicate scripts on either side', () => {
    assert.equal(new Set(indexScripts).size, indexScripts.length, 'index.html loads a script twice');
    assert.equal(new Set(scriptPaths).size, scriptPaths.length, 'productionBundle.js lists a script twice');
  });

  it('bundles exactly the production Agent scripts in index.html order', () => {
    const productionAgentScripts = indexScripts.filter((script) => !intentionallyNotBundled.has(script));

    assert.deepEqual(productionAgentScripts, scriptPaths);
  });
});
