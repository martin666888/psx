# PSX

语言：[English](../../README.md) | [简体中文](README.md)

PSX 是一个面向 AI Agent 工作流的 Windows 桌面终端。它把 Terminal、Agent 对话、任务计划、.NET 和 Portable Node 组合为免安装便携应用。Terminal 解压后即可使用；启动过程不会自行联网，只有需要下载或更新托管 Runtime 时才由用户明确触发。

## 为什么做 PSX

- **便携优先**：以 ZIP 分发，不需要安装器和管理员权限。
- **Terminal 立即可用**：发布包内置 .NET 和 Portable Node，不依赖首次下载。
- **所有模式的统一工作台**：最多三列，可在每列中堆叠和切换 Terminal、ACP Agent 与 Web App 标签。
- **四个托管 Provider**：Claude Code、Kimi Code、Qwen Code、OpenCode 使用受控 Runtime 与入口。
- **托管 Web App**：Kimi Code Web 与 DeepSeek Harness 在专属的本地 WebView
  Workspace 中运行，同时各自保留应用数据与配置。
- **全局 History 与配置**：只有 Terminal 时也能查看历史、Profile、Usage 和经过脱敏的用户级 Config。
- **完整工作流**：对话、工具调用、权限请求和任务计划在界面中分区展示。
- **原生终端能力**：通过 Windows ConPTY 提供真实的多标签终端会话。

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../assets/psx-web-app-workspaces-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="../assets/psx-web-app-workspaces-light.png">
  <img alt="PSX 在三个列中同时运行 DeepSeek Harness、Kimi Code Web 与 Qwen Agent" src="../assets/psx-web-app-workspaces-light.png">
</picture>

## 主要功能

- 基于 Windows ConPTY 的 Terminal 模式，以及最多三列的 Terminal、Agent 与
  Web App 混合 Workspace 标签栈。
- 面向四个 Managed Provider 的 ACP Agent 会话。
- 内嵌 Kimi Code Web 与 DeepSeek Harness Workspace；每种 Web App 在一个 PSX
  进程中最多运行一个 Workspace。
- Workspace 内的 Plan 浮层/窄档 Overlay、工具调用卡片、权限及问题交互。
- 进程级 History、Profile、Usage 与脱敏 Config 面板。
- 基于 shadcn/ui 与 AI Elements 的现代 Agent 界面，内置 Vercel Neutral 深浅色预设。
- 通过 `psx.ini` 与 `theme-presets/` 配置主题与字体。
- 面向 Windows x64、内置 .NET 和 Node 的便携版。

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="../assets/psx-web-app-terminal-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="../assets/psx-web-app-terminal-light.png">
  <img alt="PSX 并排运行 DeepSeek Harness、Kimi Code Web 与 PowerShell 终端" src="../assets/psx-web-app-terminal-light.png">
</picture>

## 下载和使用

请从 GitHub Releases 下载最新版本：

```text
PSX-1.2.0-win-x64-portable.zip
```

1. 下载 ZIP。
2. 解压到当前用户有写入权限的目录。
3. 运行 `PSX.exe`。

PSX 当前面向 Windows x64，并需要 Microsoft Edge WebView2 Runtime。系统未安装 WebView2 时，PSX 会显示提示，而不是打开空白窗口。

公开发布包包含 .NET、Portable Node、Claude 与 DeepSeek Harness 的 seed，以及
Kimi/Qwen/OpenCode 的受控基线；但**不包含** `claude.exe`、DeepSeek Harness
本体或用户安装在 `runtime/` 下的运行环境。

## Runtime 首次安装

Terminal 始终可以直接使用，也不会触发 npm 下载。Kimi、Qwen、OpenCode 可使用发布包内基线；Claude 需要用户确认后才从 npm 官方源安装受控 Runtime。界面显示进度并支持取消/重试，取消返回前会等待进程与输出管道释放，避免锁住 Runtime 目录。

Kimi Code Web 使用随包的 Kimi Runtime。DeepSeek Harness 是独立的 Web App
Workspace：首次安装固定为经过审计的 seed 版本，且只能由用户在应用内明确确认后启动。
DeepSeek Harness 的更新同样完全手动；PSX 不会自动检查或应用更新。

