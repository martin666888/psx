# Repository Guidelines

## Project Structure & Module Organization

PSX is a Windows desktop terminal built with C#, WPF, WebView2, xterm.js, and ConPTY. The root contains the application entry points and the single project, `PSX.csproj`. Keep domain records in `Models/`, UI state in `ViewModels/`, WPF controls in `Controls/`, runtime and bridge logic in `Services/`, and native interop in `Helpers/`. The WebView frontend lives in `wwwroot/`. Configuration and visual resources belong in `psx.ini`, `theme-presets/`, `Themes/`, and `Assets/`. Release tooling is under `tools/`; documentation is under `docs/`.

## Build, Test, and Development Commands

- `dotnet build PSX.slnx` - restore packages and compile the application.
- `dotnet run --project PSX.csproj` - launch a development build on Windows.
- `dotnet format PSX.slnx --verify-no-changes` - check standard .NET formatting.
- `powershell -ExecutionPolicy Bypass -File tools/build-release.ps1` - create the self-contained Windows x64 package in `bin/releases/`; this may download Node and install the ACP runtime.

Use the SDK selected by `global.json`. A plain `dotnet publish` does not assemble the complete Agent runtime.

## Coding Style & Naming Conventions

Use four spaces in C# and follow existing .NET conventions: `PascalCase` for types, properties, methods, and public members; `camelCase` for parameters and locals; `_camelCase` for private fields. Nullable reference types and implicit usings are enabled. Keep XAML names descriptive and JavaScript/CSS consistent with nearby modules. Preserve asynchronous patterns already used: `ConfigureAwait(false)` in service code and `Dispatcher.BeginInvoke` when crossing to UI-owned state. Changes to bridge message types must be mirrored in both C# and `wwwroot/js/`.

## Testing Guidelines

There is currently no test project or coverage threshold. For every change, run `dotnet build PSX.slnx` and manually exercise the affected terminal or Agent workflow. Verify startup, tab lifecycle, shutdown, and theme/config reload when relevant. If adding tests, create a dedicated `*.Tests` project and name cases by behavior, such as `SaveSettings_InvalidColor_UsesFallback`.

## Commit & Pull Request Guidelines

This repository has no commit history from which to infer a convention. Use short, imperative subjects (for example, `Fix ACP session shutdown`) and keep commits focused. Pull requests should explain the problem and solution, list verification steps, link related issues, and include screenshots or recordings for UI changes. Call out changes to runtime packaging, `psx.ini`, or the implicit C#/JavaScript bridge contract.

## Security & Configuration Tips

Do not commit generated `bin/`, `obj/`, runtime downloads, logs, or user data from `%USERPROFILE%\.psx\`. Never place secrets in `psx.ini` or frontend assets.
