# PSX

Languages: [English](README.md) | [简体中文](docs/zh-CN/README.md)

PSX is a Windows desktop terminal for AI Agent workflows. It is not trying to be
another PowerShell. It packages a terminal, an Agent conversation panel, task
plans, and the required runtime pieces into a lightweight portable app that can
be unzipped and run directly.

Many polished Agent applications start with an installer, then download more
runtime components after installation. That is often unfriendly on company
laptops, locked-down networks, machines without admin rights, or environments
where users cannot freely install software. PSX is built around a different
distribution model: download one zip, unzip it, run `PSX.exe`, and start using
it.

## Why PSX Exists

The core value of PSX is:

- **Portable first**: distributed as a zip, no installer required.
- **Runtime included**: the release package includes the .NET runtime, portable
  Node, and a preinstalled ACP runtime.
- **Friendly to restricted environments**: fewer first-run downloads and fewer
  install-time assumptions.
- **More than a terminal**: PSX includes a dedicated Agent panel in addition to
  normal terminal tabs.
- **Clear Agent workflow**: conversations, tool calls, permission prompts, and
  task plans are separated in the interface.
- **Right-side plan panel**: model-generated plans are shown beside the
  conversation instead of being buried inside the chat stream.

PSX is for people who want to run Claude Code / ACP Agent / node / npm / git and
other command-line tools on Windows, while keeping the experience lightweight,
controllable, and easy to distribute on work machines.

![PSX Agent mode with right-side plan panel](docs/assets/psx-agent-plan-panel.png)

## Features

- Terminal mode with real Windows ConPTY sessions.
- Multiple terminal tabs.
- Agent mode for ACP-compatible Claude sessions.
- Right-side task plan panel with completed items checked and struck through.
- Tool call cards for Agent inputs and outputs.
- Permission and question prompts handled in the UI.
- Theme and font configuration through `psx.ini` and `theme-presets/`.
- Self-contained portable release for Windows x64.

## Download

Download the latest Windows build from GitHub Releases:

```text
PSX-v0.1.0-win-x64-portable.zip
```

Usage:

1. Download the zip.
2. Extract it anywhere.
3. Run `PSX.exe`.

PSX currently targets Windows x64. Microsoft Edge WebView2 Runtime is required.
If WebView2 is not installed, PSX will show a prompt instead of opening a blank
window.

The release package already includes the .NET runtime, portable Node, and ACP
runtime. In normal use, users do not need to install .NET, Node, or the Claude
adapter separately.

## Configuration

PSX reads its active configuration from `psx.ini` in the application directory.
In a portable release, this is the same folder as `PSX.exe`. In a source
checkout, it is the repository root.

The `theme-presets/` folder contains templates only. To use a preset:

1. Close PSX.
2. Back up the active `psx.ini`.
3. Copy one preset ini file from `theme-presets/` to the application directory.
4. Rename the copied file to `psx.ini`.
5. Start PSX again.

Common sections:

```ini
[terminal]
fontSize=14
fontFamily=Cascadia Code, Consolas, monospace
scrollback=10000

[agent]
fontSize=14
fontFamily=Cascadia Code, Segoe UI, Microsoft YaHei UI, Microsoft YaHei, sans-serif
monoFontFamily=Cascadia Code, Consolas, monospace

[shell]
defaultProfile=powershell
```

Theme colors live in:

- `[theme]` - shared WPF and Agent background, surface, text, border, accent,
  and state colors
- `[agentTheme]` - Agent-specific code blocks, buttons, permission cards,
  overlays, and focus colors
- `[terminalColors]` - xterm.js foreground, cursor, selection, and ANSI color
  palette

Most configuration changes require restarting PSX because WPF resources and the
WebView frontend read theme values during startup.

## Build From Source

Requirements:

- Windows 10/11 x64
- .NET SDK selected by `global.json`
- PowerShell
- Network access for release builds, because the release script downloads
  portable Node and installs the ACP runtime

Build:

```powershell
dotnet build PSX.slnx
```

Run:

```powershell
dotnet run --project PSX.csproj
```

Create a portable release package:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
```

The release zip is written to:

```text
bin/releases/
```

## Technical Design

PSX is built with:

- C# / WPF for the desktop window, application state, and native UI.
- WebView2 for the terminal and Agent frontend.
- xterm.js for terminal rendering, ANSI sequences, input, and scrollback.
- Windows ConPTY for real pseudo-console sessions.
- ACP runtime for Claude sessions in Agent mode.

These technologies are implementation details. The goal of PSX is to provide a
lightweight Windows desktop entry point for AI Agent CLI workflows, especially
in environments where installing a heavier application is inconvenient.

## Project Structure

- `Services/` - terminal, Agent, ACP runtime, settings, and thread persistence
- `Models/` - settings, terminal, and Agent DTOs
- `ViewModels/` - WPF UI state
- `Controls/` - WPF controls and terminal host integration
- `Helpers/` - ConPTY and process helpers
- `wwwroot/` - WebView2 frontend for xterm.js and Agent mode
- `theme-presets/` - preset theme configuration files
- `tools/build-release.ps1` - portable release builder
- `tools/acp-seed/` - seed manifest for the bundled ACP runtime

## Release Notes For Maintainers

The release script creates a self-contained Windows x64 portable package. It
stages the published WPF app, downloads portable Node, runs `npm ci` inside
`runtime/acp-current/`, and validates that the ACP adapter and bundled
`claude.exe` are present.

Recommended package name:

```text
PSX-v<version>-win-x64-portable.zip
```

If the generated file still uses `0.0.0`, add a `<Version>` property to
`PSX.csproj`.

## License

See [LICENSE.txt](LICENSE.txt).
