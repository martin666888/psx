// TimelineView.tsx — the Station 3 React Timeline tree (core items).
//
// Renders the TimelineViewModel projection as the single owner of the thread
// subtree. CP4 area 1 swaps the Reasoning (thinking) and Tool views onto the
// AI Elements components (Reasoning / Tool / Task on Radix Collapsible) while
// keeping the projection-driven semantics: `open` changes only at lifecycle
// moments, user toggles in between are preserved, and the semantic anchor
// classes + data attributes (agent-thinking-block, agent-tool-card,
// data-tool-id, data-state, data-run-id) stay for replay tooling and tests.
// Decision cards (permission/question/elicitation/mode-transition) live in
// TimelineDecisions.tsx and render inside the same tree.

import { useLayoutEffect, useRef, useState, type JSX, type ReactNode } from 'react';
import {
  TOOL_STATE_LABELS,
  type DecisionItem,
  type InlineToolItem,
  type MessageItem,
  type RecoveryItem,
  type SystemItem,
  type ThinkingItem,
  type TimelineRow,
  type ToolGroupItem
} from './timelineViewModel.js';
import { DecisionCard, type DecisionCallbacks } from './TimelineDecisions.js';
import { PsxButton, PsxCard } from '../ui/Psx.js';
import {
  Reasoning,
  ReasoningContent,
  ReasoningTrigger
} from '../components/ai-elements/reasoning.js';
import {
  Tool,
  ToolContent,
  ToolHeader,
  type ToolState
} from '../components/ai-elements/tool.js';
import { Task, TaskContent, TaskTrigger } from '../components/ai-elements/task.js';
import { Shimmer } from '../components/ai-elements/shimmer.js';
import {
  Message,
  MessageAction,
  MessageActions,
  MessageContent,
  PsxMessageResponse
} from '../components/ai-elements/message.js';
import { Badge } from '../components/ui/badge.js';
import { CheckIcon, ChevronDownIcon, CopyIcon, WrenchIcon } from 'lucide-react';

export interface TimelineCallbacks extends DecisionCallbacks {
  /** Clipboard write with the legacy execCommand fallback; resolves ok. */
  copyText(text: string): Promise<boolean>;
  /** Recovery card action (legacy 'Open terminal' button). */
  onOpenTerminal(): void;
  /** Build a message-attachment tile (composer-owned widget, legacy seam). */
  createAttachmentTile(attachment: MessageItem['attachments'][number]): HTMLElement;
}

export interface TimelineViewProps {
  rows: TimelineRow[];
  assistantName: string;
  callbacks: TimelineCallbacks;
  /**
   * Fires after each commit is flushed to the DOM (useLayoutEffect), so the
   * controller can auto-scroll against the fresh layout — root.render() alone
   * gives no commit guarantee in React 19.
   */
  onCommitted?: () => void;
}

/** React twin of createCopyButton + showCopyFeedback, restyled onto the AI
 * Elements MessageAction (ghost icon button + tooltip). */
export function CopyButton(props: { getText(): string; copyText(text: string): Promise<boolean> }): JSX.Element {
  const [feedback, setFeedback] = useState<'idle' | 'success' | 'fail'>('idle');
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useLayoutEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
    },
    []
  );
  const label = feedback === 'success' ? '已复制' : feedback === 'fail' ? '复制失败' : '复制';
  return (
    <MessageAction
      tooltip={label}
      label={label}
      aria-label={label}
      className={
        'agent-copy-button size-7 text-muted-foreground' +
        (feedback === 'success'
          ? ' agent-copy-success text-emerald-600'
          : feedback === 'fail'
            ? ' agent-copy-fail text-destructive'
            : '')
      }
      onClick={() => {
        void props.copyText(props.getText()).then((ok) => {
          if (timer.current) clearTimeout(timer.current);
          setFeedback(ok ? 'success' : 'fail');
          timer.current = setTimeout(() => {
            timer.current = null;
            setFeedback('idle');
          }, 1500);
        });
      }}
    >
      {feedback === 'success' ? <CheckIcon className="size-3.5" /> : <CopyIcon className="size-3.5" />}
    </MessageAction>
  );
}

