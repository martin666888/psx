// TimelineDecisions.tsx — React permission, question, elicitation and
// mode-transition thread cards.

import { useRef, useState, type JSX } from 'react';
import {
  defaultOptionValue,
  orderElicitationFields,
  readElicitationOptions,
  type ElicitationOption,
  type ElicitationProperty
} from '../core/elicitation.js';
import { renderMarkdown, safeHref } from '../core/markdown.js';
import { DecisionOptionPills } from '../decisions/DecisionOptionPills.js';
import type { DecisionItem, DecisionOptionVM } from './timelineViewModel.js';
import { CopyButton, useProjectionOpen } from './TimelineView.js';
import { Button } from '../components/ui/button.js';
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger
} from '../components/ui/collapsible.js';
import { Input } from '../components/ui/input.js';
import { Textarea } from '../components/ui/textarea.js';
import { ChevronRightIcon, InfoIcon } from 'lucide-react';

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
  // external resolve/cancel leaves the user's toggle alone (projection-lifecycle
  // open sync, same contract as the old uncontrolled <details>).
  const [open, setOpen] = useProjectionOpen(!item.collapsed);
  const disabled = item.decisionState !== 'active';
  const [rawInputOpen, setRawInputOpen] = useState(false);
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
  return (
    <Collapsible
      open={open}
      onOpenChange={setOpen}
      className={
        'agent-decision agent-decision-' + item.kind +
        ' group/decision mb-3.5 w-full rounded-lg border bg-card text-card-foreground' +
        (disabled ? ' agent-decision-disabled opacity-80' : '')
      }
      data-decision-state={disabled ? 'disabled' : 'active'}
      data-request-id={item.requestId || undefined}
      data-selected-option-id={resolvedId || undefined}
      data-selected-option-name={resolvedName || undefined}
    >
      <CollapsibleTrigger className="agent-decision-header w-full cursor-pointer select-none p-3 text-left">
        <div className="agent-decision-header-content flex flex-col gap-1">
          <div className="agent-decision-header-title-row flex items-center gap-2">
            <span className="agent-decision-chevron inline-flex shrink-0 text-muted-foreground" aria-hidden="true">
              <ChevronRightIcon className="size-3 transition-transform group-data-[state=open]/decision:rotate-90" />
            </span>
            <span className="agent-decision-header-title text-[13px] font-bold">{item.title}</span>
          </div>
          <span className="agent-decision-header-subtitle text-xs text-muted-foreground">{subtitle}</span>
        </div>
      </CollapsibleTrigger>
      {/* forceMount keeps the collapsed card body in the DOM (old <details>
          semantics) for text search and replay tooling. */}
      <CollapsibleContent forceMount className="data-[state=closed]:hidden">
      <div className="agent-decision-body relative px-3 pb-3">
        <Collapsible
          className="agent-decision-raw-input group/raw mt-2.5"
          open={rawInputOpen}
          onOpenChange={setRawInputOpen}
        >
          <CollapsibleTrigger className="agent-decision-raw-input-summary flex cursor-pointer select-none items-center gap-2 px-3 py-2 text-xs font-semibold text-muted-foreground transition-colors hover:bg-accent">
            <ChevronRightIcon className="size-2.5 shrink-0 transition-transform group-data-[state=open]/raw:rotate-90" aria-hidden="true" />
            Raw Input
          </CollapsibleTrigger>
          {/* forceMount keeps the collapsed raw payload in the DOM (old
              <details> semantics) for text search and replay tooling. */}
          <CollapsibleContent forceMount className="data-[state=closed]:hidden">
            <pre className="agent-decision-raw-input-content m-0 whitespace-pre-wrap break-words bg-background p-3 font-mono text-xs leading-normal">{item.text}</pre>
          </CollapsibleContent>
        </Collapsible>
        <div className="agent-decision-actions mt-2.5">
          {item.options.length === 0 ? (
            <div className="agent-decision-status agent-decision-options-error mt-0 flex-[1_1_100%] text-xs text-destructive">
              The Agent did not provide any response options.
            </div>
          ) : null}
          {item.options.length > 0 ? (
            <DecisionOptionPills
              options={item.options}
              selectedOptionId={selected?.optionId}
              disabled={disabled}
              ariaLabel={item.kind === 'permission' ? 'Choose a permission response' : 'Choose an answer'}
              className="agent-decision-option-list"
              buttonClassName="agent-decision-option"
              onSelect={(option) => {
                if (item.decisionState !== 'active') return;
                callbacks.onDecisionOption(item, option);
              }}
            />
          ) : null}
        </div>
        {disabled && !selected && item.statusText ? (
          <div className="agent-decision-status mt-2.5 text-xs text-muted-foreground">{item.statusText}</div>
        ) : null}
      </div>
      </CollapsibleContent>
    </Collapsible>
  );
}

