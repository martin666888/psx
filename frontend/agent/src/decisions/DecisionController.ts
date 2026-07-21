// DecisionController.ts — the Phase 4 Decision domain owner.
//
// Owns every interactive decision surface the ACP session can raise:
//   * permission requests + questions  (agent-decision cards)
//   * elicitation forms                 (agent-decision-elicitation)
//   * mode-transition proposals         (agent-mode-transition card + the
//                                        composer-region mode-transition prompt)
// plus the resolve / cancel / interrupt lifecycle for each.
//
// Faithful port of the legacy DOM writes and listeners:
//   wwwroot/js/agent/permissions.js    (_appendDecision, _resolvePermission,
//                                        _cancelDecision, _disableDecisionCard,
//                                        _decisionButton, _decisionOptionClass,
//                                        _findDecisionCard)
//   wwwroot/js/agent/elicitation.js    (_appendElicitation, _createElicitationField,
//                                        _createElicitationOptionButton)
//   wwwroot/js/agent/modeTransition.js (_appendModeTransition, prompt lifecycle)
// so the produced DOM is byte-identical to the pre-refactor engine.
//
// Decision cards are placed into the timeline thread through the DecisionHost
// seam (appendToTimeline / scrollTimelineToBottom) so the thread keeps a single
// writer — the timeline engine — exactly like the composer system-message seam.
// mode-transition also has to reach two nodes it does not own: it mutates the
// timeline run-group bookkeeping (removeToolCardForModeTransition) and toggles
// the composer input row / attachment strip (setComposerPromptActive); both go
// through the host so no two engines write the same node. The dedicated
// [data-role="mode-transition-prompt"] node is decisions-owned, so the
// controller writes it directly.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';
import { createInitialWorkspaceState } from '../contracts/workspace-state.js';
import {
  defaultOptionValue,
  orderElicitationFields,
  readElicitationOptions,
  type ElicitationOption,
  type ElicitationProperty
} from '../core/elicitation.js';

/** The strangler seam the decision controller needs from the host. */
export interface DecisionHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
  /** Place a finished decision card into the timeline (legacy _appendToCurrentHost). */
  appendToTimeline(workspaceId: string, element: HTMLElement): void;
  /** Scroll the timeline to the bottom (legacy _scrollToBottom). */
  scrollTimelineToBottom(workspaceId: string): void;
  /** Render Markdown the same way the timeline engine does. */
  renderMarkdown(workspaceId: string, text: string): string;
  /** Build the shared copy button the timeline engine uses. */
  createCopyButton(workspaceId: string, getText: () => string): HTMLElement;
  /** Sanitize an outbound href (legacy _safeHref); returns '' when unsafe. */
  safeHref(workspaceId: string, url: string): string;
  /** Remove the tool card a mode-transition replaces (timeline internals). */
  removeToolCardForModeTransition(workspaceId: string, toolCallId: string): void;
  /** Whether the timeline is currently pinned to the bottom. */
  autoScrollPinned(workspaceId: string): boolean;
  /** Scroll a freshly appended mode-transition card to the top of the viewport. */
  scrollModeTransitionToStart(workspaceId: string, card: HTMLElement): void;
  /** Toggle the composer input row / attachment strip while the prompt is shown. */
  setComposerPromptActive(workspaceId: string, active: boolean): void;
  /** Return focus to the composer input after clearing the prompt. */
  focusComposerInput(workspaceId: string): void;
}

interface DecisionOption {
  optionId?: unknown;
  name?: unknown;
  kind?: unknown;
}

