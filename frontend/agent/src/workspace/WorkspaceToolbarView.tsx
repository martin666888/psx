// WorkspaceToolbarView.tsx — the single React view for the workspace toolbar.
//
// One island (host: [data-role="toolbar-host"]) renders the history toggle,
// the session meta line, the plan toggle (with unread dot) and the runtime
// Update button. The WorkspaceToolbarController is the only setElement
// writer; the Session/History/Plan controllers push their slices through it,
// so each state kind keeps exactly one authoritative owner.

import type { JSX, RefObject } from 'react';
import { Button } from '../components/ui/button.js';
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger
} from '../components/ui/tooltip.js';
import { SessionMeta, type SessionMetaProps } from './SessionToolbar.js';

export interface WorkspaceToolbarProps {
  session: SessionMetaProps | null;
  history: {
    open: boolean;
    onToggle(): void;
    toggleRef: RefObject<HTMLButtonElement | null>;
  };
  plan: {
    visible: boolean;
    unread: boolean;
    onToggle(): void;
    toggleRef: RefObject<HTMLButtonElement | null>;
  };
  update: {
    // idle | checking | up_to_date | staged_restart_required | unsupported
    // | install_required | unavailable | failed
    state: string;
    currentVersion: string;
    pendingVersion: string;
    onRequest(): void;
  };
}

const UPDATE_DISABLED_STATES: ReadonlySet<string> = new Set([
  'unsupported',
  'install_required',
  'unavailable',
  'checking',
  'staged_restart_required'
]);

function updateButtonLabel(state: string): string {
  switch (state) {
    case 'checking':
      return 'Checking…';
    case 'up_to_date':
      return 'Up to date';
    case 'staged_restart_required':
      return 'Restart to update';
    case 'failed':
      return 'Retry update';
    default:
      return 'Update';
  }
}

function updateButtonTitle(update: WorkspaceToolbarProps['update']): string {
  if (update.state === 'unsupported') return 'Updates ship with PSX releases';
  if (update.state === 'install_required') return 'Install the Agent runtime first';
  if (update.state === 'unavailable') return 'Updates are not available for this workspace';
  if (update.state === 'staged_restart_required') {
    return update.pendingVersion
      ? 'Update to ' + update.pendingVersion + ' is ready; restart PSX to apply'
      : 'Update is ready; restart PSX to apply';
  }
  return update.currentVersion
    ? 'Check for Agent runtime updates (current: ' + update.currentVersion + ')'
    : 'Check for Agent runtime updates';
}

function HistoryIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true" focusable="false">
      <path
        d="M2.5 3h11v10h-11z M6 3v10"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.2"
        strokeLinejoin="round"
      />
    </svg>
  );
}

function PlanIcon(): JSX.Element {
  return (
    <svg viewBox="0 0 16 16" width="14" height="14" aria-hidden="true" focusable="false">
      <path
        d="M6.5 4h7M6.5 8h7M6.5 12h7"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.3"
        strokeLinecap="round"
      />
      <circle cx="3" cy="4" r="1.1" fill="currentColor" />
      <circle cx="3" cy="8" r="1.1" fill="currentColor" />
      <circle cx="3" cy="12" r="1.1" fill="currentColor" />
    </svg>
  );
}

export function WorkspaceToolbar(props: WorkspaceToolbarProps): JSX.Element {
  const planLabel = props.plan.unread ? 'Toggle Plan card, plan updated' : 'Toggle Plan card';
  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            ref={props.history.toggleRef}
            data-role="history-toggle"
            type="button"
            variant="ghost"
            size="icon"
            className={
              'agent-icon-toggle agent-history-toggle size-7 border border-input' +
              (props.history.open ? '' : ' text-muted-foreground')
            }
            aria-label="Toggle Agent history"
            aria-expanded={props.history.open}
            onClick={props.history.onToggle}
          >
            <HistoryIcon />
          </Button>
        </TooltipTrigger>
        <TooltipContent>Agent history</TooltipContent>
      </Tooltip>
      <div className="agent-meta">{props.session ? <SessionMeta {...props.session} /> : null}</div>
      <div className="agent-toolbar-actions">
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              ref={props.plan.toggleRef}
              data-role="plan-toggle"
              type="button"
              variant="ghost"
              size="icon"
              className={
                'agent-icon-toggle agent-plan-toggle relative size-7 border border-input' +
                (props.plan.visible ? '' : ' text-muted-foreground')
              }
              aria-label={planLabel}
              aria-expanded={props.plan.visible}
              onClick={props.plan.onToggle}
            >
              <PlanIcon />
              <span
                data-role="plan-toggle-unread"
                className="agent-plan-toggle-unread"
                aria-label="Plan updated"
                hidden={!props.plan.unread}
              ></span>
            </Button>
          </TooltipTrigger>
          <TooltipContent>{planLabel}</TooltipContent>
        </Tooltip>
        <Button
          data-role="update"
          data-update-state={props.update.state}
          type="button"
          variant="ghost"
          size="sm"
          className="h-7 border border-input text-xs"
          disabled={UPDATE_DISABLED_STATES.has(props.update.state)}
          title={updateButtonTitle(props.update)}
          onClick={props.update.onRequest}
        >
          {updateButtonLabel(props.update.state)}
        </Button>
      </div>
    </TooltipProvider>
  );
}
