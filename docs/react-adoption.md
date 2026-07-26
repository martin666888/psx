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

### Verdict: `PARTIAL_NOT_READY`

The branch is fully green (all three test groups, Fast suite, Release ZIP
smoke), React is default-on, and every migrated island is wired — but the
**Timeline + Decision thread subtree still renders through the legacy
engine in React mode** (Station 3 shipped its projection layer tested but
unwired). Per the plan's hard criteria, any Timeline fallback caps the
verdict at `PARTIAL_NOT_READY`. The branch is safe to keep using; it is
not yet a mainline merge candidate.

### Station outcomes

| Station | Result | Commit(s) |
| --- | --- | --- |
| S0 flag inversion + shared island loader + test matrix | DONE | `cde927f`, `b84ab81`, `d80c3c5` |
| S1 PlanCard island (plan-panel children) | DONE, wired | `a43eab4` |
| S2 History list island (history-content children) | DONE, wired | `3c918fc` |
| S3 Timeline + Decision | **PARTIAL**: `timeline/timelineViewModel.ts` projection (TimelineItem types: message/thinking/tool/system/decision — no plan) committed with 11 unit tests, **not wired**; Timeline routing is 100% legacy in both modes (no mixed ownership) | `c00b130` |
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

### Not migrated (legacy-rendered in react mode)

- **Timeline thread + Decision cards** (largest subtree; projection layer
  ready at `timeline/timelineViewModel.ts`, React tree + one-shot switch
  pending)
- Composer textarea + keyboard/IME + slash command menu + MenuSelect
  popups (planned permanent exclusions for this phase)
- Mode-transition composer prompt (decisions-owned composer region)
- Workspace shell/chrome, terminal UI (out of scope by contract)

### Test matrix results (all green)

- Group A (legacy pinned): harness defaults every test to flag-off; legacy
  path fully protected.
- Group B (React mode): `reactRuntimeCard`, `reactPlanCard` (node-reuse
  evidence), `reactHistoryDock`, `reactSessionToolbar`,
  `reactComposer`, `timelineViewModel` (projection).
- Group C (fallback): `reactUiMode` — injected import failure ⇒ permanent
  legacy fallback, buffered-props rendering, single warning, clean close.
- Totals: **268/268 web tests**, `tools/test.ps1 -Suite Fast` green
  (182 C# + web), `verify:agent` byte-clean (41 files), `typecheck` clean.

### Release ZIP (S7)

- `PSX-1.1.2-win-x64-portable.zip`, 123.1 MB, SHA-256
  `91143a02c0edc5f76b52b528ce585d4928742283e581f35e311faa3779fc0103`.
- Browser smoke over local HTTP against the unpacked ZIP (base href
  rewritten to `/` by `tools/smoke-server.mjs` only for the smoke; all
  other bytes served as packaged): `agentUiMode === 'react'`;
  `import('react')` → 19.2.8 via the import map; `createRoot` available;
  planIsland mounts and renders real DOM; **zero console errors, zero
  warnings, zero 404s across 65 requests**.
- Unpacked `PSX.exe` starts and stays alive (6 s liveness check); deep GUI
  verification is on the manual checklist.

### Known risks / follow-ups

1. S3 wiring is the remaining big rock: TimelineView React tree +
   Decision/elicitation/mode-transition cards + the one-shot routing
   switch, driven by the committed projection. No mixed ownership exists
   today — Timeline is all-legacy until that switch lands.
2. The composer prompt / Decision coordination must move with S3 (the
   active mode-transition prompt occupies the composer region; historical
   snapshots enter the Timeline).
3. `tools/smoke-server.mjs` rewrites the base href for browser smokes;
   WebView2 continues to use `https://psx.local/` unchanged.
4. Mainline merge strategy (unchanged): merge with React default-on, keep
   legacy renderers one release cycle as the emergency fallback, then
   delete legacy + flag.

### Manual acceptance checklist (run before trusting the branch daily)

1. Unpack the Release ZIP, start `PSX.exe`; DevTools console must show
   `[agent] UI mode: React` and no import-map warnings.
2. Create several Agent workspaces; switch, hide, restore, close.
3. Run a long streaming turn (Timeline is legacy-rendered — verify no
   regressions).
4. Exercise tools, stop, a permission decision, and an error recovery.
5. Continue an old thread from History (React list → full open flow).
6. Toggle narrow-window responsive layout; check the Plan card + unread
   dot (React-rendered).
7. Attach images: pill renders, remove works, preview opens
   (React-rendered strip); send with attachment.
8. Restart PSX; verify persisted threads and workspace state.
9. Flip the emergency fallback (`localStorage['psx.agent.experimental.react']='0'`,
   reload): app must run fully legacy; clear it to return to React.
10. Use the branch for a day or two before proposing the S3 switch or any
    mainline merge; run `tools/test.ps1 -Suite Full` before merging.

