// ComposerView.tsx — the single React owner of the whole composer subtree.
// It owns the controlled PromptInput draft and IME lifecycle, local attachment
// list, command/menu interaction, mode/config controls, submit/cancel state,
// mode-transition prompt and image-preview dialog.
//
// ComposerController supplies state projections and protocol actions only.
// AttachmentBridge maps PromptInput-local IDs/Files to controller upload and
// server-ID state without creating a second visible attachment source. The
// context-usage host remains an empty React-owned shell that the ContextUsage
// island portals into within the same Agent React root.
//
// onReady fires after the first commit so the controller can replay any state
// projection that arrived while the island chunk was loading.

import { useEffect, useLayoutEffect, useRef, useState, type JSX, type RefObject } from 'react';
import {
  AttachmentStrip,
  CommandHint,
  ComposerActions,
  type AttachmentStripProps,
  type CommandHintProps,
  type ComposerActionsProps
} from './ComposerBits.js';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue
} from '../components/ui/select.js';
import {
  Command,
  CommandGroup,
  CommandItem,
  CommandList
} from '../components/ui/command.js';
import {
  Dialog,
  DialogContent,
  DialogTitle
} from '../components/ui/dialog.js';
import { Switch } from '../components/ui/switch.js';
import {
  PromptInput,
  PromptInputSubmit,
  PromptInputTextarea,
  type PromptInputMessage,
  usePromptInputAttachments
} from '../components/ai-elements/prompt-input.js';
import {
  AttachmentBridge,
  type AttachmentBridgeProps
} from './AttachmentBridge.js';
import {
  ModeTransitionPrompt,
  type ComposerModeTransitionPromptVM
} from './ModeTransitionPrompt.js';

export interface ComposerSelectItemVM {
  value: string;
  label: string;
  title?: string;
}

export interface ComposerSelectVM {
  id: string;
  label: string;
  title: string;
  value: string;
  items: ComposerSelectItemVM[];
  disabled: boolean;
  hidden?: boolean;
  role?: string;
  onValueChange(value: string): void;
}

export interface ComposerSwitchVM {
  kind: 'switch';
  id: string;
  label: string;
  title: string;
  checked: boolean;
  disabled: boolean;
  onCheckedChange(checked: boolean): void;
}

export interface ComposerSelectControlVM extends ComposerSelectVM {
  kind: 'select';
}

export interface ComposerControlsProps {
  mode: ComposerSelectVM;
  configs: Array<ComposerSelectControlVM | ComposerSwitchVM>;
}

export interface ComposerCommandItemVM {
  id: string;
  source: string;
  name: string;
  label: string;
}

export interface ComposerCommandMenuProps {
  id: string;
  open: boolean;
  activeId: string;
  items: ComposerCommandItemVM[];
  onHighlight(id: string): void;
  onSelect(id: string): void;
  onDismiss(): void;
}

export interface ComposerPreviewProps {
  requestToken: number;
  closeToken: number;
  src: string;
}

export interface ComposerDraftProps {
  initialDraft: string;
  draftToken: number;
  focusToken: number;
  disabled: boolean;
  busy: boolean;
  placeholder: string;
  submitDisabled: boolean;
  submitLabel: string;
  submitTitle: string;
  onDraftChange(text: string): void;
  onSubmit(message: PromptInputMessage): Promise<void>;
  onCancel(): void;
}

export interface ComposerViewProps {
  attachmentBridge: AttachmentBridgeProps;
  attachments: AttachmentStripProps;
  attachmentsVisible: boolean;
  actions: ComposerActionsProps;
  commands: ComposerCommandMenuProps;
  controls: ComposerControlsProps;
  draft: ComposerDraftProps;
  hint: CommandHintProps;
  modeTransitionPrompt: ComposerModeTransitionPromptVM | null;
  preview: ComposerPreviewProps;
  /** First-commit signal used to replay the latest controller projection. */
  onReady(): void;
}

