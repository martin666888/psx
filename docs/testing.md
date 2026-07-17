# PSX 自动化测试指南

PSX 的测试体系采用“本地优先、CI 兼容”的分层结构。自动化测试不会登录真实 Claude/Kimi，不会消耗模型额度，也不会访问 `%USERPROFILE%\.psx`；所有测试工作目录、日志、覆盖率和报告都位于仓库内的 `TestResults/`。

## 分层结构

- `tests/PSX.Tests/Unit/`：INI/Bridge JSON 解析、设置与主题路径隔离、Theme Picker 状态、ACP Permission 策略和 Mode Transition 历史合并等无副作用逻辑。
- `tests/PSX.Tests/Integration/`：线程存储、Fake ACP JSON-RPC 传输、完整会话、权限选择、取消、历史 replay，以及 ACP runtime 安装与升级编排。
- `tests/PSX.Web.Tests/`：使用 Node 内置测试运行器、jsdom 和 c8，加载与生产页面相同顺序的真实前端脚本，覆盖 Markdown、Composer、Permission、Mode Transition 和 Plan Inspector。
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

## 结果与诊断

- C# TRX、Cobertura 覆盖率和 Microsoft Testing Platform 诊断：`TestResults/dotnet/<suite>/`
- Fake ACP、线程数据与进程探针数据：`TestResults/runtime/`
- 前端生产脚本 bundle 与覆盖率：`TestResults/web/`
- GitHub Actions 无论成功或失败都会上传 `TestResults/`，保留 14 天。

当前覆盖率是观察指标，不设百分比门槛。测试命令通过“最少发现测试数”防止筛选错误导致零测试却显示成功。Release 构建会再次检查 zip，不允许包含测试程序集、Fake Agent、Fake npm、Desktop Probe、测试目录、日志或已安装 ACP runtime。

## 仍需人工验收的真实 Agent 流程

以下行为依赖账号、网络、模型版本、服务端状态或主观视觉判断，不应放入稳定自动化门禁：

1. 安装真实 ACP runtime，完成 Claude 登录；如果未来启用 Kimi provider，也完成对应登录。
2. 发起真实普通对话，确认流式文本、Thinking、Tool Activity、上下文用量和文件编辑结果。
3. 进入 Plan 模式并触发 `switch_mode`，确认正文 Markdown、复制、折叠、Composer 位置的动态 ACP 选项和最终选择状态。
4. 分别验证允许、拒绝、Stop、新请求替换旧决策，以及应用重启后的 selected/cancelled/interrupted 历史显示。
5. 验证网络中断、登录过期、额度不足和 provider 进程异常时的错误文案与恢复路径。
6. 在深色/浅色主题、最小窗口和高 DPI 下检查布局、滚动、键盘焦点与可读性。

人工验收前先运行 `-Suite Full`，这样人工步骤只负责真实服务与体验层，不重复验证可自动化的协议和状态机。
