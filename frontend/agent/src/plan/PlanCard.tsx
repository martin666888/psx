// PlanCard.tsx — React twin of PlanController.renderPlan.
//
// Renders the plan panel content (inside data-role="plan-panel") with DOM
// output equivalent to the legacy renderer: the empty state, the plain-text
// fallback and the entry list with status class, marker, aria-label and the
// optional data-priority. Entries keep positional keys: plan updates only
// ever append or restate the same ordered list, so unchanged rows keep their
// DOM nodes across renders (verified by the node-identity test).

import type { JSX } from 'react';
import type { WorkspacePlanState } from '../contracts/workspace-state.js';
import { planStatusClass, planStatusLabel, planStatusMarker } from '../core/plan.js';

export interface PlanCardProps {
  plan: WorkspacePlanState;
}

export function PlanCard({ plan }: PlanCardProps): JSX.Element {
  if (!plan.active) {
    return <div className="agent-plan-empty">No active plan</div>;
  }
  if (plan.entries.length === 0) {
    return <pre className="agent-plan-fallback">{plan.fallbackText || 'No plan items.'}</pre>;
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
          <span className="agent-plan-marker">{planStatusMarker(entry.status)}</span>
          <span className="agent-plan-content">{entry.content}</span>
        </li>
      ))}
    </ol>
  );
}
