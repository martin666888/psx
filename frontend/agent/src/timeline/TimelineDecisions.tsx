// TimelineDecisions.tsx — React twins of the DecisionController thread cards
// (permission/question, mode-transition; elicitation renders a read-only
// notice pending full form support). DOM mirrors permissions.js /
// modeTransition.js so the produced markup matches the legacy engine.

import { useLayoutEffect, useRef, type JSX } from 'react';
import { renderMarkdown } from '../core/markdown.js';
import { decisionOptionClass } from '../decisions/DecisionController.js';
import type { DecisionItem, DecisionOptionVM } from './timelineViewModel.js';
import { CopyButton, useDetailsOpen } from './TimelineView.js';

export interface DecisionCallbacks {
  /** Permission/question option chosen (posts the bridge response). */
  onDecisionOption(item: DecisionItem, option: DecisionOptionVM): void;
  copyText(text: string): Promise<boolean>;
}

function PermissionQuestionCard({
  item,
  assistantName,
  callbacks
}: {
  item: DecisionItem;
  assistantName: string;
  callbacks: DecisionCallbacks;
}): JSX.Element {
  // legacy: the card starts open and only a local option click closes it;
  // external resolve/cancel leaves the user's toggle alone.
  const ref = useDetailsOpen(!item.collapsed);
  const disabled = item.decisionState !== 'active';
  const resolvedId = item.selectedOptionId;
  const resolvedName = item.selectedOptionName;
  const selected =
    item.options.find((option) => resolvedId && option.optionId === resolvedId) ||
    (resolvedName ? item.options.find((option) => option.name === resolvedName) : undefined);
  const subtitle = selected
    ? 'Selection recorded.'
    : item.kind === 'permission'
    ? assistantName + ' Agent needs your approval before continuing.'
    : assistantName + ' Agent is waiting for your answer.';
  // Selected option leads the row; the rest hide (legacy completeDecisionCard).
  const ordered = selected ? [selected, ...item.options.filter((option) => option !== selected)] : item.options;
  return (
    <details
      ref={ref}
      className={
        'agent-decision agent-decision-' + item.kind + (disabled ? ' agent-decision-disabled' : '')
      }
      data-decision-state={disabled ? 'disabled' : 'active'}
      data-request-id={item.requestId || undefined}
      data-selected-option-id={resolvedId || undefined}
      data-selected-option-name={resolvedName || undefined}
    >
      <summary className="agent-decision-header">
        <div className="agent-decision-header-content">
          <div className="agent-decision-header-title-row">
            <span className="agent-decision-chevron"></span>
            <span className="agent-decision-header-title">{item.title}</span>
          </div>
          <span className="agent-decision-header-subtitle">{subtitle}</span>
        </div>
      </summary>
      <div className="agent-decision-body">
        <details className="agent-decision-raw-input">
          <summary className="agent-decision-raw-input-summary">Raw Input</summary>
          <pre className="agent-decision-raw-input-content">{item.text}</pre>
        </details>
        <div className="agent-decision-actions">
          {item.options.length === 0 ? (
            <div className="agent-decision-status agent-decision-options-error">
              The Agent did not provide any response options.
            </div>
          ) : null}
          {ordered.map((option) => {
            const isSelected = selected === option;
            // legacy completeDecisionCard strips semantics from the selected
            // button only; hidden losers keep theirs.
            const semantic = isSelected ? '' : decisionOptionClass(option);
            return (
              <button
                key={option.optionId || option.name}
                type="button"
                className={
                  'agent-decision-option' +
                  (semantic ? ' ' + semantic : '') +
                  (isSelected ? ' agent-decision-option-selected' : '')
                }
                data-option-id={option.optionId}
                aria-pressed={isSelected ? 'true' : 'false'}
                hidden={!!selected && !isSelected}
                disabled={disabled}
                onClick={() => {
                  if (item.decisionState !== 'active') return;
                  callbacks.onDecisionOption(item, option);
                }}
              >
                {option.name}
              </button>
            );
          })}
        </div>
        {disabled && !selected && item.statusText ? (
          <div className="agent-decision-status">{item.statusText}</div>
        ) : null}
      </div>
    </details>
  );
}

