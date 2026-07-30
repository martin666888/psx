// usageIsland.ts — island entry for the global Usage panel dialog.
//
// Mirrors historyIsland.ts: one lazily imported React root, mounted by the
// UsagePanelController into its display:contents host.

import { createElement } from 'react';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { UsagePanel, type UsagePanelProps } from './UsagePanel.js';

export function mountUsageIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<UsagePanelProps> {
  return mountReactIsland('usage-panel', host, reportFailure, (props) =>
    createElement(UsagePanel, props)
  );
}
