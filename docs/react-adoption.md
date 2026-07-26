# React Adoption Decision Record (Stage 0 + Stage 1 Technical Validation)

Status: Stage 1 validation complete on branch `dev-react`; all acceptance
gates measured in this record pass. See Results below.

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

The in-app WPF + WebView2 runtime numbers above remain a manual checklist to
run before merging `dev-react` into the mainline (temporary
`performance.mark` instrumentation, never committed). The Stage 1 proxy
measurements below (Chromium, same engine family as WebView2, committed
vendor bytes served over localhost, cache disabled, 5 runs, median) already
bound the flag-on cost far below the acceptance gates, and flag-off cost is
structurally zero (no React request is ever issued).

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

Captured on branch `dev-react` at commit `384a12a`, 2026-07-27. Stage 1
scope shipped: root npm workspace, reproducible vendor pipeline
(`npm run vendor:react`, esbuild core + facades, manifest with SHA-256 and
per-file import surface), import map, TSX via the locked tsc, the
flag-gated runtime-card island, tests, and release packaging.

### Size comparison

| Metric | Baseline (`de7a704`) | Stage 1 (`384a12a`) | Delta | Gate |
| --- | --- | --- | --- | --- |
| Release ZIP | 128,971,052 bytes | 129,038,664 bytes | +67,612 bytes (+66 KB) | < 5 MB: PASS |
| `wwwroot/js/agent-app` | 27 files, 289,971 bytes | 30 files, 297,530 bytes | +3 files, +7,559 bytes | — |
| `wwwroot/vendor/react` | — | 8 files, ~196 KB (core 192,496 bytes + 4 facades + manifest + README + LICENSE) | new | offline, manifest-pinned: PASS |

Stage 1 ZIP SHA-256:
`2f993e92d293decad890e8d3b2fdbb395c3a925d225ab6958dbb14e3f5fafbc7`.
`build-release.ps1` validates all five vendor bundles, the manifest and
`licenses/react/LICENSE.txt` as required files; the forbidden-file scan
still reports no `.ts`/`.map` under `wwwroot/js/agent-app`.

### Runtime cost (flag on, Chromium proxy, cache disabled, 5 runs, median)

| Metric | Median (min–max) | Gate |
| --- | --- | --- |
| `import('react')` + `import('react-dom/client')` via import map | 18.6 ms (16.9–22.3) | ≤ 150 ms Agent-open delta: PASS |
| First island mount commit (`createRoot` + render + `useLayoutEffect`) | 4.1 ms (3.0–6.2) | included above |
| Warm re-mounts in the same page | 0.5–1.1 ms | — |
| Total JS heap after load + mount (whole page) | 5.25 MB | ≤ 30 MB memory delta: PASS |

Flag off adds zero cost by construction: `SessionRuntimeController` holds no
static import of the island (type-only import is erased) and only executes
`import('./runtimeIsland.js')` after the flag reads true, which the
`vendorReact.test.js` zero-load guard pins against the committed output.
The earlier WebView2-oriented smoke (Chromium via import map) confirmed all
six requests stay on localhost and `react-core.js` loads exactly once
(single instance).

In-app manual verification (WPF + WebView2, 2026-07-27): the user ran PSX
through normal Agent usage in the default flag-off mode and observed no
behavior change. The flag-on in-app pass stays on the pre-merge checklist.

### Gate-by-gate verdict

- Flag off — zero React loading, zero behavior change: PASS (static guard
  over every compiled module; 236/236 web tests and the Fast suite green).
- Flag on — runtime-card equivalence: PASS (DOM snapshot equality across
  all five runtime states against the legacy renderer, install/cancel
  bridge commands byte-identical, first-event gating, single
  `data-role="runtime-card"`, clean unmount on workspace close; covered by
  `reactRuntimeCard.test.js`).
- Vendor offline / self-contained / traceable: PASS (manifest SHA-256 +
  import-surface guards; facades import cleanly in plain Node with no
  network or bundler).
- ZIP growth < 5 MB: PASS (+66 KB).
- Agent open delta ≤ 150 ms: PASS by proxy (≈ 23 ms load + first mount);
  confirm in-app before mainline merge using the manual checklist above.
- Memory delta ≤ 30 MB: PASS by proxy (≈ 5 MB whole-page heap ceiling);
  same in-app confirmation applies.
- Developer experience: the React card replaces ~60 lines of imperative
  DOM mutation with a declarative component whose props mirror
  `WorkspaceRuntimeState`; state-to-DOM drift is no longer possible and
  the act-based tests assert whole-card snapshots instead of field pokes.
  Verdict: favors the island for equivalent work.

