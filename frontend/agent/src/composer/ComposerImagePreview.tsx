import { useEffect, useRef, useState, type JSX } from 'react';
import {
  Dialog,
  DialogContent,
  DialogTitle
} from '../components/ui/dialog.js';

export interface ComposerPreviewProps {
  requestToken: number;
  closeToken: number;
  src: string;
}

export function ComposerImagePreview({ preview }: { preview: ComposerPreviewProps }): JSX.Element {
  const [open, setOpen] = useState(false);
  const returnFocusRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (preview.requestToken > 0 && preview.src) {
      returnFocusRef.current =
        document.activeElement instanceof HTMLElement ? document.activeElement : null;
      setOpen(true);
    }
  }, [preview.requestToken, preview.src]);

  useEffect(() => {
    if (preview.closeToken > 0) setOpen(false);
  }, [preview.closeToken]);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogContent
        data-role="image-preview"
        className="max-w-[min(92vw,1200px)] border-0 bg-transparent p-0 shadow-none"
        onCloseAutoFocus={(event) => {
          event.preventDefault();
          const returnTarget = returnFocusRef.current;
          setTimeout(() => returnTarget?.focus(), 0);
        }}
      >
        <DialogTitle className="sr-only">Image preview</DialogTitle>
        <img
          data-role="image-preview-img"
          src={preview.src || undefined}
          alt="Image preview"
          className="max-h-[88vh] w-full rounded-[var(--agent-radius-card)] object-contain shadow-[var(--agent-shadow-dialog)]"
        />
      </DialogContent>
    </Dialog>
  );
}
