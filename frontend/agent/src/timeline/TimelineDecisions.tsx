// TimelineDecisions.tsx — React permission, question, elicitation and
// mode-transition thread cards.

import { useRef, useState, type JSX } from 'react';
import { useTranslation } from 'react-i18next';
import { useAnnounceLive } from '../ui/announce.js';
import {
  defaultOptionValue,
  orderElicitationFields,
  readElicitationOptions,
  type ElicitationOption,
  type ElicitationProperty
} from '../core/elicitation.js';
import { MarkdownContent } from '../markdown/MarkdownContent.js';
import { normalizePsxHref } from '../markdown/security.js';
import { DecisionOptionPills } from '../decisions/DecisionOptionPills.js';
import { resolveDisplay } from './copy.js';
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
  /** Elicitation resolved locally: value is the JSON action payload; the
   *  status is a fixed code resolved at render (never a composed sentence). */
  onElicitationAction(item: DecisionItem, payload: string, statusCode: string): void;
  copyText(text: string): Promise<boolean>;
}

/** Well-known synthesized option ids resolve to localized pill labels. */
function optionLabel(option: DecisionOptionVM, t: (key: string, options?: Record<string, unknown>) => string): string {
  if (option.name) return option.name;
  const wellKnown = ['allow', 'reject', 'yes', 'no'];
  return wellKnown.includes(option.optionId)
    ? t(`timeline.decision.option.${option.optionId}`)
    : t('timeline.decision.option.select');
}

