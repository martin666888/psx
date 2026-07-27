// HistoryList.tsx — React twin of HistoryDockController.renderContent.
//
// Renders the content of data-role="history-content": the empty / loading /
// unavailable / error states, the thread-open-error notice, and the grouped
// thread list with fold headers, preview limits, open markers and per-row
// accessible names. CP4 area 4 swaps the visuals onto shadcn (Badge, Button,
// lucide folder icons, Tailwind rows) while keeping the legacy branch order
// and every semantic anchor (agent-history-* classes, data-thread-id,
// data-folded, aria-current/expanded). All interaction state stays with the
// controller and arrives as props.

import type { JSX } from 'react';
import type { AgentHistoryState } from '../contracts/agent-history.js';
import {
  buildHistoryGroups,
  filterHistoryGroups,
  formatHistoryTime
} from './historyModel.js';
import { Badge } from '../components/ui/badge.js';
import { Button } from '../components/ui/button.js';
import { FolderIcon, FolderOpenIcon } from 'lucide-react';

interface HistoryThreadView {
  threadId: string;
  title: string;
  updatedAt: string;
  providerDisplay: string;
}

interface HistoryGroupView {
  key: string;
  name: string;
  path: string;
  threads: HistoryThreadView[];
}

export interface HistoryListProps {
  hasWorkspaces: boolean;
  state: AgentHistoryState;
  query: string;
  providerFilter: string;
  activeThreadId: string;
  openedElsewhere: ReadonlySet<string>;
  foldedGroups: ReadonlySet<string>;
  expandedGroups: ReadonlySet<string>;
  onToggleFold(key: string): void;
  onExpandGroup(key: string): void;
  onOpenThread(threadId: string): void;
  onDismissOpenError(): void;
  onRetryRefresh(): void;
}

const PREVIEW_LIMIT = 5;

function StateBox({ name, text }: { name: string; text: string }): JSX.Element {
  const live = name === 'loading' ? { role: 'status', 'aria-live': 'polite' as const } : {};
  return (
    <div className={'agent-history-state agent-history-' + name + ' text-[13px] text-muted-foreground leading-normal'} {...live}>
      {text}
    </div>
  );
}

function ThreadRow(
  { thread, activeThreadId, openedElsewhere, onOpenThread }: {
    thread: HistoryThreadView;
    activeThreadId: string;
    openedElsewhere: ReadonlySet<string>;
    onOpenThread(threadId: string): void;
  }
): JSX.Element {
  const titleText = thread.title || 'Agent Chat';
  const isActive = thread.threadId === activeThreadId;
  const isOpenElsewhere = !isActive && !!thread.threadId && openedElsewhere.has(thread.threadId);
  const badgeText = isActive ? 'Current' : isOpenElsewhere ? 'Open' : '';

  const parts: string[] = [];
  if (thread.providerDisplay) parts.push(thread.providerDisplay);
  const timeLabel = formatHistoryTime(thread.updatedAt);
  if (timeLabel) parts.push(timeLabel);
  const fullDescription = parts.join(' | ');

  return (
    <button
      type="button"
      className={
        'agent-history-item grid w-full gap-0.5 rounded-md p-2 text-left transition-colors hover:bg-accent' +
        (isActive ? ' bg-muted' : '')
      }
      data-thread-id={thread.threadId || ''}
      aria-current={isActive ? 'true' : 'false'}
      title={fullDescription || undefined}
      aria-label={[titleText, badgeText, fullDescription].filter((part) => part).join(', ')}
      onClick={() => {
        if (thread.threadId) onOpenThread(thread.threadId);
      }}
    >
      <span className="agent-history-heading flex min-w-0 items-baseline gap-2">
        <strong className="min-w-0 flex-1 truncate font-medium text-[13px]">{titleText}</strong>
        {isActive && (
          <Badge className="agent-history-current shrink-0 rounded-full text-[10px]" variant="secondary">
            Current
          </Badge>
        )}
        {isOpenElsewhere && (
          <Badge className="agent-history-open shrink-0 rounded-full text-[10px]" variant="outline">
            Open
          </Badge>
        )}
        <small className="shrink-0 truncate text-muted-foreground text-xs">
          {timeLabel || thread.providerDisplay || ''}
        </small>
      </span>
    </button>
  );
}

