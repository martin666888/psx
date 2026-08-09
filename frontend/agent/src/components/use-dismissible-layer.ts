import { useEffect, type RefObject } from 'react';

export interface DismissibleLayerOptions {
  open: boolean;
  rootRef: RefObject<HTMLElement | null>;
  triggerRef?: RefObject<HTMLElement | null>;
  onDismiss(): void;
}

/**
 * Adds the common non-modal popover dismissal contract to lightweight layers
 * that cannot use the Radix Popover primitive directly.
 *
 * Nested portalled controls (for example the Composer's Select menu) opt in as
 * part of the layer with data-agent-dismissible-branch="true".
 */
export function useDismissibleLayer({
  open,
  rootRef,
  triggerRef,
  onDismiss
}: DismissibleLayerOptions): void {
  useEffect(() => {
    if (!open) return;
    const ownerDocument = rootRef.current?.ownerDocument ?? document;
    const ownerWindow = ownerDocument.defaultView ?? window;

    const onPointerDown = (event: PointerEvent): void => {
      const target = event.target;
      if (!(target instanceof ownerWindow.Node)) return;
      if (rootRef.current?.contains(target)) return;
      if (
        target instanceof ownerWindow.Element &&
        target.closest('[data-agent-dismissible-branch="true"]')
      ) {
        return;
      }
      onDismiss();
    };

    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      onDismiss();
      ownerWindow.requestAnimationFrame(() => triggerRef?.current?.focus());
    };

    ownerDocument.addEventListener('pointerdown', onPointerDown, true);
    ownerDocument.addEventListener('keydown', onKeyDown);
    return () => {
      ownerDocument.removeEventListener('pointerdown', onPointerDown, true);
      ownerDocument.removeEventListener('keydown', onKeyDown);
    };
  }, [onDismiss, open, rootRef, triggerRef]);
}
