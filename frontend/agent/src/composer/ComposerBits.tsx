// ComposerBits.tsx — React twins of the ComposerController's three prop-driven
// subtrees: the attachment strip pills, the attach action row and the command
// hint status area. The textarea, keyboard/IME handling and the MenuSelect
// popups are deliberately excluded legacy regions (independent subtrees).
// DOM mirrors renderPendingAttachments/createAttachmentTile,
// the attach button + hidden file input template row, and showCommandHint.

import { useRef, useState, type JSX } from 'react';
import { PsxPill } from '../ui/Psx.js';

export interface AttachmentTileVM {
  clientId: string;
  fileName: string;
  status: string;
  url: string;
}

export interface AttachmentStripProps {
  attachments: AttachmentTileVM[];
  onPreview(url: string): void;
  onRemove(clientId: string): void;
}

const GLYPH_PATH =
  'M19 5v14H5V5h14zm0-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-4.86 8.86-3 3.87L9 13.14 6 17h12l-3.86-5.14z';

function AttachmentGlyph(): JSX.Element {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" className="agent-attachment-glyph">
      <path d={GLYPH_PATH} />
    </svg>
  );
}

function AttachmentTile(props: {
  attachment: AttachmentTileVM;
  onPreview(url: string): void;
  onRemove(clientId: string): void;
}): JSX.Element {
  const { attachment } = props;
  // History-loaded attachments may point at long-gone files: swap in the
  // neutral glyph and disable the preview (legacy image error listener).
  const [broken, setBroken] = useState(!attachment.url);
  const name = attachment.fileName || 'Image attachment';
  return (
    <PsxPill className="agent-attachment-shell">
      <button
        type="button"
        className={
          'agent-attachment-tile agent-attachment-' +
          (attachment.status || 'ready') +
          (broken ? ' agent-attachment-broken' : '')
        }
        title={broken ? name + ' (image unavailable)' : name}
        aria-label={'Preview ' + name}
        onClick={() => {
          if (!broken) props.onPreview(attachment.url);
        }}
      >
        {broken ? (
          <AttachmentGlyph />
        ) : (
          <img src={attachment.url} alt={name} onError={() => setBroken(true)} />
        )}
        <span className="agent-attachment-name">{attachment.fileName || 'Image'}</span>
        {attachment.status === 'uploading' ? <span className="agent-attachment-status">...</span> : null}
      </button>
      <button
        type="button"
        className="agent-attachment-remove"
        aria-label={'Remove ' + name}
        onClick={(event) => {
          event.stopPropagation();
          props.onRemove(attachment.clientId);
        }}
      >
        ×
      </button>
    </PsxPill>
  );
}

export function AttachmentStrip(props: AttachmentStripProps): JSX.Element {
  return (
    <>
      {props.attachments.map((attachment) => (
        <AttachmentTile
          key={attachment.clientId}
          attachment={attachment}
          onPreview={props.onPreview}
          onRemove={props.onRemove}
        />
      ))}
    </>
  );
}

export interface ComposerActionsProps {
  attachDisabled: boolean;
  attachTitle: string;
  /** Guard mirror of the legacy attach click (busier than disabled alone). */
  canPick: boolean;
  onFiles(files: File[]): void;
}

export function ComposerActions(props: ComposerActionsProps): JSX.Element {
  const inputRef = useRef<HTMLInputElement | null>(null);
  return (
    <>
      <button
        data-role="attach"
        className="agent-attach"
        type="button"
        title={props.attachTitle}
        aria-label="Attach images"
        disabled={props.attachDisabled}
        onClick={() => {
          if (props.canPick) inputRef.current?.click();
        }}
      >
        +
      </button>
      <input
        data-role="attachment-input"
        ref={inputRef}
        type="file"
        accept="image/*"
        multiple
        hidden
        onChange={() => {
          const input = inputRef.current;
          if (!input) return;
          props.onFiles(Array.from(input.files || []));
          input.value = '';
        }}
      />
    </>
  );
}

export interface CommandHintProps {
  text: string;
}

export function CommandHint({ text }: CommandHintProps): JSX.Element {
  return <>{text}</>;
}
