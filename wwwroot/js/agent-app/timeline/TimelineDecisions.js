import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
// TimelineDecisions.tsx — React twins of the DecisionController thread cards
// (permission/question, mode-transition; elicitation renders a read-only
// notice pending full form support). DOM mirrors permissions.js /
// modeTransition.js so the produced markup matches the legacy engine.
import { useLayoutEffect, useRef, useState } from 'react';
import { defaultOptionValue, orderElicitationFields, readElicitationOptions } from '../core/elicitation.js';
import { renderMarkdown, safeHref } from '../core/markdown.js';
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
function fieldSpecs(schema) {
    const requiredList = Array.isArray(schema.required) ? schema.required : [];
    const required = new Set(requiredList);
    return orderElicitationFields(schema).map((entry) => {
        const options = readElicitationOptions(entry.property);
        const isArray = entry.property.type === 'array' && !!entry.property.items;
        const kind = entry.property.type === 'boolean'
            ? 'boolean'
            : options.length && !isArray
                ? 'options'
                : isArray
                    ? 'array'
                    : entry.property.type === 'integer' || entry.property.type === 'number'
                        ? 'number'
                        : 'text';
        return {
            name: entry.name,
            property: entry.property,
            required: required.has(entry.name),
            isSupplement: entry.isSupplement,
            options,
            isArray,
            kind
        };
    });
}
function initialFieldValue(spec) {
    if (spec.kind === 'boolean')
        return !!spec.property.default;
    if (spec.kind === 'options')
        return defaultOptionValue(spec.options, spec.property.default, true);
    if (spec.kind === 'array') {
        return new Set(Array.isArray(spec.property.default) ? spec.property.default : []);
    }
    return spec.property.default !== undefined && spec.property.default !== null
        ? String(spec.property.default)
        : '';
}
/** legacy per-kind validate(); returns the error message or ''. */
function validateField(spec, value) {
    if (spec.kind === 'options') {
        if (spec.required && (value === undefined || value === null || value === '')) {
            return 'Choose an option to continue.';
        }
        return '';
    }
    if (spec.kind === 'array') {
        if (spec.required && value.size === 0) {
            return 'Choose at least one option to continue.';
        }
        return '';
    }
    if (spec.kind === 'number') {
        const raw = String(value ?? '').trim();
        if (spec.required && raw === '')
            return 'Enter a number to continue.';
        if (raw !== '') {
            const numeric = Number(raw);
            if (!Number.isFinite(numeric) || (spec.property.type === 'integer' && !Number.isInteger(numeric))) {
                return spec.property.type === 'integer' ? 'Enter a whole number.' : 'Enter a valid number.';
            }
        }
        return '';
    }
    if (spec.kind === 'text') {
        if (spec.required && String(value ?? '').trim() === '')
            return 'Enter a response to continue.';
        return '';
    }
    return '';
}
/** legacy per-kind read(); undefined omits the field from the payload. */
function readField(spec, value) {
    if (spec.kind === 'boolean')
        return !!value;
    if (spec.kind === 'options')
        return value;
    if (spec.kind === 'array')
        return Array.from(value);
    if (spec.kind === 'number') {
        const raw = String(value ?? '').trim();
        return raw === '' ? undefined : Number(raw);
    }
    const text = String(value ?? '');
    return text === '' && !spec.required ? undefined : text;
}
function ElicitationOptionButton({ option, multi, selected, disabled, onClick }) {
    return (_jsxs("button", { type: "button", className: 'agent-elicitation-option' + (selected ? ' agent-elicitation-option-selected' : ''), role: multi ? 'button' : 'radio', "aria-checked": multi ? undefined : selected, "aria-pressed": multi ? selected : undefined, disabled: disabled, onClick: onClick, children: [_jsx("span", { className: "agent-elicitation-option-title", children: option.title }), option.description ? (_jsx("small", { className: "agent-elicitation-option-description", children: option.description })) : null] }));
}
function ElicitationCard({ item, callbacks }) {
    const raw = (item.schema ?? {});
    const schema = (raw.schema && typeof raw.schema === 'object' ? raw.schema : {});
    const mode = typeof raw.mode === 'string' ? raw.mode : '';
    const url = typeof raw.url === 'string' ? raw.url : '';
    const specsRef = useRef(null);
    if (!specsRef.current) {
        let specs = fieldSpecs(schema);
        if (specs.length === 0 && mode !== 'url') {
            specs = fieldSpecs({ properties: { response: { type: 'string', title: 'Response' } }, required: ['response'] });
        }
        specsRef.current = specs;
    }
    const specs = specsRef.current;
    const [values, setValues] = useState(() => {
        const initial = {};
        for (const spec of specs)
            initial[spec.name] = initialFieldValue(spec);
        return initial;
    });
    const [errors, setErrors] = useState({});
    const disabled = item.decisionState !== 'active';
    const setValue = (name, value) => {
        setValues((previous) => ({ ...previous, [name]: value }));
        setErrors((previous) => (previous[name] ? { ...previous, [name]: '' } : previous));
    };
    const act = (action) => {
        if (item.decisionState !== 'active')
            return;
        action();
    };
    const submit = () => {
        const nextErrors = {};
        let valid = true;
        for (const spec of specs) {
            const message = validateField(spec, values[spec.name]);
            if (message) {
                valid = false;
                nextErrors[spec.name] = message;
            }
        }
        setErrors(nextErrors);
        if (!valid)
            return;
        const content = {};
        for (const spec of specs) {
            const value = readField(spec, values[spec.name]);
            if (value !== undefined)
                content[spec.name] = value;
        }
        callbacks.onElicitationAction(item, JSON.stringify({ action: 'accept', content }), 'Response sent.');
    };
    const safeUrl = url ? safeHref(url) : '';
    return (_jsxs("section", { className: 'agent-decision agent-decision-elicitation' + (disabled ? ' agent-decision-disabled' : ''), "data-decision-state": disabled ? 'disabled' : 'active', "data-request-id": item.requestId || undefined, children: [_jsx("div", { className: "agent-decision-title", children: item.title }), _jsx("div", { className: "agent-decision-subtitle", children: item.elicitationMessage }), _jsxs("form", { className: "agent-elicitation-form", noValidate: true, children: [specs.map((spec) => {
                        const error = errors[spec.name] || '';
                        const label = (String(spec.property.title ?? '') || spec.name) + (spec.required ? ' *' : '');
                        return (_jsxs("div", { className: 'agent-elicitation-field' +
                                (spec.isSupplement ? ' agent-elicitation-field-supplement' : '') +
                                (error ? ' agent-elicitation-field-error' : ''), children: [_jsx("div", { className: "agent-elicitation-label", children: label }), spec.kind === 'boolean' ? (_jsx("label", { className: "agent-elicitation-checkbox", children: _jsx("input", { type: "checkbox", checked: !!values[spec.name], disabled: disabled, onChange: (event) => setValue(spec.name, event.target.checked) }) })) : spec.kind === 'options' ? (_jsx("div", { className: "agent-elicitation-option-list", role: "radiogroup", "aria-label": String(spec.property.title ?? '') || spec.name, children: spec.options.map((option, index) => (_jsx(ElicitationOptionButton, { option: option, multi: false, selected: values[spec.name] === option.value, disabled: disabled, onClick: () => act(() => setValue(spec.name, option.value)) }, index))) })) : spec.kind === 'array' ? (_jsx("div", { className: "agent-elicitation-option-list", role: "group", "aria-label": String(spec.property.title ?? '') || spec.name, children: readElicitationOptions(spec.property.items).map((option, index) => (_jsx(ElicitationOptionButton, { option: option, multi: true, selected: values[spec.name].has(option.value), disabled: disabled, onClick: () => act(() => {
                                            const next = new Set(values[spec.name]);
                                            if (next.has(option.value))
                                                next.delete(option.value);
                                            else
                                                next.add(option.value);
                                            setValue(spec.name, next);
                                        }) }, index))) })) : spec.kind === 'number' ? (_jsx("input", { type: "number", step: spec.property.type === 'integer' ? '1' : undefined, value: String(values[spec.name] ?? ''), disabled: disabled, onChange: (event) => setValue(spec.name, event.target.value) })) : (_jsx("textarea", { rows: spec.isSupplement ? 2 : 3, value: String(values[spec.name] ?? ''), disabled: disabled, onChange: (event) => setValue(spec.name, event.target.value) })), spec.property.description ? _jsx("small", { children: String(spec.property.description) }) : null, _jsx("div", { className: "agent-elicitation-error", hidden: !error, children: error })] }, spec.name));
                    }), mode === 'url' && url ? (_jsx("div", { className: "agent-elicitation-url", children: safeUrl ? (_jsx("a", { href: safeUrl, target: "_blank", rel: "noreferrer", children: url })) : (url) })) : null] }), _jsxs("div", { className: "agent-decision-actions agent-elicitation-actions", children: [_jsx("button", { type: "button", className: "agent-btn-primary", disabled: disabled, onClick: () => act(submit), children: "Continue" }), _jsx("button", { type: "button", className: "agent-btn-subtle", disabled: disabled, onClick: () => act(() => callbacks.onElicitationAction(item, JSON.stringify({ action: 'decline' }), 'Declined.')), children: "Decline" }), _jsx("button", { type: "button", className: "agent-btn-subtle", disabled: disabled, onClick: () => act(() => callbacks.onElicitationAction(item, JSON.stringify({ action: 'cancel' }), 'Cancelled.')), children: "Cancel" })] }), disabled && item.statusText ? _jsx("div", { className: "agent-decision-status", children: item.statusText }) : null] }));
}
export function DecisionCard({ item, assistantName, callbacks }) {
    if (item.kind === 'mode_transition') {
        return _jsx(ModeTransitionCard, { item: item, callbacks: callbacks });
    }
    if (item.kind === 'elicitation') {
        return _jsx(ElicitationCard, { item: item, callbacks: callbacks });
    }
    return _jsx(PermissionQuestionCard, { item: item, assistantName: assistantName, callbacks: callbacks });
}