function asString(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function asOptionArray(value: unknown): DecisionOption[] {
  return Array.isArray(value) ? (value as DecisionOption[]) : [];
}

/** Faithful port of _decisionOptionClass — maps an option to its button class. */
export function decisionOptionClass(option: DecisionOption | null | undefined): string {
  const kind = String(option?.kind || '').toLowerCase();
  if (kind === 'allow_once') return 'agent-btn-allow';
  if (kind === 'allow_always') return 'agent-btn-always-allow';
  if (kind === 'reject_once' || kind === 'reject_always') return 'agent-btn-reject';

  const optionId = String(option?.optionId || '').toLowerCase();
  if (optionId === 'allow' || optionId === 'yes') return 'agent-btn-allow';
  if (optionId === 'always_allow') return 'agent-btn-always-allow';
  if (optionId === 'reject' || optionId === 'no') return 'agent-btn-reject';
  return '';
}

export class DecisionController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: DecisionHost;

  private state: AgentWorkspaceState;
  private modeTransitionCards: Record<string, HTMLElement> = {};
  private activeModeTransitionRequestId = '';
  private readonly animationFrames = new Set<number>();

  constructor(workspaceId: string, host: DecisionHost) {
    this.workspaceId = workspaceId;
    this.host = host;
    this.state = createInitialWorkspaceState(workspaceId);
  }

  mount(): void {
    // Decision cards are created lazily in response to ACP requests; there is no
    // static DOM to build at mount time.
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    this.state = state;
    const raw = event.raw;
    switch (event.type) {
      case 'permission_request':
        if (raw.presentation === 'mode_transition' && raw.documentText) {
          this.appendModeTransition(raw, false);
        } else {
          this.appendDecision(raw, 'permission');
        }
        break;
      case 'permission_resolved':
        this.resolvePermission(raw);
        break;
      case 'question_request':
        this.appendDecision(raw, 'question');
        break;
      case 'elicitation_request':
        this.appendElicitation(raw);
        break;
      case 'permission_cancelled':
      case 'elicitation_cancelled':
        this.cancelDecision(asString(raw.requestId), asString(raw.text) || 'Request cancelled.');
        break;
      case 'run_failed':
        // Matches legacy AgentThreadManager.js: a failed run interrupts a pending
        // mode transition before the timeline error handling runs.
        this.interruptModeTransition('The request ended before a selection was completed.');
        break;
      case 'run_finished':
        // Matches legacy AgentThreadManager.js RunFinished: a completed run
        // interrupts any still-pending mode transition prompt.
        this.interruptModeTransition('This request is no longer active.');
        break;
      default:
        break;
    }
  }

  dispose(): void {
    for (const frame of this.animationFrames) cancelAnimationFrame(frame);
    this.animationFrames.clear();
    this.modeTransitionCards = {};
    this.activeModeTransitionRequestId = '';
  }

  // --- Derived state -------------------------------------------------------

  private get assistantName(): string {
    return this.state.identity.assistantName;
  }

  private bridge(): AgentBridgePort | null {
    return this.host.bridgeFor(this.workspaceId);
  }

  private get thread(): HTMLElement | null {
    const panel = this.host.getPanel(this.workspaceId);
    return panel ? panel.querySelector<HTMLElement>('[data-role="thread"]') : null;
  }

  private get promptNode(): HTMLElement | null {
    const panel = this.host.getPanel(this.workspaceId);
    return panel ? panel.querySelector<HTMLElement>('[data-role="mode-transition-prompt"]') : null;
  }

  private get inputRowNode(): HTMLElement | null {
    const panel = this.host.getPanel(this.workspaceId);
    return panel ? panel.querySelector<HTMLElement>('[data-role="input-row"]') : null;
  }

  // --- Shared decision card infrastructure ---------------------------------

  /** Faithful port of _decisionButton. */
  private decisionButton(label: string, action: () => void, className?: string): HTMLButtonElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    if (className) {
      button.classList.add(className);
    }
    button.addEventListener('click', () => {
      const card = button.closest('[data-decision-state]') as HTMLElement | null;
      if (card && card.dataset.decisionState !== 'active') return;
      action();
    });
    return button;
  }

  /** Faithful port of _findDecisionCard. */
  private findDecisionCard(requestId: string): HTMLElement | null {
    if (!requestId) return null;
    const thread = this.thread;
    if (!thread) return null;
    return (
      Array.from(thread.querySelectorAll<HTMLElement>('[data-request-id]')).find(
        (element) => element.dataset.requestId === requestId
      ) || null
    );
  }

  /** Faithful port of _disableDecisionCard. */
  private disableDecisionCard(card: HTMLElement | null, text: string): void {
    if (!card) return;
    if (card.dataset.decisionState !== 'disabled') {
      card.dataset.decisionState = 'disabled';
      card.classList.add('agent-decision-disabled');
      card.querySelectorAll<HTMLInputElement>('button, input, textarea, select').forEach((control) => {
        control.disabled = true;
      });
    }

    let status = card.querySelector<HTMLElement>('.agent-decision-status');
    if (!status) {
      status = document.createElement('div');
      status.className = 'agent-decision-status';
      card.appendChild(status);
    }
    status.textContent = text || 'Request closed.';
  }

  // --- Permission / question cards -----------------------------------------

  /** Faithful port of _appendDecision. */
  private appendDecision(event: RawHostMessage, kind: 'permission' | 'question'): void {
    const details = document.createElement('details');
    details.className = 'agent-decision agent-decision-' + kind;
    details.open = true;
    details.dataset.decisionState = 'active';
    const requestId = asString(event.requestId);
    if (requestId) {
      details.dataset.requestId = requestId;
    }

    const header = document.createElement('summary');
    header.className = 'agent-decision-header';

    const content = document.createElement('div');
    content.className = 'agent-decision-header-content';

    const titleRow = document.createElement('div');
    titleRow.className = 'agent-decision-header-title-row';

    const chevron = document.createElement('span');
    chevron.className = 'agent-decision-chevron';

    const titleSpan = document.createElement('span');
    titleSpan.className = 'agent-decision-header-title';
    titleSpan.textContent =
      asString(event.title) || (kind === 'permission' ? 'Permission request' : this.assistantName + ' question');

    const subtitleSpan = document.createElement('span');
    subtitleSpan.className = 'agent-decision-header-subtitle';
    subtitleSpan.textContent =
      kind === 'permission'
        ? this.assistantName + ' Agent needs your approval before continuing.'
        : this.assistantName + ' Agent is waiting for your answer.';

    titleRow.appendChild(chevron);
    titleRow.appendChild(titleSpan);
    content.appendChild(titleRow);
    content.appendChild(subtitleSpan);
    header.appendChild(content);

    const body = document.createElement('div');
    body.className = 'agent-decision-body';

    const rawInputDetails = document.createElement('details');
    rawInputDetails.className = 'agent-decision-raw-input';

    const rawInputSummary = document.createElement('summary');
    rawInputSummary.className = 'agent-decision-raw-input-summary';
    rawInputSummary.textContent = 'Raw Input';

    const rawInputBody = document.createElement('pre');
    rawInputBody.className = 'agent-decision-raw-input-content';
    rawInputBody.textContent = asString(event.text) || '{}';

    rawInputDetails.appendChild(rawInputSummary);
    rawInputDetails.appendChild(rawInputBody);

    const actions = document.createElement('div');
    actions.className = 'agent-decision-actions';

    const hasStructuredOptions = Array.isArray(event.options);
    const options: DecisionOption[] = hasStructuredOptions
      ? asOptionArray(event.options)
      : [
          { name: kind === 'permission' ? 'Allow' : 'Yes', optionId: kind === 'permission' ? 'allow' : 'yes' },
          { name: kind === 'permission' ? 'Reject' : 'No', optionId: kind === 'permission' ? 'reject' : 'no' }
        ];

    if (options.length === 0) {
      const error = document.createElement('div');
      error.className = 'agent-decision-status agent-decision-options-error';
      error.textContent = 'The Agent did not provide any response options.';
      actions.appendChild(error);
    }

    options.forEach((option) => {
      const optionId = asString(option.optionId);
      const optionName = asString(option.name) || optionId || 'Select';

      const btn = this.decisionButton(optionName, () => {
        if (details.dataset.decisionState !== 'active') return;
        details.open = false;

        if (kind === 'permission') {
          this.bridge()?.sendAgentPermissionResponse(requestId, optionId);
        } else {
          this.bridge()?.sendAgentQuestionResponse(requestId, optionId);
        }

        this.disableDecisionCard(details, 'Response sent.');
      });

      const semanticClass = decisionOptionClass(option);
      if (semanticClass) btn.classList.add(semanticClass);

      actions.appendChild(btn);
    });

    body.appendChild(rawInputDetails);
    body.appendChild(actions);
    details.appendChild(header);
    details.appendChild(body);

    this.host.appendToTimeline(this.workspaceId, details);
    this.host.scrollTimelineToBottom(this.workspaceId);
  }

  /** Faithful port of _resolvePermission. */
  private resolvePermission(event: RawHostMessage): void {
    const requestId = asString(event.requestId);
    const card = this.findDecisionCard(requestId);
    if (!card) {
      this.clearModeTransitionPrompt(requestId, true);
      return;
    }

    if (!card.classList.contains('agent-mode-transition')) {
      const optionName = asString(event.optionName);
      const text = optionName ? 'Selected: ' + optionName : 'Response sent.';
      if (card.dataset.decisionState === 'disabled') {
        const status = card.querySelector<HTMLElement>('.agent-decision-status');
        if (status) status.textContent = text;
      } else {
        this.disableDecisionCard(card, text);
      }
      return;
    }

    card.dataset.decisionState = 'disabled';
    card.classList.remove('agent-mode-transition-sending');
    const decisionSection = card.querySelector<HTMLElement>('.agent-mode-transition-decision');
    if (decisionSection) decisionSection.hidden = false;
    const selectedOptionId = asString(event.optionId);
    card.querySelectorAll<HTMLButtonElement>('.agent-mode-transition-option').forEach((button) => {
      button.disabled = true;
      button.classList.remove('agent-mode-transition-option-pending');
      const selected = button.dataset.optionId === selectedOptionId;
      button.classList.toggle('agent-mode-transition-option-selected', selected);
      button.setAttribute('aria-pressed', selected ? 'true' : 'false');
    });

    const headerState = card.querySelector<HTMLElement>('.agent-mode-transition-header-state');
    if (headerState) headerState.textContent = 'Selected';
    const status = card.querySelector<HTMLElement>('.agent-mode-transition-status');
    const optionName = asString(event.optionName);
    if (status) status.textContent = optionName ? 'Selected: ' + optionName : 'Selection recorded.';
    this.clearModeTransitionPrompt(requestId, true);
  }

  /** Faithful port of _cancelDecision. */
  private cancelDecision(requestId: string, text: string): void {
    const card = this.findDecisionCard(requestId);
    if (!card) {
      this.clearModeTransitionPrompt(requestId, true);
      return;
    }

    if (card.classList.contains('agent-mode-transition')) {
      card.dataset.decisionState = 'disabled';
      card.classList.remove('agent-mode-transition-sending');
      const decisionSection = card.querySelector<HTMLElement>('.agent-mode-transition-decision');
      if (decisionSection) decisionSection.hidden = false;
      card.querySelectorAll<HTMLButtonElement>('.agent-mode-transition-option').forEach((button) => {
        button.disabled = true;
        button.classList.remove('agent-mode-transition-option-pending');
      });
      const headerState = card.querySelector<HTMLElement>('.agent-mode-transition-header-state');
      if (headerState) headerState.textContent = 'Cancelled';
      const status = card.querySelector<HTMLElement>('.agent-mode-transition-status');
      if (status) status.textContent = text || 'Request cancelled.';
      this.clearModeTransitionPrompt(requestId, true);
      return;
    }

    this.disableDecisionCard(card, text || 'Request cancelled.');
  }

  // --- Elicitation forms ---------------------------------------------------

  /** Faithful port of _appendElicitation. */
  private appendElicitation(event: RawHostMessage): void {
    const card = document.createElement('section');
    card.className = 'agent-decision agent-decision-elicitation';
    card.dataset.decisionState = 'active';
    const requestId = asString(event.requestId);
    if (requestId) {
      card.dataset.requestId = requestId;
    }

    const title = document.createElement('div');
    title.className = 'agent-decision-title';
    title.textContent = this.assistantName + ' Agent needs input';

    const subtitle = document.createElement('div');
    subtitle.className = 'agent-decision-subtitle';
    subtitle.textContent = asString(event.message) || 'Provide the requested information to continue.';

    const form = document.createElement('form');
    form.className = 'agent-elicitation-form';
    form.noValidate = true;

    const schema = (event.schema || {}) as Record<string, unknown>;
    const requiredList = Array.isArray(schema.required) ? (schema.required as unknown[]) : [];
    const required = new Set(requiredList);
    const controls: ElicitationFieldControl[] = [];

    orderElicitationFields(schema).forEach((entry) => {
      const field = this.createElicitationField(
        entry.name,
        entry.property,
        required.has(entry.name),
        entry.isSupplement
      );
      controls.push(field);
      form.appendChild(field.row);
    });

    if (event.mode === 'url' && event.url) {
      const urlRow = document.createElement('div');
      urlRow.className = 'agent-elicitation-url';
      const safeUrl = this.host.safeHref(this.workspaceId, asString(event.url));
      if (safeUrl) {
        const link = document.createElement('a');
        link.href = safeUrl;
        link.target = '_blank';
        link.rel = 'noreferrer';
        link.textContent = asString(event.url);
        urlRow.appendChild(link);
      } else {
        urlRow.textContent = asString(event.url);
      }
      form.appendChild(urlRow);
    }

    if (controls.length === 0 && event.mode !== 'url') {
      const field = this.createElicitationField('response', { type: 'string', title: 'Response' }, true, false);
      controls.push(field);
      form.appendChild(field.row);
    }

    const actions = document.createElement('div');
    actions.className = 'agent-decision-actions agent-elicitation-actions';

    actions.appendChild(
      this.decisionButton(
        'Continue',
        () => {
          if (card.dataset.decisionState !== 'active') return;

          let valid = true;
          const content: Record<string, unknown> = {};
          controls.forEach((field) => {
            if (!field.validate()) {
              valid = false;
            }
          });

          if (!valid) {
            return;
          }

          controls.forEach((field) => {
            const value = field.read();
            if (value !== undefined) {
              content[field.name] = value;
            }
          });

          this.bridge()?.sendAgentElicitationResponse(
            requestId,
            JSON.stringify({ action: 'accept', content })
          );
          this.disableDecisionCard(card, 'Response sent.');
        },
        'agent-btn-primary'
      )
    );

    actions.appendChild(
      this.decisionButton(
        'Decline',
        () => {
          if (card.dataset.decisionState !== 'active') return;
          this.bridge()?.sendAgentElicitationResponse(requestId, JSON.stringify({ action: 'decline' }));
          this.disableDecisionCard(card, 'Declined.');
        },
        'agent-btn-subtle'
      )
    );

    actions.appendChild(
      this.decisionButton(
        'Cancel',
        () => {
          if (card.dataset.decisionState !== 'active') return;
          this.bridge()?.sendAgentElicitationResponse(requestId, JSON.stringify({ action: 'cancel' }));
          this.disableDecisionCard(card, 'Cancelled.');
        },
        'agent-btn-subtle'
      )
    );

    card.appendChild(title);
    card.appendChild(subtitle);
    card.appendChild(form);
    card.appendChild(actions);
    this.host.appendToTimeline(this.workspaceId, card);
    this.host.scrollTimelineToBottom(this.workspaceId);
  }

  /** Faithful port of _createElicitationField. */
  private createElicitationField(
    name: string,
    property: ElicitationProperty,
    required: boolean,
    isSupplement: boolean
  ): ElicitationFieldControl {
    const row = document.createElement('div');
    row.className = 'agent-elicitation-field';
    if (isSupplement) {
      row.classList.add('agent-elicitation-field-supplement');
    }

    const label = document.createElement('div');
    label.className = 'agent-elicitation-label';
    label.textContent = (asString(property.title) || name) + (required ? ' *' : '');
    row.appendChild(label);

    const description = asString(property.description);
    const error = document.createElement('div');
    error.className = 'agent-elicitation-error';
    error.hidden = true;

    const options = readElicitationOptions(property);
    const isArray = property.type === 'array' && !!property.items;
    let read: () => unknown;
    let validate: () => boolean;

    const setError = (message: string): void => {
      row.classList.toggle('agent-elicitation-field-error', !!message);
      error.textContent = message || '';
      error.hidden = !message;
    };
    const clearError = (): void => setError('');

    if (property.type === 'boolean') {
      const labelRow = document.createElement('label');
      labelRow.className = 'agent-elicitation-checkbox';
      const control = document.createElement('input');
      control.type = 'checkbox';
      control.checked = !!property.default;
      control.addEventListener('change', clearError);
      labelRow.appendChild(control);
      row.appendChild(labelRow);
      read = () => control.checked;
      validate = () => true;
    } else if (options.length && !isArray) {
      let selected = defaultOptionValue(options, property.default, true);
      const list = document.createElement('div');
      list.className = 'agent-elicitation-option-list';
      list.setAttribute('role', 'radiogroup');
      list.setAttribute('aria-label', asString(property.title) || name);

      const sync = (): void => {
        list.querySelectorAll<OptionButton>('.agent-elicitation-option').forEach((button) => {
          const isSelected = button._optionValue === selected;
          button.classList.toggle('agent-elicitation-option-selected', isSelected);
          button.setAttribute('aria-checked', String(isSelected));
        });
      };

      options.forEach((option) => {
        const button = this.createElicitationOptionButton(option, false);
        button._optionValue = option.value;
        button.addEventListener('click', () => {
          selected = option.value;
          clearError();
          sync();
        });
        list.appendChild(button);
      });

      row.appendChild(list);
      sync();
      read = () => selected;
      validate = () => {
        if (required && (selected === undefined || selected === null || selected === '')) {
          setError('Choose an option to continue.');
          return false;
        }
        clearError();
        return true;
      };
    } else if (isArray) {
      const arrayOptions = readElicitationOptions(property.items);
      const selected = new Set<unknown>(Array.isArray(property.default) ? (property.default as unknown[]) : []);
      const list = document.createElement('div');
      list.className = 'agent-elicitation-option-list';
      list.setAttribute('role', 'group');
      list.setAttribute('aria-label', asString(property.title) || name);

      const sync = (): void => {
        list.querySelectorAll<OptionButton>('.agent-elicitation-option').forEach((button) => {
          const isSelected = selected.has(button._optionValue);
          button.classList.toggle('agent-elicitation-option-selected', isSelected);
          button.setAttribute('aria-pressed', String(isSelected));
        });
      };

      arrayOptions.forEach((option) => {
        const button = this.createElicitationOptionButton(option, true);
        button._optionValue = option.value;
        button.addEventListener('click', () => {
          if (selected.has(option.value)) {
            selected.delete(option.value);
          } else {
            selected.add(option.value);
          }
          clearError();
          sync();
        });
        list.appendChild(button);
      });

      row.appendChild(list);
      sync();
      read = () => Array.from(selected);
      validate = () => {
        if (required && selected.size === 0) {
          setError('Choose at least one option to continue.');
          return false;
        }
        clearError();
        return true;
      };
    } else {
      const control =
        property.type === 'integer' || property.type === 'number'
          ? document.createElement('input')
          : document.createElement('textarea');

      if (control.tagName === 'INPUT') {
        const input = control as HTMLInputElement;
        input.type = 'number';
        if (property.type === 'integer') input.step = '1';
        read = () => {
          const rawValue = input.value.trim();
          if (rawValue === '') return undefined;
          return Number(rawValue);
        };
        validate = () => {
          const rawValue = input.value.trim();
          if (required && rawValue === '') {
            setError('Enter a number to continue.');
            return false;
          }
          if (rawValue !== '') {
            const numeric = Number(rawValue);
            if (!Number.isFinite(numeric) || (property.type === 'integer' && !Number.isInteger(numeric))) {
              setError(property.type === 'integer' ? 'Enter a whole number.' : 'Enter a valid number.');
              return false;
            }
          }
          clearError();
          return true;
        };
      } else {
        const textarea = control as HTMLTextAreaElement;
        textarea.rows = isSupplement ? 2 : 3;
        read = () => {
          const value = textarea.value;
          return value === '' && !required ? undefined : value;
        };
        validate = () => {
          if (required && textarea.value.trim() === '') {
            setError('Enter a response to continue.');
            return false;
          }
          clearError();
          return true;
        };
      }

      if (property.default !== undefined && property.default !== null) {
        (control as HTMLInputElement | HTMLTextAreaElement).value = String(property.default);
      }

      control.addEventListener('input', clearError);
      row.appendChild(control);
    }

    if (description) {
      const hint = document.createElement('small');
      hint.textContent = description;
      row.appendChild(hint);
    }

    row.appendChild(error);

    return { name, row, read, validate };
  }

  /** Faithful port of _createElicitationOptionButton. */
  private createElicitationOptionButton(option: ElicitationOption, multi: boolean): OptionButton {
    const button = document.createElement('button') as OptionButton;
    button.type = 'button';
    button.className = 'agent-elicitation-option';
    button.setAttribute('role', multi ? 'button' : 'radio');

    const title = document.createElement('span');
    title.className = 'agent-elicitation-option-title';
    title.textContent = option.title;
    button.appendChild(title);

    if (option.description) {
      const description = document.createElement('small');
      description.className = 'agent-elicitation-option-description';
      description.textContent = option.description;
      button.appendChild(description);
    }

    return button;
  }

  // --- Mode transition -----------------------------------------------------

  /**
   * Render a historical mode-transition card during thread replay. The timeline
   * replay loop owns the replay pass but the mode-transition card is a decisions
   * node, so it reaches this domain through the TimelineHost seam.
   */
  renderHistoricalModeTransition(event: RawHostMessage): void {
    this.appendModeTransition(event, true);
  }

  /** Faithful port of _appendModeTransition. */
  private appendModeTransition(event: RawHostMessage, historical: boolean): void {
    const requestId = asString(event.requestId);
    const toolCallId = asString(event.toolCallId);
    const key = requestId || 'tool:' + toolCallId;
    const options = asOptionArray(event.options);
    const selectedOptionId = asString(event.selectedOptionId);
    const storedState = asString(event.decisionState) || (historical ? 'interrupted' : 'pending');
    const pending = !historical && storedState === 'pending';
    const interactive = pending && !!requestId && options.length > 0;
    const displayState = pending && (!requestId || options.length === 0) ? 'error' : storedState;
    const wasPinned = this.host.autoScrollPinned(this.workspaceId);

    if (pending && this.activeModeTransitionRequestId && this.activeModeTransitionRequestId !== requestId) {
      this.interruptModeTransition('A newer mode transition request replaced this one.');
    }
    this.host.removeToolCardForModeTransition(this.workspaceId, toolCallId);

    const existing = key ? this.modeTransitionCards[key] : null;
    if (existing?.isConnected) existing.remove();

    const card = document.createElement('section');
    card.className = 'agent-mode-transition';
    card.dataset.decisionState = pending ? 'active' : 'disabled';
    if (requestId) card.dataset.requestId = requestId;
    if (toolCallId) card.dataset.toolCallId = toolCallId;

    const header = document.createElement('header');
    header.className = 'agent-mode-transition-header';

    const title = document.createElement('div');
    title.className = 'agent-mode-transition-title';
    title.textContent = asString(event.title) || 'Mode transition';

    const headerState = document.createElement('span');
    headerState.className = 'agent-mode-transition-header-state';
    headerState.textContent = this.modeTransitionStateLabel(displayState, interactive);

    header.appendChild(title);
    header.appendChild(headerState);

    const documentDetails = document.createElement('details');
    documentDetails.className = 'agent-mode-transition-details';
    documentDetails.open = !historical;

    const documentSummary = document.createElement('summary');
    documentSummary.className = 'agent-mode-transition-summary';
    documentSummary.textContent = 'Proposal details';

    const documentBody = document.createElement('div');
    documentBody.className = 'agent-mode-transition-document agent-message-body';
    documentBody.innerHTML = this.host.renderMarkdown(this.workspaceId, asString(event.documentText));

    const documentActions = document.createElement('div');
    documentActions.className = 'agent-message-actions agent-mode-transition-document-actions';
    documentActions.appendChild(this.host.createCopyButton(this.workspaceId, () => asString(event.documentText)));

    documentDetails.appendChild(documentSummary);
    documentDetails.appendChild(documentBody);
    documentDetails.appendChild(documentActions);

    const decisionSection = document.createElement('div');
    decisionSection.className = 'agent-mode-transition-decision';

    const decisionLabel = document.createElement('div');
    decisionLabel.className = 'agent-mode-transition-decision-label';
    decisionLabel.textContent = 'Choose how to continue';
    decisionSection.appendChild(decisionLabel);

    const actions = document.createElement('div');
    actions.className = 'agent-mode-transition-options';
    actions.setAttribute('role', 'group');
    actions.setAttribute('aria-label', 'Choose how to continue');

    if (options.length === 0) {
      const error = document.createElement('div');
      error.className = 'agent-mode-transition-error';
      error.textContent = 'The ACP Agent did not provide any response options.';
      actions.appendChild(error);
    } else {
      options.forEach((option) => {
        const optionId = asString(option.optionId);
        const optionName = asString(option.name) || optionId || 'Select';
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'agent-mode-transition-option';
        button.textContent = optionName;
        button.dataset.optionId = optionId;
        button.dataset.optionKind = asString(option.kind);

        const semanticClass = decisionOptionClass(option);
        if (semanticClass) button.classList.add(semanticClass);

        if (optionId === selectedOptionId) {
          button.classList.add('agent-mode-transition-option-selected');
          button.setAttribute('aria-pressed', 'true');
        } else {
          button.setAttribute('aria-pressed', 'false');
        }

        button.disabled = true;

        actions.appendChild(button);
      });
    }

    decisionSection.appendChild(actions);
    decisionSection.hidden = pending;

    const status = document.createElement('div');
    status.className = 'agent-mode-transition-status';
    status.setAttribute('aria-live', 'polite');
    status.textContent = this.modeTransitionStatusText(displayState, options, selectedOptionId, interactive);

    documentDetails.appendChild(decisionSection);
    documentDetails.appendChild(status);
    card.appendChild(header);
    card.appendChild(documentDetails);
    this.host.appendToTimeline(this.workspaceId, card);

    if (key) this.modeTransitionCards[key] = card;
    if (requestId) this.modeTransitionCards['request:' + requestId] = card;
    if (toolCallId) this.modeTransitionCards['tool:' + toolCallId] = card;
    if (pending) {
      this.showModeTransitionPrompt(event, card, interactive);
      if (wasPinned) this.host.scrollModeTransitionToStart(this.workspaceId, card);
    } else {
      this.host.scrollTimelineToBottom(this.workspaceId);
    }
  }

  /** Faithful port of _modeTransitionStateLabel. */
  private modeTransitionStateLabel(state: string, active: boolean): string {
    if (active) return 'Decision required';
    if (state === 'error') return 'Unavailable';
    if (state === 'selected') return 'Selected';
    if (state === 'cancelled') return 'Cancelled';
    return 'Interrupted';
  }

  /** Faithful port of _modeTransitionStatusText. */
  private modeTransitionStatusText(
    state: string,
    options: DecisionOption[],
    selectedOptionId: string,
    active: boolean
  ): string {
    if (active) return 'The Agent is waiting for your selection.';
    if (state === 'error') return 'The request cannot continue without an ACP response option.';
    if (state === 'selected') {
      const selected = options.find((option) => asString(option.optionId) === selectedOptionId);
      return selected ? 'Selected: ' + asString(selected.name) : 'Selection recorded.';
    }
    if (state === 'cancelled') return 'Request cancelled.';
    return 'This request is no longer active.';
  }

  /** Faithful port of _showModeTransitionPrompt. */
  private showModeTransitionPrompt(event: RawHostMessage, card: HTMLElement, interactive: boolean): void {
    const prompt = this.promptNode;
    const inputRow = this.inputRowNode;
    if (!prompt || !inputRow) return;

    const requestId = asString(event.requestId);
    const options = asOptionArray(event.options);
    const composerHadFocus = inputRow.contains(document.activeElement);

    this.clearModeTransitionPrompt('', false);
    this.activeModeTransitionRequestId = requestId;
    prompt.innerHTML = '';

    const header = document.createElement('div');
    header.className = 'agent-composer-decision-header';

    const heading = document.createElement('div');
    heading.className = 'agent-composer-decision-heading';

    const title = document.createElement('div');
    title.className = 'agent-composer-decision-title';
    title.textContent = asString(event.title) || 'Mode transition';

    const instruction = document.createElement('div');
    instruction.className = 'agent-composer-decision-instruction';
    instruction.textContent = 'Choose how to continue';

    heading.appendChild(title);
    heading.appendChild(instruction);

    const stopButton = document.createElement('button');
    stopButton.type = 'button';
    stopButton.className = 'agent-composer-decision-stop';
    stopButton.textContent = 'Stop';
    stopButton.title = 'Stop ' + this.assistantName;
    stopButton.addEventListener('click', () => {
      if (stopButton.disabled) return;
      stopButton.disabled = true;
      stopButton.textContent = 'Stopping';
      status.textContent = 'Stopping the current Agent run…';
      prompt.querySelectorAll<HTMLButtonElement>('.agent-composer-decision-option').forEach((button) => {
        button.disabled = true;
      });
      this.bridge()?.sendAgentCommand('stop');
    });

    header.appendChild(heading);
    header.appendChild(stopButton);

    const actions = document.createElement('div');
    actions.className = 'agent-composer-decision-options';
    actions.setAttribute('role', 'group');
    actions.setAttribute('aria-label', 'Choose how to continue');

    if (interactive) {
      options.forEach((option) => {
        const optionId = asString(option.optionId);
        const optionName = asString(option.name) || optionId || 'Select';
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'agent-composer-decision-option';
        button.textContent = optionName;
        button.dataset.optionId = optionId;
        button.dataset.optionKind = asString(option.kind);

        const semanticClass = decisionOptionClass(option);
        if (semanticClass) button.classList.add(semanticClass);

        button.addEventListener('click', () => {
          if (this.activeModeTransitionRequestId !== requestId || !optionId) return;

          prompt.dataset.decisionState = 'sending';
          prompt.querySelectorAll<HTMLButtonElement>('.agent-composer-decision-option').forEach((candidate) => {
            candidate.disabled = true;
          });
          button.classList.add('agent-mode-transition-option-pending');
          card.dataset.decisionState = 'sending';
          card.classList.add('agent-mode-transition-sending');

          const cardHeaderState = card.querySelector<HTMLElement>('.agent-mode-transition-header-state');
          if (cardHeaderState) cardHeaderState.textContent = 'Sending';
          const cardStatus = card.querySelector<HTMLElement>('.agent-mode-transition-status');
          if (cardStatus) cardStatus.textContent = 'Sending ' + optionName + '…';
          status.textContent = 'Sending ' + optionName + '…';
          this.bridge()?.sendAgentPermissionResponse(requestId, optionId);
        });

        actions.appendChild(button);
      });
    } else {
      const error = document.createElement('div');
      error.className = 'agent-mode-transition-error';
      error.textContent = requestId
        ? 'The ACP Agent did not provide any response options.'
        : 'The ACP Agent did not provide a valid request identifier.';
      actions.appendChild(error);
    }

    const status = document.createElement('div');
    status.className = 'agent-composer-decision-status';
    status.setAttribute('role', 'status');
    status.setAttribute('aria-live', 'polite');
    status.textContent = interactive
      ? 'The Agent is waiting for your selection.'
      : 'The request cannot continue until the Agent provides valid ACP response data.';

    prompt.appendChild(header);
    prompt.appendChild(actions);
    prompt.appendChild(status);
    prompt.dataset.decisionState = interactive ? 'active' : 'error';
    prompt.hidden = false;
    this.host.setComposerPromptActive(this.workspaceId, true);

    if (composerHadFocus) {
      this.requestFrame(() => {
        const target =
          prompt.querySelector<HTMLElement>('.agent-composer-decision-option:not(:disabled)') || stopButton;
        target.focus();
      });
    }
  }

  /** Faithful port of _clearModeTransitionPrompt. */
  private clearModeTransitionPrompt(requestId: string, restoreFocus: boolean): void {
    const prompt = this.promptNode;
    const inputRow = this.inputRowNode;
    if (!prompt || !inputRow) return;
    if (requestId && this.activeModeTransitionRequestId !== requestId) return;

    const promptHadFocus = prompt.contains(document.activeElement);
    prompt.hidden = true;
    prompt.innerHTML = '';
    delete prompt.dataset.decisionState;
    this.activeModeTransitionRequestId = '';
    this.host.setComposerPromptActive(this.workspaceId, false);

    if (restoreFocus !== false && promptHadFocus) {
      this.requestFrame(() => this.host.focusComposerInput(this.workspaceId));
    }
  }

  /** Faithful port of _interruptModeTransition. */
  private interruptModeTransition(text: string): void {
    const requestId = this.activeModeTransitionRequestId;
    const prompt = this.promptNode;
    if (!requestId && (!prompt || prompt.hidden)) return;

    const thread = this.thread;
    const card = requestId
      ? this.findDecisionCard(requestId)
      : thread
      ? thread.querySelector<HTMLElement>('.agent-mode-transition[data-decision-state="active"]')
      : null;
    if (card?.classList.contains('agent-mode-transition')) {
      card.dataset.decisionState = 'disabled';
      card.classList.remove('agent-mode-transition-sending');
      const decisionSection = card.querySelector<HTMLElement>('.agent-mode-transition-decision');
      if (decisionSection) decisionSection.hidden = false;
      const headerState = card.querySelector<HTMLElement>('.agent-mode-transition-header-state');
      if (headerState) headerState.textContent = 'Interrupted';
      const status = card.querySelector<HTMLElement>('.agent-mode-transition-status');
      if (status) status.textContent = text || 'This request is no longer active.';
    }

    this.clearModeTransitionPrompt(requestId || '', true);
  }

  private requestFrame(callback: () => void): void {
    let frame = 0;
    let completed = false;
    frame = requestAnimationFrame(() => {
      completed = true;
      this.animationFrames.delete(frame);
      callback();
    });
    if (!completed) this.animationFrames.add(frame);
  }
}

/** A wired elicitation form control returned by createElicitationField. */
interface ElicitationFieldControl {
  name: string;
  row: HTMLElement;
  read: () => unknown;
  validate: () => boolean;
}

/** Elicitation option button carrying its value (legacy button._optionValue). */
interface OptionButton extends HTMLButtonElement {
  _optionValue?: unknown;
}