function ComposerSelectControl({ control }: { control: ComposerSelectVM }): JSX.Element {
  const selected =
    control.items.find((item) => item.value === control.value) ??
    control.items[0];
  const currentLabel = selected?.label || control.value || 'default';
  return (
    <label
      className={
        (control.role === 'mode' ? 'agent-mode-control' : 'agent-config-control') +
        ' flex min-w-0 items-center gap-[var(--agent-space-1)] whitespace-nowrap text-xs text-muted-foreground'
      }
      title={control.title}
      hidden={control.hidden}
    >
      <span>{control.label}</span>
      <Select
        value={selected?.value}
        disabled={control.disabled || control.items.length === 0}
        onValueChange={control.onValueChange}
      >
        <SelectTrigger
          data-role={control.role}
          data-config-id={control.role === 'mode' ? undefined : control.id}
          size="sm"
          className="agent-menu-select h-7 min-w-0 gap-1 border-0 bg-transparent px-1.5 text-xs shadow-none"
          aria-label={control.label + ': ' + currentLabel}
        >
          <SelectValue placeholder={currentLabel} />
        </SelectTrigger>
        <SelectContent side="top" align="start" className="agent-menu-select-popup">
          {control.items.map((item) => (
            <SelectItem
              key={item.value}
              value={item.value}
              title={item.title}
              className="agent-menu-select-option text-xs"
            >
              {item.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </label>
  );
}

function ComposerConfigControls({ controls }: { controls: ComposerControlsProps }): JSX.Element {
  return (
    <>
      <ComposerSelectControl control={controls.mode} />
      <div
        data-role="config-options"
        className="agent-config-options flex min-w-0 flex-[0_1_auto] flex-nowrap items-center justify-start gap-[var(--agent-space-2)] max-[760px]:flex-wrap"
      >
        {controls.configs.map((control) =>
          control.kind === 'select' ? (
            <ComposerSelectControl key={control.id} control={control} />
          ) : (
            <label
              key={control.id}
              className="agent-config-control flex min-w-0 items-center gap-[var(--agent-space-1)] whitespace-nowrap text-xs text-muted-foreground"
              title={control.title}
            >
              <span>{control.label}</span>
              <Switch
                className="agent-config-switch"
                data-config-id={control.id}
                checked={control.checked}
                aria-label={control.label}
                disabled={control.disabled}
                onCheckedChange={control.onCheckedChange}
              />
            </label>
          )
        )}
      </div>
    </>
  );
}

function ComposerCommandMenu({ menu }: { menu: ComposerCommandMenuProps }): JSX.Element {
  const menuRef = useRef<HTMLDivElement | null>(null);
  const groups = Array.from(new Set(menu.items.map((item) => item.source)));

  useLayoutEffect(() => {
    if (!menu.open || !menu.activeId) return;
    const activeItem = Array.from(
      menuRef.current?.querySelectorAll<HTMLElement>('[data-slot="command-item"]') ?? []
    ).find((item) => item.id === menu.activeId);
    activeItem?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }, [menu.activeId, menu.open]);

  return (
    <div
      ref={menuRef}
      id={menu.id}
      data-role="command-menu"
      className="absolute bottom-[calc(100%+var(--agent-space-2))] left-0 z-[var(--agent-layer-menu)] w-full max-w-full overflow-hidden rounded-[var(--agent-radius-card)] border bg-popover shadow-[var(--agent-shadow-popover)]"
      aria-label="Available commands"
      hidden={!menu.open}
    >
      <Command
        shouldFilter={false}
        value={menu.activeId}
        onValueChange={menu.onHighlight}
        className="h-auto"
      >
        <CommandList
          className="agent-command-menu-scroll max-h-[260px]"
          aria-label="Available commands"
        >
          {groups.map((source) => (
            <CommandGroup key={source} heading={source}>
              {menu.items
                .filter((item) => item.source === source)
                .map((item) => (
                  <CommandItem
                    key={item.id}
                    id={item.id}
                    ref={(node) => {
                      if (node) node.id = item.id;
                    }}
                    value={item.id}
                    data-command-name={item.name}
                    className="justify-between gap-4 px-3 py-2"
                    onMouseDown={(event) => event.preventDefault()}
                    onSelect={menu.onSelect}
                  >
                    <span className="font-mono text-[13px]">{item.name}</span>
                    <small className="text-xs text-muted-foreground">{item.label}</small>
                  </CommandItem>
                ))}
            </CommandGroup>
          ))}
        </CommandList>
      </Command>
    </div>
  );
}

function ComposerTextarea(props: {
  attachmentsDisabled: boolean;
  commands: ComposerCommandMenuProps;
  draft: string;
  draftProps: ComposerDraftProps;
  inputRef: RefObject<HTMLTextAreaElement | null>;
  onCompositionEnd(): void;
  onCompositionStart(): void;
  onDraftChange(text: string): void;
}): JSX.Element {
  const attachments = usePromptInputAttachments();
  return (
    <PromptInputTextarea
      data-role="input"
      ref={props.inputRef}
      value={props.draft}
      disabled={props.draftProps.disabled}
      spellCheck={false}
      className="agent-native-scroll min-h-[var(--agent-composer-input-min-height)] max-h-[180px] w-full resize-none overflow-y-auto border-0 bg-transparent p-0 font-mono text-sm leading-[1.45] text-foreground shadow-none placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-0"
      aria-autocomplete="list"
      aria-haspopup="listbox"
      aria-expanded={props.commands.open}
      aria-controls={props.commands.id}
      aria-activedescendant={props.commands.open ? props.commands.activeId : undefined}
      aria-label="Message Agent"
      placeholder={props.draftProps.placeholder}
      onBlur={() => setTimeout(props.commands.onDismiss, 120)}
      onChange={() => {}}
      onInput={(event) => props.onDraftChange(event.currentTarget.value)}
      onCompositionEnd={props.onCompositionEnd}
      onCompositionStart={props.onCompositionStart}
      onKeyDown={(event) => {
        if (props.commands.open) {
          if (event.key === 'Escape') {
            event.preventDefault();
            props.commands.onDismiss();
            return;
          }
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            const currentIndex = Math.max(
              0,
              props.commands.items.findIndex((item) => item.id === props.commands.activeId)
            );
            const delta = event.key === 'ArrowDown' ? 1 : -1;
            const nextIndex = Math.min(
              props.commands.items.length - 1,
              Math.max(0, currentIndex + delta)
            );
            const next = props.commands.items[nextIndex];
            if (next) props.commands.onHighlight(next.id);
            return;
          }
          if (event.key === 'Enter') {
            event.preventDefault();
            if (props.commands.activeId) props.commands.onSelect(props.commands.activeId);
            return;
          }
        }
        const isHistory = /^\/history(?:\s|$)/i.test(props.draft.trim());
        if (
          event.key === 'Enter' &&
          !event.shiftKey &&
          !event.nativeEvent.isComposing &&
          props.draftProps.busy &&
          !isHistory
        ) {
          event.preventDefault();
          props.draftProps.onCancel();
        }
      }}
      onPaste={(event) => {
        if (props.attachmentsDisabled) return;
        const files = Array.from(event.clipboardData?.items || [])
          .filter((item) => item.kind === 'file')
          .map((item) => item.getAsFile())
          .filter((file): file is File => !!file);
        if (files.length === 0) return;
        event.preventDefault();
        attachments.add(files);
      }}
    />
  );
}

function ComposerImagePreview({ preview }: { preview: ComposerPreviewProps }): JSX.Element {
  const [open, setOpen] = useState(false);
  const returnFocusRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (preview.requestToken > 0 && preview.src) {
      returnFocusRef.current =
        document.activeElement instanceof HTMLElement ? document.activeElement : null;
      setOpen(true);
    }
  }, [preview.requestToken, preview.src]);

  useEffect(() => {
    if (preview.closeToken > 0) setOpen(false);
  }, [preview.closeToken]);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogContent
        data-role="image-preview"
        className="max-w-[min(92vw,1200px)] border-0 bg-transparent p-0 shadow-none"
        onCloseAutoFocus={(event) => {
          event.preventDefault();
          const returnTarget = returnFocusRef.current;
          setTimeout(() => returnTarget?.focus(), 0);
        }}
      >
        <DialogTitle className="sr-only">Image preview</DialogTitle>
        <img
          data-role="image-preview-img"
          src={preview.src || undefined}
          alt="Image preview"
          className="max-h-[88vh] w-full rounded-[var(--agent-radius-card)] object-contain shadow-[var(--agent-shadow-dialog)]"
        />
      </DialogContent>
    </Dialog>
  );
}

