# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## What this project is

PSX is a Windows desktop terminal shell (C# + WPF + WebView2 + xterm.js + ConPTY) for running AI Agent CLIs and dev tools. Two coexisting views share one window: a `Terminal` view (real ConPTY shells via xterm.js) and an `Agent` view backed by the Agent Client Protocol. Agent mode uses a curated provider architecture; Claude Code (default), Kimi Code, Qwen Code, Qoder CLI, and OpenCode are the registered production providers. Runtime updates are strictly user-triggered from the Agent toolbar (startup only promotes an already-staged update locally, never touching the network); Claude Code updates through the `runtime/acp-current`/`acp-next` staging model, Qoder CLI through `runtime/qoder-current`/`qoder-next` after user-confirmed install, and Kimi Code / Qwen Code / OpenCode through `runtime/*-current`/`*-next` with the bundled `tools/*` baseline as fallback. Multi-tab, themeable, persistent threads.

Project-level interface rules live in `.interface-design/system.md`. Read and follow them before changing WPF or WebView2 UI.

---

## Runtime data & paths

Two distinct path roots — don't confuse them:

- **User data** lives in `%USERPROFILE%\.psx\` (NOT `%APPDATA%`; defined in `AgentThreadStore` constructor):
  - `config.json` — last thread id / working directory / restore flag
  - `agent/threads/<threadId>.json` — full message history per Agent session
  - `agent/attachments/<threadId>/` — image attachments (binary + `.json` metadata)
  - `agent/index.json` — thread index (capped at 100 entries)
  - `agent/acp-logs/` — per-run NDJSON of JSON-RPC traffic + `acp-runtime.log`
- **Install-relative** (next to `PSX.exe`, resolved by `RuntimeLocator`):
  - `psx.ini` — **active** app config (theme/font/shell). Note: NOT in `.psx\`
  - `theme-presets/` — built-in themes exposed by the global Theme picker
  - `tools/node/` — bundled portable Node.js
  - `tools/acp-seed/` — read-only seed manifest for the user-confirmed `npm ci`
  - `runtime/acp-current/` — live ACP adapter + bundled `claude.exe` (what Agent mode loads)
  - `runtime/acp-next/` — staging dir for background updates (see below)
  - `runtime/webview2-fixed/` — optional WebView2 Fixed Version runtime

**Terminal sessions are NOT persisted** — closing the app loses them. Only Agent threads survive (in `.psx/agent/threads/`).

---

## Build & run

```bash
# Build
dotnet build PSX.slnx

# Run from source
dotnet run --project PSX.csproj

# Publish a self-contained x64 build. The real release pipeline is
# tools/build-release.ps1: it runs dotnet publish, downloads and verifies
# Portable Node, includes the ACP seed, and optionally bundles WebView2.
# It deliberately does not include an installed ACP runtime or claude.exe.
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1

# On any build, Agent mode shows an installation card when the runtime is
# missing. Only explicit user confirmation runs npm ci in runtime/acp-current/.
```

**Automated tests are repository-local** — `tests/PSX.Tests` covers C# unit/integration/desktop behavior, `tests/PSX.Web.Tests` covers production frontend and bridge contracts, and the Fake Agent/npm/Desktop Probe projects cover process boundaries. Runner minimums are Unit 390, Integration 112, Desktop 2, Fast 502, Full 504, and Web 317 (including expanded `DataRow`s). Fast enforces C# 67% line / 55% branch and Web 78% line / 75% statement / 75% function / 65% branch coverage, but never launches a browser. Full adds release smoke, locked-Chromium visual/axe checks, and official dependency audits. Use `tools/test.ps1 -Suite Fast` during development and `-Suite Full` before release; tests must never access the user's real `%USERPROFILE%\.psx`.

**Test commands must run sequentially.** Never run typecheck, lint, Vitest, frontend build/verification, or `tools/test.ps1` concurrently. C# tests intentionally use `[assembly: DoNotParallelize]` in `tests/PSX.Tests/MSTestSettings.cs`; keep that assembly-level guard. The Web tests intentionally set `fileParallelism: false` and `maxWorkers: 1` because the jsdom/React harness mutates process-wide browser globals. The full Web gate uses `tools/run-web-tests.ps1` to give every test file its own guarded process tree, waits for it to exit before starting the next file, and lets Vitest's blob reporter merge the final test/coverage reports. Parallel workers have caused memory exhaustion, orphaned processes, and workstation freezes. Keep both test stacks single-worker. After a failed or interrupted Web test, check that no test-owned `node` processes remain before starting another gate.

**SDK pinning** — `global.json` pins the .NET 10 `10.0.300` feature band with `rollForward: latestFeature`; CI asserts the same feature band before running tests. Target framework is `net10.0-windows`. Theme colors and fonts can be previewed and confirmed at runtime; other manual `psx.ini` changes still require a restart.

---

## Architecture (read this before touching anything)

The codebase has 4 large subsystems that look independent but are tightly coupled through events:

### 1. The two bridges share one WebView2

`Controls/TerminalHost.cs` is a code-behind `UserControl` that hosts **one** `WebView2` (no XAML file). Both `TerminalBridgeService` and `AgentBridgeService` subscribe to the same `WebMessageReceived` event and dispatch on the `type` field of the incoming JSON.

Browser-level capabilities for that shared instance belong only to `WebViewHostPolicy`: it configures WebView settings, navigation/new-window handling, permissions, filtered native context menus and download denial. Editable fields retain localized editing commands and selected text retains copy/select-all, but PSX never exposes browser page/image/link menus, print, source inspection or “Save page as”; intentional future exports must be explicit host commands with reviewed data contracts. `RuntimeDiagnostics` replaces raw `window.onerror` overlays: the two exact Chromium ResizeObserver delivery warnings are non-fatal, while every other uncaught shell error keeps its console record and shows one generic, dismissible notice without paths, stacks or conversation data. ResizeObserver callbacks may measure immediately but must defer class, scroll and geometry writes to an animation frame.

Terminal view switching is a resume operation, not a new session. While `#terminal-container` is hidden, never call FitAddon or resize ConPTY; update output/theme state only and defer one stable fit until the container is visible again.

- `wwwroot/` is mapped to `https://psx.local/` via `SetVirtualHostNameToFolderMapping` (`Services/TerminalBridgeService.cs:43-48`)
- `~/.psx/agent/attachments/` is mapped to `https://psx-attachments.local/` (same file, lines 50-56)
- The shipped page is the Vite build output at `https://psx.local/app/index.html` (`wwwroot/app/`, built from `frontend/webview/`)
- All cross-boundary binary IO is base64 strings (JS limitation of `PostWebMessageAsJson`)

If you change the message contract, update **both** the C# dispatcher in the two bridges (`TerminalBridgeService.OnWebMessageReceived`, `AgentBridgeService.OnWebMessageReceived`) and the JS `switch` in `frontend/webview/src/main.js` (the `handleEvent` / `switch (message.type)` block). Top-level message `type` values are centralized in `frontend/webview/src/BridgeMessages.js` (`BridgeSendType` / `BridgeEventType`); `frontend/agent/src/contracts/bridge-contract.d.ts` mirrors the payloads for the TypeScript typecheck. There is no schema validation between them beyond these gates.

### 2. ConPTY is hand-rolled P/Invoke, not a library

`Helpers/NativeMethods.cs` declares the 5 ConPTY Win32 APIs. `Helpers/ProcessFactory.cs:22-107` is the only place that assembles: `CreatePipe` → `CreatePseudoConsole` → `InitializeProcThreadAttributeList` + `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE)` → `CreateProcessW(EXTENDED_STARTUPINFO_PRESENT)`. **Do not "modernize" this** — this is intentional, it gives PowerShell real `COLORTERM=truecolor` detection.

Output is batched in `Services/ConPtyService.ReadOutputLoopAsync`: `8ms` flush interval, `64KB` max batch, `ArrayBufferWriter<byte>`, `PeekNamedPipe` to drain the pipe. Touching these constants requires understanding that `cat largefile` will blow up the WebMessage channel if you don't batch.

`OutputPipeRead` is a `SafeFileHandle` that needs `DangerousAddRef/DangerousRelease` across the P/Invoke boundary — see `ConPtyService.cs:174, 235-238`. Don't refactor to a plain `IntPtr`.

### 3. Agent uses workspace-scoped ACP engines with curated providers

`AgentWorkspaceCoordinator` is the application-level Agent router. Every Agent Tab owns one `AcpAgentSessionService` through `IAgentWorkspaceSession`, together with an immutable Provider, Thread, ACP transport and process tree. `AcpAgentSessionService` orchestrates the run, transcript and session state. `AcpSessionUpdateReader` parses ACP updates, `AcpToolStateTracker` owns live tool projections, `AcpDecisionCoordinator` pairs permission/elicitation requests with UI responses, `AcpTransportLifecycle` owns transport generations and reset state, and `AcpTerminalRequestHandler` owns reverse terminal processes. Keep these responsibilities in their dedicated internal components instead of rebuilding parallel state in the session orchestrator.

Provider identity lives in `IAcpAgentProvider`; ACP wire quirks such as Claude's `claude_command` and `_meta.claudeCode.toolName` live behind `IAcpProviderCompatibility`. Runtime installation, update and process launch descriptions live in `IAcpAgentRuntime`; `AgentRuntimeCoordinator` deduplicates shared runtime work while each Agent Workspace starts an independent process. `NpmRuntimeProcessRunner` and `StagedRuntimeStore` provide the common npm-process and current/next-directory mechanics, but seed layout, package validation, executable selection, Qoder's `--ignore-scripts` and OpenCode's AVX2/platform rules remain provider-local. `AcpJsonRpcTransport` accepts an `AcpProcessSpec` and knows only how to run a process and exchange JSON-RPC over stdio. Provider, Thread and an established session's CWD are never swapped inside a live instance. Do not add provider-name conditionals to the shared engine.

Managed runtime npm and smoke-check processes redirect stdout/stderr. On cancellation or timeout they must go through `RuntimeProcessCleanup`, which kills the process tree, waits for exit, and drains both redirected streams before the runtime operation returns. Do not replace that lifecycle with a bare `Process.Kill`; on Windows it can leave the current/next runtime directory locked after the UI reports completion.

`ClaudeAcpAgentProvider` (default), `KimiCodeAcpAgentProvider`, `QwenCodeAcpAgentProvider`, `QoderCliAcpAgentProvider`, and `OpencodeAcpAgentProvider` are the registered production providers. Claude owns the `acp-claude` identity, the legacy `claude-cli` alias, command filtering, ACP session parameters and the native `claude --resume` Terminal profile; `AcpRuntimeManager` implements its `IAcpAgentRuntime` for the bundled Node + `@agentclientprotocol/claude-agent-acp` runtime (seed → `runtime/acp-current`). Kimi owns `acp-kimi` on the bundled `tools/kimi` baseline with `kimi-current`/`kimi-next` self-update. Qwen owns `acp-qwen` on `tools/qwen` with the same current/next model. Qoder owns `acp-qoder` with Claude-style `tools/qoder-seed` → user-confirmed `runtime/qoder-current` (npm `--ignore-scripts`) and `qoder-next` updates; entry is always `node <bundle/qodercli.js> --acp`. OpenCode owns `acp-opencode` on the bundled `tools/opencode` baseline — the `opencode-windows-x64` platform package (native Bun-compiled exe) depended on directly, never the `opencode-ai` wrapper — with `opencode-current`/`opencode-next` self-update and `OPENCODE_DISABLE_AUTOUPDATE` forced in the process environment; entry is the managed `opencode.exe acp` with no portable Node at session time. AVX2-less machines install `opencode-windows-x64-baseline` into `opencode-current` after user confirmation, and startup never drops that copy for a same-or-newer bundled build. Unknown thread providers open transcript-only and must never be restored through the default adapter.

Agent slash commands are allowlisted. `AcpAgentSessionService` is the authoritative gate: it accepts PSX commands plus the current filtered `available_commands_update` catalog, and rejects every other leading `/command` before `session/prompt`. Keep the JS composer validation and the C# gate synchronized; inline `/text` inside a normal prompt is not a command.

History is a single global left-side dock (one per page) backed by the process-wide `AgentHistoryStore` and `AgentHistoryRequestBroker`; it is a cross-directory catalog fed by `agent_threads`, and opening an already-open Thread activates its existing Workspace. History lists, loading states and history errors live in the dock and never render into the main transcript. History rows show a per-provider brand icon resolved from the catalog `IconKey` (`claude`/`kimi`/generic `agent` fallback) — never a provider-name string check — rendered from local inline SVGs. The Agent page is a soft workbench: `#agent-workspace-container` tints its backdrop and two independent rounded panels (History dock + main conversation panel) float on it. Shell geometry is owned by the CSS tokens in `shell.css` and the pane/layout controllers' measured DOM rectangles; do not invent JavaScript mirror constants for deleted `AgentShellLayoutController` geometry. Plan must stay the main panel's floating card, never a third fixed column. Plan is workspace-local: a content-height context card below the toolbar (`PlanController`, visible/hidden with a narrow overlay entry) — no resizer and no width persistence. Persisted keys are `psx.agent.historyDockOpen` and `psx.agent.historyDockWidth`; `psx.agent.planWidth` and `psx.agent.inspectorWidth` are retired leftovers and must never be read. Preserve each Workspace's composer, decisions, DOM and scroll state while its Tab is hidden; History search/filter/scroll state is global.

Provider identity is sent to the frontend in `agent_state` and `runtime_status`. JavaScript uses `providerKey`, `agentName` and `assistantName`, with Claude fallbacks for bridge compatibility. Keep these payloads and the frontend identity handling synchronized.

The History dock footer (`data-role="history-profile"`) is global dock chrome: it shows the process-wide user profile (`AgentProfileStore`, `~/.psx/profile/` — display name capped at 32 chars, PNG-only avatar ≤ 512KB, atomic writes) and opens the singleton Usage panel dialog (`UsagePanelController` + `UsagePanel.tsx`, title「用量与配置」, tabs「用量 | 配置」). Profile/usage/config commands (`profile_get`/`profile_set_name`/`profile_set_avatar`, `usage_report` / `config_report` `cached`/`force`) travel only through `agent_global_command` and are handled by `AgentWorkspaceCoordinator.HandleGlobalCommand`; a workspace-scoped `agent_command` with one of those names is ignored and never reaches a session. Replies use the global `agent_profile`/`agent_usage_report`/`agent_config_report` events; the frontend `UsageRequestBroker` matches every reply by `requestId` end-to-end (late/superseded usage and config replies are dropped) while requestId-less `agent_profile` broadcasts apply through a monotonic `revision` guard in `UsageStore`. Aggregation lives in `AgentUsageService` only: each Provider's `IAgentUsageSource` streams exact records into bounded per-Provider accumulators, using `input + output + cacheRead + cacheCreation` for the independently reported `today`/`last7Days`/`last30Days` totals and fixed 365-local-day `dailyTokens` series. Provider scans are serial and JSONL lines are bounded; the service never builds an all-record list. `AgentThreadStore.ReadUsageSnapshot()` enumerates every thread file (never the 100-entry index) through a minimal streaming projection of provider and session identifiers, so titles, messages, cwd, models and context snapshots never enter the report. The second-generation wire exposes aggregate totals and heatmap, descriptor-driven `providers[]`, Provider and overall completeness, and the top-level request error. Only ordered fixed completeness reason keys may cross the bridge; paths, parser/source identifiers, versions and raw details stay internal. A partial Provider's “已记录” amount is the sum of every successfully parsed exact row, including readable rows from a session that could not be declared fully matched; reasons describe the remaining gap. The overall total always renders the aggregate of successfully parsed exact rows; when completeness is not `available`, the panel labels it “统计不完整；当前总量仅包含已读取到的精确 Token。” and leaves the incomplete “全部” heatmap filter disabled. `ClaudeSessionUsageSource` probes only requested PSX session filenames beneath the Claude project directories; `KimiCodeSessionUsageSource` probes only requested ACP session directories beneath each Kimi work-directory key, then parses recognized persisted `usage.record` entries from main and subagent wires without deriving work-directory keys or estimating missing data. The「配置」tab mirrors this architecture with `AgentConfigService` + optional `IAgentConfigSource` (Claude/Kimi/Qwen/Qoder/OpenCode): user-level disk config only, never project-level, never live ACP `agent_config_options`; secrets are stripped by `AgentConfigSanitizer` (key names only for env/headers, URL query/userinfo stripped, stdio long-token masking, auth.json ids only, 2 MiB file cap). Runtime updates remain independent of usage/config parser support, and no shared C# or frontend code may branch on a provider name. Usage/config/profile errors render in the panel, never in the conversation; scan failures expose a fixed safe message and keep detailed exceptions in internal diagnostics.

The runtime product version lives beside the toolbar Update button, not a bottom status bar: `RuntimeVersionSnapshot` carries `CurrentVersion`/`PendingVersion`/`ProductName`, and `runtime_update_status` adds `versionLabel` (e.g. `Claude Code v2.1.0`, empty = render nothing) plus tooltip-only `versionDetail`. The global bottom status region keeps only persistent startup warnings (e.g. a missing WebView2 runtime); runtime status never projects there.

### 4. ACP's "request/response" pattern uses `TaskCompletionSource`

Long-running ACP requests (permission prompts and elicitation forms) block on a `TaskCompletionSource` owned by `AcpDecisionCoordinator` until the JS layer sends the matching response. The coordinator validates offered choices, maps AskUser forms and cancels every outstanding request when the workspace closes. Cancellation currently uses the internal sentinel `"__cancelled__"`; do not expose it over the Bridge or add another sentinel—replace it with a typed result if this protocol needs another outcome.

### 5. ACP runtime: dual-directory install + manual staged updates

`AcpRuntimeManager` is the current Claude implementation of `IAcpAgentRuntime` and owns the installed ACP adapter across two directories:
- `runtime/acp-current/` — what the live Agent session reads, exclusively
- `runtime/acp-next/` — where a user-requested update stages

Updates never mutate `acp-current` in place and are strictly user-triggered from the Agent toolbar — PSX startup only promotes an already-staged `acp-next` → `acp-current` (`TryPromoteNextToCurrentAsync`) and never touches npm or the network. A user-requested refresh writes to `acp-next`, flips the `acp-active.txt` marker to `"next"`, and the **next PSX launch** applies it. This eliminates the half-updated `node_modules` hazard. Kimi, Qwen, Qoder and OpenCode share the current/next pointer and rollback mechanics through `StagedRuntimeStore`, and their npm-backed install/update work uses `NpmRuntimeProcessRunner`; provider-specific validation and fallback policy stays in each runtime. `KimiCodeAcpRuntime` adds an `npm view` version pre-check, an exact-version `--engine-strict` install, a staged `--version` smoke check, and a bundled `tools/kimi` fallback whenever the bundled version is the same or newer. If you see code paths trying to fall back from `acp-current` to `tools/acp/`, that's intentional hard refusal — version skew.

When `runtime/acp-current/` is missing, Agent mode publishes `runtime_status: missing` and disables the composer. The `install_runtime` command is the only normal path to `EnsureInstalledCoreAsync`; it seeds from `tools/acp-seed/` and runs `npm ci --include=optional` after explicit confirmation. `cancel_runtime_install` cancels npm and terminates its process tree. `EnsureTransportAsync` never installs implicitly. The installed runtime stays beside `PSX.exe`, so the same extracted directory reuses it and a new directory needs a new install.

---

## Cross-cutting rules (learned from the code, not invented)

1. **All browser-bound JSON goes through `WebViewJsonDispatcher`.** It posts directly on the owning thread, returns the real `Dispatcher.InvokeAsync` task off-thread, preserves queue order, and drops callbacks after bridge disposal. Do not add bridge-local `BeginInvoke` calls or capture a mutable `_coreWebView` field inside a queued callback. High-volume Terminal output may keep fire-and-forget call sites; awaiting the returned task is reserved for flows that need observable delivery ordering.

2. **All thread-hopping to `ObservableCollection<T>` in ViewModels uses `Dispatcher.BeginInvoke`** (see the `OnTabCreated` / `OnTabClosed` / `OnTabTitleChanged` handlers in `MainViewModel`). Don't modify `Tabs` directly from a service event handler.

3. **Async services use `ConfigureAwait(false)` everywhere** except the final hop that touches UI. `AcpJsonRpcTransport` is the canonical example (~15 occurrences).

4. **The `XAML brush key` ↔ `psx.ini [theme] field` ↔ `JS CSS variable` are three faces of the same color.** When you add a new color, update `Models/AppSettings.cs`, the active config and every preset, `AppearanceService`, and the matching WebView CSS variable. Eight-digit INI colors use CSS `#RRGGBBAA` semantics and are converted only when written to WPF resources.

5. **The shutdown sequence is load-bearing.** `MainWindow.OnClosing` first marks Workspace shutdown, then disposes the view model, Agent coordinator, Workspace manager, terminal sessions and both bridges before re-issuing `Close()` via `Dispatcher.BeginInvoke`. Workspace shutdown must not create a replacement Terminal. If you add a new `IDisposable` service, preserve this ownership order and register it consistently in `App.ConfigureServices`.

6. **SettingsViewModel only writes 3 fields back to `psx.ini`.** Don't add "save settings" features assuming the pipeline exists; the rest of `psx.ini` requires a manual edit + restart. `SettingsService.SaveSettings` only persists `FontSize`, `FontFamily`, `DefaultShellProfileId` (see `SettingsViewModel`'s `OnFontSizeChanged` / `OnFontFamilyChanged` / `OnDefaultShellProfileIdChanged` handlers, and `SettingsService.SaveSettings` — which atomically rewrites the whole psx.ini via a `.tmp` + `File.Move`).

