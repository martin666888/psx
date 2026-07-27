import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { buildHistoryGroups, filterHistoryGroups, formatHistoryTime } from './historyModel.js';
import { PsxTag } from '../ui/Psx.js';
const PREVIEW_LIMIT = 5;
function StateBox({ name, text }) {
    const live = name === 'loading' ? { role: 'status', 'aria-live': 'polite' } : {};
    return (_jsx("div", { className: 'agent-history-state agent-history-' + name, ...live, children: text }));
}
function FolderIcons() {
    return (_jsxs(_Fragment, { children: [_jsx("svg", { className: "agent-history-folder-closed", viewBox: "0 0 16 16", width: "13", height: "13", "aria-hidden": "true", focusable: "false", children: _jsx("path", { d: "M2.5 4h4l1.5 2h5.5v6.5h-11z", fill: "none", stroke: "currentColor", strokeWidth: "1.2", strokeLinejoin: "round" }) }), _jsxs("svg", { className: "agent-history-folder-open", viewBox: "0 0 16 16", width: "13", height: "13", "aria-hidden": "true", focusable: "false", children: [_jsx("path", { d: "M2.5 4h4l1.5 2h5.5v2h-8.5l-2.5 6.5h-1z", fill: "none", stroke: "currentColor", strokeWidth: "1.2", strokeLinejoin: "round" }), _jsx("path", { d: "M4.5 8.5h9l-2 5h-9z", fill: "none", stroke: "currentColor", strokeWidth: "1.2", strokeLinejoin: "round" })] })] }));
}
function ThreadRow({ thread, activeThreadId, openedElsewhere, onOpenThread }) {
    const titleText = thread.title || 'Agent Chat';
    const isActive = thread.threadId === activeThreadId;
    const isOpenElsewhere = !isActive && !!thread.threadId && openedElsewhere.has(thread.threadId);
    const badgeText = isActive ? 'Current' : isOpenElsewhere ? 'Open' : '';
    const parts = [];
    if (thread.providerDisplay)
        parts.push(thread.providerDisplay);
    const timeLabel = formatHistoryTime(thread.updatedAt);
    if (timeLabel)
        parts.push(timeLabel);
    const fullDescription = parts.join(' | ');
    return (_jsx("button", { type: "button", className: "agent-history-item", "data-thread-id": thread.threadId || '', "aria-current": isActive ? 'true' : 'false', title: fullDescription || undefined, "aria-label": [titleText, badgeText, fullDescription].filter((part) => part).join(', '), onClick: () => {
            if (thread.threadId)
                onOpenThread(thread.threadId);
        }, children: _jsxs("span", { className: "agent-history-heading", children: [_jsx("strong", { children: titleText }), isActive && _jsx(PsxTag, { className: "agent-history-current", children: "Current" }), isOpenElsewhere && _jsx(PsxTag, { className: "agent-history-open", children: "Open" }), _jsx("small", { children: timeLabel || thread.providerDisplay || '' })] }) }));
}
function HistoryGroup({ group, isSearching, props }) {
    const allThreads = group.threads;
    const showAll = isSearching || props.expandedGroups.has(group.key);
    const visibleThreads = showAll ? allThreads : allThreads.slice(0, PREVIEW_LIMIT);
    const hiddenCount = allThreads.length - visibleThreads.length;
    const folded = !isSearching && props.foldedGroups.has(group.key);
    const hasActive = allThreads.some((thread) => thread.threadId === props.activeThreadId);
    return (_jsxs("div", { className: "agent-history-group", "data-folded": String(folded), children: [_jsxs("button", { type: "button", className: "agent-history-group-header", "aria-expanded": !folded, onClick: () => props.onToggleFold(group.key), children: [_jsx("span", { className: "agent-history-group-folder", "aria-hidden": "true", children: _jsx(FolderIcons, {}) }), _jsxs("span", { className: "agent-history-group-label", children: [_jsx("strong", { children: group.name }), group.path && _jsx("small", { title: group.path, children: group.path })] }), _jsx("span", { className: "agent-history-group-count", children: String(allThreads.length) }), hasActive && _jsx("span", { className: "agent-history-group-active", "aria-label": "Contains the active session" })] }), _jsxs("div", { className: "agent-history-group-threads", hidden: folded, children: [visibleThreads.map((thread, index) => (_jsx(ThreadRow, { thread: thread, activeThreadId: props.activeThreadId, openedElsewhere: props.openedElsewhere, onOpenThread: props.onOpenThread }, thread.threadId || 'row-' + index))), hiddenCount > 0 && (_jsx("button", { type: "button", className: "agent-history-show-more", onClick: () => props.onExpandGroup(group.key), children: 'Show all ' + allThreads.length + ' threads' }))] })] }));
}
export function HistoryList(props) {
    const { state } = props;
    if (!props.hasWorkspaces) {
        return _jsx(StateBox, { name: "empty", text: "Open an Agent workspace to load history." });
    }
    if (state.status === 'initial-loading') {
        return _jsx(StateBox, { name: "loading", text: "Loading history..." });
    }
    if (state.status === 'unavailable') {
        return _jsx(StateBox, { name: "empty", text: "Open an Agent workspace to load history." });
    }
    if (state.status === 'error') {
        return (_jsxs("div", { className: "agent-history-state agent-history-error", role: "alert", children: [_jsx("p", { children: state.errorText || 'Unable to load Agent thread history.' }), _jsx("button", { type: "button", className: "agent-history-retry", onClick: props.onRetryRefresh, children: "Retry" })] }));
    }
    const groups = filterHistoryGroups(buildHistoryGroups(state.threads, state.providers), props.query, props.providerFilter);
    const isSearching = !!props.query || !!props.providerFilter;
    const notice = state.threadOpenError ? (_jsxs("div", { className: "agent-history-open-error", role: "alert", children: [_jsx("p", { children: state.threadOpenError.text || 'PSX could not open the selected Agent thread. Try again.' }), _jsxs("div", { className: "agent-history-open-error-actions", children: [_jsx("button", { type: "button", className: "agent-history-retry", onClick: () => props.onOpenThread(state.threadOpenError.threadId), children: "Retry" }), _jsx("button", { type: "button", className: "agent-history-dismiss", onClick: props.onDismissOpenError, children: "Dismiss" })] })] })) : null;
    if (state.threads.length === 0) {
        return (_jsxs(_Fragment, { children: [notice, _jsx(StateBox, { name: "empty", text: state.loaded ? 'No saved Agent threads.' : 'Loading history...' })] }));
    }
    if (groups.length === 0) {
        return (_jsxs(_Fragment, { children: [notice, _jsx(StateBox, { name: "empty", text: "No threads match the current search or filter." })] }));
    }
    return (_jsxs(_Fragment, { children: [notice, _jsx("div", { className: "agent-history-list", children: groups.map((group) => (_jsx(HistoryGroup, { group: group, isSearching: isSearching, props: props }, group.key))) })] }));
}
