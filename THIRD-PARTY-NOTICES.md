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
- Radix UI — MIT; see `licenses/radix-ui/LICENSE`.
- Lucide icons — ISC; see `licenses/lucide/LICENSE`.
- Lobe Icons static SVG brand marks — MIT; see `licenses/lobe-icons/LICENSE`.
  The pinned `claude.svg`, `kimi.svg`, `qwen.svg`, and `qoder.svg` assets from
  `@lobehub/icons-static-svg` are retained under
  `Assets/AgentIcons/` and converted to WPF geometry for the
  TabBar. Provider names and marks remain trademarks of their
  respective owners.
- Tailwind CSS — MIT; see `licenses/tailwindcss/LICENSE`.
- ACP seed manifests — the adapter is Apache-2.0; see `licenses/acp/LICENSE`.
- Kimi Code ACP runtime — bundled under `tools/kimi/`; MIT. See
  `licenses/kimi/LICENSE` and `licenses/kimi/THIRD-PARTY-NOTICES.md` for the
  runtime and its production dependencies (`node-pty`, `node-addon-api`,
  `@mariozechner/clipboard`), all MIT.
- Qwen Code ACP runtime — bundled under `tools/qwen/`; Apache-2.0. See
  `licenses/qwen/LICENSE` and `licenses/qwen/THIRD-PARTY-NOTICES.md` for the
  runtime and its production dependencies (`sharp`, `node-pty`, clipboard
  helpers, and related packages).

The release includes only the ACP package manifests and lockfile. It does not
redistribute Claude Code, the Claude Agent SDK native binary, or an installed
ACP runtime. When the user explicitly installs Agent support, npm downloads the
locked packages from their publishers. Those downloaded packages retain their
own license files and remain subject to their respective terms, including the
[Anthropic legal agreements](https://code.claude.com/docs/en/legal-and-compliance).

Copies of the license and notice files for the binary dependencies shipped by
PSX are stored under `licenses/` and included in the portable release.

Unlike the Claude ACP adapter (installed on first use from the seed lockfile),
the Kimi Code and Qwen Code runtimes ARE redistributed inside the portable
release under `tools/kimi/` and `tools/qwen/`. They are installed at build time
from the pinned lockfiles in `tools/kimi-seed/` and `tools/qwen-seed/`
(Windows x64 only) and remain subject to the license terms of each bundled
package. Qoder CLI is not redistributed; PSX only detects an externally
installed `qodercli` on the user machine.