下载或更新的运行环境保存在 `PSX.exe` 旁边的 `runtime/` 目录。同一个解压目录后续会复用；PSX 启动只会在本地提升已 staged 的更新，绝不会自行访问 npm。

PSX 不读取、保存或展示 API Key。请使用 Agent 运行环境支持的方式自行配置凭据和供应商。可选的第三方工具 [CC Switch](https://github.com/farion1231/cc-switch) 可以帮助管理 Claude Code 供应商配置；它不是 PSX 的组成部分，也不是 PSX 或 Anthropic 的官方组件。

## 配置文件 `psx.ini`

PSX 读取应用目录下的 `psx.ini`。便携版中，它与 `PSX.exe` 位于同一目录；从源码运行时，实际生效的是构建输出目录中的副本，例如 `bin/Debug/net10.0-windows/psx.ini`，不是仓库根目录的源文件。

使用 activity rail 中的全局 Theme 菜单可实时预览并确认 `theme-presets/` 或 `%USERPROFILE%\.psx\themes` 中的主题。确认后会校验、备份并原子更新 `psx.ini`；预览本身不会持久化。

常用配置段：

```ini
[terminal]
fontSize=14
fontFamily=Cascadia Code, Consolas, monospace
scrollback=10000

[agent]
fontSize=14
fontFamily=Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Segoe UI Emoji, sans-serif
monoFontFamily=PSX Maple Mono, Segoe UI Emoji, Microsoft YaHei UI, monospace

[shell]
defaultProfile=powershell
```

主题颜色与 Terminal/Agent 字体可实时更新；其他尚未暴露 UI 的手工配置在下次启动生效。

## 从源码构建

要求：

- Windows 10/11 x64
- `global.json` 指定的 .NET SDK
- PowerShell
- 发布构建需要联网下载 Portable Node，除非显式使用已存在且校验通过的缓存

```powershell
dotnet build PSX.slnx
dotnet run --project PSX.csproj
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
```

运行自动化测试门禁：

```powershell
# 日常开发与 CI
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Fast

# 本机发布前全量门禁（含 Windows 桌面探针与发布包校验）
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Full
```

测试分层、仓库隔离约束、诊断产物以及仍需人工执行的真实 Agent 验收项，见
[自动化测试指南](../testing.md)。

发布包输出到 `bin/releases/`。

## 项目结构

- `Services/`：Terminal、Agent、ACP 运行环境、设置和会话持久化
- `Models/`：设置、Terminal 和 Agent DTO
- `ViewModels/`：WPF UI 状态
- `Controls/`：WPF 控件和终端宿主
- `Helpers/`：ConPTY 与进程辅助代码
- `wwwroot/`：WebView2 前端、xterm.js 和 Agent 界面
- `theme-presets/`：主题配置模板
- `tools/build-release.ps1`：便携发布包构建脚本
- `tools/acp-seed/`：用户确认安装 Agent 时使用的依赖清单与 Windows 平台策略；实际安装解析 npm 官方源的最新兼容版本
- `tools/dsh-seed/`：DeepSeek Harness 首次安装时使用的固定依赖清单，必须由用户确认后执行

## 维护者发布说明

发布脚本会发布自包含 WPF 应用，下载并校验 Portable Node 22.23.1，然后打入 ACP seed、前端和许可证文件。压缩前会检查所有必需文件，并拒绝包含 `claude.exe`、`runtime/acp-current`、日志或临时文件的公开包。

推荐包名：

```text
PSX-<version>-win-x64-portable.zip
```

`-SkipNodeDownload` 只允许使用已经存在且 SHA-256 校验通过的 Node 缓存；缓存不存在或不正确时会直接失败。

## 许可证

PSX 使用 [MIT License](../../LICENSE.txt)。随包依赖的许可证和 NOTICE 信息见 [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) 与 `licenses/` 目录。Anthropic 组件由用户后续安装，并受其自身条款约束。
