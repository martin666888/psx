# Phase 0 — Qwen / Qoder ACP compatibility notes

Local probes run on 2026-08-05 against installed CLIs. These notes pin
capabilities and floor versions for PSX providers. They are **not** a CI gate.

## Probe tooling

| Script | Role |
|--------|------|
| [`tools/acp-initialize-probe.mjs`](../tools/acp-initialize-probe.mjs) | **Release/CI smoke**: start → `initialize` result → close stdin / kill. No `session/new`, no prompt, no login. |
| [`tools/acp-compat-probe.mjs`](../tools/acp-compat-probe.mjs) | **Local Phase 0 only**: `initialize` → optional authenticate → `session/new` → cancel. Optional `ACP_PROBE_PROMPT=1` for a real prompt. |

## Verified CLI versions

| CLI | Entry | Version probed | ACP flag |
|-----|-------|----------------|----------|
| Qwen Code | `qwen.cmd` → `node …/@qwen-code/qwen-code/cli.js` | **0.15.11** (local); npm latest at probe time **0.21.5** | `--acp` (stable) |
| Qoder CLI | `qodercli.cmd` → npm shim / real binary | **0.2.11** | `--acp` |

### Qoder minimum compatible version (pinned)

**`0.2.11`** — the lowest version that completed ACP `initialize` in this probe
pass. Finding `qodercli.cmd` alone is not enough; PSX must run `--version` at
Workspace creation time and reject lower versions with
`ExternalUnsupportedVersion` before starting ACP.

## Initialize results (CI-safe smoke — both passed)

### Qwen (`qwen --acp`)

- `agentInfo`: `qwen-code` / "Qwen Code"
- `loadSession`: true
- `sessionCapabilities.resume`: present
- `sessionCapabilities.list`: present
- Prompt: image / audio / embeddedContext
- MCP: http + sse
- Auth methods: `openai`, `qwen-oauth`

### Qoder (`qodercli --acp`)

- `agentInfo`: `qoder-cli` / "Qoder CLI"
- `loadSession`: true
- `sessionCapabilities.resume`: **absent** (only `list`)
- Prompt: image / audio / embeddedContext
- MCP: http + sse
- Auth methods: `qodercli-login`, `qoder-personal-access-token` (`QODER_PERSONAL_ACCESS_TOKEN`)

## Local session probe (not for CI)

### Qwen

- `session/new` succeeded (models + modes + configOptions returned).
- `session/cancel` returned method-not-found (`-32601`) — cancellation likely
  uses another ACP path; rely on PSX transport teardown, not this method name.
- Modes observed: `plan`, `default`, `auto-edit`, `yolo`.

### Qoder

- `initialize` fast and healthy.
- `session/new` **timed out (~45s+)** in this environment while dumping skill
  conflict warnings to stderr. Treat unauthenticated / heavy-startup hangs as a
  known risk: PSX maps that onto `auth_required` with a **90s** `NewSessionTimeout`
  and `TreatNewSessionTimeoutAsAuthRequired`, then opens `qodercli login`
  (not `qodercli --acp --login`).
- Prompt / permission / Chinese reply still need a manual soak after login
  (`ACP_PROBE_PROMPT=1`).

## Auto-update conflict (implementation notes)

### Qwen

- Setting: `general.enableAutoUpdate` (default `true`).
- **Do not set `QWEN_HOME`** (would isolate credentials under a PSX-only home).
- Prefer `QWEN_CODE_SYSTEM_SETTINGS_PATH` pointing at a PSX-owned JSON that
  forces `"general": { "enableAutoUpdate": false }`. System settings override
  user/project without relocating `~/.qwen`.
- PSX Update must stage fixed npm versions into `runtime/qwen-next/` then flip
  `runtime/qwen-active.txt` — never call Qwen's own updater.

### Qoder

- External ownership only in Phase 2.
- Do not edit `~/.qoder/settings.json`.
- Do not run `qodercli` during `PrepareForStartupAsync` / preflight (path-only
  discovery). `--version` and ACP start only after the user opens a Qoder Workspace.

## Recommended PSX client capabilities (do **not** copy Kimi)

Kimi disables reverse `fs` due to hang risk. Qoder docs state file/terminal
operations use IDE-side ACP capabilities; Qwen advertise rich prompt/session
features. First ship:

| Capability | Qwen | Qoder |
|------------|------|-------|
| `FileSystemReadText` | true | true |
| `FileSystemWriteText` | true | true |
| `Terminal` | true | true |
| Usage source | unavailable (phase 1) | unavailable (phase 1) |

If reverse `fs` hangs appear in soak tests, tighten per provider without
adding name branches in the shared engine.

## Packaging seed target

- Package: `@qwen-code/qwen-code`
- Pin for first seed: **0.21.5** (npm latest at planning) unless install/smoke
  fails — then fall back to the verified **0.15.11**.
- Bin field: `"qwen": "cli.js"`
- Process: `node <resolved-cli.js> --acp`
- Engines: Node `>=22` (matches PSX portable Node policy)

## Qoder external states (Phase 2)

| State | Meaning |
|-------|---------|
| `Missing` | No `qodercli` / `qodercli.cmd` on PATH (path discovery only at startup) |
| `ExternalReady` | Entry found + version ≥ 0.2.11 after Workspace-time `--version` |
| `ExternalUnsupportedVersion` | Entry found but version too old |
| `AuthenticationRequired` | ACP/auth failure → guide `qodercli login` |

UI must say **「外部安装，由 Qoder 管理」**, hide PSX Update, and must **not**
reuse the bundled-`unsupported` tooltip (“Updates ship with PSX releases”).
