// PlanCard.tsx — React twin of PlanController.renderPlan.
//
// Renders the WHOLE plan card inside data-role="plan-card": the shadcn Card
// shell, the "Plan" header and the entry list (CP4 area 3, commit 2d). The
// Card is structural only — border/background/shadow/radius stay on the
// shell-owned .agent-plan-card section CSS (theme tokens + hidden/entry
// animation), so the Card is chrome-neutralized to avoid a card-in-card.
// Entries keep the semantic contract: agent-plan-item status class,
// aria-label, optional data-priority, and positional keys so unchanged rows
// keep their DOM nodes across renders (node-identity test). The scrollable
// body keeps the agent-plan-panel class/data-role (panel scroll CSS and
// scrollbar styling key off it).

import type { JSX } from 'react';
import type { WorkspacePlanState } from '../contracts/workspace-state.js';
import { planStatusClass, planStatusLabel } from '../core/plan.js';
import { renderMarkdown } from '../core/markdown.js';
import { Card, CardContent, CardHeader, CardTitle } from '../components/ui/card.js';
import { TaskItem } from '../components/ai-elements/task.js';
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

function PlanBody({ plan }: PlanCardProps): JSX.Element {
  if (!plan.active) {
    return <div className="agent-plan-empty text-[13px] text-muted-foreground">No active plan</div>;
  }
  if (plan.entries.length === 0) {
    // Document mode: a plan without checklist entries is a full plan
    // document — render it through the shared safe Markdown pipeline
    // instead of dumping preformatted text.
    return (
      <div
        className="agent-plan-document agent-message-body px-3 pb-3 text-[13px] leading-normal"
        dangerouslySetInnerHTML={{ __html: renderMarkdown(plan.fallbackText || 'No plan items.') }}
      ></div>
    );
  }
  return (
    <div className="agent-plan-list" role="list">
      {plan.entries.map((entry, index) => (
        <TaskItem
          key={index}
          role="listitem"
          className={'agent-plan-item ' + planStatusClass(entry.status)}
          aria-label={planStatusLabel(entry.status) + ': ' + entry.content}
          data-priority={entry.priority || undefined}
        >
          <span className="agent-plan-marker flex justify-center pt-0.5">
            <StatusIcon status={entry.status} />
          </span>
          <span className="agent-plan-content">{entry.content}</span>
        </TaskItem>
      ))}
    </div>
  );
}

export function PlanCard({ plan }: PlanCardProps): JSX.Element {
  return (
    <Card className="min-h-0 grow gap-0 rounded-none border-0 bg-transparent py-0 shadow-none">
      <CardHeader className="flex min-h-9 flex-row items-center gap-2 px-2 pl-3">
        <CardTitle className="min-w-0 flex-1 truncate text-[13px]">Plan</CardTitle>
      </CardHeader>
      <CardContent
        data-role="plan-panel"
        className="agent-plan-panel border-t border-[var(--agent-border)] px-0"
      >
        <PlanBody plan={plan} />
      </CardContent>
    </Card>
  );
}
