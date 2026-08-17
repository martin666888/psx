# PSX 自动化测试与发布证据

PSX 的门禁以本地可复现为第一原则。所有验证必须串行执行；不要并发运行
typecheck、lint、Vitest、前端构建/校验或 `tools/test.ps1`。C# 测试通过
`[assembly: DoNotParallelize]` 禁止并行，Vitest 固定
`fileParallelism: false`、`maxWorkers: 1`，因为 jsdom/React Harness 会修改
进程级浏览器全局。完整 Web 门禁通过 `tools/run-web-tests.ps1` 让每个测试文件
在独立的受限进程树中串行执行，前一个进程退出后才启动下一个，并由 Vitest
官方 blob reporter 合并测试与覆盖率报告。中断 Web 测试后，重试前先确认没有测试所属的 `node`
进程残留。

自动化测试不会登录真实 Provider、消耗模型额度或读取
`%USERPROFILE%\.psx`。测试工作区、依赖缓存、浏览器、日志、TRX、覆盖率和
视觉差异全部位于已忽略的 `TestResults/`。

## 测试层与真实 runner 基线

| 层级 | 内容 | 最低发现数 |
|---|---|---:|
| Unit | 纯逻辑、解析、状态与架构约束 | 446 |
| Integration | Fake ACP/npm、持久化、Runtime 与进程边界 | 107 |
| Desktop | WPF/ConPTY 桌面探针 | 2 |
| Fast | Unit + Integration | 553 |
| Full | 全部 C# 测试 | 555 |
| Web | Vitest/jsdom 契约 | 317 |

数字来自 Microsoft Testing Platform TRX 和 Vitest JSON reporter；C# 数量包含
`DataRow` 展开结果，不是源码中的 `[TestMethod]` 个数。只有明确删除测试时
才允许降低门槛，并必须在提交说明中记录原因。

## 常用命令

```powershell
# 日常开发/本地 CI 门禁；不下载、不启动浏览器
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Fast

# 正式本地发布门禁
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Full

# 分层定位
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Unit
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Integration
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Desktop
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Frontend
```

Fast 严格按以下顺序运行：

```text
依赖状态检查
→ Release build
→ C# Unit/Integration + coverage
→ TypeScript typecheck
→ ESLint
→ verify:web
→ Vitest/jsdom + coverage
→ dotnet format verify
```

Full 在 Fast 能力上加入 Desktop、便携 ZIP 构建与浏览器 smoke、锁版本
Chromium 的视觉/axe 与 Markdown 长对话性能门禁，以及 NuGet/npm 官方源依赖审计。Full 必须使用
Node 22；脚本会优先使用 `TestResults/node22/` 或发布 staging 中的便携 Node，
不会误用 PATH 上的其他主版本。

## npm 恢复状态

`TestResults/npm-state.json` 记录 `package-lock.json` SHA-256、Node 主版本、
npm 版本和最后一次成功恢复时间。stamp 完整匹配且
`npm ls --depth=0` 成功时跳过 `npm ci`；缺失、损坏、lock/toolchain 变化或
依赖树异常时执行干净恢复。只有成功的 `npm ci` 才写 stamp。

需要强制重装时使用：

```powershell
powershell -ExecutionPolicy Bypass -File tools/test.ps1 -Suite Fast -ForceFrontendRestore
```

## 覆盖率与机器可读结果

- C#（Fast/Full）：行 ≥ 67%，分支 ≥ 55%。
- Web：行 ≥ 78%，语句 ≥ 75%，函数 ≥ 75%，分支 ≥ 65%。
- TRX/Cobertura：`TestResults/dotnet/<suite>/`。
- Vitest JSON：`TestResults/web/test-summary.json`。
- Web coverage：`TestResults/web/coverage/coverage-summary.json`。

PowerShell 读取 JSON/XML，不解析本地化控制台文本。当前基线通过并不允许
以修改 reporter 输出或降低门槛的方式掩盖测试/覆盖率回退。

## 两级 UI 门禁

Fast 仅使用 Vitest/jsdom 验证高价值结构契约：Terminal-only 首次启动的
History/Profile/Provider 图标、代表性全局与 Agent 弹层的重复触发/外点/Esc/
焦点恢复、响应式状态抽样，以及 React 生命周期错误。Fast 不准备或启动
Playwright Chromium。

