// TimelineView.tsx — the Station 3 React Timeline tree (core items).
//
// Renders the TimelineViewModel projection as the single owner of the thread
// subtree. Every element mirrors a legacy DOM write in TimelineController so
// the produced DOM stays byte-identical to the legacy engine. Decision cards
// (permission/question/elicitation/mode-transition) live in
// TimelineDecisions.tsx and render inside the same tree.
//
// <details> open state: the projection changes `open` only at lifecycle
// moments (legacy setAttribute/removeAttribute); user toggles in between must
// not be overridden, so the components sync `open` through a ref effect
// instead of a controlled prop.

import { useLayoutEffect, useRef, useState, type JSX, type ReactNode } from 'react';
import { renderMarkdown } from '../core/markdown.js';
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

const COPY_ICON = (
  <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <rect x="9" y="9" width="13" height="13" rx="2" ry="2"></rect>
    <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"></path>
  </svg>
);
const CHECK_ICON = (
  <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <polyline points="20 6 9 17 4 12"></polyline>
  </svg>
);

/** React twin of createCopyButton + showCopyFeedback (icon swap + classes). */
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
    <button
      type="button"
      className={
        'agent-copy-button' +
        (feedback === 'success' ? ' agent-copy-success' : feedback === 'fail' ? ' agent-copy-fail' : '')
      }
      aria-label={label}
      title={label}
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
      {feedback === 'success' ? CHECK_ICON : COPY_ICON}
    </button>
  );
}

/** Uncontrolled <details> whose open state follows the projection lifecycle. */
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

function SystemRow({ item }: { item: SystemItem }): JSX.Element {
  return <div className="agent-system">{item.text}</div>;
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
      <div className="agent-thinking" role="status" aria-live="polite" aria-busy="true">
        <span className="agent-spinner"></span>
        <span>Thinking</span>
      </div>
    );
  }
  return <ThinkingBlock item={item} />;
}

function ThinkingBlock({ item }: { item: ThinkingItem }): JSX.Element {
  const ref = useDetailsOpen(item.running);
  return (
    <details
      ref={ref}
      className={'agent-thinking-block' + (item.running ? ' agent-thinking-running' : '')}
      aria-busy={item.running ? 'true' : 'false'}
    >
      <summary className="agent-thinking-header">
        <span className="agent-thinking-chevron"></span>
        {item.running ? <span className="agent-spinner"></span> : null}
        <span className="agent-thinking-title">Thinking</span>
      </summary>
      <pre className="agent-thinking-content">{item.text}</pre>
    </details>
  );
}

function ToolCardView({
  card
}: {
  card: ToolGroupItem['cards'][number];
}): JSX.Element {
  const ref = useDetailsOpen(card.open);
  const label = TOOL_STATE_LABELS[card.state] || TOOL_STATE_LABELS.done;
  return (
    <details ref={ref} className={'agent-tool-card agent-tool-card-' + card.state} data-tool-id={card.toolCallId} data-state={card.state}>
      <summary className="agent-tool-card-header">
        <span className="agent-tool-card-summary">{card.summary}</span>
        <span className="agent-tool-card-status" aria-label={'Tool status: ' + label}>
          {label}
        </span>
      </summary>
      <div className="agent-tool-card-body">
        <pre className="agent-tool-card-content">{card.output}</pre>
      </div>
    </details>
  );
}

// Items are mutated in place by the projection, so components stay unmemoized:
// keyed reconciliation already preserves DOM node identity across renders.
function ToolGroup({ item }: { item: ToolGroupItem }): JSX.Element {
  const ref = useDetailsOpen(item.open);
  const total = item.cards.length;
  const running = item.cards.filter((card) => card.state === 'running').length;
  const label =
    total === 0
      ? 'Tool activity'
      : 'Tool activity \u00b7 ' + total + ' call' + (total > 1 ? 's' : '') + (item.live && running > 0 ? ' \u00b7 ' + running + ' running' : '');
  return (
    <details
      ref={ref}
      className={'agent-run-group' + (item.error ? ' agent-run-group-error' : '')}
      data-run-id={item.runId}
      style={item.visible ? undefined : { display: 'none' }}
    >
      <summary className="agent-run-group-header">
        <span className="agent-run-group-chevron"></span>
        <span className="agent-run-group-summary">{label}</span>
      </summary>
      <div className="agent-run-group-body">
        {item.cards.map((card) => (
          <ToolCardView key={card.toolCallId} card={card} />
        ))}
      </div>
    </details>
  );
}

function InlineTool({ item }: { item: InlineToolItem }): JSX.Element {
  const statusLabel = item.state === 'error' ? 'Failed' : item.state === 'fallback' ? 'Needs terminal' : 'Done';
  return (
    <section className={'agent-tool agent-tool-' + item.state} data-state={item.state}>
      <div className="agent-tool-header">
        <span>{item.name}</span>
        <span className="agent-tool-card-status">{statusLabel}</span>
      </div>
      <pre>{item.text}</pre>
    </section>
  );
}

/** legacy _appendMessage image-JSON cleanup for user messages. */
function cleanUserText(text: string): string {
  return (text || '').replace(/\{"type":"image"[^\}]*\}/g, '\n\n[image attachment]\n\n');
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
    <article
      className={
        'agent-message agent-message-' + item.role + (showLabel ? '' : ' agent-message-continuation')
      }
      aria-label={ariaLabel}
    >
      {showLabel ? <div className="agent-message-label">{ariaLabel}</div> : null}
      {isUser ? (
        <div className="agent-message-body">
          {item.attachments.length > 0 ? (
            <AttachmentGrid attachments={item.attachments} callbacks={callbacks} />
          ) : null}
          <div
            className="agent-message-content"
            dangerouslySetInnerHTML={{ __html: renderMarkdown(cleanUserText(item.raw)) }}
          ></div>
        </div>
      ) : (
        <div
          className="agent-message-body"
          data-raw={item.raw}
          dangerouslySetInnerHTML={{ __html: renderMarkdown(item.raw) }}
        ></div>
      )}
      {showActions ? (
        <div className="agent-message-actions">
          <CopyButton getText={() => item.raw} copyText={callbacks.copyText} />
        </div>
      ) : null}
    </article>
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
