// SessionRuntimeCard.tsx — the React pilot component (dev-react validation).
//
// Renders the runtime install card as a faithful DOM equivalent of
// SessionRuntimeController.renderRuntime + the wwwroot/index.html template
// markup: same element structure, classes, data attributes, aria attributes,
// button labels and hidden/disabled behavior. Pure props-driven — the island
// re-renders it from the reduced state on every runtime_status event, and the
// install/cancel intents flow back through props callbacks to the existing
// bridge path.

import { useLayoutEffect } from 'react';
import type { JSX } from 'react';
import type { WorkspaceRuntimeState } from '../contracts/workspace-state.js';

const RUNTIME_TITLES: Readonly<Record<string, string>> = {
  missing: 'Agent runtime required',
  installing: 'Installing Agent runtime',
  failed: 'Agent runtime installation failed',
  cancelled: 'Agent runtime installation cancelled'
};

export interface SessionRuntimeCardProps {
  runtime: WorkspaceRuntimeState;
  onInstall: () => void;
  onCancel: () => void;
  /**
   * Fires after the first commit is flushed to the DOM (useLayoutEffect), so
   * the island can atomically retire the legacy card without a blank frame.
   */
  onCommitted: () => void;
}

export function SessionRuntimeCard({ runtime, onInstall, onCancel, onCommitted }: SessionRuntimeCardProps): JSX.Element {
  useLayoutEffect(() => {
    onCommitted();
  }, [onCommitted]);

  return (
    <section
      data-role="runtime-card"
      className="agent-runtime-card"
      aria-live="polite"
      data-state={runtime.state}
      aria-busy={runtime.state === 'installing'}
      hidden={runtime.state === 'ready'}
    >
      <div className="agent-runtime-indicator" aria-hidden="true"></div>
      <div className="agent-runtime-copy">
        <h2 data-role="runtime-title">{RUNTIME_TITLES[runtime.state] || 'Agent runtime'}</h2>
        <p data-role="runtime-message">{runtime.message}</p>
        <p className="agent-runtime-note">
          Downloads pinned components from the official npm registry into this PSX folder and reuses them on later
          launches.
        </p>
      </div>
      <div className="agent-runtime-actions">
        <button
          data-role="runtime-install"
          type="button"
          hidden={!runtime.canInstall}
          disabled={!runtime.canInstall}
          onClick={onInstall}
        >
          {runtime.state === 'missing' ? 'Install runtime' : 'Retry installation'}
        </button>
        <button
          data-role="runtime-cancel"
          type="button"
          hidden={!runtime.canCancel}
          disabled={!runtime.canCancel}
          onClick={onCancel}
        >
          Cancel
        </button>
      </div>
    </section>
  );
}