function DecisionOptionPillsLocalized({
  options,
  selectedOptionId,
  disabled,
  ariaLabel,
  className,
  buttonClassName,
  onSelect
}: {
  options: DecisionOptionVM[];
  selectedOptionId?: string;
  disabled?: boolean;
  ariaLabel: string;
  className?: string;
  buttonClassName?: string;
  onSelect(option: DecisionOptionVM): void;
}): JSX.Element {
  const { t } = useTranslation('agent');
  return (
    <DecisionOptionPills
      options={options}
      selectedOptionId={selectedOptionId}
      disabled={disabled}
      ariaLabel={ariaLabel}
      className={className}
      buttonClassName={buttonClassName}
      labelFor={(option) => optionLabel(option, t)}
      onSelect={onSelect}
    />
  );
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
  const { t } = useTranslation('agent');
  // legacy: the card starts open and only a local option click closes it;
  // external resolve/cancel leaves the user's toggle alone (projection-lifecycle
  // open sync, same contract as the old uncontrolled <details>).
  const [open, setOpen] = useProjectionOpen(!item.collapsed);
  const disabled = item.decisionState !== 'active';
  const [rawInputOpen, setRawInputOpen] = useState(false);
  const hasRawInput = item.text.trim().length > 0;
  const resolvedId = item.selectedOptionId;
  const resolvedName = item.selectedOptionName;
  const selected =
    item.options.find((option) => resolvedId && option.optionId === resolvedId) ||
    (resolvedName ? item.options.find((option) => option.name === resolvedName) : undefined);
  const statusLine = resolveDisplay(item.statusCode, t);
  const subtitle = selected
    ? t('timeline.decision.selectionRecorded')
    : item.kind === 'permission'
    ? t('timeline.decision.permissionSubtitle', { agentName: assistantName })
    : t('timeline.decision.questionSubtitle', { agentName: assistantName });
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
            <span className="agent-decision-header-title text-[13px] font-semibold">
              {item.title || resolveDisplay(item.titleCode, t)}
            </span>
          </div>
          <span className="agent-decision-header-subtitle text-xs text-muted-foreground">{subtitle}</span>
        </div>
      </CollapsibleTrigger>
      {/* forceMount keeps the collapsed card body in the DOM (old <details>
          semantics) for text search and replay tooling. */}
      <CollapsibleContent forceMount className="data-[state=closed]:hidden">
      <div className="agent-decision-body relative px-3 pb-3">
        {item.description ? (
          <div className="agent-decision-description mt-1 break-words text-sm leading-normal text-foreground">
            {item.description}
          </div>
        ) : null}
        {hasRawInput ? (
          <Collapsible
            className="agent-decision-raw-input group/raw mt-2.5"
            open={rawInputOpen}
            onOpenChange={setRawInputOpen}
          >
            <CollapsibleTrigger className="agent-decision-raw-input-summary flex cursor-pointer select-none items-center gap-2 px-3 py-2 text-xs font-semibold text-muted-foreground transition-colors hover:bg-accent">
              <ChevronRightIcon className="size-2.5 shrink-0 transition-transform group-data-[state=open]/raw:rotate-90" aria-hidden="true" />
              {t('timeline.decision.rawInput')}
            </CollapsibleTrigger>
            {/* forceMount keeps the collapsed raw payload in the DOM (old
                <details> semantics) for text search and replay tooling. */}
            <CollapsibleContent forceMount className="data-[state=closed]:hidden">
              <pre className="agent-decision-raw-input-content m-0 whitespace-pre-wrap break-words bg-background p-3 font-mono text-xs leading-normal">{item.text}</pre>
            </CollapsibleContent>
          </Collapsible>
        ) : null}
        <div className="agent-decision-actions mt-2.5">
          {item.options.length === 0 ? (
            <div className="agent-decision-status agent-decision-options-error mt-0 flex-[1_1_100%] text-xs text-destructive">
              {t('timeline.decision.noOptions')}
            </div>
          ) : null}
          {item.options.length > 0 ? (
            <DecisionOptionPillsLocalized
              options={item.options}
              selectedOptionId={selected?.optionId}
              disabled={disabled}
              ariaLabel={item.kind === 'permission'
                ? t('timeline.decision.choosePermission')
                : t('timeline.decision.chooseAnswer')}
              className="agent-decision-option-list"
              buttonClassName="agent-decision-option"
              onSelect={(option) => {
                if (item.decisionState !== 'active') return;
                callbacks.onDecisionOption(item, option);
              }}
            />
          ) : null}
        </div>
        {disabled && !selected && (statusLine || item.statusText) ? (
          <div className="agent-decision-status mt-2.5 text-xs text-muted-foreground">{statusLine || item.statusText}</div>
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
  const { t } = useTranslation('agent');
  const live = useAnnounceLive();
  const isModeTransition = item.kind === 'mode_transition';
  const pending = item.decisionState === 'active';
  const [technicalDetailsOpen, setTechnicalDetailsOpen] = useState(false);
  // Status precedence: fixed statusCode (render-time localized) → raw
  // statusText (legacy persisted rows) → state-derived default.
  const statusLine = resolveDisplay(item.statusCode, t);
  const statusText = statusLine
    ? statusLine
    : item.statusText
    ? item.statusText
    : pending
    ? t('timeline.decision.waitingSelection')
    : item.selectedOptionId
    ? (() => {
        const selected = item.options.find((option) => option.optionId === item.selectedOptionId);
        return selected && selected.name
          ? t('timeline.decision.selectedOption', { name: selected.name })
          : t('timeline.decision.selectionRecorded');
      })()
    : t('timeline.decision.inactive');
  const headerStateKey =
    item.headerState === 'selected' || item.headerState === 'cancelled'
      ? `timeline.decision.headerState.${item.headerState}`
      : !pending
      ? 'timeline.decision.headerState.interrupted'
      : '';
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
        <div className="agent-mode-transition-title min-w-0 break-words text-[13px] font-semibold leading-snug">
          {item.title || resolveDisplay(item.titleCode, t)}
        </div>
        <span className="agent-mode-transition-header-state shrink-0 text-xs font-semibold text-muted-foreground">
          {pending ? t('timeline.decision.required') : t(headerStateKey || 'timeline.decision.headerState.interrupted')}
        </span>
      </header>
      <Collapsible defaultOpen={!item.historical} className="agent-mode-transition-details group/mt bg-background">
        <CollapsibleTrigger className="agent-mode-transition-summary flex w-full cursor-pointer select-none items-center gap-2 px-4 py-2 text-left text-xs font-semibold text-muted-foreground transition-colors hover:bg-accent hover:text-foreground">
          <ChevronRightIcon className="size-2.5 shrink-0 transition-transform group-data-[state=open]/mt:rotate-90" aria-hidden="true" />
          {isModeTransition ? t('timeline.decision.proposalDetails') : t('timeline.decision.documentDetails')}
        </CollapsibleTrigger>
        {/* forceMount keeps the collapsed proposal in the DOM (old <details>
            semantics) for text search and replay tooling. */}
        <CollapsibleContent forceMount className="data-[state=closed]:hidden">
        <MarkdownContent
          className="agent-mode-transition-document agent-message-body border-t px-4 pt-3 pb-4"
          mode="static"
          source={item.text}
          surface="decision"
        />
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
              {t('timeline.recovery.technicalDetails')}
            </CollapsibleTrigger>
            <CollapsibleContent forceMount className="data-[state=closed]:hidden">
              <pre className="m-0 whitespace-pre-wrap break-words border-t bg-background px-4 py-3 font-mono text-xs leading-normal">{item.rawText}</pre>
            </CollapsibleContent>
          </Collapsible>
        ) : null}
        <div className="agent-mode-transition-decision border-t px-4 pt-3">
          <div className="agent-mode-transition-decision-label mb-2 text-xs font-semibold text-muted-foreground">
            {isModeTransition ? t('timeline.decision.chooseHow') : t('timeline.decision.chooseResponse')}
          </div>
          {item.options.length === 0 ? (
            <div className="agent-mode-transition-error rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs leading-normal text-destructive">
              {t('timeline.decision.noOptionsAcp')}
            </div>
          ) : (
            <DecisionOptionPillsLocalized
              options={item.options}
              selectedOptionId={item.selectedOptionId}
              disabled={!pending}
              ariaLabel={isModeTransition ? t('timeline.decision.chooseHow') : t('timeline.decision.chooseResponse')}
              className="agent-mode-transition-options"
              buttonClassName="agent-mode-transition-option"
              onSelect={(option) => {
                if (item.decisionState !== 'active') return;
                callbacks.onDecisionOption(item, option);
              }}
            />
          )}
        </div>
        <div className="agent-mode-transition-status min-h-[17px] px-4 pt-2 pb-3 text-xs leading-snug text-muted-foreground" aria-live={live}>
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

interface ElicitationSpecs {
  fields: FieldSpec[];
  usesFallbackResponse: boolean;
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

/** legacy per-kind validate(); returns a fixed locale key or ''. Errors
 *  resolve at render so a language switch re-localizes them. */
function validateField(spec: FieldSpec, value: unknown): string {
  if (spec.kind === 'options') {
    return spec.required && (value === undefined || value === null || value === '')
      ? 'timeline.decision.validation.chooseOption'
      : '';
  }
  if (spec.kind === 'array') {
    return spec.required && (value as Set<unknown>).size === 0
      ? 'timeline.decision.validation.chooseOne'
      : '';
  }
  if (spec.kind === 'number') {
    const raw = String(value ?? '').trim();
    if (spec.required && raw === '') return 'timeline.decision.validation.enterNumber';
    if (raw !== '') {
      const numeric = Number(raw);
      if (!Number.isFinite(numeric) || (spec.property.type === 'integer' && !Number.isInteger(numeric))) {
        return spec.property.type === 'integer'
          ? 'timeline.decision.validation.wholeNumber'
          : 'timeline.decision.validation.validNumber';
      }
    }
    return '';
  }
  if (spec.kind === 'text') {
    return spec.required && String(value ?? '').trim() === ''
      ? 'timeline.decision.validation.enterResponse'
      : '';
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
  const { t } = useTranslation('agent');
  const raw = (item.schema ?? {}) as Record<string, unknown>;
  const schema = (raw.schema && typeof raw.schema === 'object' ? raw.schema : {}) as Record<string, unknown>;
  const mode = typeof raw.mode === 'string' ? raw.mode : '';
  const url = typeof raw.url === 'string' ? raw.url : '';
  const specsRef = useRef<ElicitationSpecs | null>(null);
  if (!specsRef.current) {
    let specs = fieldSpecs(schema);
    const usesFallbackResponse = specs.length === 0 && mode !== 'url';
    if (usesFallbackResponse) {
      // Keep only semantic fallback data in component state. The label is
      // resolved below on every render so a locale switch updates it in place.
      specs = fieldSpecs({ properties: { response: { type: 'string' } }, required: ['response'] });
    }
    specsRef.current = { fields: specs, usesFallbackResponse };
  }
  const { fields: specs, usesFallbackResponse } = specsRef.current;
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
      const errorKey = validateField(spec, values[spec.name]);
      if (errorKey) {
        valid = false;
        nextErrors[spec.name] = errorKey;
      }
    }
    setErrors(nextErrors);
    if (!valid) return;
    const content: Record<string, unknown> = {};
    for (const spec of specs) {
      const value = readField(spec, values[spec.name]);
      if (value !== undefined) content[spec.name] = value;
    }
    callbacks.onElicitationAction(item, JSON.stringify({ action: 'accept', content }), 'elicitation.responseSent');
  };

  const safeUrl = url ? normalizePsxHref(url) : '';
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
          <div className="agent-decision-title text-[13px] font-semibold">
            {item.title || resolveDisplay(item.titleCode, t)}
          </div>
        </div>
        <div className="agent-decision-header-status shrink-0 text-xs text-muted-foreground">
          {disabled ? resolveDisplay(item.statusCode, t) || item.statusText : t('timeline.elicitation.waitingInput')}
        </div>
      </CollapsibleTrigger>
      {/* forceMount keeps the collapsed form in the DOM (old <details>
          semantics) for text search and replay tooling. */}
      <CollapsibleContent forceMount className="data-[state=closed]:hidden">
      <div className="agent-decision-body p-3">
      <div className="agent-decision-subtitle break-words text-sm leading-normal">
        {item.elicitationMessage || resolveDisplay(item.elicitationMessageCode, t)}
      </div>
      <form className="agent-elicitation-form mt-3 grid gap-3" noValidate>
        {specs.map((spec) => {
          const error = errors[spec.name] || '';
          const rawTitle = String(spec.property.title ?? '');
          const localizedFallback = usesFallbackResponse && spec.name === 'response'
            ? t('timeline.elicitation.responseTitle')
            : spec.isSupplement && !rawTitle
            ? t('timeline.elicitation.otherTitle')
            : '';
          const label = (rawTitle || localizedFallback || spec.name) + (spec.required ? ' *' : '');
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
                {error ? t(error) : ''}
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
          {t('timeline.elicitation.continue')}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={disabled}
          onClick={() =>
            act(() => callbacks.onElicitationAction(item, JSON.stringify({ action: 'decline' }), 'elicitation.declined'))
          }
        >
          {t('timeline.elicitation.decline')}
        </Button>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          disabled={disabled}
          onClick={() =>
            act(() => callbacks.onElicitationAction(item, JSON.stringify({ action: 'cancel' }), 'elicitation.cancelled'))
          }
        >
          {t('timeline.elicitation.cancel')}
        </Button>
      </div>
      {disabled && (item.statusText || resolveDisplay(item.statusCode, t)) ? (
        <div className="agent-decision-status mt-2.5 text-xs text-muted-foreground">
          {resolveDisplay(item.statusCode, t) || item.statusText}
        </div>
      ) : null}
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
  // Permission form variant reuses the elicitation form body over the
  // permission response channel (see TimelineController).
  if (item.kind === 'elicitation' || (item.kind === 'permission' && item.schema)) {
    return <ElicitationCard item={item} callbacks={callbacks} />;
  }
  return <PermissionQuestionCard item={item} assistantName={assistantName} callbacks={callbacks} />;
}
