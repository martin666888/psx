// runtimeIsland.ts — thin mount/unmount wrapper around the React runtime-card
// island. This is the ONLY module that statically imports React: the
// controller reaches it exclusively through a dynamic import guarded by the
// experimental flag, so a flag-off session never loads any vendor/react code.

import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { SessionRuntimeCard } from './SessionRuntimeCard.js';
import type { WorkspaceRuntimeState } from '../contracts/workspace-state.js';

export interface RuntimeIslandHandlers {
  onInstall: () => void;
  onCancel: () => void;
  /** Fires once the first commit reaches the DOM (see SessionRuntimeCard). */
  onCommitted: () => void;
}

export interface RuntimeIslandHandle {
  render(runtime: WorkspaceRuntimeState): void;
  /** Unmounts the root and removes the host node from the document. */
  dispose(): void;
}

export function mountRuntimeIsland(host: HTMLElement, handlers: RuntimeIslandHandlers): RuntimeIslandHandle {
  const root = createRoot(host);
  let disposed = false;
  return {
    render(runtime: WorkspaceRuntimeState): void {
      if (disposed) return;
      root.render(
        createElement(SessionRuntimeCard, {
          runtime,
          onInstall: handlers.onInstall,
          onCancel: handlers.onCancel,
          onCommitted: handlers.onCommitted
        })
      );
    },
    dispose(): void {
      if (disposed) return;
      disposed = true;
      root.unmount();
      host.remove();
    }
  };
}
