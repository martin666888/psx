// portalContainer.ts — Radix portals default to document.body, which sits
// outside the .agent-ui variable boundary (CP2 contract: only the Agent UI
// container carries the shadcn variable layer). WorkspaceHost registers its
// container here so every portal (tooltip, select, dialog, ...) mounts inside
// the boundary and keeps resolving the themed CSS variables.

let container: HTMLElement | null = null;

export function setPortalContainer(el: HTMLElement | null): void {
  container = el;
}

/** Portal target inside .agent-ui; undefined falls back to document.body. */
export function getPortalContainer(): HTMLElement | undefined {
  return container ?? undefined;
}
