# Phase 0 — Qwen / Qoder ACP compatibility notes

Local probes run on 2026-08-05 (and re-verified for Qoder managed install on
2026-08-05 against `@qoder-ai/qodercli@1.1.14`). These notes pin capabilities
and floor versions for PSX providers. They are **not** a CI gate.

## Probe tooling

| Script | Role |
|--------|------|
| [`tools/acp-initialize-probe.mjs`](../tools/acp-initialize-probe.mjs) | **Build/dev smoke only**: start → `initialize` result → close stdin / kill. Not shipped in the public ZIP; user-machine install success uses C# structure / version / `--version` / pointer gates. |
| [`tools/acp-compat-probe.mjs`](../tools/acp-compat-probe.mjs) | **Local Phase 0 only**: `initialize` → optional authenticate → `session/new` → cancel. Optional `ACP_PROBE_PROMPT=1` for a real prompt. |

## Verified CLI versions

| CLI | Entry | Version probed | ACP flag |
|-----|-------|----------------|----------|
| Qwen Code | `node …/@qwen-code/qwen-code/cli.js` | **0.21.5** (seed pin); earlier local **0.15.11** | `--acp` |
| Qoder CLI | `node …/@qoder-ai/qodercli/bundle/qodercli.js` | **1.1.14** (seed pin; initialize + `--version` OK with `--ignore-scripts`) | `--acp` |

### Qoder minimum compatible / seed pin

**`1.1.14`** — npm latest at the managed-install re-probe. Seeded under
`tools/qoder-seed/` with lockfile. First-use install and refresh both use
`--ignore-scripts` so the package postinstall cannot mutate the user PATH.

## Ownership matrix (production)

| Provider | Public ZIP | Live runtime | Update |
|----------|------------|--------------|--------|
| Claude Code | `tools/acp-seed` | `runtime/acp-current` | `acp-next` + pointer |
| Kimi Code | `tools/kimi` (full) | bundled or `kimi-current` | `kimi-next` + pointer |
| Qwen Code | `tools/qwen` (full) | bundled or `qwen-current` | `qwen-next` + pointer |
| Qoder CLI | `tools/qoder-seed` | `runtime/qoder-current` | `qoder-next` + pointer |

All four are **Managed**. There is no PATH-authoritative / `OwnershipKind.External`
provider. Startup only promotes already-staged directories; networked npm requires
explicit user confirmation.

## Initialize results (CI-safe smoke)

### Qwen (`node <entry> --acp`)

- `agentInfo`: `qwen-code` / "Qwen Code"
- `loadSession`: true
- `sessionCapabilities.resume`: present
- Prompt: image / audio / embeddedContext
- Auth methods: `openai`, `qwen-oauth`

### Qoder (`node <bundle/qodercli.js> --acp`, 1.1.14)

- `agentInfo`: `qoder-cli` / "Qoder CLI" / `1.1.14`
- `loadSession`: true
- `sessionCapabilities.resume`: **present** (also list / close / delete / fork)
- Prompt: image / embeddedContext
- Auth methods: `qodercli-login` (interactive `login` subcommand; not `--acp --login`)

## Local session probe (not for CI)

### Qwen

- `session/new` succeeded; interactive login uses the managed Node + entry TUI
  (not `--acp --login`, which rejects `login` as an unknown argument on 0.21.5).
- Prefer `QWEN_CODE_SYSTEM_SETTINGS_PATH` to force `enableAutoUpdate: false`.

### Qoder

- `initialize` fast and healthy on the managed `node + bundle` entry.
- Unauthenticated `session/new` can still stall; PSX keeps
  `NewSessionTimeout=90s` + `TreatNewSessionTimeoutAsAuthRequired`, then opens
  the managed login profile (`node <entry> login`).

## Recommended PSX client capabilities

| Capability | Qwen | Qoder |
|------------|------|-------|
| `FileSystemReadText` | true | true |
| `FileSystemWriteText` | true | true |
| `Terminal` | true | true |
| Usage source | unavailable (phase 1) | unavailable (phase 1) |

## Packaging seed target (Qoder)

- Package: `@qoder-ai/qodercli`
- Pin: **1.1.14**
- Bin: `"qodercli": "bundle/qodercli.js"`
- Process: `node <resolved-bundle> --acp`
- Install flags: `--save-exact --omit=dev --ignore-scripts --no-audit --no-fund`
- Post-install gates (C#): structure + exact version + portable Node `--version` + pointer
