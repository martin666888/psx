// TimelineDecisions.tsx — React twins of the DecisionController thread cards
// (permission/question, mode-transition; elicitation renders a read-only
// notice pending full form support). DOM mirrors permissions.js /
// modeTransition.js so the produced markup matches the legacy engine.

import { useLayoutEffect, useRef, useState, type JSX } from 'react';
import {
  defaultOptionValue,
  orderElicitationFields,
  readElicitationOptions,
  type ElicitationOption,
  type ElicitationProperty
} from '../core/elicitation.js';
import { renderMarkdown, safeHref } from '../core/markdown.js';
import { decisionOptionClass } from '../decisions/DecisionController.js';
import type { DecisionItem, DecisionOptionVM } from './timelineViewModel.js';
import { CopyButton, useDetailsOpen } from './TimelineView.js';
import { PsxButton } from '../ui/Psx.js';

export interface DecisionCallbacks {
  /** Permission/question option chosen (posts the bridge response). */
  onDecisionOption(item: DecisionItem, option: DecisionOptionVM): void;
  /** Elicitation resolved locally: value is the JSON action payload. */
  onElicitationAction(item: DecisionItem, payload: string, statusText: string): void;
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
      data-tool-call-id={item.toolCallId || undefined}
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

// --- Elicitation form ------------------------------------------------------
//
// React twin of _appendElicitation/_createElicitationField: field values and
// validation errors live in component state; reads/validation mirror the
// legacy closures byte for byte.

interface FieldSpec {
  name: string;
  property: ElicitationProperty;
  required: boolean;
  isSupplement: boolean;
  options: ElicitationOption[];
  isArray: boolean;
  kind: 'boolean' | 'options' | 'array' | 'number' | 'text';
}

function fieldSpecs(schema: Record<string, unknown>): FieldSpec[] {
  const requiredList = Array.isArray(schema.required) ? (schema.required as unknown[]) : [];
  const required = new Set(requiredList);
  return orderElicitationFields(schema).map((entry) => {
    const options = readElicitationOptions(entry.property);
    const isArray = entry.property.type === 'array' && !!entry.property.items;
    const kind: FieldSpec['kind'] =
      entry.property.type === 'boolean'
        ? 'boolean'
        : options.length && !isArray
        ? 'options'
        : isArray
        ? 'array'
        : entry.property.type === 'integer' || entry.property.type === 'number'
        ? 'number'
        : 'text';
    return {
      name: entry.name,
      property: entry.property,
      required: required.has(entry.name),
      isSupplement: entry.isSupplement,
      options,
      isArray,
      kind
    };
  });
}

function initialFieldValue(spec: FieldSpec): unknown {
  if (spec.kind === 'boolean') return !!spec.property.default;
  if (spec.kind === 'options') return defaultOptionValue(spec.options, spec.property.default, true);
  if (spec.kind === 'array') {
    return new Set<unknown>(Array.isArray(spec.property.default) ? (spec.property.default as unknown[]) : []);
  }
  return spec.property.default !== undefined && spec.property.default !== null
    ? String(spec.property.default)
    : '';
}

/** legacy per-kind validate(); returns the error message or ''. */
function validateField(spec: FieldSpec, value: unknown): string {
  if (spec.kind === 'options') {
    if (spec.required && (value === undefined || value === null || value === '')) {
      return 'Choose an option to continue.';
    }
    return '';
  }
  if (spec.kind === 'array') {
    if (spec.required && (value as Set<unknown>).size === 0) {
      return 'Choose at least one option to continue.';
    }
    return '';
  }
  if (spec.kind === 'number') {
    const raw = String(value ?? '').trim();
    if (spec.required && raw === '') return 'Enter a number to continue.';
    if (raw !== '') {
      const numeric = Number(raw);
      if (!Number.isFinite(numeric) || (spec.property.type === 'integer' && !Number.isInteger(numeric))) {
        return spec.property.type === 'integer' ? 'Enter a whole number.' : 'Enter a valid number.';
      }
    }
    return '';
  }
  if (spec.kind === 'text') {
    if (spec.required && String(value ?? '').trim() === '') return 'Enter a response to continue.';
    return '';
  }
  return '';
}

/** legacy per-kind read(); undefined omits the field from the payload. */
function readField(spec: FieldSpec, value: unknown): unknown {
  if (spec.kind === 'boolean') return !!value;
  if (spec.kind === 'options') return value;
  if (spec.kind === 'array') return Array.from(value as Set<unknown>);
  if (spec.kind === 'number') {
    const raw = String(value ?? '').trim();
    return raw === '' ? undefined : Number(raw);
  }
  const text = String(value ?? '');
  return text === '' && !spec.required ? undefined : text;
}

function ElicitationOptionButton({
  option,
  multi,
  selected,
  disabled,
  onClick
}: {
  option: ElicitationOption;
  multi: boolean;
  selected: boolean;
  disabled: boolean;
  onClick(): void;
}): JSX.Element {
  return (
    <button
      type="button"
      className={'agent-elicitation-option' + (selected ? ' agent-elicitation-option-selected' : '')}
      role={multi ? 'button' : 'radio'}
      aria-checked={multi ? undefined : selected}
      aria-pressed={multi ? selected : undefined}
      disabled={disabled}
      onClick={onClick}
    >
      <span className="agent-elicitation-option-title">{option.title}</span>
      {option.description ? (
        <small className="agent-elicitation-option-description">{option.description}</small>
      ) : null}
    </button>
  );
}

function ElicitationCard({
  item,
  callbacks
}: {
  item: DecisionItem;
  callbacks: DecisionCallbacks;
}): JSX.Element {
  const raw = (item.schema ?? {}) as Record<string, unknown>;
  const schema = (raw.schema && typeof raw.schema === 'object' ? raw.schema : {}) as Record<string, unknown>;
  const mode = typeof raw.mode === 'string' ? raw.mode : '';
  const url = typeof raw.url === 'string' ? raw.url : '';
  const specsRef = useRef<FieldSpec[] | null>(null);
  if (!specsRef.current) {
    let specs = fieldSpecs(schema);
    if (specs.length === 0 && mode !== 'url') {
      specs = fieldSpecs({ properties: { response: { type: 'string', title: 'Response' } }, required: ['response'] });
    }
    specsRef.current = specs;
  }
  const specs = specsRef.current;
  const [values, setValues] = useState<Record<string, unknown>>(() => {
    const initial: Record<string, unknown> = {};
    for (const spec of specs) initial[spec.name] = initialFieldValue(spec);
    return initial;
  });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const disabled = item.decisionState !== 'active';

  const setValue = (name: string, value: unknown): void => {
    setValues((previous) => ({ ...previous, [name]: value }));
    setErrors((previous) => (previous[name] ? { ...previous, [name]: '' } : previous));
  };

  const act = (action: () => void): void => {
    if (item.decisionState !== 'active') return;
    action();
  };

  const submit = (): void => {
    const nextErrors: Record<string, string> = {};
    let valid = true;
    for (const spec of specs) {
      const message = validateField(spec, values[spec.name]);
      if (message) {
        valid = false;
        nextErrors[spec.name] = message;
      }
    }
    setErrors(nextErrors);
    if (!valid) return;
    const content: Record<string, unknown> = {};
    for (const spec of specs) {
      const value = readField(spec, values[spec.name]);
      if (value !== undefined) content[spec.name] = value;
    }
    callbacks.onElicitationAction(item, JSON.stringify({ action: 'accept', content }), 'Response sent.');
  };

  const safeUrl = url ? safeHref(url) : '';
  return (
    <section
      className={'agent-decision agent-decision-elicitation' + (disabled ? ' agent-decision-disabled' : '')}
      data-decision-state={disabled ? 'disabled' : 'active'}
      data-request-id={item.requestId || undefined}
    >
      <div className="agent-decision-title">{item.title}</div>
      <div className="agent-decision-subtitle">{item.elicitationMessage}</div>
      <form className="agent-elicitation-form" noValidate>
        {specs.map((spec) => {
          const error = errors[spec.name] || '';
          const label = (String(spec.property.title ?? '') || spec.name) + (spec.required ? ' *' : '');
          return (
            <div
              key={spec.name}
              className={
                'agent-elicitation-field' +
                (spec.isSupplement ? ' agent-elicitation-field-supplement' : '') +
                (error ? ' agent-elicitation-field-error' : '')
              }
            >
              <div className="agent-elicitation-label">{label}</div>
              {spec.kind === 'boolean' ? (
                <label className="agent-elicitation-checkbox">
                  <input
                    type="checkbox"
                    checked={!!values[spec.name]}
                    disabled={disabled}
                    onChange={(event) => setValue(spec.name, event.target.checked)}
                  />
                </label>
              ) : spec.kind === 'options' ? (
                <div
                  className="agent-elicitation-option-list"
                  role="radiogroup"
                  aria-label={String(spec.property.title ?? '') || spec.name}
                >
                  {spec.options.map((option, index) => (
                    <ElicitationOptionButton
                      key={index}
                      option={option}
                      multi={false}
                      selected={values[spec.name] === option.value}
                      disabled={disabled}
                      onClick={() => act(() => setValue(spec.name, option.value))}
                    />
                  ))}
                </div>
              ) : spec.kind === 'array' ? (
                <div
                  className="agent-elicitation-option-list"
                  role="group"
                  aria-label={String(spec.property.title ?? '') || spec.name}
                >
                  {readElicitationOptions(spec.property.items).map((option, index) => (
                    <ElicitationOptionButton
                      key={index}
                      option={option}
                      multi={true}
                      selected={(values[spec.name] as Set<unknown>).has(option.value)}
                      disabled={disabled}
                      onClick={() =>
                        act(() => {
                          const next = new Set(values[spec.name] as Set<unknown>);
                          if (next.has(option.value)) next.delete(option.value);
                          else next.add(option.value);
                          setValue(spec.name, next);
                        })
                      }
                    />
                  ))}
                </div>
              ) : spec.kind === 'number' ? (
                <input
                  type="number"
                  step={spec.property.type === 'integer' ? '1' : undefined}
                  value={String(values[spec.name] ?? '')}
                  disabled={disabled}
                  onChange={(event) => setValue(spec.name, event.target.value)}
                />
              ) : (
                <textarea
                  rows={spec.isSupplement ? 2 : 3}
                  value={String(values[spec.name] ?? '')}
                  disabled={disabled}
                  onChange={(event) => setValue(spec.name, event.target.value)}
                ></textarea>
              )}
              {spec.property.description ? <small>{String(spec.property.description)}</small> : null}
              <div className="agent-elicitation-error" hidden={!error}>
                {error}
              </div>
            </div>
          );
        })}
        {mode === 'url' && url ? (
          <div className="agent-elicitation-url">
            {safeUrl ? (
              <a href={safeUrl} target="_blank" rel="noreferrer">
                {url}
              </a>
            ) : (
              url
            )}
          </div>
        ) : null}
      </form>
      <div className="agent-decision-actions agent-elicitation-actions">
        <PsxButton variant="primary" disabled={disabled} onClick={() => act(submit)}>
          Continue
        </PsxButton>
        <PsxButton
          variant="subtle"
          disabled={disabled}
          onClick={() =>
            act(() => callbacks.onElicitationAction(item, JSON.stringify({ action: 'decline' }), 'Declined.'))
          }
        >
          Decline
        </PsxButton>
        <PsxButton
          variant="subtle"
          disabled={disabled}
          onClick={() =>
            act(() => callbacks.onElicitationAction(item, JSON.stringify({ action: 'cancel' }), 'Cancelled.'))
          }
        >
          Cancel
        </PsxButton>
      </div>
      {disabled && item.statusText ? <div className="agent-decision-status">{item.statusText}</div> : null}
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
    return <ElicitationCard item={item} callbacks={callbacks} />;
  }
  return <PermissionQuestionCard item={item} assistantName={assistantName} callbacks={callbacks} />;
}
