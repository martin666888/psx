import { test } from 'vitest';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { repositoryRoot } from './agentHarness.js';

function sourceFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const fullPath = path.join(directory, entry.name);
    return entry.isDirectory() ? sourceFiles(fullPath) : [fullPath];
  });
}

test('scrollbars: explicit React overflow nodes opt into the shared native skin', () => {
  const sourceRoot = path.join(repositoryRoot, 'frontend', 'agent', 'src');
  const nativeOverflow = /\boverflow-(?:auto|scroll|x-auto|x-scroll|y-auto|y-scroll)\b/;

  for (const file of sourceFiles(sourceRoot).filter((candidate) => candidate.endsWith('.tsx'))) {
    const relative = path.relative(repositoryRoot, file);
    fs.readFileSync(file, 'utf8').split(/\r?\n/).forEach((line, index) => {
      if (!nativeOverflow.test(line)) return;
      assert.match(
        line,
        /\bagent-native-scroll\b/,
        `${relative}:${index + 1} must use agent-native-scroll`
      );
    });
  }
});

test('scrollbars: shared WebView skin covers the utility and emergency overlay', () => {
  const css = fs.readFileSync(
    path.join(repositoryRoot, 'frontend', 'webview', 'src', 'css', 'agent', 'scrollbars.css'),
    'utf8'
  );
  for (const pseudo of ['', '-track', '-thumb', '-button']) {
    assert.ok(
      css.includes(`.agent-native-scroll::-webkit-scrollbar${pseudo}`),
      `shared skin is missing ::-webkit-scrollbar${pseudo}`
    );
  }

  const entry = fs.readFileSync(path.join(repositoryRoot, 'frontend', 'webview', 'index.html'), 'utf8');
  assert.match(entry, /d\.className = 'agent-native-scroll'/);
});

test('scrollbars: WPF ScrollViewer bars consume the shared theme brushes', () => {
  const theme = fs.readFileSync(path.join(repositoryRoot, 'Themes', 'Dark.xaml'), 'utf8');
  assert.match(theme, /<Style TargetType="ScrollBar">/);
  assert.match(theme, /VerticalScrollbarTemplate/);
  assert.match(theme, /HorizontalScrollbarTemplate/);
  assert.match(theme, /DynamicResource ScrollbarBrush/);
  assert.match(theme, /DynamicResource ScrollbarHoverBrush/);
});