/** Uncontrolled <details> whose open state follows the projection lifecycle.
 * Still used by TimelineDecisions (CP4 area 5 swaps those cards, then this
 * hook goes away). */
export function useDetailsOpen(open: boolean) {
  const ref = useRef<HTMLDetailsElement | null>(null);
  const applied = useRef<boolean | null>(null);
  useLayoutEffect(() => {
    if (!ref.current) return;
    if (applied.current === null || applied.current !== open) {
      applied.current = open;
      ref.current.open = open;
    }
  });
  return ref;
}

/** Controlled Collapsible open state that follows the projection lifecycle:
 * a projection `open` change overrides the view, user toggles in between are
 * preserved (the exact contract of the old uncontrolled-<details> sync). */
export function useProjectionOpen(
  projectionOpen: boolean
): [boolean, (open: boolean) => void] {
  const [open, setOpen] = useState(projectionOpen);
  const applied = useRef<boolean | null>(null);
  if (applied.current !== projectionOpen) {
    applied.current = projectionOpen;
    if (open !== projectionOpen) setOpen(projectionOpen);
  }
  return [open, setOpen];
}

function SystemRow({ item }: { item: SystemItem }): JSX.Element {
  return <div className="agent-system mb-4 text-muted-foreground text-xs">{item.text}</div>;
}

function RecoveryCard({ item, callbacks }: { item: RecoveryItem; callbacks: TimelineCallbacks }): JSX.Element {
  return (
    <PsxCard className="agent-recovery" role="status" aria-live="polite" aria-atomic="true">
      <div className="agent-recovery-content">
        <div className="agent-recovery-title">Session could not be resumed</div>
        <p className="agent-recovery-message">{item.message}</p>
        <details className="agent-recovery-details" hidden={!item.detail}>
          <summary>Technical details</summary>
          <pre className="agent-recovery-technical">{item.detail}</pre>
        </details>
      </div>
      <div className="agent-recovery-actions">
        <PsxButton variant="subtle" onClick={() => callbacks.onOpenTerminal()}>
          Open terminal
        </PsxButton>
      </div>
    </PsxCard>
  );
}

function ThinkingRowView({ item }: { item: ThinkingItem }): JSX.Element {
  if (item.variant === 'row') {
    return (
      <div
        className="agent-thinking mb-4 flex items-center gap-2 text-muted-foreground text-sm"
        role="status"
        aria-live="polite"
        aria-busy="true"
      >
        <Shimmer as="span" duration={1}>
          Thinking...
        </Shimmer>
      </div>
    );
  }
  return <ThinkingBlock item={item} />;
}

function ThinkingBlock({ item }: { item: ThinkingItem }): JSX.Element {
  const [open, setOpen] = useProjectionOpen(item.running);
  return (
    <Reasoning
      className={'agent-thinking-block' + (item.running ? ' agent-thinking-running' : '')}
      isStreaming={item.running}
      open={open}
      onOpenChange={setOpen}
      // defaultOpen=false disables the upstream auto-close timer: the
      // projection already collapses the block at thinking_finished, and the
      // timer would otherwise re-collapse a user-expanded historical block.
      defaultOpen={false}
      aria-busy={item.running ? 'true' : 'false'}
    >
      <ReasoningTrigger />
      {/* forceMount keeps the collapsed transcript in the DOM (old <details>
          semantics) for text search and replay tooling. */}
      <ReasoningContent forceMount className="data-[state=closed]:hidden">
        <pre className="agent-thinking-content max-h-64 overflow-auto whitespace-pre-wrap rounded-md bg-muted/50 p-3 font-mono text-xs">
          {item.text}
        </pre>
      </ReasoningContent>
    </Reasoning>
  );
}

/** ACP tool states → AI Elements badge states (icon only; the label keeps
 * PSX's TOOL_STATE_LABELS wording via the badge override). */
const TOOL_UI_STATE: Readonly<Record<string, ToolState>> = {
  running: 'input-available',
  done: 'output-available',
  error: 'output-error',
  cancelled: 'output-denied',
  fallback: 'approval-requested'
};

