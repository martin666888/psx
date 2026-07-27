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
import { mountReactIsland } from '../core/reactIsland.js';
import { AttachmentStrip, CommandHint, ComposerActions } from './ComposerBits.js';
function mount(name, host, reportFailure, component) {
    return mountReactIsland(name, host, reportFailure, (props) => createElement(component, props));
}
export function mountAttachmentStripIsland(host, reportFailure) {
    return mount('composer-attachments', host, reportFailure, AttachmentStrip);
}
export function mountComposerActionsIsland(host, reportFailure) {
    return mount('composer-actions', host, reportFailure, ComposerActions);
}
export function mountCommandHintIsland(host, reportFailure) {
    return mount('command-hint', host, reportFailure, CommandHint);
}