---

## File-to-purpose map (only the non-obvious ones)

- `Helpers/ProcessFactory.cs` — only place that knows how to start a ConPTY child process. Reused by `ConPtyService`.
- `Services/AcpAgentSessionService.cs` — the shared ACP Agent orchestrator. It owns run, transcript and session state while delegating update parsing, tool projections, decisions, transport lifetime and reverse terminals to the five focused `Acp*` collaborators described above. Provider-specific paths, metadata and CLI commands do not belong here.
- `Services/IAcpProviderCompatibility.cs` — provider-specific ACP wire compatibility. `ClaudeAcpProviderCompatibility` is the only place shared-session code should learn Claude metadata shapes.
- `Services/NpmRuntimeProcessRunner.cs` / `Services/StagedRuntimeStore.cs` — common managed-runtime process cleanup and current/next-directory mechanics. Package and platform policy stays in each `IAcpAgentRuntime`.
- `Services/ClaudeAcpAgentProvider.cs` — the curated Claude identity and compatibility policy. New official integrations add their own provider/runtime instead of copying the session engine.
- `Services/AcpJsonRpcTransport.cs` — generic JSON-RPC 2.0 client over stdio. Could be lifted out as a standalone library.
- `Services/SettingsService.cs` — hand-rolled INI parser, **no third-party INI library**. `ValidateColor` accepts CSS `#RRGGBBAA` and silently normalizes to WPF `#AARRGGBB`. Bad colors fall back to XAML defaults — never throw.
- `Services/AgentThreadStore.cs` — owns `~/.psx/` layout: `agent/threads/<threadId>.json`, `agent/attachments/<threadId>/<id>.<ext>`, `agent/index.json`. Path safety uses `char.IsLetterOrDigit` filtering, not path canonicalization — see "not solved" below.
- `Controls/TerminalHost.cs` — sets `WebView2.DefaultBackgroundColor` from `Application.Current.Resources["WindowBackgroundBrush"]` **at construction time** to prevent white flash before the page loads. If you add another `WebView2` host, copy this pattern.
- `MainWindow.OnSourceInitialized` — DWM dark title bar is set here (HWND created, window not yet shown). `OnLoaded` is too late.
- `AppearanceService.ApplyWpf` — overwrites 18 brush keys in `Application.Current.Resources`. Runtime preview works because XAML references use `DynamicResource`.