function ToolCardView({
  card
}: {
  card: ToolGroupItem['cards'][number];
}): JSX.Element {
  const [open, setOpen] = useProjectionOpen(card.open);
  const label = TOOL_STATE_LABELS[card.state] || TOOL_STATE_LABELS.done;
  return (
    <Tool
      className={'agent-tool-card agent-tool-card-' + card.state + ' border-x-0 border-b-0 mb-0 rounded-none'}
      data-tool-id={card.toolCallId}
      data-state={card.state}
      open={open}
      onOpenChange={setOpen}
    >
      <ToolHeader
        title={card.summary}
        type={card.summary}
        state={TOOL_UI_STATE[card.state] ?? 'output-available'}
        badge={label}
        titleClassName="agent-tool-card-summary text-left"
        className="agent-tool-card-header group px-0"
      />
      {/* forceMount keeps closed outputs in the DOM (the old <details> body
          was always present) for replay tooling and text search. */}
      <ToolContent forceMount className="data-[state=closed]:hidden">
        <div className="agent-tool-card-body pb-3 pl-6">
          <pre className="agent-tool-card-content max-h-72 overflow-auto whitespace-pre-wrap break-words rounded-md bg-muted/50 p-3 font-mono text-xs">
            {card.output}
          </pre>
        </div>
      </ToolContent>
    </Tool>
  );
}

// Items are mutated in place by the projection, so components stay unmemoized:
// keyed reconciliation already preserves DOM node identity across renders.
function ToolGroup({ item }: { item: ToolGroupItem }): JSX.Element {
  const [open, setOpen] = useProjectionOpen(item.open);
  const total = item.cards.length;
  const running = item.cards.filter((card) => card.state === 'running').length;
  const label =
    total === 0
      ? 'Tool activity'
      : 'Tool activity \u00b7 ' + total + ' call' + (total > 1 ? 's' : '') + (item.live && running > 0 ? ' \u00b7 ' + running + ' running' : '');
  return (
    <Task
      className={'agent-run-group not-prose mb-4 w-full' + (item.error ? ' agent-run-group-error' : '')}
      data-run-id={item.runId}
      open={open}
      onOpenChange={setOpen}
      style={item.visible ? undefined : { display: 'none' }}
    >
      <TaskTrigger title={label}>
        <div className="agent-run-group-header flex w-full cursor-pointer items-center gap-2 text-muted-foreground text-sm transition-colors hover:text-foreground">
          <WrenchIcon className="size-4" />
          <span className="agent-run-group-summary font-medium">{label}</span>
          {item.error ? <span className="font-medium text-destructive">{'\u00b7 Failed'}</span> : null}
          <ChevronDownIcon className="size-4 transition-transform group-data-[state=open]:rotate-180" />
        </div>
      </TaskTrigger>
      <TaskContent forceMount className="data-[state=closed]:hidden">
        <div className="agent-run-group-body flex flex-col">
          {item.cards.map((card) => (
            <ToolCardView key={card.toolCallId} card={card} />
          ))}
        </div>
      </TaskContent>
    </Task>
  );
}

function InlineTool({ item }: { item: InlineToolItem }): JSX.Element {
  const statusLabel = item.state === 'error' ? 'Failed' : item.state === 'fallback' ? 'Needs terminal' : 'Done';
  return (
    <section
      className={
        'agent-tool agent-tool-' + item.state + ' not-prose mb-4 w-full rounded-md border' +
        (item.state === 'error' ? ' border-destructive' : item.state === 'fallback' ? ' border-yellow-600/50' : '')
      }
      data-state={item.state}
    >
      <div className="agent-tool-header flex items-center justify-between gap-4 p-3">
        <div className="flex items-center gap-2">
          <WrenchIcon className="size-4 text-muted-foreground" />
          <span className="font-medium text-sm">{item.name}</span>
        </div>
        <Badge className="gap-1.5 rounded-full text-xs" variant="secondary">
          {statusLabel}
        </Badge>
      </div>
      <pre className="max-h-72 overflow-auto whitespace-pre-wrap break-words px-3 pb-3 font-mono text-xs">{item.text}</pre>
    </section>
  );
}

