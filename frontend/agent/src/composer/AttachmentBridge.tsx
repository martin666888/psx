import { useLayoutEffect, useRef, type JSX } from 'react';
import {
  usePromptInputAttachments,
  type FileUIPart
} from '../components/ai-elements/prompt-input.js';

export interface AttachmentSnapshotItem extends FileUIPart {
  id: string;
}

export interface AttachmentRestoreItem {
  retryKey: string;
  file: File;
  serverAttachmentId?: string;
}

export interface PreparedAttachment {
  clientId: string;
  retryKey: string;
  file: File;
  previewUrl: string;
  serverAttachmentId?: string;
}

export interface AttachmentBridgeProps {
  readOnly: boolean;
  resetToken: number;
  restoreToken: number;
  restoreItems: AttachmentRestoreItem[];
  onSnapshot(items: AttachmentSnapshotItem[]): void;
  onPrepared(item: PreparedAttachment): void;
  onRemoved(clientId: string): void;
  onError(message: string): void;
}

interface AttachmentTask {
  abort: AbortController;
  retry?: AttachmentRestoreItem;
}

const ALLOWED_IMAGE_TYPES = new Set([
  'image/png',
  'image/jpeg',
  'image/webp',
  'image/gif'
]);
const MAX_TOTAL_SIZE = 50 * 1024 * 1024;

/**
 * Adapts PromptInput's local attachment context to the controller upload
 * protocol. PromptInput owns blob URL lifecycle; the controller owns uploads
 * and server attachment IDs.
 */
export function AttachmentBridge(props: AttachmentBridgeProps): JSX.Element | null {
  const attachments = usePromptInputAttachments();
  const tasksRef = useRef(new Map<string, AttachmentTask>());
  const acceptedSizesRef = useRef(new Map<string, number>());
  const restoreQueueRef = useRef<AttachmentRestoreItem[]>([]);
  const callbacksRef = useRef(props);
  const appliedResetTokenRef = useRef(props.resetToken);
  const appliedRestoreTokenRef = useRef(props.restoreToken);
  const suppressSnapshotRef = useRef(false);
  callbacksRef.current = props;

  useLayoutEffect(() => {
    suppressSnapshotRef.current = false;

    if (appliedResetTokenRef.current !== props.resetToken) {
      appliedResetTokenRef.current = props.resetToken;
      suppressSnapshotRef.current = true;
      restoreQueueRef.current = [];
      for (const task of tasksRef.current.values()) task.abort.abort();
      tasksRef.current.clear();
      acceptedSizesRef.current.clear();
      attachments.clear();
      queueMicrotask(() => callbacksRef.current.onSnapshot([]));
    }

    if (appliedRestoreTokenRef.current !== props.restoreToken) {
      appliedRestoreTokenRef.current = props.restoreToken;
      restoreQueueRef.current = props.restoreItems.slice();
      attachments.add(props.restoreItems.map((item) => item.file));
    }
  }, [
    attachments,
    props.resetToken,
    props.restoreItems,
    props.restoreToken
  ]);

  useLayoutEffect(() => {
    if (suppressSnapshotRef.current) return;

    const snapshot = attachments.files.map((item) => ({ ...item }));
    const currentIds = new Set(snapshot.map((item) => item.id));

    for (const [clientId, task] of tasksRef.current) {
      if (currentIds.has(clientId)) continue;
      task.abort.abort();
      tasksRef.current.delete(clientId);
      acceptedSizesRef.current.delete(clientId);
      queueMicrotask(() => callbacksRef.current.onRemoved(clientId));
    }

    queueMicrotask(() => callbacksRef.current.onSnapshot(snapshot));

    for (const item of snapshot) {
      if (tasksRef.current.has(item.id)) continue;

      const retry = restoreQueueRef.current.shift();
      const abort = new AbortController();
      const task: AttachmentTask = { abort, retry };
      tasksRef.current.set(item.id, task);

      if (props.readOnly) {
        attachments.remove(item.id);
        continue;
      }

      void prepareAttachment(item, task).then((prepared) => {
        if (!prepared || abort.signal.aborted || tasksRef.current.get(item.id) !== task) return;
        callbacksRef.current.onPrepared(prepared);
      });
    }

    async function prepareAttachment(
      item: AttachmentSnapshotItem,
      task: AttachmentTask
    ): Promise<PreparedAttachment | null> {
      try {
        const sourceFile = task.retry?.file;
        let blob: Blob;
        if (sourceFile) {
          blob = sourceFile;
        } else {
          const response = await fetch(item.url, { signal: task.abort.signal });
          blob = await response.blob();
        }
        if (task.abort.signal.aborted) return null;

        const mediaType = item.mediaType || blob.type;
        const filename = item.filename || sourceFile?.name || 'image';
        if (!ALLOWED_IMAGE_TYPES.has(mediaType)) {
          callbacksRef.current.onError('Only PNG, JPEG, WebP, and GIF images are supported.');
          attachments.remove(item.id);
          return null;
        }

        const currentTotal = Array.from(acceptedSizesRef.current.values())
          .reduce((sum, size) => sum + size, 0);
        if (currentTotal + blob.size > MAX_TOTAL_SIZE) {
          callbacksRef.current.onError('Images in one message must total 50MB or less.');
          attachments.remove(item.id);
          return null;
        }

        acceptedSizesRef.current.set(item.id, blob.size);
        const file = sourceFile ?? new File([blob], filename, {
          type: mediaType,
          lastModified: Date.now()
        });
        return {
          clientId: item.id,
          retryKey: task.retry?.retryKey ?? crypto.randomUUID(),
          file,
          previewUrl: item.url,
          serverAttachmentId: task.retry?.serverAttachmentId
        };
      } catch {
        if (!task.abort.signal.aborted) attachments.remove(item.id);
        return null;
      }
    }
  }, [attachments, props.readOnly]);

  useLayoutEffect(() => () => {
    for (const task of tasksRef.current.values()) task.abort.abort();
    tasksRef.current.clear();
    acceptedSizesRef.current.clear();
  }, []);

  return null;
}