---

## Release packaging

`tools/build-release.ps1` is the real release pipeline — **not** plain `dotnet publish`. It produces a fully self-contained `PSX-<version>-win-x64-portable.zip` (win-x64 only) bundling:

- Self-contained .NET 10 runtime (via `Properties/PublishProfiles/win-x64-self-contained.pubxml`: `SelfContained=true`, NOT single-file, NOT trimmed — WPF can't be trimmed)
- Portable Node.js v22.23.1 win-x64, pinned to the official SHA-256
- ACP seed manifests used by the explicit first-use installation flow
- Project and third-party license/NOTICE files
- Optional WebView2 Fixed Version runtime in `runtime/webview2-fixed/` — **only if** `-WebView2FixedRuntimePath` is passed at build time; otherwise users rely on system Evergreen WebView2

Users unzip and double-click `PSX.exe`; Terminal mode needs no Node or .NET installation. Agent mode needs a one-time npm download per extracted directory after the user confirms. The public ZIP must never contain `claude.exe` or `runtime/acp-current`. WebView2 remains the only startup dependency (system-installed Evergreen, or bundled fixed version).

Note: `RuntimeLocator.Locate()` resolves all install-relative paths at startup. `RuntimePreflightService` inspects WebView2/Node/ACP/Claude readiness, but only missing WebView2 blocks application startup; a missing ACP runtime is handled inside Agent mode.

---

## Things this codebase does NOT have (don't assume they exist)

- No real-provider network test in the automated gate; account login, quota and subjective UI checks remain manual
- No `IConPtyService` or `IAcpJsonRpcTransport` interfaces — those concrete classes are injected directly (`App.xaml.cs:42`)
- CI runs tests only — `.github/workflows/test.yml` runs `tools/test.ps1 -Suite Fast` (windows-latest, .NET 10 + Node 22, uploads `TestResults/`) on `dev`/`main` push and pull_request, matching the CI gate described in `AGENTS.md`. There is no CD/deployment pipeline and no `azure-pipelines.yml`; release packaging via `tools/build-release.ps1` is still run manually.
- Release artifacts are generated under `bin/releases/` and are not committed (`.gitignore` excludes `[Bb]in/`).
- No logging framework — `Debug.WriteLine` and `MessageBox.Show` are used for diagnostics
- No `Models/TerminalOptions.cs` schema validation against the JS side — the contract is implicit, matched by string keys in C# `switch` and JS `switch`
- No `Channel<T>` — output batching is hand-rolled with `ArrayBufferWriter<byte>` + `Stopwatch`

---

## Known fragility (don't paper over it; fix the root cause if you touch it)

- `MainViewModel` `RelayCommand` handlers — several do `_ = SomeAsync()` fire-and-forget (search for `_ =` in MainViewModel.cs). Exceptions in those tasks are silently lost.
- `TabManagementService.cs:11-14` — two `HashSet`s (`_closingSessions` and `_closedSessions`) with non-atomic reads across `OnConPtySessionExited` vs `OnClosing`. A duplicate `TabClosed` event is possible under load.
- `AgentThreadStore.cs:340-352` — path-traversal defense is character-class filtering, not `Path.GetFullPath` + `StartsWith` verification. Acceptable for the current use case (GUID-derived `threadId`), but if `threadId` ever becomes user-supplied, replace it.
- `AcpDecisionCoordinator` — `"__cancelled__"` remains an internal cancel sentinel between its pending-request and response paths. Replace it with a typed result before adding another outcome.
