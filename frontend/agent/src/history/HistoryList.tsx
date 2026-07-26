// HistoryList.tsx — React twin of HistoryDockController.renderContent.
//
// Renders the content of data-role="history-content": the empty / loading /
// unavailable / error states, the thread-open-error notice, and the grouped
// thread list with fold headers, preview limits, open markers and per-row
// accessible names. All interaction state (folded/expanded groups, search
// text, provider filter) stays with the controller and arrives as props; the
// component mirrors the legacy branch order exactly.

import type { JSX } from 'react';
import type { AgentHistoryState } from '../contracts/agent-history.js';
import {
  buildHistoryGroups,
  filterHistoryGroups,
  formatHistoryTime
} from './historyModel.js';

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
    <div className={'agent-history-state agent-history-' + name} {...live}>
      {text}
    </div>
  );
}

function FolderIcons(): JSX.Element {
  return (
    <>
      <svg className="agent-history-folder-closed" viewBox="0 0 16 16" width="13" height="13" aria-hidden="true" focusable="false">
        <path d="M2.5 4h4l1.5 2h5.5v6.5h-11z" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinejoin="round" />
      </svg>
      <svg className="agent-history-folder-open" viewBox="0 0 16 16" width="13" height="13" aria-hidden="true" focusable="false">
        <path d="M2.5 4h4l1.5 2h5.5v2h-8.5l-2.5 6.5h-1z" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinejoin="round" />
        <path d="M4.5 8.5h9l-2 5h-9z" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinejoin="round" />
      </svg>
    </>
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
      className="agent-history-item"
      data-thread-id={thread.threadId || ''}
      aria-current={isActive ? 'true' : 'false'}
      title={fullDescription || undefined}
      aria-label={[titleText, badgeText, fullDescription].filter((part) => part).join(', ')}
      onClick={() => {
        if (thread.threadId) onOpenThread(thread.threadId);
      }}
    >
      <span className="agent-history-heading">
        <strong>{titleText}</strong>
        {isActive && <span className="agent-history-current">Current</span>}
        {isOpenElsewhere && <span className="agent-history-open">Open</span>}
        <small>{timeLabel || thread.providerDisplay || ''}</small>
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
    <div className="agent-history-group" data-folded={String(folded)}>
      <button
        type="button"
        className="agent-history-group-header"
        aria-expanded={!folded}
        onClick={() => props.onToggleFold(group.key)}
      >
        <span className="agent-history-group-folder" aria-hidden="true">
          <FolderIcons />
        </span>
        <span className="agent-history-group-label">
          <strong>{group.name}</strong>
          {group.path && <small title={group.path}>{group.path}</small>}
        </span>
        <span className="agent-history-group-count">{String(allThreads.length)}</span>
        {hasActive && <span className="agent-history-group-active" aria-label="Contains the active session" />}
      </button>
      <div className="agent-history-group-threads" hidden={folded}>
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
          <button type="button" className="agent-history-show-more" onClick={() => props.onExpandGroup(group.key)}>
            {'Show all ' + allThreads.length + ' threads'}
          </button>
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
      <div className="agent-history-state agent-history-error" role="alert">
        <p>{state.errorText || 'Unable to load Agent thread history.'}</p>
        <button type="button" className="agent-history-retry" onClick={props.onRetryRefresh}>
          Retry
        </button>
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
    <div className="agent-history-open-error" role="alert">
      <p>{state.threadOpenError.text || 'PSX could not open the selected Agent thread. Try again.'}</p>
      <div className="agent-history-open-error-actions">
        <button
          type="button"
          className="agent-history-retry"
          onClick={() => props.onOpenThread(state.threadOpenError!.threadId)}
        >
          Retry
        </button>
        <button type="button" className="agent-history-dismiss" onClick={props.onDismissOpenError}>
          Dismiss
        </button>
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
      <div className="agent-history-list">
        {groups.map((group) => (
          <HistoryGroup key={group.key} group={group} isSearching={isSearching} props={props} />
        ))}
      </div>
    </>
  );
}