### Decision

Proceed: the Stage 1 gates pass, so a full migration project may be
proposed. When that project starts:

- Re-evaluate a bundler (precondition for @lobehub/ui); tsc-only + import
  map is sufficient for islands but not for a component-library stack.
- Update the AGENTS.md frontend constraints and retire the historical
  "do not introduce React" guidance to describe the vendored-React island
  model instead.
- Keep React pinned at 19.2.8; any upgrade must rerun
  `npm run vendor:react` and commit the regenerated bundles + manifest
  (the hash guard fails otherwise).
- The `psx.agent.experimental.react` flag stays a temporary, undocumented
  switch; flipping the default is a mainline decision, not part of this
  branch.

If the mainline decision is instead to abandon the experiment, delete the
`dev-react` branch; the mainline carries no residue.

## Full-migration night run (2026-07-27, dev-react)

Overnight execution of the approved "PSX Agent React 全面迁移" plan
(Stations 0–8). React rendering is now the **default** on this branch;
`psx.agent.experimental.react` = `'0'`/`'false'`/`'off'` is the emergency
legacy fallback. Startup logs `[agent] UI mode: React|Legacy` and mirrors it
on `document.body.dataset.agentUiMode`.

### Verdict: `READY_FOR_DOGFOOD`

Stations 0–5 are complete and **wired in React mode** (S6 optional,
skipped), the Release ZIP was rebuilt after the Timeline switch and its
browser smoke is fully green, and all three test groups plus the Fast
suite pass. The Timeline + Decision thread subtree — the largest and last
holdout — now renders through a single React root in react mode
(`15b8bdf`), with the legacy engine kept only as the emergency fallback
path. The planned Composer exclusions (textarea/IME/MenuSelect) remain
legacy by design and are listed below. Run the manual checklist before
trusting the branch daily; run `tools/test.ps1 -Suite Full` before any
mainline merge.

### Station outcomes

| Station | Result | Commit(s) |
| --- | --- | --- |
| S0 flag inversion + shared island loader + test matrix | DONE | `cde927f`, `b84ab81`, `d80c3c5` |
| S1 PlanCard island (plan-panel children) | DONE, wired | `a43eab4` |
| S2 History list island (history-content children) | DONE, wired | `3c918fc` |
| S3 Timeline + Decision | DONE, wired: `timelineViewModel.ts` projection (TimelineItem types: message/thinking/tool/system/decision — no plan) + `TimelineView`/`TimelineDecisions` React tree (incl. full elicitation forms) + one-shot routing switch; DecisionController keeps only the composer-region prompt in react mode | `c00b130`, `af6e087`, `3ad9f18`, `15b8bdf`, `8afd9ea`, `ca1e71b` |
| S4 Composer attachments strip + actions row + command hint islands | DONE, wired (textarea/IME/MenuSelect are planned legacy exclusions) | `d8e7ed1` |
| S5 session toolbar meta line + Context ring islands | DONE, wired | `6ebe3b2` |
| S6 Psx base components + tokens (optional) | NOT STARTED (does not affect readiness) | — |
| S7 Release ZIP + HTTP smoke | DONE — and it caught a real production bug (below) | `6a2dd5b` |
| S8 verdict + report | DONE (this section) | — |

### Production bug found by the ZIP smoke (S7)

The import map in `wwwroot/index.html` used addresses like
`vendor/react/react.js` **without a `./` prefix**. Per the import-map spec
every browser (including WebView2) silently ignores such addresses
("Ignored an import map value … Bare specifier"), so **every React island
would have permanently fallen back to legacy in the shipped app** — the
exact failure mode the fallback path was designed to absorb, which is why
it went unnoticed. Fixed in `6a2dd5b` (`./vendor/react/…`), guarded by a
new `vendorReact.test.js` test that pins address validity and file
existence, and re-verified in a real browser against the rebuilt ZIP.

### Migrated (React-owned subtrees in react mode)

- Runtime install card (`runtimeIsland`) — since Stage 1
- Plan context card content (`planIsland`) — Plan is a Workspace context
  card, never Timeline chat content
- History dock thread list (`historyIsland`)
- Session toolbar meta line + Context usage ring (`sessionIsland`, two
  roots)
- Composer attachment pills, attach action row, command hint
  (`composerIsland`, three roots)
