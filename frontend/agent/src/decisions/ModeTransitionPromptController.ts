// ModeTransitionPromptController.ts — composer-region ACP mode prompt.
//
// Decision cards in the conversation are React Timeline items. This controller
// owns only the separate prompt that temporarily takes over the Composer.

import type { AgentBridgePort } from '../contracts/bridge-port.js';
import type { FeatureController } from '../contracts/feature-controller.js';
import type { AgentWorkspaceEvent, RawHostMessage } from '../contracts/host-events.js';
import type { AgentWorkspaceState } from '../contracts/workspace-state.js';
import { decisionOptionClass } from './decisionPresentation.js';

export interface ModeTransitionPromptHost {
  getPanel(workspaceId: string): HTMLElement | null;
  bridgeFor(workspaceId: string): AgentBridgePort | null;
  setComposerPromptActive(workspaceId: string, active: boolean): void;
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

export class ModeTransitionPromptController implements FeatureController {
  private readonly workspaceId: string;
  private readonly host: ModeTransitionPromptHost;
  private assistantName = 'Agent';
  private activeRequestId = '';
  private readonly animationFrames = new Set<number>();

  constructor(workspaceId: string, host: ModeTransitionPromptHost) {
    this.workspaceId = workspaceId;
    this.host = host;
  }

  mount(): void {
    // The prompt is rendered lazily when ACP raises a mode transition.
  }

  update(event: AgentWorkspaceEvent, state: AgentWorkspaceState): void {
    this.assistantName = state.identity.assistantName;
    const raw = event.raw;
    switch (event.type) {
      case 'permission_request': {
        if (raw.presentation !== 'mode_transition' || !raw.documentText) return;
        const requestId = asString(raw.requestId);
        const options = asOptionArray(raw.options);
        const storedState = asString(raw.decisionState) || 'pending';
        if (storedState !== 'pending') return;
        this.showPrompt(raw, !!requestId && options.length > 0);
        return;
      }
      case 'permission_resolved':
      case 'permission_cancelled':
      case 'elicitation_cancelled':
        this.clearPrompt(asString(raw.requestId), true);
        return;
      case 'run_failed':
      case 'run_finished':
        this.clearPrompt(this.activeRequestId, true);
        return;
      default:
        return;
    }
  }

  dispose(): void {
    for (const frame of this.animationFrames) cancelAnimationFrame(frame);
    this.animationFrames.clear();
    this.activeRequestId = '';
  }

  private showPrompt(event: RawHostMessage, interactive: boolean): void {
    const prompt = this.promptNode;
    const inputRow = this.inputRowNode;
    if (!prompt || !inputRow) return;

    const requestId = asString(event.requestId);
    const options = asOptionArray(event.options);
    const composerHadFocus = inputRow.contains(document.activeElement);

    this.clearPrompt('', false);
    this.activeRequestId = requestId;
    prompt.replaceChildren();

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
    heading.append(title, instruction);

    const status = document.createElement('div');
    status.className = 'agent-composer-decision-status';
    status.setAttribute('role', 'status');
    status.setAttribute('aria-live', 'polite');

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
    header.append(heading, stopButton);

    const actions = document.createElement('div');
    actions.className = 'agent-composer-decision-options';
    actions.setAttribute('role', 'group');
    actions.setAttribute('aria-label', 'Choose how to continue');

    if (interactive) {
      for (const option of options) {
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
          if (this.activeRequestId !== requestId || !optionId) return;
          prompt.dataset.decisionState = 'sending';
          prompt.querySelectorAll<HTMLButtonElement>('.agent-composer-decision-option').forEach((candidate) => {
            candidate.disabled = true;
          });
          button.classList.add('agent-mode-transition-option-pending');
          status.textContent = 'Sending ' + optionName + '…';
          this.bridge()?.sendAgentPermissionResponse(requestId, optionId);
        });
        actions.appendChild(button);
      }
    } else {
      const error = document.createElement('div');
      error.className = 'agent-mode-transition-error';
      error.textContent = requestId
        ? 'The ACP Agent did not provide any response options.'
        : 'The ACP Agent did not provide a valid request identifier.';
      actions.appendChild(error);
    }

    status.textContent = interactive
      ? 'The Agent is waiting for your selection.'
      : 'The request cannot continue until the Agent provides valid ACP response data.';

    prompt.append(header, actions, status);
    prompt.dataset.decisionState = interactive ? 'active' : 'error';
    prompt.hidden = false;
    this.host.setComposerPromptActive(this.workspaceId, true);

    if (composerHadFocus) {
      this.requestFrame(() => {
        const target =
          prompt.querySelector<HTMLElement>('.agent-composer-decision-option:not(:disabled)') ??
          stopButton;
        target.focus();
      });
    }
  }

  private clearPrompt(requestId: string, restoreFocus: boolean): void {
    const prompt = this.promptNode;
    if (!prompt) return;
    if (requestId && this.activeRequestId && this.activeRequestId !== requestId) return;

    const promptHadFocus = prompt.contains(document.activeElement);
    prompt.hidden = true;
    prompt.replaceChildren();
    delete prompt.dataset.decisionState;
    this.activeRequestId = '';
    this.host.setComposerPromptActive(this.workspaceId, false);

    if (restoreFocus && promptHadFocus) {
      this.requestFrame(() => this.host.focusComposerInput(this.workspaceId));
    }
  }

  private requestFrame(callback: () => void): void {
    let frame = 0;
    frame = requestAnimationFrame(() => {
      this.animationFrames.delete(frame);
      callback();
    });
    this.animationFrames.add(frame);
  }

  private bridge(): AgentBridgePort | null {
    return this.host.bridgeFor(this.workspaceId);
  }

  private get promptNode(): HTMLElement | null {
    return this.host
      .getPanel(this.workspaceId)
      ?.querySelector<HTMLElement>('[data-role="mode-transition-prompt"]') ?? null;
  }

  private get inputRowNode(): HTMLElement | null {
    return this.host
      .getPanel(this.workspaceId)
      ?.querySelector<HTMLElement>('[data-role="input-row"]') ?? null;
  }
}
