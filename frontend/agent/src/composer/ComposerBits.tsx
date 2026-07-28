// ComposerBits.tsx — PromptInput-backed attachment chrome plus the command
// hint status area. Upload and server-ID state remain controller-owned through
// AttachmentBridge; PromptInput is the sole owner of the visible file list.

import { PaperclipIcon } from 'lucide-react';
import type { JSX } from 'react';
import {
  PromptInputAttachment,
  PromptInputAttachments,
  PromptInputButton,
  usePromptInputAttachments
} from '../components/ai-elements/prompt-input.js';

export interface AttachmentStatusVM {
  clientId: string;
  status: string;
}

export interface AttachmentStripProps {
  attachments: AttachmentStatusVM[];
  readOnly: boolean;
  onPreview(url: string): void;
}

export function AttachmentStrip(props: AttachmentStripProps): JSX.Element {
  const statuses = new Map(
    props.attachments.map((attachment) => [attachment.clientId, attachment.status])
  );
  return (
    <PromptInputAttachments className="agent-attachments-strip p-0">
      {(attachment) => {
        const status = statuses.get(attachment.id) ?? 'uploading';
        return (
          <PromptInputAttachment
            data={attachment}
            data-status={status}
            removable={!props.readOnly}
            showPreviewCard={false}
            className={
              'agent-attachment-' + status +
              (status === 'failed' ? ' border-destructive text-destructive' : '') +
              (status === 'uploading' ? ' opacity-70' : '')
            }
            aria-label={'Preview ' + (attachment.filename || 'image attachment')}
            role="button"
            tabIndex={0}
            title={
              (attachment.filename || 'Image attachment') +
              (status === 'uploading' ? ' (uploading)' : status === 'failed' ? ' (upload failed)' : '')
            }
            onClick={() => props.onPreview(attachment.url)}
            onKeyDown={(event) => {
              if (event.key !== 'Enter' && event.key !== ' ') return;
              event.preventDefault();
              props.onPreview(attachment.url);
            }}
          />
        );
      }}
    </PromptInputAttachments>
  );
}

export interface ComposerActionsProps {
  attachDisabled: boolean;
  attachTitle: string;
  /** Guard mirror of the legacy attach click (busier than disabled alone). */
  canPick: boolean;
}

export function ComposerActions(props: ComposerActionsProps): JSX.Element {
  const attachments = usePromptInputAttachments();
  return (
    <PromptInputButton
      data-role="attach"
      className="size-8"
      type="button"
      title={props.attachTitle}
      aria-label="Attach images"
      disabled={props.attachDisabled}
      onClick={() => {
        if (props.canPick) attachments.openFileDialog();
      }}
    >
      <PaperclipIcon className="size-4" />
    </PromptInputButton>
  );
}

export interface CommandHintProps {
  text: string;
  visible: boolean;
}

export function CommandHint({ text }: CommandHintProps): JSX.Element {
  return <>{text}</>;
}