- **Timeline thread + Decision/Permission/Question/Mode-Transition/
  Elicitation cards** (`timelineIsland`, one root owning the whole
  `data-role="thread"` subtree; events fold into the controller-local
  `TimelineProjection`)

### Not migrated (legacy-rendered in react mode)

- Composer textarea + keyboard/IME + slash command menu + MenuSelect
  popups (planned permanent exclusions for this phase; do not affect
  readiness per plan)
- Mode-transition composer prompt region (Decision-owned prompt buttons in
  the composer area; the Timeline card itself is React)
- Workspace shell/chrome, narrow-mode visibility rules (controller-owned,
  identical code path in both modes), terminal UI (out of scope by
  contract)

### Test matrix results (all green)

- Group A (legacy pinned): harness defaults every test to flag-off; legacy
  path fully protected.
- Group B (React mode): `reactRuntimeCard`, `reactPlanCard` (node-reuse
  evidence), `reactHistoryDock`, `reactSessionToolbar`, `reactComposer`,
  `timelineViewModel` (projection), `timelineReactTree` (structural DOM
  equivalence vs legacy: streaming, replay, run_failed,
  permission/question states, elicitation forms + validation, node
  identity), `reactTimelineApp` (full-app react mode: streaming/replay
  thread equivalence, agent_cleared, byte-identical permission and
  mode-transition responses, workspace switch/hide/restore with node
  identity, mid-stream close teardown).
- Group C (fallback): `reactUiMode` — injected import failure ⇒ permanent
  legacy fallback, buffered-props rendering, single warning, clean close.
- Totals: **284/284 web tests**, `tools/test.ps1 -Suite Fast` green
  (182 C# + web) re-run after the Timeline switch, `verify:agent`
  byte-clean, `typecheck` clean, no unhandled console errors/rejections
  (act-environment enforced).

### Release ZIP (S7, rebuilt after the Timeline switch)

- `PSX-1.1.2-win-x64-portable.zip`, 123.1 MB, SHA-256
  `eafc138eb2fae617f5f0846fdd2c6d2d7581ea7a8c6f70710163393960f29513`.
- Browser smoke over local HTTP against the unpacked ZIP (base href
  rewritten to `/` by `tools/smoke-server.mjs` only for the smoke; all
  other bytes served as packaged): `agentUiMode === 'react'`;
  `import('react')` → 19.2.8 via the import map; planIsland mounts and
  renders real DOM; **zero console errors, zero warnings, zero 404s
  across 64 requests**.
- Unpacked `PSX.exe` starts and stays alive (6 s liveness check); deep GUI
  verification is on the manual checklist.

### Known risks / follow-ups

1. If the timeline island's dynamic import fails at runtime, events that
   already folded into the projection cannot be replayed by the legacy
   engine — only subsequent events render legacy. The island is preheated
   at mount so the failure window is effectively an empty thread; Group C
   covers the loader contract on the runtime card path.
2. Known cosmetic deviation: after clicking an option in the composer
   mode-transition prompt, the React Timeline card shows the sending
   state only when `permission_resolved` arrives (legacy flipped its card
   to `sending` immediately). Command payloads are byte-identical.
3. `tools/smoke-server.mjs` rewrites the base href for browser smokes;
   WebView2 continues to use `https://psx.local/` unchanged.
4. Mainline merge strategy (unchanged): merge with React default-on, keep
   legacy renderers one release cycle as the emergency fallback, then
   delete legacy + flag. Run `tools/test.ps1 -Suite Full` before merging.

### Manual acceptance checklist (run before trusting the branch daily)

1. Unpack the Release ZIP, start `PSX.exe`; DevTools console must show
   `[agent] UI mode: React` and no import-map warnings.
2. Create several Agent workspaces; switch, hide, restore, close.
3. Run a long streaming turn (Timeline is **React-rendered** now — watch
   thinking rows, tool groups, markdown, auto-scroll and the copy
   buttons).
4. Exercise tools, stop, a permission decision, and an error recovery.
5. Continue an old thread from History (React list → full open flow).
6. Toggle narrow-window responsive layout; check the Plan card + unread
   dot (React-rendered).
7. Attach images: pill renders, remove works, preview opens
   (React-rendered strip); send with attachment.
8. Restart PSX; verify persisted threads and workspace state.
9. Flip the emergency fallback (`localStorage['psx.agent.experimental.react']='0'`,
   reload): app must run fully legacy; clear it to return to React.
10. Use the branch for a day or two; run `tools/test.ps1 -Suite Full`
    before proposing any mainline merge.

