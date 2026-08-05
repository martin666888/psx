// runtimeIsland.ts — thin mount/unmount wrapper around the React runtime-card
// island. This is the ONLY module that statically imports React: the
// controller reaches it exclusively through a dynamic import, so a session
// that never shows the runtime card never loads any React code.

import { createElement } from 'react';
import { SessionRuntimeCard } from './SessionRuntimeCard.js';
import type { WorkspaceRuntimeState } from '../contracts/workspace-state.js';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';

export interface RuntimeIslandHandlers {
  onInstall: () => void;
  onCancel: () => void;
}

export function mountRuntimeIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter,
  handlers: RuntimeIslandHandlers
): IslandHandle<WorkspaceRuntimeState> {
  return mountReactIsland('runtime-card', host, reportFailure, (runtime) =>
      createElement(SessionRuntimeCard, {
        runtime,
        onInstall: handlers.onInstall,
        onCancel: handlers.onCancel
      })
  );
}
