// sessionIsland.ts — mounts the React Context usage island.
//
// The Context usage ring (host: .agent-hints in the composer footer) keeps
// its own island until the ComposerView shell absorbs it; the session meta
// line moved into the workspace toolbar island (2b). Static React imports
// are confined to island modules; SessionRuntimeController reaches this file
// only through a dynamic import. createRoot takes ownership of the host's
// children — the template-rendered defaults are replaced atomically by the
// first commit — and dispose unmounts while leaving the host nodes to the
// panel lifecycle.

import { createElement } from 'react';
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import { ContextUsage, type ContextUsageProps } from './SessionToolbar.js';

export function mountContextUsageIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<ContextUsageProps> {
  return mountReactIsland('context-usage', host, reportFailure, (props) =>
    createElement(ContextUsage, props)
  );
}