export function ComposerView(props: ComposerViewProps): JSX.Element {
  const [draft, setDraft] = useState(props.draft.initialDraft);
  const [isComposing, setIsComposing] = useState(false);
  const appliedDraftTokenRef = useRef(props.draft.draftToken);
  const pendingDraftProjectionRef = useRef<{ token: number; text: string } | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);

  useLayoutEffect(() => {
    props.onReady();
    // Mount-only signal: the latest onReady closure is irrelevant because the
    // controller's ready handler is stable and re-runnable.
  }, []);

  useEffect(() => {
    if (props.draft.draftToken <= appliedDraftTokenRef.current) return;
    appliedDraftTokenRef.current = props.draft.draftToken;
    if (isComposing) {
      pendingDraftProjectionRef.current = {
        token: props.draft.draftToken,
        text: props.draft.initialDraft
      };
      return;
    }
    pendingDraftProjectionRef.current = null;
    setDraft(props.draft.initialDraft);
  }, [isComposing, props.draft.draftToken, props.draft.initialDraft]);

  useEffect(() => {
    if (props.draft.focusToken > 0) inputRef.current?.focus();
  }, [props.draft.focusToken]);

  const handleDraftChange = (text: string) => {
    setDraft(text);
    props.draft.onDraftChange(text);
  };

  const handleCompositionEnd = () => {
    setIsComposing(false);
    const pending = pendingDraftProjectionRef.current;
    if (!pending) return;
    pendingDraftProjectionRef.current = null;
    setDraft(pending.text);
  };

  return (
    <>
      <div className="agent-composer-main">
        <PromptInput
          accept="image/*"
          convertBlobUrls={false}
          multiple
          maxFiles={5}
          maxFileSize={20 * 1024 * 1024}
          className="w-full [&>[data-slot=input-group]]:contents"
          onError={(error) => props.attachmentBridge.onError(error.message)}
          onSubmit={async (message) => {
            setDraft('');
            try {
              await props.draft.onSubmit(message);
              props.draft.onDraftChange('');
            } catch (error) {
              setDraft(message.text);
              throw error;
            }
          }}
        >
          <AttachmentBridge {...props.attachmentBridge} />
          <div className="agent-composer-card">
            <div
              data-role="attachments-strip"
              className="agent-attachments-strip"
              hidden={!props.attachmentsVisible}
            >
              <AttachmentStrip {...props.attachments} />
            </div>
            <section
              data-role="mode-transition-prompt"
              className="mx-[var(--agent-space-3)] mt-[var(--agent-space-3)]"
              aria-label="Agent decision"
              hidden={!props.modeTransitionPrompt}
            >
              {props.modeTransitionPrompt ? (
                <ModeTransitionPrompt
                  key={props.modeTransitionPrompt.requestId}
                  prompt={props.modeTransitionPrompt}
                />
              ) : null}
            </section>
            <div
              data-role="input-row"
              className="agent-input-row min-w-0 px-[18px] pt-[14px] pb-2"
              hidden={!!props.modeTransitionPrompt}
            >
              <ComposerTextarea
                attachmentsDisabled={!props.actions.canPick}
                commands={props.commands}
                draft={draft}
                draftProps={props.draft}
                inputRef={inputRef}
                onCompositionEnd={handleCompositionEnd}
                onCompositionStart={() => setIsComposing(true)}
                onDraftChange={handleDraftChange}
              />
            </div>
            <div
              data-role="command-hint"
              className="agent-command-hint whitespace-pre-line px-[var(--agent-space-3)] pb-[var(--agent-space-2)] text-xs leading-[1.45] text-muted-foreground"
              role="status"
              aria-live="polite"
              hidden={!props.hint.visible}
            >
              <CommandHint {...props.hint} />
            </div>
            <div className="agent-composer-footer box-border flex min-h-[51px] min-w-0 items-center gap-[var(--agent-space-2)] rounded-b-[var(--agent-radius-composer)] pt-1.5 pr-[7px] pb-[7px] pl-[14px] max-[760px]:flex-wrap">
              <div className="agent-composer-actions flex shrink-0 items-center">
                <ComposerActions {...props.actions} />
              </div>
              <div
                data-role="context-usage-host"
                className="agent-hints flex shrink-0 gap-[var(--agent-space-2)] whitespace-nowrap text-[11px] text-muted-foreground"
              />
              <div className="agent-composer-controls flex min-w-0 flex-[1_1_auto] items-center justify-end gap-[var(--agent-space-2)] max-[760px]:order-5 max-[760px]:flex-[1_0_100%] max-[760px]:justify-start">
                <ComposerConfigControls controls={props.controls} />
              </div>
              <PromptInputSubmit
                data-role="send"
                data-status={props.draft.busy ? 'streaming' : 'ready'}
                className="size-[34px] shrink-0 rounded-full"
                status={props.draft.busy ? 'streaming' : 'ready'}
                disabled={props.draft.submitDisabled}
                title={props.draft.submitTitle}
                aria-label={props.draft.submitLabel}
                onClick={(event) => {
                  const isHistory = /^\/history(?:\s|$)/i.test(draft.trim());
                  if (!props.draft.busy || isHistory) return;
                  event.preventDefault();
                  props.draft.onCancel();
                }}
              />
            </div>
          </div>
        </PromptInput>
        <ComposerCommandMenu menu={props.commands} />
      </div>
      <ComposerImagePreview preview={props.preview} />
    </>
  );
}