function ModeTransitionCard({
  item,
  callbacks
}: {
  item: DecisionItem;
  callbacks: DecisionCallbacks;
}): JSX.Element {
  const pending = item.decisionState === 'active';
  const detailsRef = useRef<HTMLDetailsElement | null>(null);
  const openedOnce = useRef(false);
  useLayoutEffect(() => {
    if (openedOnce.current || !detailsRef.current) return;
    openedOnce.current = true;
    detailsRef.current.open = !item.historical;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  const statusText = item.statusText
    ? item.statusText
    : pending
    ? 'The Agent is waiting for your selection.'
    : item.selectedOptionId
    ? (() => {
        const selected = item.options.find((option) => option.optionId === item.selectedOptionId);
        return selected ? 'Selected: ' + selected.name : 'Selection recorded.';
      })()
    : 'This request is no longer active.';
  return (
    <section
      className="agent-mode-transition"
      data-decision-state={pending ? 'active' : 'disabled'}
      data-request-id={item.requestId || undefined}
    >
      <header className="agent-mode-transition-header">
        <div className="agent-mode-transition-title">{item.title}</div>
        <span className="agent-mode-transition-header-state">
          {pending ? 'Decision required' : item.headerState || 'Interrupted'}
        </span>
      </header>
      <details ref={detailsRef} className="agent-mode-transition-details">
        <summary className="agent-mode-transition-summary">Proposal details</summary>
        <div
          className="agent-mode-transition-document agent-message-body"
          dangerouslySetInnerHTML={{ __html: renderMarkdown(item.text) }}
        ></div>
        <div className="agent-message-actions agent-mode-transition-document-actions">
          <CopyButton getText={() => item.text} copyText={callbacks.copyText} />
        </div>
        <div className="agent-mode-transition-decision" hidden={pending}>
          <div className="agent-mode-transition-decision-label">Choose how to continue</div>
          <div className="agent-mode-transition-options" role="group" aria-label="Choose how to continue">
            {item.options.length === 0 ? (
              <div className="agent-mode-transition-error">
                The ACP Agent did not provide any response options.
              </div>
            ) : (
              item.options.map((option) => (
                <button
                  key={option.optionId || option.name}
                  type="button"
                  className={
                    'agent-mode-transition-option' +
                    (decisionOptionClass(option) ? ' ' + decisionOptionClass(option) : '') +
                    (option.optionId === item.selectedOptionId ? ' agent-mode-transition-option-selected' : '')
                  }
                  data-option-id={option.optionId}
                  data-option-kind={option.kind}
                  aria-pressed={option.optionId === item.selectedOptionId ? 'true' : 'false'}
                  disabled
                >
                  {option.name}
                </button>
              ))
            )}
          </div>
        </div>
        <div className="agent-mode-transition-status" aria-live="polite">
          {statusText}
        </div>
      </details>
    </section>
  );
}

export function DecisionCard({
  item,
  assistantName,
  callbacks
}: {
  item: DecisionItem;
  assistantName: string;
  callbacks: DecisionCallbacks;
}): JSX.Element {
  if (item.kind === 'mode_transition') {
    return <ModeTransitionCard item={item} callbacks={callbacks} />;
  }
  if (item.kind === 'elicitation') {
    // Elicitation forms keep the legacy renderer until the S3 switch lands;
    // in the unwired tree this is a placeholder card that never ships.
    return (
      <section className="agent-decision agent-decision-elicitation" data-decision-state={item.decisionState}>
        <div className="agent-decision-title">{item.title}</div>
        <div className="agent-decision-subtitle">{item.elicitationMessage}</div>
        {item.statusText ? <div className="agent-decision-status">{item.statusText}</div> : null}
      </section>
    );
  }
  return <PermissionQuestionCard item={item} assistantName={assistantName} callbacks={callbacks} />;
}
