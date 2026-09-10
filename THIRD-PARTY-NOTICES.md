# Third-Party Notices

PSX source code is licensed under the MIT License in `LICENSE.txt`. That license
does not replace the licenses of the third-party components listed below.

The portable release redistributes:

- Microsoft .NET runtime — MIT and accompanying third-party notices.
- Microsoft WebView2 SDK — see the bundled Microsoft license and notice.
- CommunityToolkit.Mvvm — MIT and accompanying third-party notices.
- Microsoft.Extensions.DependencyInjection — MIT and accompanying third-party notices.
- Node.js and npm — licenses are included inside `tools/node/` in the release.
- xterm.js and its addons — MIT; see `licenses/xterm/LICENSE`. Bundled from the
  `@xterm/*` npm packages into the Vite build output under `wwwroot/app/`.
- React and React DOM — MIT; see `licenses/react/LICENSE.txt`. Bundled into the
  Vite build output under `wwwroot/app/` (react-vendor chunk).
- shadcn/ui component sources — MIT; see `licenses/shadcn/LICENSE`. Vendored
  as TypeScript sources under `frontend/agent/src/components/ui/`
  (provenance and local modifications in
  `frontend/agent/src/components/SOURCES.md`).
- Vercel AI Elements component sources — Apache-2.0; see
  `licenses/ai-elements/LICENSE`. Vendored with documented local modifications
  under `frontend/agent/src/components/ai-elements/` (same SOURCES.md).
- Vercel Streamdown and its CJK, code, math and Mermaid plugins — Apache-2.0;
  see `licenses/streamdown/LICENSE`. They are bundled only into Agent Vite
  chunks and run behind the PSX-owned sanitization and loading policy.
- Shiki — MIT; see `licenses/shiki/LICENSE`. It is loaded on demand for fenced
  code through a local, idle-terminated Worker. The production document CSP
  remains unchanged and does not allow `unsafe-eval` or `wasm-unsafe-eval`.
- KaTeX — MIT; see `licenses/katex/LICENSE`. Its CSS and fonts ship as local
  Vite assets for mathematical notation.
- Mermaid — MIT; see `licenses/mermaid/LICENSE`. It is loaded on demand for
  fenced diagrams and always uses strict security.
- Radix UI — MIT; see `licenses/radix-ui/LICENSE`.
- Lucide icons — ISC; see `licenses/lucide/LICENSE`.
- Lobe Icons static SVG brand marks — MIT; see `licenses/lobe-icons/LICENSE`.
  The pinned `claude.svg`, `kimi.svg`, `qwen.svg`, `opencode.svg`, and
  `deepseek.svg` assets from `@lobehub/icons-static-svg` are retained under
  `Assets/AgentIcons/` and converted to WPF geometry for the
  TabBar. Provider names and marks remain trademarks of their
  respective owners.
- Tailwind CSS — MIT; see `licenses/tailwindcss/LICENSE`.
- Maple Mono Normal CN 7.9 — SIL Open Font License 1.1; see
  `licenses/maple-mono/LICENSE.txt`. The unmodified Regular and SemiBold TTF
  files ship under `wwwroot/vendor/fonts/maple-mono/` for Agent code and other
  monospaced technical content. This adds approximately 35.4 MiB unpacked (about 16 MiB compressed)
  to the portable package.
- ACP seed manifests — the adapter is Apache-2.0; see `licenses/acp/LICENSE`.
- DeepSeek Harness (DSH) seed manifests — MIT; see `licenses/dsh/LICENSE` and
  `licenses/dsh/THIRD-PARTY-NOTICES.md`. PSX pins `@deepseek-ai/dsh` at
  0.1.5-rc.2 for first install. Only `tools/dsh-seed/` ships; the installed
  runtime under `runtime/dsh-current/` is downloaded from the official npm
  registry after explicit user confirmation and is never redistributed in the
  ZIP. Later versions are downloaded only after a separate user-triggered
  update and restart confirmation. The `dsh web` server runs as an independent
  loopback process; DSH owns
  its own models, projects, sessions and configuration under `~/.dsh`.
- Kimi Code ACP runtime — bundled under `tools/kimi/`; MIT. See
  `licenses/kimi/LICENSE` and `licenses/kimi/THIRD-PARTY-NOTICES.md` for the
  runtime and its production dependencies (`node-pty`, `node-addon-api`,
  `@mariozechner/clipboard`), all MIT.
- Qwen Code ACP runtime — bundled under `tools/qwen/`; Apache-2.0. See
  `licenses/qwen/LICENSE` and `licenses/qwen/THIRD-PARTY-NOTICES.md` for the
  runtime and its production dependencies (`sharp`, `node-pty`, clipboard
  helpers, and related packages).
- OpenCode ACP runtime — bundled under `tools/opencode/`; MIT. See
  `licenses/opencode/LICENSE` and `licenses/opencode/THIRD-PARTY-NOTICES.md`.
  The bundled artifact is the `opencode-windows-x64` platform package: a
  self-contained native executable (~175 MB) embedding the Bun runtime (MIT),
  depended on directly rather than through the `opencode-ai` wrapper.
- Microsoft.Data.Sqlite — MIT; see `licenses/microsoft.data.sqlite/LICENSE.txt`.
  The SQLitePCLRaw packages (`core`, `bundle_e_sqlite3`, `provider.e_sqlite3`,
  `lib.e_sqlite3`) — Apache-2.0; see `licenses/sqlitepclraw/LICENSE`. The
  SQLite engine compiled into the bundled `e_sqlite3.dll` native library is
  in the public domain; see `licenses/sqlite/LICENSE.txt`.

The release includes only the ACP and DSH package manifests and lockfiles. It
does not redistribute Claude Code, the Claude Agent SDK native binary, an
installed ACP runtime, or an installed DeepSeek Harness runtime. When the user explicitly
installs Agent support, npm downloads the locked packages from their
publishers. Those downloaded packages retain their own license files and remain
subject to their respective terms, including the
[Anthropic legal agreements](https://code.claude.com/docs/en/legal-and-compliance).

Copies of the license and notice files for the binary dependencies shipped by
PSX are stored under `licenses/` and included in the portable release.

Unlike the Claude ACP adapter (installed on first use from its seed manifest),
the Kimi Code, Qwen Code, and OpenCode runtimes ARE
redistributed inside the portable release under `tools/kimi/`, `tools/qwen/`,
and `tools/opencode/`. They are installed at build time from the pinned
lockfiles in `tools/kimi-seed/`, `tools/qwen-seed/`, and
`tools/opencode-seed/` (Windows x64 only) and remain subject to the license
terms of each bundled package. The AVX2-less OpenCode variant
(`opencode-windows-x64-baseline`) is never redistributed; on machines without
AVX2 it is downloaded after user confirmation, like the seed-based runtimes.