/** legacy _appendMessage image-JSON cleanup for user messages. */
function cleanUserText(text: string): string {
  return (text || '').replace(/\{"type":"image"[^}]*\}/g, '\n\n[image attachment]\n\n');
}

/** Attachment grid host: tiles are composer-built DOM (legacy seam). */
function AttachmentGrid({
  attachments,
  callbacks
}: {
  attachments: MessageItem['attachments'];
  callbacks: TimelineCallbacks;
}): JSX.Element {
  const host = useRef<HTMLDivElement | null>(null);
  const built = useRef(false);
  useLayoutEffect(() => {
    if (built.current || !host.current) return;
    built.current = true;
    for (const attachment of attachments) {
      host.current.appendChild(callbacks.createAttachmentTile(attachment));
    }
  }, [attachments, callbacks]);
  return <div className="agent-message-attachments" ref={host}></div>;
}

function UserMessageBody({
  item,
  callbacks
}: {
  item: MessageItem;
  callbacks: TimelineCallbacks;
}): JSX.Element {
  const body = useRef<HTMLDivElement | null>(null);
  const content = useRef<HTMLDivElement | null>(null);
  const [collapsible, setCollapsible] = useState(false);
  const [collapsed, setCollapsed] = useState(true);

  useLayoutEffect(() => {
    const contentNode = content.current;
    if (!contentNode || !contentNode.textContent?.trim()) return;
    let firstFrame = 0;
    let secondFrame = 0;
    firstFrame = requestAnimationFrame(() => {
      firstFrame = 0;
      secondFrame = requestAnimationFrame(() => {
        secondFrame = 0;
        const style = window.getComputedStyle(contentNode);
        const fontSize = parseFloat(style.fontSize) || 15;
        const parsedLineHeight = parseFloat(style.lineHeight);
        const lineHeight = Number.isFinite(parsedLineHeight) ? parsedLineHeight : fontSize * 1.65;
        if (Math.ceil(contentNode.scrollHeight / lineHeight) < 17) return;
        contentNode.style.setProperty(
          '--agent-user-message-collapsed-height',
          lineHeight * 16 + 'px'
        );
        setCollapsible(true);
        setCollapsed(true);
      });
    });
    return () => {
      if (firstFrame) cancelAnimationFrame(firstFrame);
      if (secondFrame) cancelAnimationFrame(secondFrame);
    };
  }, [item.raw]);

  const toggle = collapsible
    ? (
        <button
          type="button"
          className="agent-message-collapse-toggle"
          aria-expanded={!collapsed}
          onClick={() => {
            const nextCollapsed = !collapsed;
            setCollapsed(nextCollapsed);
            if (!nextCollapsed) {
              requestAnimationFrame(() => {
                const node = body.current;
                if (!node || node.getBoundingClientRect().bottom <= window.innerHeight) return;
                const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
                node.scrollIntoView({
                  block: 'nearest',
                  behavior: reduceMotion ? 'auto' : 'smooth'
                });
              });
            }
          }}
        >
          {collapsed ? '展开' : '收起'}
        </button>
      )
    : null;

  return (
    <div
      ref={body}
      className={
        'agent-message-body w-full' +
        (collapsible ? ' agent-message-collapsible' : '') +
        (collapsible && collapsed ? ' agent-message-collapsed' : '')
      }
    >
      {item.attachments.length > 0 ? (
        <AttachmentGrid attachments={item.attachments} callbacks={callbacks} />
      ) : null}
      <MessageContent>
        <PsxMessageResponse
          ref={content}
          className="agent-message-content"
          markdown={cleanUserText(item.raw)}
        />
      </MessageContent>
      {toggle}
    </div>
  );
}

