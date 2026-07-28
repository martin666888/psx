// toolbarIsland.ts — mounts the React workspace toolbar.
//
// Static React imports are confined to island modules; the
// WorkspaceToolbarController reaches this file only through a dynamic import.

import { createElement } from 'react';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { WorkspaceToolbar, type WorkspaceToolbarProps } from './WorkspaceToolbarView.js';

export function mountToolbarIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<WorkspaceToolbarProps> {
  return mountReactIsland('workspace-toolbar', host, reportFailure, (props) =>
    createElement(WorkspaceToolbar, props)
  );
}
