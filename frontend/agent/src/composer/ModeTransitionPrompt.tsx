import { useEffect, useRef, useState, type JSX } from 'react';
import { Button } from '../components/ui/button.js';
import { DecisionOptionPills } from '../decisions/DecisionOptionPills.js';

export interface ComposerModeTransitionOptionVM {
  optionId: string;
  name: string;
  kind: string;
}

export interface ComposerModeTransitionPromptVM {
  requestId: string;
  title: string;
  assistantName: string;
  autoFocus: boolean;
  interactive: boolean;
  errorText: string;
  options: ComposerModeTransitionOptionVM[];
  onRespond(optionId: string): void;
  onStop(): void;
}

export function ModeTransitionPrompt({
  prompt
}: {
  prompt: ComposerModeTransitionPromptVM;
}): JSX.Element {
  const [decisionState, setDecisionState] = useState(
    prompt.interactive ? 'active' : 'error'
  );
  const [status, setStatus] = useState(
    prompt.interactive
      ? 'The Agent is waiting for your selection.'
      : 'The request cannot continue until the Agent provides valid ACP response data.'
  );
  const [stopping, setStopping] = useState(false);
  const firstOptionRef = useRef<HTMLButtonElement | null>(null);
  const stopRef = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    if (!prompt.autoFocus) return;
    const frame = requestAnimationFrame(() => {
      firstOptionRef.current?.focus();
      if (!firstOptionRef.current) stopRef.current?.focus();
    });
    return () => cancelAnimationFrame(frame);
  }, [prompt.autoFocus]);

  const optionsLocked = decisionState === 'sending' || stopping;

  return (
    <div
      className="box-border rounded-[var(--agent-radius-card)] border bg-card p-[var(--agent-space-3)] text-card-foreground"
      data-decision-state={decisionState}
    >
      <div className="mb-[var(--agent-space-3)] flex items-start justify-between gap-[var(--agent-space-3)]">
        <div className="min-w-0">
          <div className="break-words text-[13px] font-bold leading-[1.4]">
            {prompt.title}
          </div>
          <div className="mt-0.5 text-xs leading-[1.4] text-muted-foreground">
            Choose how to continue
          </div>
        </div>
        <Button
          ref={stopRef}
          type="button"
          variant="outline"
          size="sm"
          className="min-w-[72px] shrink-0 rounded-full text-xs"
          disabled={stopping}
          title={'Stop ' + prompt.assistantName}
          onClick={() => {
            if (stopping) return;
            setStopping(true);
            setStatus('Stopping the current Agent run…');
            prompt.onStop();
          }}
        >
          {stopping ? 'Stopping' : 'Stop'}
        </Button>
      </div>

      {prompt.interactive ? (
        <DecisionOptionPills
          options={prompt.options}
          disabled={optionsLocked}
          ariaLabel="Choose how to continue"
          className="agent-composer-decision-options"
          buttonClassName="agent-composer-decision-option"
          firstOptionRef={firstOptionRef}
          onSelect={(option) => {
            if (optionsLocked) return;
            setDecisionState('sending');
            setStatus('Sending ' + option.name + '…');
            prompt.onRespond(option.optionId);
          }}
        />
      ) : (
        <div role="group" aria-label="Choose how to continue">
          <div className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs leading-normal text-destructive">
            {prompt.errorText}
          </div>
        </div>
      )}

      <div
        className={
          'mt-[var(--agent-space-2)] min-h-[17px] text-xs leading-[1.4] ' +
          (decisionState === 'sending' ? 'text-primary' : 'text-muted-foreground')
        }
        role="status"
        aria-live="polite"
      >
        {status}
      </div>
    </div>
  );
}
