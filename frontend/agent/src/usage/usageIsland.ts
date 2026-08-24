// usageIsland.ts — island entry for the global Usage panel dialog.
//
// Mirrors historyIsland.ts: one lazily imported React root, mounted by the
// UsagePanelController into its display:contents host.

import { createElement } from 'react';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { registerNamespace } from '../../../webview/src/i18n.js';
import settingsZhHans from './locales/zh-Hans/settings.json';
import settingsZhHant from './locales/zh-Hant/settings.json';
import settingsEn from './locales/en/settings.json';
import settingsJa from './locales/ja/settings.json';
import { UsagePanel, type UsagePanelProps } from './UsagePanel.js';

// The settings namespace ships with this chunk for every released language,
// so a language switch never awaits resources (atomic single-frame switch).
registerNamespace('settings', {
  'zh-Hans': settingsZhHans,
  'zh-Hant': settingsZhHant,
  en: settingsEn,
  ja: settingsJa
});

export function mountUsageIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<UsagePanelProps> {
  return mountReactIsland('usage-panel', host, reportFailure, (props) =>
    createElement(UsagePanel, props)
  );
}
