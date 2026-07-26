// composerIsland.ts — mounts the React composer islands.
//
// Three independent roots inside the composer: the attachment strip (host:
// [data-role="attachments-strip"]), the attach action row (host:
// .agent-composer-actions) and the command hint status area (host:
// [data-role="command-hint"]). Static React imports are confined to island
// modules; ComposerController reaches this file only through a dynamic
// import. Host visibility attributes (strip.hidden / hint.hidden) stay
// controller-owned — React owns only the children.

import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import {
  AttachmentStrip,
  CommandHint,
  ComposerActions,
  type AttachmentStripProps,
  type CommandHintProps,
  type ComposerActionsProps
} from './ComposerBits.js';

export interface ComposerIslandHandle<TProps> {
  render(props: TProps): void;
  dispose(): void;
}

function mount<TProps extends object>(
  host: HTMLElement,
  component: (props: TProps) => unknown
): ComposerIslandHandle<TProps> {
  const root = createRoot(host);
  return {
    render(props: TProps): void {
      root.render(createElement(component as never, props as never));
    },
    dispose(): void {
      root.unmount();
    }
  };
}

export function mountAttachmentStripIsland(host: HTMLElement): ComposerIslandHandle<AttachmentStripProps> {
  return mount(host, AttachmentStrip);
}

export function mountComposerActionsIsland(host: HTMLElement): ComposerIslandHandle<ComposerActionsProps> {
  return mount(host, ComposerActions);
}

export function mountCommandHintIsland(host: HTMLElement): ComposerIslandHandle<CommandHintProps> {
  return mount(host, CommandHint);
}
