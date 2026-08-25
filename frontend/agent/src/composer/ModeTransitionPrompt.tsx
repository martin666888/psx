import { useEffect, useRef, useState, type JSX } from 'react';
import { useTranslation } from 'react-i18next';
import { useAnnounceLive } from '../ui/announce.js';
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
  const { t } = useTranslation('agent');
  const live = useAnnounceLive();
  const [decisionState, setDecisionState] = useState(
    prompt.interactive ? 'active' : 'error'
  );
  const [statusCode, setStatusCode] = useState(
    prompt.interactive ? 'waitingSelection' : 'invalidResponseData'
  );
  const [statusParams, setStatusParams] = useState<Record<string, unknown> | undefined>(undefined);
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
          <div className="break-words text-[13px] font-semibold leading-[1.4]">
            {prompt.title || t('timeline.decision.modeTransitionTitle')}
          </div>
          <div className="mt-0.5 text-xs leading-[1.4] text-muted-foreground">
            {t('composer.transition.chooseHow')}
          </div>
        </div>
        <Button
          ref={stopRef}
          type="button"
          variant="outline"
          size="sm"
          className="min-w-[72px] shrink-0 rounded-full text-xs"
          disabled={stopping}
          title={t('composer.transition.stopTitle', { assistantName: prompt.assistantName })}
          onClick={() => {
            if (stopping) return;
            setStopping(true);
            setStatusCode('stoppingRun');
            prompt.onStop();
          }}
        >
          {t(stopping ? 'composer.transition.stopping' : 'composer.transition.stop')}
        </Button>
      </div>

      {prompt.interactive ? (
        <DecisionOptionPills
          options={prompt.options}
          disabled={optionsLocked}
          ariaLabel={t('composer.transition.chooseHow')}
          labelFor={(option) =>
            option.name ||
            t(['allow', 'reject', 'yes', 'no'].includes(option.optionId)
              ? `timeline.decision.option.${option.optionId}`
              : 'timeline.decision.option.select')
          }
          className="agent-composer-decision-options"
          buttonClassName="agent-composer-decision-option"
          firstOptionRef={firstOptionRef}
          onSelect={(option) => {
            if (optionsLocked) return;
            setDecisionState('sending');
            setStatusParams({ name: option.name });
            setStatusCode('sendingOption');
            prompt.onRespond(option.optionId);
          }}
        />
      ) : (
        <div role="group" aria-label={t('composer.transition.chooseHow')}>
          <div className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs leading-normal text-destructive">
            {prompt.errorText ? t(prompt.errorText) : ''}
          </div>
        </div>
      )}

      <div
        className={
          'mt-[var(--agent-space-2)] min-h-[17px] text-xs leading-[1.4] ' +
          (decisionState === 'sending' ? 'text-primary' : 'text-muted-foreground')
        }
        role="status"
        aria-live={live}
      >
        {t(`composer.transition.status.${statusCode}`, {
          defaultValue: '',
          ...(statusParams ?? {})
        })}
      </div>
    </div>
  );
}
