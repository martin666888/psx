# React Adoption Decision Record (Stage 0 + Stage 1 Technical Validation)

Status: in progress on branch `dev-react`.

## Decision

Validate introducing a React rendering layer for the Agent Web UI without
changing the tsc-only build model:

- React 19.2.8 vendored as offline ESM bundles under `wwwroot/vendor/react/`
  (generated reproducibly by `npm run vendor:react`, single React instance
  guaranteed by exact-external bundling).
- Bare specifiers resolved by an import map in `wwwroot/index.html`.
- TSX compiled by the existing locked TypeScript 5.9.3 (`jsx: react-jsx`);
  no bundler for application code, compiled output stays committed, and
  `dotnet build` still never runs Node.
- Pilot scope: the runtime install card (`data-role="runtime-card"`) rendered
  by a React island behind the localStorage flag
  `psx.agent.experimental.react` (default off). Flag off means React is never
  fetched or evaluated.
- No @lobehub/ui, no state library, no Psx component library in this stage.
  A bundler decision (precondition for Lobe UI) is deferred to a full
  migration project if this validation passes.

## Baseline (Stage 0)

Captured on branch `dev-react` at commit `de7a704` (dev), 2026-07-26.

### Sizes

| Metric | Value |
| --- | --- |
| Release ZIP (`PSX-1.1.2-win-x64-portable.zip`) | 128,971,052 bytes (123 MB) |
| Baseline ZIP SHA-256 | `420f7cbdcff9c22d4c3c548e243a366880889b6caa96b675fee7bc6407373e20` |
| `wwwroot/js/agent-app` | 27 files, 289,971 bytes |
| `wwwroot/css` | 18 files, 80,865 bytes |
| `wwwroot/vendor` (xterm only) | 7 files, 2,722,554 bytes |

### Test baseline

| Gate | Result |
| --- | --- |
| C# unit + integration (Fast) | 182 passed, 0 failed |
| Web tests (`npm test`) | 224 passed, 0 failed |
| `verify:agent` + `typecheck` + `dotnet format` | clean |

Note: one first-run failure of `broker: queues at most one follow-up when
invalidated mid-request` did not reproduce in two isolated runs and one full
re-run; recorded as a pre-existing timing-sensitive flake unrelated to this
branch.

### Runtime baseline (manual, measure 5x, record median)

Procedure (same for baseline and post-change runs, flag off and on):

1. Cold start: launch PSX, open the first Agent tab; measure from the
   `import('./agent-app/entry.js')` start to module-ready using temporary
   `performance.mark` instrumentation (never committed).
2. First workspace creation and a subsequent hot workspace creation.
3. Memory: sum of the PSX process plus all associated WebView2 child
   processes (`msedgewebview2.exe`), before and after opening one Agent
   workspace.
4. Streaming: one long streaming reply with the DevTools Performance panel;
   record long task count, dropped frames, and peak CPU.

| Metric | Baseline | Flag off | Flag on |
| --- | --- | --- | --- |
| Agent module cold start | TBD | TBD | TBD |
| First workspace creation | TBD | TBD | TBD |
| Hot workspace creation | TBD | TBD | TBD |
| Memory delta (PSX + WebView2) | TBD | TBD | TBD |
| Streaming long tasks / dropped frames / peak CPU | TBD | TBD | TBD |

## Acceptance gates for full migration

- Flag off: zero React loading (no vendor/react requests), zero behavior
  change, all existing tests green.
- Flag on: runtime-card DOM/visual/interaction/accessibility equivalence;
  a single React instance on the page; no leftover roots or listeners after
  workspace close.
- Vendor files fully offline, self-contained, traceable via
  `vendor-manifest.json`.
- ZIP growth < 5 MB; Agent open delta <= 150 ms; memory delta <= 30 MB.
- Developer experience verdict favors the React island over the imperative
  controller for equivalent work.

## Results

To be filled at the end of Stage 1.
