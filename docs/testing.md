# PSX 自动化测试指南

> Process-safety rule: run every verification command sequentially. Do not run
> typecheck, lint, Vitest, frontend build/verification, or `tools/test.ps1` at
> the same time. C# tests are assembly-level non-parallel, and Vitest is
> intentionally pinned to one worker
> (`fileParallelism: false`, `maxWorkers: 1`) because this repository's
> jsdom/React harness mutates process-wide browser globals. After an interrupted
> Web test, verify that no test-owned `node` process remains before retrying.

PSX 的测试体系采用“本地优先、CI 兼容”的分层结构。自动化测试不会登录真实 Claude/Kimi，不会消耗模型额度，也不会访问 `%USERPROFILE%\.psx`；所有测试工作目录、日志、覆盖率和报告都位于仓库内的 `TestResults/`。

## 分层结构

- `tests/PSX.Tests/Unit/`：INI/Bridge JSON 解析、设置与主题路径隔离、Theme Picker 状态、ACP Permission 策略、Mode Transition 历史合并，以及 Workspace 上限、Provider 注册冲突、Runtime 去重和关闭隔离等无副作用逻辑。
- `tests/PSX.Tests/Integration/`：线程存储、Fake ACP JSON-RPC 传输、完整会话、权限选择、取消、历史 replay，以及 ACP runtime 安装与升级编排。多 Agent 场景使用测试内 Fake Provider/Runtime，不登录真实服务。
- `tests/PSX.Web.Tests/`：使用 Node 内置测试运行器、jsdom 和 c8，加载与生产页面相同顺序的真实前端脚本（脚本顺序由生产 bundle 测试与 `index.html` 双向核对），覆盖 Markdown、Composer、Permission、Mode Transition、Plan 卡片状态、全局 History dock 与窄窗口自动收起、多 Workspace DOM/路由隔离、桥消息常量表和终端剪贴板桥；`npm run typecheck` 额外对桥契约文件执行 `tsc --checkJs`。
- `tests/PSX.TestAgent/`：可控的 Fake ACP Agent，支持正常响应、通知、并发请求、超时、协议错误、权限请求、取消和历史 replay。
- `tests/PSX.TestNpm/`：作为假 `node.exe` 启动的独立进程，模拟 npm 成功、退出码、网络错误、挂起与安装产物，验证 `AcpRuntimeManager` 而不访问 registry。
- `tests/PSX.DesktopProbe/`：无窗口 WinExe 探针，在与正式应用相同的宿主类型下验证 ConPTY 输出、自然退出、resize 和进程树清理。
- `MainWindowSmokeTests`：在 STA 线程构建 WPF XAML 树，但不显示窗口、不初始化 WebView2 用户数据，也不加载真实配置。

## 统一命令

```powershell
# 日常开发与 GitHub CI：格式、Release build、C# Unit/Integration、前端测试与覆盖率
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Fast

# 发布前：Fast 的全部内容 + WPF/ConPTY Desktop + Release zip 构建与内容审计
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Full

# 按层定位问题
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Unit
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Frontend
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Integration
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Desktop
```

`Fast` 排除桌面探针，以降低 CI 对图形会话和 Windows 运行环境差异的敏感度；`Full` 是本机发布门禁。首次运行前端测试会通过锁文件执行 `npm ci`。npm、NuGet、.NET CLI 和临时目录都在脚本执行期间固定到 `TestResults/`，不会把测试依赖或运行数据写入用户配置目录。

Workspace 自动化重点验证 Agent 与 Terminal 上限口径、`workspaceId` 桥路由、Thread 去重打开、关闭 tombstone、Provider/Runtime 扩展边界，以及多个 `AgentWorkspaceController` 的状态隔离。真实 Claude 登录、多进程并行对话的主观体验、900px 布局和系统进程树观察仍属于桌面人工验收。

## 结果与诊断

- C# TRX、Cobertura 覆盖率和 Microsoft Testing Platform 诊断：`TestResults/dotnet/<suite>/`
- Fake ACP、线程数据与进程探针数据：`TestResults/runtime/`
- 前端生产脚本 bundle 与覆盖率：`TestResults/web/`
- GitHub Actions 无论成功或失败都会上传 `TestResults/`，保留 14 天。

