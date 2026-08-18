import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { test } from 'vitest';
import { repositoryRoot } from './agentHarness.js';

const moduleUrl = pathToFileURL(
  path.join(repositoryRoot, 'frontend', 'webview', 'src', 'colorScheme.js')
).href;

test('colorSchemeForBackground maps theme hex to dark or light', async () => {
  const { colorSchemeForBackground } = await import(moduleUrl);
  assert.equal(colorSchemeForBackground('#111111'), 'dark');
  assert.equal(colorSchemeForBackground('#000'), 'dark');
  assert.equal(colorSchemeForBackground('#ffffff'), 'light');
  assert.equal(colorSchemeForBackground('#FFF'), 'light');
  assert.equal(colorSchemeForBackground('not-a-color'), null);
  assert.equal(colorSchemeForBackground(undefined), null);
});
