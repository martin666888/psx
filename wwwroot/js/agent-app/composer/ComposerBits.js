import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
// ComposerBits.tsx — React twins of the ComposerController's three prop-driven
// subtrees: the attachment strip pills, the attach action row and the command
// hint status area. The textarea, keyboard/IME handling and the MenuSelect
// popups are deliberately excluded legacy regions (independent subtrees).
// DOM mirrors renderPendingAttachments/createAttachmentTile,
// the attach button + hidden file input template row, and showCommandHint.
import { useRef, useState } from 'react';
const GLYPH_PATH = 'M19 5v14H5V5h14zm0-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-4.86 8.86-3 3.87L9 13.14 6 17h12l-3.86-5.14z';
function AttachmentGlyph() {
    return (_jsx("svg", { viewBox: "0 0 24 24", "aria-hidden": "true", className: "agent-attachment-glyph", children: _jsx("path", { d: GLYPH_PATH }) }));
}
function AttachmentTile(props) {
    const { attachment } = props;
    // History-loaded attachments may point at long-gone files: swap in the
    // neutral glyph and disable the preview (legacy image error listener).
    const [broken, setBroken] = useState(!attachment.url);
    const name = attachment.fileName || 'Image attachment';
    return (_jsxs("div", { className: "agent-attachment-shell", children: [_jsxs("button", { type: "button", className: 'agent-attachment-tile agent-attachment-' +
                    (attachment.status || 'ready') +
                    (broken ? ' agent-attachment-broken' : ''), title: broken ? name + ' (image unavailable)' : name, "aria-label": 'Preview ' + name, onClick: () => {
                    if (!broken)
                        props.onPreview(attachment.url);
                }, children: [broken ? (_jsx(AttachmentGlyph, {})) : (_jsx("img", { src: attachment.url, alt: name, onError: () => setBroken(true) })), _jsx("span", { className: "agent-attachment-name", children: attachment.fileName || 'Image' }), attachment.status === 'uploading' ? _jsx("span", { className: "agent-attachment-status", children: "..." }) : null] }), _jsx("button", { type: "button", className: "agent-attachment-remove", "aria-label": 'Remove ' + name, onClick: (event) => {
                    event.stopPropagation();
                    props.onRemove(attachment.clientId);
                }, children: "\u00D7" })] }));
}
export function AttachmentStrip(props) {
    return (_jsx(_Fragment, { children: props.attachments.map((attachment) => (_jsx(AttachmentTile, { attachment: attachment, onPreview: props.onPreview, onRemove: props.onRemove }, attachment.clientId))) }));
}
export function ComposerActions(props) {
    const inputRef = useRef(null);
    return (_jsxs(_Fragment, { children: [_jsx("button", { "data-role": "attach", className: "agent-attach", type: "button", title: props.attachTitle, "aria-label": "Attach images", disabled: props.attachDisabled, onClick: () => {
                    if (props.canPick)
                        inputRef.current?.click();
                }, children: "+" }), _jsx("input", { "data-role": "attachment-input", ref: inputRef, type: "file", accept: "image/*", multiple: true, hidden: true, onChange: () => {
                    const input = inputRef.current;
                    if (!input)
                        return;
                    props.onFiles(Array.from(input.files || []));
                    input.value = '';
                } })] }));
}
export function CommandHint({ text }) {
    return _jsx(_Fragment, { children: text });
}