Full 使用 `playwright@1.62.0` 清单锁定的 Chrome for Testing 151.0.7922.34
（revision 1234）。浏览器只解压到
`TestResults/playwright-browsers/`，不会安装进系统，也不会进入发布包。首次
准备命令：

```powershell
npm.cmd run prepare:visual
```

若网络需要代理，可只为当前 PowerShell 会话设置 `HTTP_PROXY`、
`HTTPS_PROXY`、`ALL_PROXY` 后重试。准备脚本支持断点续传；浏览器已完整存在
时命令立即返回，不再联网。

视觉门禁固定 device scale、locale、主题、reduced motion 和字体等待，覆盖
720/719、520/519、400/399px Pane 边界、非对称 Agent Composer 底边、
Terminal-only History/Profile、History 推移与 12px gap、宽/窄 Plan、
Permission、Elicitation、Usage Dialog 以及深浅主题。每个场景同时执行 axe，
serious/critical 直接失败；console error、page error、资源失败也直接失败。

```powershell
# 只比较，不创建或覆盖基线
npm.cmd run test:visual

# 仅在有意修改 UI 时更新；必须人工查看 actual/diff 后单独提交
npm.cmd run update:visual
```

基线位于 `tests/PSX.Web.Tests/visual-baselines/windows-chromium/`。差异超过总
像素 0.15% 失败；actual、expected diff 和 axe 报告位于
`TestResults/visual/`。普通 `test:visual` 永远不会自动接受差异。

Markdown 性能场景固定为 500 turn、约 1 MiB Markdown、20 个代码块、20 个公式、
5 个 Mermaid 图和 240 次流式增量。它验证末条可见时间、离屏重型资源、P95 帧时
与掉帧率，并将机器可读结果写入
`TestResults/visual/markdown-performance.json`。可单独运行：

```powershell
npm.cmd run test:markdown-performance
```

## 依赖审计

Full 默认执行：

```text
dotnet list PSX.slnx package --vulnerable --include-transitive
npm audit --omit=dev --audit-level=low --registry=https://registry.npmjs.org
npm audit --audit-level=high --registry=https://registry.npmjs.org
```

正式发布证据要求 NuGet 0、生产 npm 0、完整 npm 无 high/critical。仅为离线
定位其他 Full 失败时可用 `-SkipDependencyAudit`；输出会明确标记
“Full diagnostics passed, release gate incomplete”，不能作为发布通过证据。
npmmirror advisories 不可用或返回 404 也不能视为审计通过。

## 仍需人工验收

自动化完成后，正式发布前仍需一次真实 Terminal + Agent 验收：

1. 首次启动只有 Terminal 时，直接打开 History，确认保存的 Profile、Provider
   图标、Usage/Config 均正确；关闭最后一个 Agent 后再次验证同样行为。
2. 真实登录五个 Managed Provider，验证流式文本、Thinking、Tool、Permission、
   Elicitation、Plan、停止与恢复；不要求每个 Provider 都提供精确 Usage parser。
3. 实际拖拽两/三列、History 宽度和 divider，在 100/125/150% DPI 下确认圆角、
   Composer 底边、焦点环和 Terminal fit 没有主观异常。
4. 真实点击 Runtime Update，验证取消/超时返回前进程已退出、目录可立即操作，
   重启后只在本地 promote 已 staged 目录；启动本身不得触网。
5. 验证断网、登录过期、额度不足和 Provider 异常时的用户文案与恢复路径。
6. 在 Windows 10 和 Windows 11 上分别验证 WebView 右键策略：输入框保留撤销、
   剪切、复制、粘贴和全选，选中文本只保留复制类操作，页面空白处没有浏览器
   菜单，且不存在“网页另存为”、打印、查看源代码或浏览器下载入口。
7. 调整窗口、History 和 Pane 宽度并持续接收流式消息，确认 ResizeObserver 的
   非致命交付通知不会形成用户错误条；人为触发的真实未捕获错误只出现一条脱敏、
   可关闭的提示，不展示堆栈、路径或会话内容。

只有未使用 `-SkipDependencyAudit` 的 Full、人工查看过的视觉差异，以及上述
真实 Terminal + Agent 验收共同完成，才能作为发布证据。
