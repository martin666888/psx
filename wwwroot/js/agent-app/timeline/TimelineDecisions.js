import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
// TimelineDecisions.tsx — React twins of the DecisionController thread cards
// (permission/question, mode-transition; elicitation renders a read-only
// notice pending full form support). DOM mirrors permissions.js /
// modeTransition.js so the produced markup matches the legacy engine.
import { useLayoutEffect, useRef } from 'react';
import { renderMarkdown } from '../core/markdown.js';
import { decisionOptionClass } from '../decisions/DecisionController.js';
import { CopyButton, useDetailsOpen } from './TimelineView.js';
function PermissionQuestionCard({ item, assistantName, callbacks }) {
    // legacy: the card starts open and only a local option click closes it;
    // external resolve/cancel leaves the user's toggle alone.
    const ref = useDetailsOpen(!item.collapsed);
    const disabled = item.decisionState !== 'active';
    const resolvedId = item.selectedOptionId;
    const resolvedName = item.selectedOptionName;
    const selected = item.options.find((option) => resolvedId && option.optionId === resolvedId) ||
        (resolvedName ? item.options.find((option) => option.name === resolvedName) : undefined);
    const subtitle = selected
        ? 'Selection recorded.'
        : item.kind === 'permission'
            ? assistantName + ' Agent needs your approval before continuing.'
            : assistantName + ' Agent is waiting for your answer.';
    // Selected option leads the row; the rest hide (legacy completeDecisionCard).
    const ordered = selected ? [selected, ...item.options.filter((option) => option !== selected)] : item.options;
    return (_jsxs("details", { ref: ref, className: 'agent-decision agent-decision-' + item.kind + (disabled ? ' agent-decision-disabled' : ''), "data-decision-state": disabled ? 'disabled' : 'active', "data-request-id": item.requestId || undefined, "data-selected-option-id": resolvedId || undefined, "data-selected-option-name": resolvedName || undefined, children: [_jsx("summary", { className: "agent-decision-header", children: _jsxs("div", { className: "agent-decision-header-content", children: [_jsxs("div", { className: "agent-decision-header-title-row", children: [_jsx("span", { className: "agent-decision-chevron" }), _jsx("span", { className: "agent-decision-header-title", children: item.title })] }), _jsx("span", { className: "agent-decision-header-subtitle", children: subtitle })] }) }), _jsxs("div", { className: "agent-decision-body", children: [_jsxs("details", { className: "agent-decision-raw-input", children: [_jsx("summary", { className: "agent-decision-raw-input-summary", children: "Raw Input" }), _jsx("pre", { className: "agent-decision-raw-input-content", children: item.text })] }), _jsxs("div", { className: "agent-decision-actions", children: [item.options.length === 0 ? (_jsx("div", { className: "agent-decision-status agent-decision-options-error", children: "The Agent did not provide any response options." })) : null, ordered.map((option) => {
                                const isSelected = selected === option;
                                // legacy completeDecisionCard strips semantics from the selected
                                // button only; hidden losers keep theirs.
                                const semantic = isSelected ? '' : decisionOptionClass(option);
                                return (_jsx("button", { type: "button", className: 'agent-decision-option' +
                                        (semantic ? ' ' + semantic : '') +
                                        (isSelected ? ' agent-decision-option-selected' : ''), "data-option-id": option.optionId, "aria-pressed": isSelected ? 'true' : 'false', hidden: !!selected && !isSelected, disabled: disabled, onClick: () => {
                                        if (item.decisionState !== 'active')
                                            return;
                                        callbacks.onDecisionOption(item, option);
                                    }, children: option.name }, option.optionId || option.name));
                            })] }), disabled && !selected && item.statusText ? (_jsx("div", { className: "agent-decision-status", children: item.statusText })) : null] })] }));
}
function ModeTransitionCard({ item, callbacks }) {
    const pending = item.decisionState === 'active';
    const detailsRef = useRef(null);
    const openedOnce = useRef(false);
    useLayoutEffect(() => {
        if (openedOnce.current || !detailsRef.current)
            return;
        openedOnce.current = true;
        detailsRef.current.open = !item.historical;
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);
    const statusText = item.statusText
        ? item.statusText
        : pending
            ? 'The Agent is waiting for your selection.'
            : item.selectedOptionId
                ? (() => {
                    const selected = item.options.find((option) => option.optionId === item.selectedOptionId);
                    return selected ? 'Selected: ' + selected.name : 'Selection recorded.';
                })()
                : 'This request is no longer active.';
    return (_jsxs("section", { className: "agent-mode-transition", "data-decision-state": pending ? 'active' : 'disabled', "data-request-id": item.requestId || undefined, children: [_jsxs("header", { className: "agent-mode-transition-header", children: [_jsx("div", { className: "agent-mode-transition-title", children: item.title }), _jsx("span", { className: "agent-mode-transition-header-state", children: pending ? 'Decision required' : item.headerState || 'Interrupted' })] }), _jsxs("details", { ref: detailsRef, className: "agent-mode-transition-details", children: [_jsx("summary", { className: "agent-mode-transition-summary", children: "Proposal details" }), _jsx("div", { className: "agent-mode-transition-document agent-message-body", dangerouslySetInnerHTML: { __html: renderMarkdown(item.text) } }), _jsx("div", { className: "agent-message-actions agent-mode-transition-document-actions", children: _jsx(CopyButton, { getText: () => item.text, copyText: callbacks.copyText }) }), _jsxs("div", { className: "agent-mode-transition-decision", hidden: pending, children: [_jsx("div", { className: "agent-mode-transition-decision-label", children: "Choose how to continue" }), _jsx("div", { className: "agent-mode-transition-options", role: "group", "aria-label": "Choose how to continue", children: item.options.length === 0 ? (_jsx("div", { className: "agent-mode-transition-error", children: "The ACP Agent did not provide any response options." })) : (item.options.map((option) => (_jsx("button", { type: "button", className: 'agent-mode-transition-option' +
                                        (decisionOptionClass(option) ? ' ' + decisionOptionClass(option) : '') +
                                        (option.optionId === item.selectedOptionId ? ' agent-mode-transition-option-selected' : ''), "data-option-id": option.optionId, "data-option-kind": option.kind, "aria-pressed": option.optionId === item.selectedOptionId ? 'true' : 'false', disabled: true, children: option.name }, option.optionId || option.name)))) })] }), _jsx("div", { className: "agent-mode-transition-status", "aria-live": "polite", children: statusText })] })] }));
}
export function DecisionCard({ item, assistantName, callbacks }) {
    if (item.kind === 'mode_transition') {
        return _jsx(ModeTransitionCard, { item: item, callbacks: callbacks });
    }
    if (item.kind === 'elicitation') {
        // Elicitation forms keep the legacy renderer until the S3 switch lands;
        // in the unwired tree this is a placeholder card that never ships.
        return (_jsxs("section", { className: "agent-decision agent-decision-elicitation", "data-decision-state": item.decisionState, children: [_jsx("div", { className: "agent-decision-title", children: item.title }), _jsx("div", { className: "agent-decision-subtitle", children: item.elicitationMessage }), item.statusText ? _jsx("div", { className: "agent-decision-status", children: item.statusText }) : null] }));
    }
    return _jsx(PermissionQuestionCard, { item: item, assistantName: assistantName, callbacks: callbacks });
}
