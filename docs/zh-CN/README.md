# PSX

语言：[English](../../README.md) | [简体中文](README.md)

PSX 是一个面向 AI Agent 工作流的 Windows 桌面终端。它把 Terminal、Agent 对话、任务计划、.NET 和 Portable Node 组合为免安装便携应用。Terminal 解压后即可使用；Agent 运行环境只会在用户明确确认后，从官方 npm 源安装。

## 为什么做 PSX

- **便携优先**：以 ZIP 分发，不需要安装器和管理员权限。
- **Terminal 立即可用**：发布包内置 .NET 和 Portable Node，不依赖首次下载。
- **Agent 明确安装**：首次进入 Agent 页面时显示安装卡片，不会在启动或发送第一条消息时偷偷下载。
- **完整工作流**：对话、工具调用、权限请求和任务计划在界面中分区展示。
- **原生终端能力**：通过 Windows ConPTY 提供真实的多标签终端会话。

![PSX Agent 模式和右侧计划面板](../assets/psx-agent-plan-panel.png)

## 主要功能

- 基于 Windows ConPTY 的 Terminal 模式和多标签页。
- 面向 ACP-compatible Claude 会话的 Agent 模式。
- 右侧任务计划面板、工具调用卡片、权限及问题交互。
- 通过 `psx.ini` 和 `theme-presets/` 配置主题与字体。
- 面向 Windows x64、内置 .NET 和 Node 的便携版。

## 下载和使用

请从 GitHub Releases 下载最新版本：

```text
PSX-1.0.0-win-x64-portable.zip
```

1. 下载 ZIP。
2. 解压到当前用户有写入权限的目录。
3. 运行 `PSX.exe`。

PSX 当前面向 Windows x64，并需要 Microsoft Edge WebView2 Runtime。系统未安装 WebView2 时，PSX 会显示提示，而不是打开空白窗口。

公开发布包包含 .NET、Portable Node 和 ACP seed 清单，但**不包含** `claude.exe` 或已经安装好的 ACP 运行环境。

## Agent 首次安装

Terminal 始终可以直接使用，也不会触发 npm 下载。第一次进入 Agent 页面时，PSX 会显示安装说明；点击“安装 Agent 运行环境”后，才会从官方 npm 源下载固定版本的 ACP 依赖。界面会显示进度，并支持取消、失败重试。安装成功后无需重启即可使用 Agent。

运行环境保存在 `PSX.exe` 旁边的 `runtime/` 目录。同一个解压目录后续会直接复用；重新解压到一个新目录则需要再次安装。因此请将 PSX 放在有写权限的位置，并保留原目录以复用运行环境。首次安装需要能够访问 npm 官方源。

PSX 不读取、保存或展示 API Key。请使用 Agent 运行环境支持的方式自行配置凭据和供应商。可选的第三方工具 [CC Switch](https://github.com/farion1231/cc-switch) 可以帮助管理 Claude Code 供应商配置；它不是 PSX 的组成部分，也不是 PSX 或 Anthropic 的官方组件。

## 配置文件 `psx.ini`

PSX 读取应用目录下的 `psx.ini`。便携版中，它与 `PSX.exe` 位于同一目录；从源码运行时，实际生效的是构建输出目录中的副本，例如 `bin/Debug/net10.0-windows/psx.ini`，不是仓库根目录的源文件。

`theme-presets/` 中的文件只是模板。使用预设主题时：

1. 关闭 PSX。
2. 备份当前 `psx.ini`。
3. 将预设文件复制到应用目录并重命名为 `psx.ini`。
4. 重新启动 PSX。

常用配置段：

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

大多数配置修改需要重启 PSX，因为 WPF 资源和 WebView 前端在启动时读取主题值。

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
- `tools/acp-seed/`：用户确认安装 Agent 时使用的固定依赖清单

## 维护者发布说明

发布脚本会发布自包含 WPF 应用，下载并校验 Portable Node 22.23.1，然后打入 ACP seed、前端和许可证文件。压缩前会检查所有必需文件，并拒绝包含 `claude.exe`、`runtime/acp-current`、日志或临时文件的公开包。

推荐包名：

```text
PSX-<version>-win-x64-portable.zip
```

`-SkipNodeDownload` 只允许使用已经存在且 SHA-256 校验通过的 Node 缓存；缓存不存在或不正确时会直接失败。

## 许可证

PSX 使用 [MIT License](../../LICENSE.txt)。随包依赖的许可证和 NOTICE 信息见 [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) 与 `licenses/` 目录。Anthropic 组件由用户后续安装，并受其自身条款约束。
