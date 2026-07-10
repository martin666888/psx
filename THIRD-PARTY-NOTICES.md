# Third-Party Notices

PSX source code is licensed under the MIT License in `LICENSE.txt`. That license
does not replace the licenses of the third-party components listed below.

The portable release redistributes:

- Microsoft .NET runtime — MIT and accompanying third-party notices.
- Microsoft WebView2 SDK — see the bundled Microsoft license and notice.
- CommunityToolkit.Mvvm — MIT and accompanying third-party notices.
- Microsoft.Extensions.DependencyInjection — MIT and accompanying third-party notices.
- Node.js and npm — licenses are included inside `tools/node/` in the release.
- xterm.js and its addons — MIT; see `wwwroot/vendor/xterm/LICENSE`.
- ACP seed manifests — the adapter is Apache-2.0; see `licenses/acp/LICENSE`.

The release includes only the ACP package manifests and lockfile. It does not
redistribute Claude Code, the Claude Agent SDK native binary, or an installed
ACP runtime. When the user explicitly installs Agent support, npm downloads the
locked packages from their publishers. Those downloaded packages retain their
own license files and remain subject to their respective terms, including the
[Anthropic legal agreements](https://code.claude.com/docs/en/legal-and-compliance).

Copies of the license and notice files for the binary dependencies shipped by
PSX are stored under `licenses/` and included in the portable release.