const MessageRow = function MessageRow({
  item,
  showLabel,
  assistantName,
  callbacks
}: {
  item: MessageItem;
  showLabel: boolean;
  assistantName: string;
  callbacks: TimelineCallbacks;
}): JSX.Element {
  const isUser = item.role === 'user';
  const ariaLabel = isUser ? 'You' : assistantName;
  const showActions = !isUser && item.finalized && !!item.raw.trim();
  return (
    <Message
      from={isUser ? 'user' : 'assistant'}
      className={
        'agent-message agent-message-' + item.role + (showLabel ? ' mb-5' : ' agent-message-continuation mb-5 -mt-2')
      }
      aria-label={ariaLabel}
      role="article"
    >
      {showLabel ? (
        <div className="agent-message-label font-semibold text-[13px] text-muted-foreground">{ariaLabel}</div>
      ) : null}
      {isUser ? (
        <UserMessageBody item={item} callbacks={callbacks} />
      ) : (
        <MessageContent className="agent-message-body w-full" data-raw={item.raw}>
          <PsxMessageResponse markdown={item.raw} />
        </MessageContent>
      )}
      {showActions ? (
        <MessageActions className="agent-message-actions">
          <CopyButton getText={() => item.raw} copyText={callbacks.copyText} />
        </MessageActions>
      ) : null}
    </Message>
  );
};

function renderItem(
  row: TimelineRow,
  rowsInTurn: TimelineRow[],
  assistantName: string,
  callbacks: TimelineCallbacks
): ReactNode {
  const item = row.item;
  switch (item.type) {
    case 'system':
      return 'variant' in item && item.variant === 'recovery' ? (
        <RecoveryCard key={item.id} item={item as RecoveryItem} callbacks={callbacks} />
      ) : (
        <SystemRow key={item.id} item={item as SystemItem} />
      );
    case 'thinking':
      return <ThinkingRowView key={item.id} item={item} />;
    case 'tool':
      return item.variant === 'run-group' ? (
        <ToolGroup key={item.id} item={item} />
      ) : (
        <InlineTool key={item.id} item={item} />
      );
    case 'message': {
      // Label logic mirrors legacy: the first message of each role in a turn
      // carries the label, later ones are continuations.
      const first = rowsInTurn.find(
        (candidate) => candidate.item.type === 'message' && (candidate.item as MessageItem).role === item.role
      );
      return (
        <MessageRow
          key={item.id}
          item={item}
          showLabel={first?.item === item}
          assistantName={assistantName}
          callbacks={callbacks}
        />
      );
    }
    case 'decision':
      return (
        <DecisionCard key={item.id} item={item as DecisionItem} assistantName={assistantName} callbacks={callbacks} />
      );
    default:
      return null;
  }
}

export function TimelineView({ rows, assistantName, callbacks, onCommitted }: TimelineViewProps): JSX.Element {
  // After every commit the fresh layout is observable; let the controller
  // run its pinned auto-scroll then (never against the pre-commit DOM).
  useLayoutEffect(() => {
    onCommitted?.();
  });
  // Group rows by turn id into agent-turn sections. Rows of one turn always
  // collect into a single block anchored at the turn's first row — mirroring
  // legacy, where the turn <section> node persists and later rows keep
  // appending into it even when a root-level row (the recovery card) was
  // inserted in between. This also keeps every block key unique.
  const blocks: Array<{ key: string; turn: boolean; rows: TimelineRow[] }> = [];
  const turnBlocks = new Map<string, { key: string; turn: boolean; rows: TimelineRow[] }>();
  for (const row of rows) {
    if (row.turnId) {
      let block = turnBlocks.get(row.turnId);
      if (!block) {
        block = { key: row.turnId, turn: true, rows: [] };
        turnBlocks.set(row.turnId, block);
        blocks.push(block);
      }
      block.rows.push(row);
    } else {
      blocks.push({ key: 'root-' + row.item.id, turn: false, rows: [row] });
    }
  }
  return (
    <>
      {blocks.map((block) =>
        block.turn ? (
          <section key={block.key} className="agent-turn">
            {block.rows.map((row) => renderItem(row, block.rows, assistantName, callbacks))}
          </section>
        ) : (
          block.rows.map((row) => renderItem(row, block.rows, assistantName, callbacks))
        )
      )}
    </>
  );
}
