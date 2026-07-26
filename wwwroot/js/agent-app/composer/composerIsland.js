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
import { AttachmentStrip, CommandHint, ComposerActions } from './ComposerBits.js';
function mount(host, component) {
    const root = createRoot(host);
    return {
        render(props) {
            root.render(createElement(component, props));
        },
        dispose() {
            root.unmount();
        }
    };
}
export function mountAttachmentStripIsland(host) {
    return mount(host, AttachmentStrip);
}
export function mountComposerActionsIsland(host) {
    return mount(host, ComposerActions);
}
export function mountCommandHintIsland(host) {
    return mount(host, CommandHint);
}
