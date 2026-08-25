// SessionToolbar.tsx — React twins of the SessionRuntimeController session
// Owns the toolbar metadata and Composer Context usage ring.

import type { JSX } from 'react';
import { useTranslation } from 'react-i18next';
import type { ContextUsageView } from './sessionFormat.js';
import { Button } from '../components/ui/button.js';
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger
} from '../components/ui/tooltip.js';

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
  const { t } = useTranslation('agent');
  return (
    <>
      <span data-role="status" data-status={props.status}>
        {props.statusText}
      </span>
      <span data-role="cwd">{props.cwd}</span>
      <Button
        data-role="change-cwd"
        variant="link"
        size="sm"
        className="h-auto p-0 text-xs"
        disabled={props.changeCwdDisabled}
        title={props.changeCwdTitle}
        onClick={props.onPickCwd}
      >
        {t('session.change')}
      </Button>
      <span data-role="session">{props.sessionLabel}</span>
    </>
  );
}

export interface ContextUsageProps {
  view: ContextUsageView;
}

export function ContextUsage({ view }: ContextUsageProps): JSX.Element {
  const { t } = useTranslation('agent');
  const summary = t(view.summaryKey, { defaultValue: '', ...view.summaryParams });
  const detail = t(view.detailKey, { defaultValue: '', ...view.detailParams });
  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger asChild>
          <div
            data-role="context-used"
            className="agent-context-usage"
            role="img"
            tabIndex={0}
            data-context-state={view.state}
            aria-label={t('session.context.aria', { summary, detail })}
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
          </div>
        </TooltipTrigger>
        <TooltipContent className="agent-context-tooltip">
          <div className="flex flex-col">
            <strong data-role="context-tooltip-summary">{summary}</strong>
            <span data-role="context-tooltip-detail">{detail}</span>
            <span data-role="context-tooltip-cost" hidden={!view.costKey}>
              {view.costKey ? t(view.costKey, view.costParams) : ''}
            </span>
          </div>
        </TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}