当前覆盖率是观察指标，不设百分比门槛。测试命令通过“最少发现测试数”防止筛选错误导致零测试却显示成功。Release 构建会再次检查 zip，不允许包含测试程序集、Fake Agent、Fake npm、Desktop Probe、测试目录、日志或已安装 ACP runtime。

## Agent React 前端门禁

Agent 前端只有 React 渲染路径。Web 前端源码位于 `frontend/webview/`（外壳）与
`frontend/agent/src/`（Agent TS/TSX），Vite 构建产物位于 `wwwroot/app/` 并随仓库
提交。修改前端源码后必须依次运行 `npm.cmd run build:web`、`npm.cmd run verify:web`、
`npm.cmd run typecheck:web`、`npm.cmd run lint:web`
和 Web 测试。测试直接验证 React 语义 DOM、状态变化和 Bridge payload，不再以
已删除的命令式渲染器作为对照。Release smoke 以
`data-island-state="mounted"`、真实 React DOM、React vendor chunk 请求和浏览器控制台
为证据。

## 仍需人工验收的真实 Agent 流程

以下行为依赖账号、网络、模型版本、服务端状态或主观视觉判断，不应放入稳定自动化门禁：

1. 安装真实 ACP runtime，完成 Claude 登录；同时完成 Kimi Code、Qwen Code、Qoder CLI 的登录验收（Qoder：安装卡 → `runtime/qoder-current` → 托管入口 `login`）。
2. 发起真实普通对话，确认流式文本、Thinking、Tool Activity、上下文用量和文件编辑结果。
3. 进入 Plan 模式并触发 `switch_mode`，确认正文 Markdown、复制、折叠、Composer 位置的动态 ACP 选项和最终选择状态。
4. 分别验证允许、拒绝、Stop、新请求替换旧决策，以及应用重启后的 selected/cancelled/interrupted 历史显示。
5. 验证网络中断、登录过期、额度不足和 provider 进程异常时的错误文案与恢复路径。
6. 在深色/浅色主题、最小窗口和高 DPI 下检查布局、滚动、键盘焦点与可读性。
7. 阅读列人工验收（jsdom 不做布局，此项必须人工）：在 900 / 1000 / 1240 / 1440 / 1800px 窗口宽度下，分别开关 History dock 和 Plan 卡片，用 WebView DevTools 对对话内容列与 Composer 输入行做 `getBoundingClientRect()` 测量。无 History 时，两者在 `920px` 舒适宽度内居中，直到触及视口边距才缩窄；History 打开且空间足够时，两者保持 `920px`，仅在会碰撞时向右让出 dock + 16px，右侧不得出现镜像 dock 宽度的留白；当前 History 宽度导致 `dock + 16px + 920px + 24px` 放不下时，History 应临时收起、阅读列宽度不得跳变，放大后恢复保存的偏好。Plan overlay 开/关不得引起对话区任何重排。验证 <1000px 时 History 保持左侧 dock、Plan 保持内容高度卡片，二者自动收起且手动打开时互斥。
8. 手动更新流程（真实 registry，不可自动化）：启动 PSX 后确认无任何 npm 进程被拉起（启动只推广已 staged 目录）；在 Claude / Kimi / Qwen / Qoder 各自的 Agent Tab 点击 Update，确认 Checking → 结果（Up to date / Restart to update）生命周期在同 runtime 的多个 Tab 同步显示，且新建 Tab 能看到上一次结果；断网后点击 Update 应显示 Retry update，Tooltip 展示后端失败原因，恢复网络后重试成功；Kimi/Qwen 有新版本时确认下载落入 `runtime/*-next`、重启后推广到 `runtime/*-current` 并可正常对话；Qoder 有新版本时确认 `npm install` 带 `--ignore-scripts`、产物在 `runtime/qoder-next`、重启 promote 后对话可用；升级 PSX 到含同版或更新 bundled Kimi/Qwen 的包后，确认 runtime 副本被丢弃并回落 bundled。公开 ZIP 不得包含 `runtime/acp-current` 或 `runtime/qoder-current`。

人工验收前先运行 `-Suite Full`，这样人工步骤只负责真实服务与体验层，不重复验证可自动化的协议和状态机。
