// sessionIsland.ts — mounts the React session toolbar islands.
//
// Two independent roots: the toolbar meta line (host: .agent-meta) and the
// Context usage ring (host: .agent-hints). Static React imports are confined
// to island modules; SessionRuntimeController reaches this file only through
// a dynamic import. createRoot takes ownership of each host's children — the
// template-rendered defaults are replaced atomically by the first commit —
// and dispose unmounts while leaving the host nodes to the panel lifecycle.
import { createElement } from 'react';
import { mountReactIsland } from '../core/reactIsland.js';
import { ContextUsage, SessionMeta } from './SessionToolbar.js';
export function mountSessionMetaIsland(host, reportFailure) {
    return mountReactIsland('session-meta', host, reportFailure, (props) => createElement(SessionMeta, props));
}
export function mountContextUsageIsland(host, reportFailure) {
    return mountReactIsland('context-usage', host, reportFailure, (props) => createElement(ContextUsage, props));
}
