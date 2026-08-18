// reactIsland.tsx — bridges the imperative island contract to the single
// Agent React root (CP3a).
//
// Controllers keep calling mountReactIsland exactly as before; instead of
// creating a root per island, each mount registers an entry with
// AgentAppRoot, which renders one portal per entry inside ONE React tree.
// Error boundaries and commit diagnostics live in AgentAppRoot's per-entry
// frame, so this module only adapts the IslandHandle lifecycle.

import type { ReactNode } from 'react';
import type {
  IslandFailureReporter,
  IslandHandle
} from './islandHost.js';
import { registerIsland } from './AgentAppRoot.js';

export function mountReactIsland<TProps>(
  name: string,
  host: HTMLElement,
  reportFailure: IslandFailureReporter,
  renderElement: (props: TProps) => ReactNode
): IslandHandle<TProps> {
  let disposed = false;
  const registered = registerIsland(name, host, reportFailure);

  return {
    render(props: TProps): void {
      if (disposed) return;
      registered.setElement(renderElement(props));
    },
    dispose(): void {
      if (disposed) return;
      disposed = true;
      registered.remove();
    }
  };
}
