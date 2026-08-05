// SessionRuntimeCard.tsx — the runtime install card.
//
// CP4 area 3: visuals move onto shadcn Button + lucide status icons +
// Tailwind card styling; the behavioral contract is untouched — same
// data-role/data-state/aria attributes, hidden/disabled behavior and button
// labels, verified by reactRuntimeCard.test. Pure props-driven; install and
// cancel intents flow back through props callbacks to the existing bridge
// path. The grid/reading-column layout stays in runtime.css (shell wiring).

import type { JSX } from 'react';
import type { WorkspaceRuntimeState } from '../contracts/workspace-state.js';
import { Button } from '../components/ui/button.js';
import { LoaderCircleIcon, TriangleAlertIcon, XCircleIcon } from 'lucide-react';

const RUNTIME_TITLES: Readonly<Record<string, string>> = {
  missing: 'Agent runtime required',
  installing: 'Installing Agent runtime',
  failed: 'Agent runtime installation failed',
  cancelled: 'Agent runtime installation cancelled',
  external_missing: 'Qoder CLI required',
  external_unsupported_version: 'Qoder CLI version unsupported'
};

function isRuntimeCardHidden(state: string): boolean {
  return state === 'ready' || state === 'external_ready';
}

function RuntimeIndicator({ state }: { state: string }): JSX.Element {
  if (state === 'installing') {
    return (
      <span className="agent-runtime-indicator" aria-hidden="true">
        <LoaderCircleIcon className="size-4 animate-spin text-primary" />
      </span>
    );
  }
  if (state === 'failed' || state === 'cancelled') {
    return (
      <span className="agent-runtime-indicator" aria-hidden="true">
        <XCircleIcon className="size-4 text-destructive" />
      </span>
    );
  }
  return (
    <span className="agent-runtime-indicator" aria-hidden="true">
      <TriangleAlertIcon className="size-4 text-yellow-600" />
    </span>
  );
}

export interface SessionRuntimeCardProps {
  runtime: WorkspaceRuntimeState;
  onInstall: () => void;
  onCancel: () => void;
  onOpenGuide?: () => void;
  onCopyGuideCommand?: () => void;
}

export function SessionRuntimeCard({
  runtime,
  onInstall,
  onCancel,
  onOpenGuide,
  onCopyGuideCommand
}: SessionRuntimeCardProps): JSX.Element {
  const isExternal = runtime.ownership === 'external';

  return (
    <section
      data-role="runtime-card"
      className={
        'agent-runtime-card rounded-lg border text-card-foreground ' +
        (runtime.state === 'failed' || runtime.state === 'cancelled'
          ? 'border-destructive/50 bg-destructive/10'
          : 'bg-card')
      }
      aria-live="polite"
      data-state={runtime.state}
      aria-busy={runtime.state === 'installing'}
      hidden={isRuntimeCardHidden(runtime.state)}
    >
      <RuntimeIndicator state={runtime.state} />
      <div className="agent-runtime-copy min-w-0">
        <h2 className="m-0 font-semibold text-sm leading-snug" data-role="runtime-title">
          {RUNTIME_TITLES[runtime.state] || 'Agent runtime'}
        </h2>
        <p className="mt-1 mb-0 break-words text-muted-foreground text-xs leading-normal" data-role="runtime-message">
          {runtime.message}
        </p>
        {runtime.ownershipLabel ? (
          <p
            className="agent-runtime-note mt-1 mb-0 text-muted-foreground text-xs leading-normal"
            data-role="runtime-ownership"
          >
            {runtime.ownershipLabel}
          </p>
        ) : null}
        {!isExternal ? (
          <p className="agent-runtime-note mt-1 mb-0 text-muted-foreground text-xs leading-normal">
            Downloads pinned components from the official npm registry into this PSX folder and reuses them on later
            launches.
          </p>
        ) : null}
      </div>
      <div className="agent-runtime-actions flex items-center gap-2">
        {runtime.canGuide ? (
          <>
            <Button
              data-role="runtime-guide-open"
              size="sm"
              disabled={!runtime.guideUrl}
              onClick={onOpenGuide}
            >
              打开安装文档
            </Button>
            <Button
              data-role="runtime-guide-copy"
              size="sm"
              variant="outline"
              disabled={!runtime.guideCommand}
              onClick={onCopyGuideCommand}
            >
              复制安装命令
            </Button>
          </>
        ) : (
          <>
            <Button
              data-role="runtime-install"
              size="sm"
              hidden={!runtime.canInstall}
              disabled={!runtime.canInstall}
              onClick={onInstall}
            >
              {runtime.state === 'missing' ? 'Install runtime' : 'Retry installation'}
            </Button>
            <Button
              data-role="runtime-cancel"
              size="sm"
              variant="outline"
              hidden={!runtime.canCancel}
              disabled={!runtime.canCancel}
              onClick={onCancel}
            >
              Cancel
            </Button>
          </>
        )}
      </div>
    </section>
  );
}