function HistoryGroup(
  { group, isSearching, props }: { group: HistoryGroupView; isSearching: boolean; props: HistoryListProps }
): JSX.Element {
  const allThreads = group.threads;
  const showAll = isSearching || props.expandedGroups.has(group.key);
  const visibleThreads = showAll ? allThreads : allThreads.slice(0, PREVIEW_LIMIT);
  const hiddenCount = allThreads.length - visibleThreads.length;
  const folded = !isSearching && props.foldedGroups.has(group.key);
  const hasActive = allThreads.some((thread) => thread.threadId === props.activeThreadId);

  return (
    <div className="agent-history-group mt-4 first:mt-0" data-folded={String(folded)}>
      <button
        type="button"
        className="agent-history-group-header flex w-full items-center gap-2 rounded-md px-2 pt-2 pb-1 text-left transition-colors hover:bg-accent"
        aria-expanded={!folded}
        onClick={() => props.onToggleFold(group.key)}
      >
        <span className="agent-history-group-folder inline-flex shrink-0 text-muted-foreground" aria-hidden="true">
          {folded ? <FolderIcon className="size-3.5" /> : <FolderOpenIcon className="size-3.5" />}
        </span>
        <span className="agent-history-group-label grid min-w-0 flex-1 gap-px">
          <strong className="truncate font-semibold text-xs">{group.name}</strong>
          {group.path && (
            <small className="truncate text-[11px] text-muted-foreground" title={group.path}>
              {group.path}
            </small>
          )}
        </span>
        <span className="agent-history-group-count shrink-0 text-[11px] text-muted-foreground">
          {String(allThreads.length)}
        </span>
        {hasActive && (
          <span
            className="agent-history-group-active size-[7px] shrink-0 rounded-full bg-primary"
            aria-label="Contains the active session"
          />
        )}
      </button>
      <div className="agent-history-group-threads pl-5" hidden={folded}>
        {visibleThreads.map((thread, index) => (
          <ThreadRow
            key={thread.threadId || 'row-' + index}
            thread={thread}
            activeThreadId={props.activeThreadId}
            openedElsewhere={props.openedElsewhere}
            onOpenThread={props.onOpenThread}
          />
        ))}
        {hiddenCount > 0 && (
          <Button
            variant="ghost"
            size="sm"
            className="agent-history-show-more mt-1 w-full justify-start px-2 text-muted-foreground text-xs"
            onClick={() => props.onExpandGroup(group.key)}
          >
            {'Show all ' + allThreads.length + ' threads'}
          </Button>
        )}
      </div>
    </div>
  );
}

export function HistoryList(props: HistoryListProps): JSX.Element {
  const { state } = props;
  if (!props.hasWorkspaces) {
    return <StateBox name="empty" text="Open an Agent workspace to load history." />;
  }
  if (state.status === 'initial-loading') {
    return <StateBox name="loading" text="Loading history..." />;
  }
  if (state.status === 'unavailable') {
    return <StateBox name="empty" text="Open an Agent workspace to load history." />;
  }
  if (state.status === 'error') {
    return (
      <div className="agent-history-state agent-history-error text-[13px] text-destructive leading-normal" role="alert">
        <p className="mt-0 mb-2.5">{state.errorText || 'Unable to load Agent thread history.'}</p>
        <Button variant="outline" size="sm" className="agent-history-retry" onClick={props.onRetryRefresh}>
          Retry
        </Button>
      </div>
    );
  }

  const groups = filterHistoryGroups(
    buildHistoryGroups(state.threads, state.providers),
    props.query,
    props.providerFilter
  ) as HistoryGroupView[];
  const isSearching = !!props.query || !!props.providerFilter;

  const notice = state.threadOpenError ? (
    <div
      className="agent-history-open-error mb-3 rounded-md border border-yellow-600/50 bg-yellow-600/10 p-2 text-xs leading-normal"
      role="alert"
    >
      <p className="m-0">{state.threadOpenError.text || 'PSX could not open the selected Agent thread. Try again.'}</p>
      <div className="agent-history-open-error-actions mt-2 flex gap-2">
        <Button
          variant="outline"
          size="sm"
          className="agent-history-retry"
          onClick={() => props.onOpenThread(state.threadOpenError!.threadId)}
        >
          Retry
        </Button>
        <Button variant="ghost" size="sm" className="agent-history-dismiss" onClick={props.onDismissOpenError}>
          Dismiss
        </Button>
      </div>
    </div>
  ) : null;

  if (state.threads.length === 0) {
    return (
      <>
        {notice}
        <StateBox name="empty" text={state.loaded ? 'No saved Agent threads.' : 'Loading history...'} />
      </>
    );
  }
  if (groups.length === 0) {
    return (
      <>
        {notice}
        <StateBox name="empty" text="No threads match the current search or filter." />
      </>
    );
  }
  return (
    <>
      {notice}
      <div className="agent-history-list grid gap-0.5">
        {groups.map((group) => (
          <HistoryGroup key={group.key} group={group} isSearching={isSearching} props={props} />
        ))}
      </div>
    </>
  );
}
