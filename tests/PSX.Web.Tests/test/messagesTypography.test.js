import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { repositoryRoot } from './agentHarness.js';

// Assistant body and tool chrome must share --agent-font-size rather than
// drifting onto fixed Tailwind text-sm/text-xs sizes. jsdom does not resolve
// CSS custom properties for getComputedStyle, so this pins the source contract.

test('messages.css keeps body/tool typography on the agent font contract', () => {
  const css = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent', 'messages.css'),
    'utf8'
  );

  assert.match(css, /\.agent-message-body\s*\{[^}]*font-size:\s*var\(--agent-font-size\);[^}]*line-height:\s*1\.6;/s);
  assert.match(
    css,
    /\.agent-run-group-header,\s*\n\.agent-tool-title\s*\{[^}]*font-size:\s*calc\(var\(--agent-font-size\)\s*\*\s*0\.93\);[^}]*line-height:\s*1\.5;/s
  );
  assert.match(
    css,
    /\.agent-tool-card-input-content,\s*\n\.agent-tool-card-content,\s*\n\.agent-tool-output\s*\{[^}]*font-size:\s*clamp\(11px,\s*calc\(var\(--agent-font-size\)\s*\*\s*0\.8\),\s*18px\);[^}]*line-height:\s*1\.5;/s
  );
});

test('Timeline tool chrome does not hard-code Tailwind text-sm/text-xs on sized text', () => {
  const source = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'agent', 'src', 'timeline', 'TimelineView.tsx'),
    'utf8'
  );
  assert.match(source, /agent-run-group-header[^"]*(?!text-sm)[^"]*"/);
  assert.match(source, /className="agent-run-group-header[^"]*"/);
  assert.doesNotMatch(source, /agent-run-group-header[^"]*text-sm/);
  assert.doesNotMatch(source, /agent-tool-card-input-content[^"]*text-xs/);
  assert.doesNotMatch(source, /agent-tool-card-content[^"]*text-xs/);
  assert.match(source, /agent-tool-output agent-native-scroll/);
  assert.match(source, /agent-message-continuation/);
  assert.match(source, /tightenToPrevious \? ' -mt-2' : ''/);
  assert.doesNotMatch(source, /agent-message-continuation mb-4 -mt-2/);
  assert.match(source, /' mb-4'/);
  assert.doesNotMatch(source, /agent-message-[^']*mb-5/);
});
