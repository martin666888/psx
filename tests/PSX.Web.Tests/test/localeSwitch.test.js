// localeSwitch.test.js — four-language sampling acceptance: switching the
// shared i18n instance must render every sampled surface in the target
// language through the statically bundled namespaces (shell / agent /
// settings). Runs in its own guarded process like every other file.

import { test } from 'vitest';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { i18n, applyDocumentLang } from '../../../frontend/webview/src/i18n.js';

// This file exercises the bare i18n instance (no agentHarness window); a
// minimal DOM is enough for the <html lang> assertions.
const dom = new JSDOM('<!doctype html><html lang="zh-Hans"><body></body></html>');
globalThis.window = dom.window;
globalThis.document = dom.window.document;

const SAMPLES = [
  {
    locale: 'zh-Hans',
    menuCreate: '新建工作区',
    attentionPermission: '需确认',
    dshFailedTitle: '运行时不可用',
    toolbarUpdate: '更新',
    settingsLanguage: '语言',
    composerConfigAria: 'Agent 配置'
  },
  {
    locale: 'zh-Hant',
    menuCreate: '新增工作區',
    attentionPermission: '需要確認',
    dshFailedTitle: '執行階段無法使用',
    toolbarUpdate: '更新',
    settingsLanguage: '語言',
    composerConfigAria: 'Agent 設定'
  },
  {
    locale: 'en',
    menuCreate: 'New workspace',
    attentionPermission: 'Needs confirmation',
    dshFailedTitle: 'Runtime unavailable',
    toolbarUpdate: 'Update',
    settingsLanguage: 'Language',
    composerConfigAria: 'Agent configuration'
  },
  {
    locale: 'ja',
    menuCreate: '新規ワークスペース',
    attentionPermission: '要確認',
    dshFailedTitle: 'ランタイムを利用できません',
    toolbarUpdate: '更新',
    settingsLanguage: '言語',
    composerConfigAria: 'Agent 設定'
  }
];

test.afterEach(async () => {
  // Restore the default language so per-file isolation stays boring.
  await i18n.changeLanguage('zh-Hans');
});

for (const sample of SAMPLES) {
  test(`language switch renders shell, agent and settings surfaces in ${sample.locale}`, async () => {
    await i18n.changeLanguage(sample.locale);
    assert.equal(i18n.resolvedLanguage, sample.locale);
    // Harness teardown may swap the jsdom window between tests, so assert the
    // lang write synchronously instead of racing the changeLanguage callback.
    applyDocumentLang();
    assert.equal(document.documentElement.lang, sample.locale);

    assert.equal(i18n.t('menu.create', { ns: 'shell' }), sample.menuCreate);
    assert.equal(
      i18n.t('attention.permission', { ns: 'shell' }),
      sample.attentionPermission
    );
    assert.equal(
      i18n.t('dsh.card.failedTitle', { ns: 'shell' }),
      sample.dshFailedTitle
    );
    assert.equal(
      i18n.t('toolbar.update', { ns: 'agent' }),
      sample.toolbarUpdate
    );
    assert.equal(
      i18n.t('section.language', { ns: 'settings' }),
      sample.settingsLanguage
    );
    assert.equal(
      i18n.t('composer.compactConfigAria', { ns: 'agent' }),
      sample.composerConfigAria
    );
    assert.equal(document.documentElement.lang, sample.locale);
  });
}

test('interpolation works after a language switch', async () => {
  await i18n.changeLanguage('en');
  assert.equal(
    i18n.t('tab.closeTitle', { ns: 'shell', title: 'Terminal' }),
    'Close Terminal'
  );
  await i18n.changeLanguage('ja');
  assert.equal(
    i18n.t('tab.closeTitle', { ns: 'shell', title: 'Terminal' }),
    'Terminal を閉じる'
  );
});
