import type { Ref } from 'react';
import { Button } from '../components/ui/button.js';
import { cn } from '../lib/utils.js';

export interface DecisionPillOption {
  optionId: string;
  name: string;
  kind?: string;
}

export function DecisionOptionPills<TOption extends DecisionPillOption>({
  options,
  selectedOptionId,
  disabled = false,
  ariaLabel,
  className,
  buttonClassName,
  firstOptionRef,
  onSelect
}: {
  options: readonly TOption[];
  selectedOptionId?: string;
  disabled?: boolean;
  ariaLabel: string;
  className?: string;
  buttonClassName?: string;
  firstOptionRef?: Ref<HTMLButtonElement>;
  onSelect(option: TOption): void;
}) {
  const selectedOption = selectedOptionId
    ? options.find((option) => option.optionId === selectedOptionId)
    : undefined;
  const visibleOptions = selectedOption ? [selectedOption] : options;

  return (
    <div
      className={cn('agent-decision-option-pills flex flex-wrap gap-2', className)}
      role="group"
      aria-label={ariaLabel}
    >
      {visibleOptions.map((option, index) => {
        const selected = selectedOption === option;
        return (
          <Button
            key={option.optionId || option.name}
            ref={index === 0 ? firstOptionRef : undefined}
            type="button"
            variant="outline"
            size="sm"
            className={cn(
              'agent-decision-option-pill min-h-8 max-w-full cursor-pointer whitespace-normal break-words rounded-full px-4 py-1 text-xs leading-normal hover:bg-accent disabled:cursor-default',
              buttonClassName,
              selected
                ? 'agent-decision-option-pill-selected border-border bg-muted text-muted-foreground'
                : 'disabled:opacity-70'
            )}
            data-option-id={option.optionId}
            data-option-kind={option.kind}
            aria-pressed={selected}
            disabled={disabled}
            onClick={() => {
              if (!disabled) onSelect(option);
            }}
          >
            {option.name}
          </Button>
        );
      })}
    </div>
  );
}
