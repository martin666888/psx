// Psx.tsx — the Psx base component layer (Station 6 groundwork).
//
// Thin wrappers that emit the existing agent-* class vocabulary unchanged
// (zero visual delta; the legacy DOM-equivalence suites are the proof). CP4
// replaces these with shadcn primitives region by region; only the pieces
// still referenced by unswapped regions remain.

import type { HTMLAttributes, JSX } from 'react';

type DataAttributes = { [attribute: `data-${string}`]: string | undefined };

export interface PsxPillProps extends HTMLAttributes<HTMLDivElement>, DataAttributes {}

/** Pill primitive: the composite chip shell (e.g. composer attachment tiles). */
export function PsxPill(props: PsxPillProps): JSX.Element {
  return <div {...props} />;
}

