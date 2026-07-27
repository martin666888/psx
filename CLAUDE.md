# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## What this project is

PSX is a Windows desktop terminal shell (C# + WPF + WebView2 + xterm.js + ConPTY) for running AI Agent CLIs and dev tools. Two coexisting views share one window: a `Terminal` view (real ConPTY shells via xterm.js) and an `Agent` view backed by the Agent Client Protocol. Agent mode uses a curated provider architecture; Claude Code is currently the only registered/default provider. Multi-tab, themeable, persistent threads.

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

**Automated tests are repository-local** — `tests/PSX.Tests` covers C# unit/integration/desktop behavior, `tests/PSX.Web.Tests` covers production frontend and bridge contracts, and the Fake Agent/npm/Desktop Probe projects cover process boundaries. Use `tools/test.ps1 -Suite Fast` during development and `-Suite Full` before release; tests must never access the user's real `%USERPROFILE%\.psx`.

**SDK pinning** — `global.json` pins `9.0.300` with `rollForward: latestMajor` (allows resolving to the 10.0.x SDK). Target framework is `net10.0-windows`. Theme colors and fonts can be previewed and confirmed at runtime; other manual `psx.ini` changes still require a restart.

---

## Architecture (read this before touching anything)

The codebase has 4 large subsystems that look independent but are tightly coupled through events:

### 1. The two bridges share one WebView2

`Controls/TerminalHost.cs` is a code-behind `UserControl` that hosts **one** `WebView2` (no XAML file). Both `TerminalBridgeService` and `AgentBridgeService` subscribe to the same `WebMessageReceived` event and dispatch on the `type` field of the incoming JSON.

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

`AgentWorkspaceCoordinator` is the application-level Agent router. Every Agent Tab owns one `AcpAgentSessionService` through `IAgentWorkspaceSession`, together with an immutable Provider, Thread, ACP transport and process tree. Standard ACP session, permissions, elicitation, Plan, terminal and filesystem behavior stays in this provider-agnostic per-workspace engine.

Provider identity and compatibility policy live in `IAcpAgentProvider`. Runtime installation, update and process launch descriptions live in `IAcpAgentRuntime`; `AgentRuntimeCoordinator` deduplicates shared runtime work while each Agent Workspace starts an independent process. `AcpJsonRpcTransport` accepts an `AcpProcessSpec` and knows only how to run a process and exchange JSON-RPC over stdio. Provider, Thread and an established session's CWD are never swapped inside a live instance. Do not add provider-name conditionals to the shared engine.

`ClaudeAcpAgentProvider` is currently the only registered/default provider. It owns the `acp-claude` identity, the legacy `claude-cli` alias, command filtering, ACP session parameters and the native `claude --resume` Terminal profile. `AcpRuntimeManager` currently implements `IAcpAgentRuntime` for the bundled Node + `@agentclientprotocol/claude-agent-acp` runtime. Unknown thread providers open transcript-only and must never be restored through Claude.

Agent slash commands are allowlisted. `AcpAgentSessionService` is the authoritative gate: it accepts PSX commands plus the current filtered `available_commands_update` catalog, and rejects every other leading `/command` before `session/prompt`. Keep the JS composer validation and the C# gate synchronized; inline `/text` inside a normal prompt is not a command.

Each Agent Workspace has its own resizable right-side Inspector with persistent-in-process `Plan` and `History` tabs. History is a global cross-directory catalog backed by `agent_threads`; opening an already-open Thread activates its existing Workspace. It never renders into the main transcript. Preserve each Workspace's composer, decisions, DOM and scroll state while its Tab is hidden.

Provider identity is sent to the frontend in `agent_state` and `runtime_status`. JavaScript uses `providerKey`, `agentName` and `assistantName`, with Claude fallbacks for bridge compatibility. Keep these payloads and the frontend identity handling synchronized.

### 4. ACP's "request/response" pattern uses `TaskCompletionSource`

Long-running ACP requests (permission prompts, elicitation forms) block on a `TCS` until the JS layer sends a response message back. See `AcpAgentSessionService.PendingPermission.Completion` and the `HandlePermissionRequestAsync` flow. Cancellation is a magic string `"__cancelled__"` — fragile, but it's the only one. Don't add more magic strings; if you need a second one, replace the pattern with an enum.

### 5. ACP runtime: dual-directory install + background refresh

`AcpRuntimeManager` is the current Claude implementation of `IAcpAgentRuntime` and owns the installed ACP adapter across two directories:
- `runtime/acp-current/` — what the live Agent session reads, exclusively
- `runtime/acp-next/` — where background `npm update` writes

Updates never mutate `acp-current` in place. Background refresh writes to `acp-next`, flips the `acp-active.txt` marker to `"next"`, and the **next PSX launch** promotes `acp-next` → `acp-current` (`TryPromoteNextToCurrentAsync`). This eliminates the half-updated `node_modules` hazard. If you see code paths trying to fall back from `acp-current` to `tools/acp/`, that's intentional hard refusal — version skew.

When `runtime/acp-current/` is missing, Agent mode publishes `runtime_status: missing` and disables the composer. The `install_runtime` command is the only normal path to `EnsureInstalledCoreAsync`; it seeds from `tools/acp-seed/` and runs `npm ci --include=optional` after explicit confirmation. `cancel_runtime_install` cancels npm and terminates its process tree. `EnsureTransportAsync` never installs implicitly. The installed runtime stays beside `PSX.exe`, so the same extracted directory reuses it and a new directory needs a new install.

---

## Cross-cutting rules (learned from the code, not invented)

1. **All thread-hopping to WebView2 uses `BeginInvoke`, never `Invoke`.** `TerminalBridgeService` has a comment explaining deadlock avoidance near its `BeginInvoke` calls (search for "deadlock" in that file). Match this pattern everywhere you call `PostWebMessageAsJson` from a background thread.

2. **All thread-hopping to `ObservableCollection<T>` in ViewModels uses `Dispatcher.BeginInvoke`** (see the `OnTabCreated` / `OnTabClosed` / `OnTabTitleChanged` handlers in `MainViewModel`). Don't modify `Tabs` directly from a service event handler.

3. **Async services use `ConfigureAwait(false)` everywhere** except the final hop that touches UI. `AcpJsonRpcTransport` is the canonical example (~15 occurrences).

4. **The `XAML brush key` ↔ `psx.ini [theme] field` ↔ `JS CSS variable` are three faces of the same color.** When you add a new color, update `Models/AppSettings.cs`, the active config and every preset, `AppearanceService`, and the matching WebView CSS variable. Eight-digit INI colors use CSS `#RRGGBBAA` semantics and are converted only when written to WPF resources.

5. **The shutdown sequence is load-bearing.** `MainWindow.OnClosing` first marks Workspace shutdown, then disposes the view model, Agent coordinator, Workspace manager, terminal sessions and both bridges before re-issuing `Close()` via `Dispatcher.BeginInvoke`. Workspace shutdown must not create a replacement Terminal. If you add a new `IDisposable` service, preserve this ownership order and register it consistently in `App.ConfigureServices`.

6. **SettingsViewModel only writes 3 fields back to `psx.ini`.** Don't add "save settings" features assuming the pipeline exists; the rest of `psx.ini` requires a manual edit + restart. `SettingsService.SaveSettings` only persists `FontSize`, `FontFamily`, `DefaultShellProfileId` (see `SettingsViewModel`'s `OnFontSizeChanged` / `OnFontFamilyChanged` / `OnDefaultShellProfileIdChanged` handlers, and `SettingsService.SaveSettings` — which atomically rewrites the whole psx.ini via a `.tmp` + `File.Move`).

---

## File-to-purpose map (only the non-obvious ones)

- `Helpers/ProcessFactory.cs` — only place that knows how to start a ConPTY child process. Reused by `ConPtyService`.
- `Services/AcpAgentSessionService.cs` — the shared ACP Agent engine. Has an internal state machine (`ready` / `running` / `restoring` / `transcript_only` / `error` / `fallback`) and a `ReplayHistoryState` that distinguishes live vs replay event handling. Provider-specific paths and CLI commands do not belong here.
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
- `AcpAgentSessionService` — `"__cancelled__"` magic string as cancel signal (referenced from both the cancellation path and the response-handling path). Replace with enum if you add a second signal.
