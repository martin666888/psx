// composerIsland.ts — mounts the single React composer island.
//
// One island owns the whole composer subtree: ComposerView renders the
// PromptInput, attachments, command UI, controls, decision prompt and preview.
// Static React imports are confined to island modules; ComposerController
// reaches this file only through a dynamic import and supplies prop projections
// rather than mutating Composer-owned DOM.

import { createElement } from 'react';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { ComposerView, type ComposerViewProps } from './ComposerView.js';

export function mountComposerIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<ComposerViewProps> {
  return mountReactIsland('composer', host, reportFailure, (props) =>
    createElement(ComposerView, props)
  );
}
