// SessionToolbar.tsx — React twins of the SessionRuntimeController session
// Owns the toolbar metadata and Composer Context usage ring.

import type { JSX } from 'react';
import type { ContextUsageView } from './sessionFormat.js';

export interface SessionMetaProps {
  statusText: string;
  status: string;
  cwd: string;
  sessionLabel: string;
  changeCwdDisabled: boolean;
  changeCwdTitle: string;
  onPickCwd(): void;
}

export function SessionMeta(props: SessionMetaProps): JSX.Element {
  return (
    <>
      <span data-role="status" data-status={props.status}>
        {props.statusText}
      </span>
      <span data-role="cwd">{props.cwd}</span>
      <button
        data-role="change-cwd"
        className="agent-link-button"
        type="button"
        disabled={props.changeCwdDisabled}
        title={props.changeCwdTitle}
        onClick={props.onPickCwd}
      >
        Change
      </button>
      <span data-role="session">{props.sessionLabel}</span>
    </>
  );
}

export interface ContextUsageProps {
  view: ContextUsageView;
}

export function ContextUsage({ view }: ContextUsageProps): JSX.Element {
  return (
    <div
      data-role="context-used"
      className="agent-context-usage"
      role="img"
      tabIndex={0}
      data-context-state={view.state}
      aria-label={view.ariaLabel}
    >
      <svg className="agent-context-ring" viewBox="0 0 20 20" aria-hidden="true">
        <circle className="agent-context-ring-track" cx="10" cy="10" r="7.5" pathLength="100" />
        <circle
          data-role="context-ring-progress"
          className="agent-context-ring-progress"
          cx="10"
          cy="10"
          r="7.5"
          pathLength="100"
          strokeDashoffset={String(100 - view.percent)}
        />
      </svg>
      <span className="agent-context-tooltip" role="tooltip">
        <strong data-role="context-tooltip-summary">{view.summary}</strong>
        <span data-role="context-tooltip-detail">{view.detail}</span>
        <span data-role="context-tooltip-cost" hidden={!view.cost}>
          {view.cost}
        </span>
      </span>
    </div>
  );
}
