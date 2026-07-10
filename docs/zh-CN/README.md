# PSX

语言：[English](../../README.md) | [简体中文](README.md)

PSX 是一个面向 AI Agent 使用场景的 Windows 桌面终端。它的重点不是“再做一个
PowerShell”，而是把终端、Agent 对话、任务计划和运行环境打包成一个轻量、免安装、
解压即用的工具。

很多漂亮的 Agent 应用都需要下载安装器，安装后再下载运行时或额外组件。对于公司内部电脑、
受限网络、无管理员权限、不能随便安装软件的环境，这种方式并不友好。PSX 的目标是提供另一种
分发方式：下载一个 zip，解压，运行 `PSX.exe`，即可开始使用。

## 为什么做 PSX

PSX 的核心价值是：

- **免安装**：portable zip 解压即用，不需要安装器。
- **运行时随包提供**：发布包内置 .NET runtime、portable Node 和预安装的 ACP runtime。
- **适合受限环境**：尽量减少首次启动时的下载和安装步骤，更适合公司电脑和内网环境。
- **不只是终端**：除了普通 Terminal 模式，还提供专门的 Agent 面板。
- **Agent 工作流更清楚**：Agent 的对话、工具调用、权限请求和计划清单在界面中分区展示。
- **右侧计划面板**：模型生成的计划会显示在右侧，不再混在聊天流里，执行状态更容易扫一眼看懂。

PSX 适合想在 Windows 上运行 Claude Code / ACP Agent / node / npm / git 等命令行工具，
同时又希望界面更轻、更可控、更适合公司电脑分发的人。

![PSX Agent 模式和右侧计划面板](../assets/psx-agent-plan-panel.png)

## 主要功能

- Terminal 模式：基于 Windows ConPTY 的真实终端会话。
- 多标签页：同时打开多个 shell。
- Agent 模式：面向 ACP-compatible Claude sessions 的独立对话界面。
- 计划面板：右侧显示 Agent 当前任务清单，已完成任务自动打勾划线。
- 工具调用展示：Agent 调用工具时以折叠卡片形式显示输入和输出。
- 权限/问题交互：Agent 请求确认或补充信息时在界面中处理。
- 主题配置：通过 `psx.ini` 和 `theme-presets/` 调整界面和终端颜色。
- Portable release：面向 Windows x64 的自包含 zip 发布包。

## 下载和使用

请从 GitHub Releases 下载最新版本：

```text
PSX-v0.1.0-win-x64-portable.zip
```

使用方式：

1. 下载 zip。
2. 解压到任意目录。
3. 双击运行 `PSX.exe`。

PSX 当前面向 Windows x64。运行时需要 Microsoft Edge WebView2 Runtime。如果系统未安装
WebView2，PSX 会显示提示，而不是打开空白窗口。

发布包已经包含 .NET runtime、portable Node 和 ACP runtime。正常情况下，用户不需要自己安装
.NET、Node 或 Claude adapter。

## 配置文件 psx.ini

PSX 会读取应用目录下的 `psx.ini` 作为当前生效配置。对于 portable 发布包，应用目录就是
`PSX.exe` 所在目录；从源码运行时，应用目录通常是仓库根目录。

`theme-presets/` 里的文件只是模板，不会自动生效。使用预置主题的方法：

1. 关闭 PSX。
2. 备份当前生效的 `psx.ini`。
3. 从 `theme-presets/` 复制一个 preset ini 文件到应用目录。
4. 将复制出来的文件重命名为 `psx.ini`。
5. 重新启动 PSX。

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

主题颜色主要分布在：

- `[theme]`：WPF 和 Agent 共用的背景、表面、文字、边框、强调色和状态色
- `[agentTheme]`：Agent 专用的代码块、按钮、权限卡片、遮罩和焦点颜色
- `[terminalColors]`：xterm.js 的前景色、光标、选区和 ANSI 调色板

大多数配置修改后需要重启 PSX，因为 WPF 资源和 WebView 前端会在启动时读取主题值。

## 从源码构建

要求：

- Windows 10/11 x64
- `global.json` 指定的 .NET SDK
- PowerShell
- 发布构建需要网络，因为脚本会下载 portable Node 并安装 ACP runtime

构建：

```powershell
dotnet build PSX.slnx
```

运行：

```powershell
dotnet run --project PSX.csproj
```

创建 portable 发布包：

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
```

发布包会输出到：

```text
bin/releases/
```

## 技术实现

PSX 使用以下技术构建：

- C# / WPF：桌面窗口、应用状态和原生 UI。
- WebView2：承载终端和 Agent 前端界面。
- xterm.js：渲染终端内容、ANSI 控制序列、输入和滚动。
- Windows ConPTY：提供真实伪控制台会话。
- ACP runtime：驱动 Agent 模式中的 Claude 会话。


## 项目结构

- `Services/`：终端、Agent、ACP runtime、设置和线程持久化
- `Models/`：设置、终端和 Agent DTO
- `ViewModels/`：WPF UI 状态
- `Controls/`：WPF 控件和终端宿主集成
- `Helpers/`：ConPTY 和进程辅助代码
- `wwwroot/`：WebView2 前端，包括 xterm.js 和 Agent 模式
- `theme-presets/`：预置主题配置
- `tools/build-release.ps1`：portable 发布包构建脚本
- `tools/acp-seed/`：内置 ACP runtime 的 seed manifest

## 维护者发布说明

发布脚本会创建一个自包含的 Windows x64 portable 包。它会发布 WPF 应用，下载 portable
Node，在 `runtime/acp-current/` 中运行 `npm ci`，并验证 ACP adapter 和内置
`claude.exe` 是否存在。

推荐的包名格式：

```text
PSX-v<version>-win-x64-portable.zip
```

如果生成的文件仍然使用 `0.0.0`，请在 `PSX.csproj` 中添加 `<Version>` 属性。

## 许可证

见 [LICENSE.txt](../../LICENSE.txt)。
