import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
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
import { useLayoutEffect, useRef, useState } from 'react';
import { renderMarkdown } from '../core/markdown.js';
import { TOOL_STATE_LABELS } from './timelineViewModel.js';
import { DecisionCard } from './TimelineDecisions.js';
import { PsxButton, PsxCard } from '../ui/Psx.js';
const COPY_ICON = (_jsxs("svg", { viewBox: "0 0 24 24", width: "14", height: "14", fill: "none", stroke: "currentColor", strokeWidth: "2", strokeLinecap: "round", strokeLinejoin: "round", "aria-hidden": "true", children: [_jsx("rect", { x: "9", y: "9", width: "13", height: "13", rx: "2", ry: "2" }), _jsx("path", { d: "M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" })] }));
const CHECK_ICON = (_jsx("svg", { viewBox: "0 0 24 24", width: "14", height: "14", fill: "none", stroke: "currentColor", strokeWidth: "2", strokeLinecap: "round", strokeLinejoin: "round", "aria-hidden": "true", children: _jsx("polyline", { points: "20 6 9 17 4 12" }) }));
/** React twin of createCopyButton + showCopyFeedback (icon swap + classes). */
export function CopyButton(props) {
    const [feedback, setFeedback] = useState('idle');
    const timer = useRef(null);
    useLayoutEffect(() => () => {
        if (timer.current)
            clearTimeout(timer.current);
    }, []);
    const label = feedback === 'success' ? '已复制' : feedback === 'fail' ? '复制失败' : '复制';
    return (_jsx("button", { type: "button", className: 'agent-copy-button' +
            (feedback === 'success' ? ' agent-copy-success' : feedback === 'fail' ? ' agent-copy-fail' : ''), "aria-label": label, title: label, onClick: () => {
            void props.copyText(props.getText()).then((ok) => {
                if (timer.current)
                    clearTimeout(timer.current);
                setFeedback(ok ? 'success' : 'fail');
                timer.current = setTimeout(() => {
                    timer.current = null;
                    setFeedback('idle');
                }, 1500);
            });
        }, children: feedback === 'success' ? CHECK_ICON : COPY_ICON }));
}
/** Uncontrolled <details> whose open state follows the projection lifecycle. */
export function useDetailsOpen(open) {
    const ref = useRef(null);
    const applied = useRef(null);
    useLayoutEffect(() => {
        if (!ref.current)
            return;
        if (applied.current === null || applied.current !== open) {
            applied.current = open;
            ref.current.open = open;
        }
    });
    return ref;
}
function SystemRow({ item }) {
    return _jsx("div", { className: "agent-system", children: item.text });
}
function RecoveryCard({ item, callbacks }) {
    return (_jsxs(PsxCard, { className: "agent-recovery", role: "status", "aria-live": "polite", "aria-atomic": "true", children: [_jsxs("div", { className: "agent-recovery-content", children: [_jsx("div", { className: "agent-recovery-title", children: "Session could not be resumed" }), _jsx("p", { className: "agent-recovery-message", children: item.message }), _jsxs("details", { className: "agent-recovery-details", hidden: !item.detail, children: [_jsx("summary", { children: "Technical details" }), _jsx("pre", { className: "agent-recovery-technical", children: item.detail })] })] }), _jsx("div", { className: "agent-recovery-actions", children: _jsx(PsxButton, { variant: "subtle", onClick: () => callbacks.onOpenTerminal(), children: "Open terminal" }) })] }));
}
function ThinkingRowView({ item }) {
    if (item.variant === 'row') {
        return (_jsxs("div", { className: "agent-thinking", role: "status", "aria-live": "polite", "aria-busy": "true", children: [_jsx("span", { className: "agent-spinner" }), _jsx("span", { children: "Thinking" })] }));
    }
    return _jsx(ThinkingBlock, { item: item });
}
function ThinkingBlock({ item }) {
    const ref = useDetailsOpen(item.running);
    return (_jsxs("details", { ref: ref, className: 'agent-thinking-block' + (item.running ? ' agent-thinking-running' : ''), "aria-busy": item.running ? 'true' : 'false', children: [_jsxs("summary", { className: "agent-thinking-header", children: [_jsx("span", { className: "agent-thinking-chevron" }), item.running ? _jsx("span", { className: "agent-spinner" }) : null, _jsx("span", { className: "agent-thinking-title", children: "Thinking" })] }), _jsx("pre", { className: "agent-thinking-content", children: item.text })] }));
}
function ToolCardView({ card }) {
    const ref = useDetailsOpen(card.open);
    const label = TOOL_STATE_LABELS[card.state] || TOOL_STATE_LABELS.done;
    return (_jsxs("details", { ref: ref, className: 'agent-tool-card agent-tool-card-' + card.state, "data-tool-id": card.toolCallId, "data-state": card.state, children: [_jsxs("summary", { className: "agent-tool-card-header", children: [_jsx("span", { className: "agent-tool-card-summary", children: card.summary }), _jsx("span", { className: "agent-tool-card-status", "aria-label": 'Tool status: ' + label, children: label })] }), _jsx("div", { className: "agent-tool-card-body", children: _jsx("pre", { className: "agent-tool-card-content", children: card.output }) })] }));
}
// Items are mutated in place by the projection, so components stay unmemoized:
// keyed reconciliation already preserves DOM node identity across renders.
function ToolGroup({ item }) {
    const ref = useDetailsOpen(item.open);
    const total = item.cards.length;
    const running = item.cards.filter((card) => card.state === 'running').length;
    const label = total === 0
        ? 'Tool activity'
        : 'Tool activity \u00b7 ' + total + ' call' + (total > 1 ? 's' : '') + (item.live && running > 0 ? ' \u00b7 ' + running + ' running' : '');
    return (_jsxs("details", { ref: ref, className: 'agent-run-group' + (item.error ? ' agent-run-group-error' : ''), "data-run-id": item.runId, style: item.visible ? undefined : { display: 'none' }, children: [_jsxs("summary", { className: "agent-run-group-header", children: [_jsx("span", { className: "agent-run-group-chevron" }), _jsx("span", { className: "agent-run-group-summary", children: label })] }), _jsx("div", { className: "agent-run-group-body", children: item.cards.map((card) => (_jsx(ToolCardView, { card: card }, card.toolCallId))) })] }));
}
function InlineTool({ item }) {
    const statusLabel = item.state === 'error' ? 'Failed' : item.state === 'fallback' ? 'Needs terminal' : 'Done';
    return (_jsxs("section", { className: 'agent-tool agent-tool-' + item.state, "data-state": item.state, children: [_jsxs("div", { className: "agent-tool-header", children: [_jsx("span", { children: item.name }), _jsx("span", { className: "agent-tool-card-status", children: statusLabel })] }), _jsx("pre", { children: item.text })] }));
}
/** legacy _appendMessage image-JSON cleanup for user messages. */
function cleanUserText(text) {
    return (text || '').replace(/\{"type":"image"[^\}]*\}/g, '\n\n[image attachment]\n\n');
}
/** Attachment grid host: tiles are composer-built DOM (legacy seam). */
function AttachmentGrid({ attachments, callbacks }) {
    const host = useRef(null);
    const built = useRef(false);
    useLayoutEffect(() => {
        if (built.current || !host.current)
            return;
        built.current = true;
        for (const attachment of attachments) {
            host.current.appendChild(callbacks.createAttachmentTile(attachment));
        }
    }, [attachments, callbacks]);
    return _jsx("div", { className: "agent-message-attachments", ref: host });
}
const MessageRow = function MessageRow({ item, showLabel, assistantName, callbacks }) {
    const isUser = item.role === 'user';
    const ariaLabel = isUser ? 'You' : assistantName;
    const showActions = !isUser && item.finalized && !!item.raw.trim();
    return (_jsxs("article", { className: 'agent-message agent-message-' + item.role + (showLabel ? '' : ' agent-message-continuation'), "aria-label": ariaLabel, children: [showLabel ? _jsx("div", { className: "agent-message-label", children: ariaLabel }) : null, isUser ? (_jsxs("div", { className: "agent-message-body", children: [item.attachments.length > 0 ? (_jsx(AttachmentGrid, { attachments: item.attachments, callbacks: callbacks })) : null, _jsx("div", { className: "agent-message-content", dangerouslySetInnerHTML: { __html: renderMarkdown(cleanUserText(item.raw)) } })] })) : (_jsx("div", { className: "agent-message-body", "data-raw": item.raw, dangerouslySetInnerHTML: { __html: renderMarkdown(item.raw) } })), showActions ? (_jsx("div", { className: "agent-message-actions", children: _jsx(CopyButton, { getText: () => item.raw, copyText: callbacks.copyText }) })) : null] }));
};
function renderItem(row, rowsInTurn, assistantName, callbacks) {
    const item = row.item;
    switch (item.type) {
        case 'system':
            return 'variant' in item && item.variant === 'recovery' ? (_jsx(RecoveryCard, { item: item, callbacks: callbacks }, item.id)) : (_jsx(SystemRow, { item: item }, item.id));
        case 'thinking':
            return _jsx(ThinkingRowView, { item: item }, item.id);
        case 'tool':
            return item.variant === 'run-group' ? (_jsx(ToolGroup, { item: item }, item.id)) : (_jsx(InlineTool, { item: item }, item.id));
        case 'message': {
            // Label logic mirrors legacy: the first message of each role in a turn
            // carries the label, later ones are continuations.
            const first = rowsInTurn.find((candidate) => candidate.item.type === 'message' && candidate.item.role === item.role);
            return (_jsx(MessageRow, { item: item, showLabel: first?.item === item, assistantName: assistantName, callbacks: callbacks }, item.id));
        }
        case 'decision':
            return (_jsx(DecisionCard, { item: item, assistantName: assistantName, callbacks: callbacks }, item.id));
        default:
            return null;
    }
}
export function TimelineView({ rows, assistantName, callbacks, onCommitted }) {
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
    const blocks = [];
    const turnBlocks = new Map();
    for (const row of rows) {
        if (row.turnId) {
            let block = turnBlocks.get(row.turnId);
            if (!block) {
                block = { key: row.turnId, turn: true, rows: [] };
                turnBlocks.set(row.turnId, block);
                blocks.push(block);
            }
            block.rows.push(row);
        }
        else {
            blocks.push({ key: 'root-' + row.item.id, turn: false, rows: [row] });
        }
    }
    return (_jsx(_Fragment, { children: blocks.map((block) => block.turn ? (_jsx("section", { className: "agent-turn", children: block.rows.map((row) => renderItem(row, block.rows, assistantName, callbacks)) }, block.key)) : (block.rows.map((row) => renderItem(row, block.rows, assistantName, callbacks)))) }));
}
