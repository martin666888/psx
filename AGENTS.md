# Repository Guidelines

## Project Structure & Module Organization

PSX is a Windows desktop terminal built with C#, WPF, WebView2, xterm.js, and ConPTY. The root contains the application entry points and production project, `PSX.csproj`; test projects and probes live under `tests/`. Keep domain records in `Models/`, UI state in `ViewModels/`, WPF controls in `Controls/`, runtime and bridge logic in `Services/`, and native interop in `Helpers/`. The WebView frontend lives in `wwwroot/`. Configuration and visual resources belong in `psx.ini`, `theme-presets/`, `Themes/`, and `Assets/`. Release tooling is under `tools/`; documentation is under `docs/`.

## Build, Test, and Development Commands

- `dotnet build PSX.slnx` - restore packages and compile the application.
- `dotnet run --project PSX.csproj` - launch a development build on Windows.
- `dotnet format PSX.slnx --verify-no-changes` - check standard .NET formatting.
- `powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Fast` - run the local/CI unit, integration, frontend, coverage, format, and Release build gate.
- `powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Full` - add WPF/ConPTY desktop probes and validate the portable Release package.
- `powershell -ExecutionPolicy Bypass -File tools/build-release.ps1` - create the self-contained Windows x64 package in `bin/releases/`; this may download the verified Portable Node archive but never pre-installs the ACP runtime.

Use the SDK selected by `global.json`. The release script assembles the public portable package; users install the Agent runtime later from Agent mode after explicit confirmation.

## Coding Style & Naming Conventions

Use four spaces in C# and follow existing .NET conventions: `PascalCase` for types, properties, methods, and public members; `camelCase` for parameters and locals; `_camelCase` for private fields. Nullable reference types and implicit usings are enabled. Keep XAML names descriptive and JavaScript/CSS consistent with nearby modules. Preserve asynchronous patterns already used: `ConfigureAwait(false)` in service code and `Dispatcher.BeginInvoke` when crossing to UI-owned state. Changes to bridge message types must be mirrored in both C# and `wwwroot/js/`.

## Testing Guidelines

Use `tests/PSX.Tests` for C# Unit, Integration, and Desktop categories; `tests/PSX.Web.Tests` for jsdom frontend tests; `tests/PSX.TestAgent` for deterministic ACP protocol scenarios; `tests/PSX.TestNpm` for process-level ACP runtime installation scenarios; and `tests/PSX.DesktopProbe` for WinExe-hosted ConPTY checks. Name cases by behavior, such as `SaveSettings_InvalidColor_UsesFallback`. Keep every test workspace, dependency cache, diagnostic, and coverage artifact under the ignored repository `TestResults/` directory; tests must never touch the user's real `%USERPROFILE%\.psx`. Coverage is reported but has no percentage gate yet. Run `tools/test.ps1 -Suite Fast` during development and `-Suite Full` before a release. Real provider login, quota, network failure, and subjective UI checks remain manual as documented in `docs/testing.md`.

## Agent Slash Command Contract

The slash-command menu is PSX's complete public command surface. A leading command is executable only when it is a PSX built-in command or appears in the current filtered ACP `available_commands_update` catalog. Reject every other leading `/command` before `session/prompt`; it must not enter busy state or be written to thread history. Slash-like text inside a normal prompt is ordinary text. Keep the JavaScript composer validation and the authoritative `AcpAgentSessionService` gate synchronized, and direct Claude Code-only interactive commands to `/terminal` instead of maintaining a command blacklist.

## Agent Inspector Contract

The resizable right-side Agent region is an Inspector with peer `Plan` and `History` tabs. Keep thread messages in the main conversation only: history lists, loading states, and history errors belong in the History tab and must never be appended as chat cards. Plan and History retain independent DOM and scroll state. New threads select Plan; loading an existing thread from History keeps History selected. When changing the `agent_threads`, `agent_history_error`, or `agent_thread_loaded` bridge payloads, update both Agent backends and the frontend dispatcher.

## Agent Provider Architecture

Agent mode uses one shared ACP session engine with curated PSX providers. `AcpAgentSessionService` must depend on `IAgentProviderRegistry.DefaultProvider`; it must not contain provider package paths, executable names, hidden-command lists, or native CLI launch commands. Provider identity and compatibility policy belong in `IAcpAgentProvider`, runtime installation and `AcpProcessSpec` creation belong in `IAcpAgentRuntime`, and `AcpJsonRpcTransport` remains provider-agnostic. Until an Agent selector is implemented, Claude is the only registered default provider. Unknown thread providers must open transcript-only and must never be restored through the default adapter. Do not add provider-name conditionals to the shared session engine or expose arbitrary user-defined ACP executables.

## Commit & Pull Request Guidelines

This repository has no commit history from which to infer a convention. Use short, imperative subjects (for example, `Fix ACP session shutdown`) and keep commits focused. Pull requests should explain the problem and solution, list verification steps, link related issues, and include screenshots or recordings for UI changes. Call out changes to runtime packaging, `psx.ini`, or the implicit C#/JavaScript bridge contract.

## Security & Configuration Tips

Do not commit generated `bin/`, `obj/`, runtime downloads, logs, or user data from `%USERPROFILE%\.psx\`. Never place secrets in `psx.ini` or frontend assets.