function DocumentDecisionCard({
  item,
  callbacks
}: {
  item: DecisionItem;
  callbacks: DecisionCallbacks;
}): JSX.Element {
  const isModeTransition = item.kind === 'mode_transition';
  const pending = item.decisionState === 'active';
  const [technicalDetailsOpen, setTechnicalDetailsOpen] = useState(false);
  // Uncontrolled: historical cards start collapsed, live ones start open;
  // afterwards the user toggles freely (legacy openedOnce-on-mount semantics).
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
      className={
        'agent-mode-transition agent-document-decision mb-5 w-full overflow-hidden rounded-lg border bg-card text-card-foreground' +
        (isModeTransition ? '' : ' agent-document-permission')
      }
      data-decision-state={pending ? 'active' : 'disabled'}
      data-request-id={item.requestId || undefined}
      data-tool-call-id={item.toolCallId || undefined}
    >
      <header className="agent-mode-transition-header flex items-baseline justify-between gap-3 border-b px-4 py-3">
        <div className="agent-mode-transition-title min-w-0 break-words text-[13px] font-bold leading-snug">{item.title}</div>
        <span className="agent-mode-transition-header-state shrink-0 text-xs font-semibold text-muted-foreground">
          {pending ? 'Decision required' : item.headerState || 'Interrupted'}
        </span>
      </header>
      <Collapsible defaultOpen={!item.historical} className="agent-mode-transition-details group/mt bg-background">
        <CollapsibleTrigger className="agent-mode-transition-summary flex w-full cursor-pointer select-none items-center gap-2 px-4 py-2 text-left text-xs font-semibold text-muted-foreground transition-colors hover:bg-accent hover:text-foreground">
          <ChevronRightIcon className="size-2.5 shrink-0 transition-transform group-data-[state=open]/mt:rotate-90" aria-hidden="true" />
          {isModeTransition ? 'Proposal details' : 'Document details'}
        </CollapsibleTrigger>
        {/* forceMount keeps the collapsed proposal in the DOM (old <details>
            semantics) for text search and replay tooling. */}
        <CollapsibleContent forceMount className="data-[state=closed]:hidden">
        <div
          className="agent-mode-transition-document agent-message-body border-t px-4 pt-3 pb-4"
          dangerouslySetInnerHTML={{ __html: renderMarkdown(item.text) }}
        ></div>
        <div className="agent-message-actions agent-mode-transition-document-actions m-0 px-4 pb-3">
          <CopyButton getText={() => item.text} copyText={callbacks.copyText} />
        </div>
        {!isModeTransition && item.rawText && item.rawText !== item.text ? (
          <Collapsible
            className="agent-document-permission-technical-details group/document-details border-t"
            open={technicalDetailsOpen}
            onOpenChange={setTechnicalDetailsOpen}
          >
            <CollapsibleTrigger className="flex w-full cursor-pointer select-none items-center gap-2 px-4 py-2 text-left text-xs font-semibold text-muted-foreground transition-colors hover:bg-accent hover:text-foreground">
              <ChevronRightIcon className="size-2.5 shrink-0 transition-transform group-data-[state=open]/document-details:rotate-90" aria-hidden="true" />
              Technical details
            </CollapsibleTrigger>
            <CollapsibleContent forceMount className="data-[state=closed]:hidden">
              <pre className="m-0 whitespace-pre-wrap break-words border-t bg-background px-4 py-3 font-mono text-xs leading-normal">{item.rawText}</pre>
            </CollapsibleContent>
          </Collapsible>
        ) : null}
        <div className="agent-mode-transition-decision border-t px-4 pt-3">
          <div className="agent-mode-transition-decision-label mb-2 text-xs font-semibold text-muted-foreground">
            {isModeTransition ? 'Choose how to continue' : 'Choose a response'}
          </div>
          {item.options.length === 0 ? (
            <div className="agent-mode-transition-error rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs leading-normal text-destructive">
              The ACP Agent did not provide any response options.
            </div>
          ) : (
            <DecisionOptionPills
              options={item.options}
              selectedOptionId={item.selectedOptionId}
              disabled={!pending}
              ariaLabel={isModeTransition ? 'Choose how to continue' : 'Choose a response'}
              className="agent-mode-transition-options"
              buttonClassName="agent-mode-transition-option"
              onSelect={(option) => {
                if (item.decisionState !== 'active') return;
                callbacks.onDecisionOption(item, option);
              }}
            />
          )}
        </div>
        <div className="agent-mode-transition-status min-h-[17px] px-4 pt-2 pb-3 text-xs leading-snug text-muted-foreground" aria-live="polite">
          {statusText}
        </div>
        </CollapsibleContent>
      </Collapsible>
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
    <Button
      type="button"
      variant="outline"
      className={
        'agent-elicitation-option flex h-auto w-full min-w-0 cursor-pointer items-start justify-start gap-2.5 whitespace-normal break-words rounded-lg px-3 py-2.5 text-left hover:bg-accent disabled:cursor-default disabled:opacity-70' +
        (selected ? ' agent-elicitation-option-selected border-ring bg-accent' : '')
      }
      role={multi ? 'button' : 'radio'}
      aria-checked={multi ? undefined : selected}
      aria-pressed={multi ? selected : undefined}
      disabled={disabled}
      onClick={onClick}
    >
      {/* Visual radio/checkbox indicator only; selection semantics stay on the
          button's role + aria-checked/aria-pressed. */}
      <span
        aria-hidden="true"
        className={
          'agent-elicitation-option-indicator mt-0.5 flex size-3.5 shrink-0 items-center justify-center border border-muted-foreground/60 bg-background ' +
          (multi ? 'rounded-[3px]' : 'rounded-full')
        }
      >
        {selected ? <span className={'size-2 bg-primary ' + (multi ? 'rounded-[1px]' : 'rounded-full')}></span> : null}
      </span>
      <span className="agent-elicitation-option-content grid min-w-0 gap-0.5">
        <span className="agent-elicitation-option-title break-words text-[13px] font-semibold leading-normal text-foreground">{option.title}</span>
        {option.description ? (
          <small className="agent-elicitation-option-description break-words text-xs leading-normal text-muted-foreground">{option.description}</small>
        ) : null}
      </span>
    </Button>
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
  // legacy contract shared with PermissionQuestionCard: a local answer folds
  // the card (projection sets collapsed); external cancel leaves the toggle.
  const [open, setOpen] = useProjectionOpen(!item.collapsed);
  return (
    <Collapsible
      open={open}
      onOpenChange={setOpen}
      className={
        'agent-decision agent-decision-elicitation group/elicitation mb-3.5 w-full overflow-hidden rounded-lg border bg-card p-0 text-card-foreground' +
        (disabled ? ' agent-decision-disabled opacity-80' : '')
      }
      data-decision-state={disabled ? 'disabled' : 'active'}
      data-request-id={item.requestId || undefined}
    >
      <CollapsibleTrigger className="agent-decision-header flex w-full cursor-pointer select-none items-center justify-between gap-2 border-b bg-muted/50 px-3 py-2 text-left">
        <div className="flex min-w-0 items-center gap-2">
          <ChevronRightIcon
            className="size-3 shrink-0 text-muted-foreground transition-transform group-data-[state=open]/elicitation:rotate-90"
            aria-hidden="true"
          />
          <InfoIcon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
          <div className="agent-decision-title text-[13px] font-bold">{item.title}</div>
        </div>
        <div className="agent-decision-header-status shrink-0 text-xs text-muted-foreground">
          {disabled ? item.statusText : 'Waiting for input'}
        </div>
      </CollapsibleTrigger>
      {/* forceMount keeps the collapsed form in the DOM (old <details>
          semantics) for text search and replay tooling. */}
      <CollapsibleContent forceMount className="data-[state=closed]:hidden">
      <div className="agent-decision-body p-3">
      <div className="agent-decision-subtitle break-words text-sm leading-normal">{item.elicitationMessage}</div>
      <form className="agent-elicitation-form mt-3 grid gap-3" noValidate>
        {specs.map((spec) => {
          const error = errors[spec.name] || '';
          const label = (String(spec.property.title ?? '') || spec.name) + (spec.required ? ' *' : '');
          return (
            <div
              key={spec.name}
              className={
                'agent-elicitation-field grid gap-1.5 text-xs text-muted-foreground' +
                (spec.isSupplement ? ' agent-elicitation-field-supplement opacity-90' : '') +
                (error ? ' agent-elicitation-field-error' : '')
              }
            >
              <div className={'agent-elicitation-label font-semibold ' + (error ? 'text-destructive' : 'text-foreground')}>{label}</div>
              {spec.property.description ? <small className="text-muted-foreground">{String(spec.property.description)}</small> : null}
              {spec.kind === 'boolean' ? (
                <label className="agent-elicitation-checkbox justify-self-start">
                  <input
                    type="checkbox"
                    checked={!!values[spec.name]}
                    disabled={disabled}
                    aria-label={String(spec.property.title ?? '') || spec.name}
                    onChange={(event) => setValue(spec.name, event.target.checked)}
                  />
                </label>
              ) : spec.kind === 'options' ? (
                <div
                  className={
                    'agent-elicitation-option-list grid gap-1.5' +
                    (error ? ' rounded-md outline outline-1 outline-offset-2 outline-destructive' : '')
                  }
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
                  className={
                    'agent-elicitation-option-list grid gap-1.5' +
                    (error ? ' rounded-md outline outline-1 outline-offset-2 outline-destructive' : '')
                  }
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
                <Input
                  type="number"
                  className="h-8 text-[13px]"
                  step={spec.property.type === 'integer' ? '1' : undefined}
                  value={String(values[spec.name] ?? '')}
                  disabled={disabled}
                  aria-label={String(spec.property.title ?? '') || spec.name}
                  aria-invalid={error ? true : undefined}
                  onChange={(event) => setValue(spec.name, event.target.value)}
                />
              ) : (
                <Textarea
                  className="min-h-0 text-[13px]"
                  rows={spec.isSupplement ? 2 : 3}
                  value={String(values[spec.name] ?? '')}
                  disabled={disabled}
                  aria-label={String(spec.property.title ?? '') || spec.name}
                  aria-invalid={error ? true : undefined}
                  onChange={(event) => setValue(spec.name, event.target.value)}
                ></Textarea>
              )}
              <div className="agent-elicitation-error text-destructive [&[hidden]]:hidden" hidden={!error}>
                {error}
              </div>
            </div>
          );
        })}
        {mode === 'url' && url ? (
          <div className="agent-elicitation-url break-all text-[13px]">
            {safeUrl ? (
              <a className="text-primary underline underline-offset-2" href={safeUrl} target="_blank" rel="noreferrer">
                {url}
              </a>
            ) : (
              url
            )}
          </div>
        ) : null}
      </form>
      <div className="agent-decision-actions agent-elicitation-actions mt-2.5 flex flex-wrap gap-2">
        <Button type="button" size="sm" disabled={disabled} onClick={() => act(submit)}>
          Continue
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={disabled}
          onClick={() =>
            act(() => callbacks.onElicitationAction(item, JSON.stringify({ action: 'decline' }), 'Declined.'))
          }
        >
          Decline
        </Button>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          disabled={disabled}
          onClick={() =>
            act(() => callbacks.onElicitationAction(item, JSON.stringify({ action: 'cancel' }), 'Cancelled.'))
          }
        >
          Cancel
        </Button>
      </div>
      {disabled && item.statusText ? <div className="agent-decision-status mt-2.5 text-xs text-muted-foreground">{item.statusText}</div> : null}
      </div>
      </CollapsibleContent>
    </Collapsible>
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
  if (item.kind === 'mode_transition' || item.kind === 'document_permission') {
    return <DocumentDecisionCard item={item} callbacks={callbacks} />;
  }
  if (item.kind === 'elicitation') {
    return <ElicitationCard item={item} callbacks={callbacks} />;
  }
  return <PermissionQuestionCard item={item} assistantName={assistantName} callbacks={callbacks} />;
}
