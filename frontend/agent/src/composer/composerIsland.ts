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
import type { IslandFailureReporter, IslandHandle } from '../core/islandHost.js';
import { mountReactIsland } from '../core/reactIsland.js';
import {
  AttachmentStrip,
  CommandHint,
  ComposerActions,
  type AttachmentStripProps,
  type CommandHintProps,
  type ComposerActionsProps
} from './ComposerBits.js';

function mount<TProps extends object>(
  name: string,
  host: HTMLElement,
  reportFailure: IslandFailureReporter,
  component: (props: TProps) => unknown
): IslandHandle<TProps> {
  return mountReactIsland(name, host, reportFailure, (props) =>
    createElement(component as never, props as never)
  );
}

export function mountAttachmentStripIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<AttachmentStripProps> {
  return mount('composer-attachments', host, reportFailure, AttachmentStrip);
}

export function mountComposerActionsIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<ComposerActionsProps> {
  return mount('composer-actions', host, reportFailure, ComposerActions);
}

export function mountCommandHintIsland(
  host: HTMLElement,
  reportFailure: IslandFailureReporter
): IslandHandle<CommandHintProps> {
  return mount('command-hint', host, reportFailure, CommandHint);
}
