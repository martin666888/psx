// PlanCard.tsx — React twin of PlanController.renderPlan.
//
// Renders the plan panel content (inside data-role="plan-panel"). CP4 area 3
// swaps the visuals onto the AI Elements TaskItem look (lucide status icons,
// muted typography) while keeping the semantic contract: entry list with
// status class, aria-label, optional data-priority, and positional keys so
// unchanged rows keep their DOM nodes across renders (node-identity test).

import type { JSX } from 'react';
import type { WorkspacePlanState } from '../contracts/workspace-state.js';
import { planStatusClass, planStatusLabel } from '../core/plan.js';
import { CheckCircle2Icon, CircleDotIcon, CircleIcon } from 'lucide-react';

export interface PlanCardProps {
  plan: WorkspacePlanState;
}

function StatusIcon({ status }: { status: string }): JSX.Element {
  const statusClass = planStatusClass(status);
  if (statusClass === 'agent-plan-item-completed') {
    return <CheckCircle2Icon className="size-3.5 text-primary" aria-hidden="true" />;
  }
  if (statusClass === 'agent-plan-item-in-progress') {
    return <CircleDotIcon className="size-3.5 text-primary" aria-hidden="true" />;
  }
  return <CircleIcon className="size-3.5 text-muted-foreground" aria-hidden="true" />;
}

export function PlanCard({ plan }: PlanCardProps): JSX.Element {
  if (!plan.active) {
    return <div className="agent-plan-empty text-[13px] text-muted-foreground">No active plan</div>;
  }
  if (plan.entries.length === 0) {
    return (
      <pre className="agent-plan-fallback m-0 whitespace-pre-wrap font-mono text-muted-foreground">
        {plan.fallbackText || 'No plan items.'}
      </pre>
    );
  }
  return (
    <ol className="agent-plan-list">
      {plan.entries.map((entry, index) => (
        <li
          key={index}
          className={'agent-plan-item ' + planStatusClass(entry.status)}
          aria-label={planStatusLabel(entry.status) + ': ' + entry.content}
          data-priority={entry.priority || undefined}
        >
          <span className="agent-plan-marker flex justify-center pt-0.5">
            <StatusIcon status={entry.status} />
          </span>
          <span className="agent-plan-content">{entry.content}</span>
        </li>
      ))}
    </ol>
  );
}
