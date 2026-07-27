// Psx.tsx — the Psx base component layer (Station 6 groundwork).
//
// Thin wrappers that emit the existing agent-* class vocabulary unchanged
// (zero visual delta; the legacy DOM-equivalence suites are the proof). They
// give the migrated islands one shared seam where a future design-system
// reskin (tokens live in frontend/webview/src/css/agent/tokens.css) plugs in without
// touching feature code.

import type { ButtonHTMLAttributes, HTMLAttributes, JSX } from 'react';

type DataAttributes = { [attribute: `data-${string}`]: string | undefined };

export type PsxButtonVariant = 'primary' | 'subtle' | 'unstyled';

const BUTTON_VARIANT_CLASS: Readonly<Record<PsxButtonVariant, string>> = {
  primary: 'agent-btn-primary',
  subtle: 'agent-btn-subtle',
  unstyled: ''
};

export interface PsxButtonProps extends ButtonHTMLAttributes<HTMLButtonElement>, DataAttributes {
  variant?: PsxButtonVariant;
}

/**
 * Button primitive: defaults type="button" and maps variants onto the
 * existing agent-btn-* classes; extra classes append verbatim.
 */
export function PsxButton({ variant = 'unstyled', className, type, ...rest }: PsxButtonProps): JSX.Element {
  const classes = [BUTTON_VARIANT_CLASS[variant], className].filter(Boolean).join(' ');
  return <button type={type ?? 'button'} className={classes || undefined} {...rest} />;
}

export interface PsxCardProps extends HTMLAttributes<HTMLElement>, DataAttributes {}

/** Card primitive: a section landmark carrying the caller's agent-* card class. */
export function PsxCard(props: PsxCardProps): JSX.Element {
  return <section {...props} />;
}

export interface PsxTagProps extends HTMLAttributes<HTMLSpanElement>, DataAttributes {}

/** Tag primitive: a small inline status label (e.g. the History Current/Open badges). */
export function PsxTag(props: PsxTagProps): JSX.Element {
  return <span {...props} />;
}

export interface PsxPillProps extends HTMLAttributes<HTMLDivElement>, DataAttributes {}

/** Pill primitive: the composite chip shell (e.g. composer attachment tiles). */
export function PsxPill(props: PsxPillProps): JSX.Element {
  return <div {...props} />;
}
