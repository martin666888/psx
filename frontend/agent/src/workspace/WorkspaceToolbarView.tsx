// WorkspaceToolbarView.tsx — the single React view for the workspace toolbar.
//
// One island (host: [data-role="toolbar-host"]) renders the session meta
// line, the plan toggle and the runtime
// Update button. The WorkspaceToolbarController is the only setElement
// writer; the Session/History/Plan controllers push their slices through it,
// so each state kind keeps exactly one authoritative owner.

import { useCallback, useRef, useState, type JSX, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import { EllipsisIcon } from 'lucide-react';
import { Button } from '../components/ui/button.js';
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger
} from '../components/ui/tooltip.js';
import { SessionMeta, type SessionMetaProps } from './SessionToolbar.js';
import { runtimeUpdateFailureLabel } from './runtimeCopy.js';
import { useDismissibleLayer } from '../components/use-dismissible-layer.js';

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
    // Fixed backend outcome code (Models/RuntimeStatusCode.cs); the tooltip
    // maps it to copy for failed checks.
    messageCode: string;
    currentVersion: string;
    pendingVersion: string;
    // Toolbar-facing product string (e.g. "Product v2.1.0"); '' hides the
    // resident version text (uninstalled / transcript-only / unavailable).
    versionLabel: string;
    // Tooltip-only ACP/runtime technical detail.
    versionDetail: string;
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

function updateButtonLabel(state: string, t: TFunction<'agent'>): string {
  const key = {
    checking: 'toolbar.checking',
    up_to_date: 'toolbar.upToDate',
    staged_restart_required: 'toolbar.stagedRestart',
    failed: 'toolbar.retryUpdate'
  }[state];
  return key ? t(key, { defaultValue: t('toolbar.update') }) : t('toolbar.update');
}

function updateButtonTitle(
  update: WorkspaceToolbarProps['update'],
  t: TFunction<'agent'>
): string {
  if (update.state === 'unsupported') return t('toolbar.unsupportedTitle');
  if (update.state === 'install_required') return t('toolbar.installRequiredTitle');
  if (update.state === 'unavailable') return t('toolbar.unavailableTitle');

  // Every other state aggregates the same tooltip: the current version, any
  // staged/pending version, the ACP/runtime technical detail and (on failure)
  // the backend reason. Blank slices drop out so short states stay terse.
  const lines: string[] = [];
  if (update.currentVersion) lines.push(t('toolbar.currentVersion', { version: update.currentVersion }));
  if (update.pendingVersion) lines.push(t('toolbar.pendingVersion', { version: update.pendingVersion }));
  if (update.versionDetail) lines.push(update.versionDetail);

  if (update.state === 'failed') {
    lines.push(runtimeUpdateFailureLabel(update.messageCode) || t('toolbar.failedTooltip'));
  } else if (update.state === 'staged_restart_required') {
    lines.push(
      update.pendingVersion
        ? t('toolbar.stagedWithPending')
        : t('toolbar.stagedWithoutPending')
    );
  } else {
    lines.push(t('toolbar.checkTooltip'));
  }
  return lines.join('\n');
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
  // This hook is intentionally owned by the toolbar root: disabled runtime
  // actions and their tooltips must re-render on languageChanged even when no
  // runtime_update_status event arrives at the same time.
  const { t } = useTranslation('agent');
  const planLabel = t(
    props.plan.unread ? 'toolbar.planToggleUnread' : 'toolbar.planToggle',
  );
  const [moreOpen, setMoreOpen] = useState(false);
  const moreRef = useRef<HTMLDivElement | null>(null);
  const moreTriggerRef = useRef<HTMLButtonElement | null>(null);
  const closeMore = useCallback(() => setMoreOpen(false), []);
  useDismissibleLayer({
    open: moreOpen,
    rootRef: moreRef,
    triggerRef: moreTriggerRef,
    onDismiss: closeMore
  });

  return (
    <TooltipProvider>
      <div className="agent-meta">{props.session ? <SessionMeta {...props.session} /> : null}</div>
      <div
        ref={moreRef}
        className="agent-toolbar-more"
        data-open={moreOpen ? 'true' : 'false'}
      >
        <button
          ref={moreTriggerRef}
          type="button"
          className="agent-toolbar-more-trigger"
          aria-label={t('toolbar.moreAria')}
          aria-expanded={moreOpen}
          onClick={() => setMoreOpen((open) => !open)}
        >
          <EllipsisIcon className="size-4" />
        </button>
        <div className="agent-toolbar-more-content">
          {props.session ? (
            <div className="agent-toolbar-more-session">
              <span>{props.session.cwd}</span>
              <Button
                variant="link"
                size="sm"
                disabled={props.session.changeCwdDisabled}
                title={props.session.changeCwdTitle}
                onClick={props.session.onPickCwd}
              >
                {t('session.change')}
              </Button>
              {props.session.sessionLabel ? <small>{props.session.sessionLabel}</small> : null}
            </div>
          ) : null}
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
          title={updateButtonTitle(props.update, t)}
          onClick={props.update.onRequest}
        >
          {/* Resident product version, hidden below a narrow toolbar and when
            * there is no known version (never a misleading v0/Unknown). The
            * Update button itself always stays visible (hiding is driven by
            * the shell narrow class in shell.css). */}
          {props.update.versionLabel ? (
            <span
              data-role="update-version"
              className="agent-update-version mr-2 truncate text-muted-foreground"
            >
              {props.update.versionLabel}
            </span>
          ) : null}
          {updateButtonLabel(props.update.state, t)}
        </Button>
          </div>
        </div>
      </div>
    </TooltipProvider>
  );
}
