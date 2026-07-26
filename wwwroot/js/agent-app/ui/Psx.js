import { jsx as _jsx } from "react/jsx-runtime";
const BUTTON_VARIANT_CLASS = {
    primary: 'agent-btn-primary',
    subtle: 'agent-btn-subtle',
    unstyled: ''
};
/**
 * Button primitive: defaults type="button" and maps variants onto the
 * existing agent-btn-* classes; extra classes append verbatim.
 */
export function PsxButton({ variant = 'unstyled', className, type, ...rest }) {
    const classes = [BUTTON_VARIANT_CLASS[variant], className].filter(Boolean).join(' ');
    return _jsx("button", { type: type ?? 'button', className: classes || undefined, ...rest });
}
/** Card primitive: a section landmark carrying the caller's agent-* card class. */
export function PsxCard(props) {
    return _jsx("section", { ...props });
}
/** Tag primitive: a small inline status label (e.g. the History Current/Open badges). */
export function PsxTag(props) {
    return _jsx("span", { ...props });
}
/** Pill primitive: the composite chip shell (e.g. composer attachment tiles). */
export function PsxPill(props) {
    return _jsx("div", { ...props });
}
