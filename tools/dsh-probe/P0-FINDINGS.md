# DSH P0 嵌入探针结论

实验起于分支 `dsh`、基线 `dev@2273baf`。探针工程位于
`tests/PSX.DshProbe/`，现已加入 `PSX.slnx` 并由 Full gate 以 mock 模式运行；
辅助材料位于 `tools/dsh-probe/`，产物全部落 `TestResults/dsh-probe/`。

## Cluster 2 — npm 安装 + 启动(通过)

- 锁定 `@deepseek-ai/dsh@0.1.0-rc.6`,官方 registry,Node 22.23.1,走本地代理。
- **带 lifecycle scripts** 安装成功:530 包 / 255.5 MiB / 约 3 分钟。
- `node-pty@1.1.0` 走预编译(`prebuilds/win32-x64/*.node` + `build/Release/conpty/*`),无本地编译。
- `koffi@3.1.5` 原生二进制来自可选依赖 `@koromix/koffi-win32-x64/win32_x64/koffi.node`,cnoke 无下载/无编译。
- **无 VS Build Tools 依赖**(日志无 gyp/MSBuild/cl 编译迹象)。
- `dsh web --host 127.0.0.1 --port 0` 就绪信号 `dsh web: http://127.0.0.1:<port>` 解析成功,HTTP 200,标题 "DeepSeek Harness",无 0.0.0.0 暴露。
- **DSH_HOME 被识别**(`$DSH_HOME/profiles` + `$DSH_HOME/storages`),但 boot 仍无条件在 `~/.dsh` 双写 `profiles`/`storages`。

## Cluster 1 — WebView2 嵌入合同(mock + 真实 DSH)

通过(核心否决项全绿):

| 检查 | 结果 |
|---|---|
| mixed-content-frame-loads | ✅ `https://psx.local` 嵌 `http://127.0.0.1:*` 不被混合内容拦截 |
| frame-injection-pointerdown | ✅ 顶层 `AddScriptToExecuteOnDocumentCreatedAsync` + origin guard + frame `WebMessageReceived` 管道可用 |
| iframe-focus-activeElement | ✅ 帧内获焦后父文档 `activeElement === iframe`(列焦点机制可行) |
| unicode-input / websocket-echo | ✅ 中文往返;ws:// 跨源可用(偶发抖动,见下) |
| window-open / odd-port / top-navigation / hide-restore | ✅ 事件面齐全、sandbox 挡顶层跳转、隐藏恢复保活保几何 |
| dsh-real-smoke | ✅ 真实 DSH 页面在 iframe 中加载(title "DeepSeek Harness") |

### 关键发现:下载管线不可用(否决原 Phase 3 下载方案)

`CoreWebView2.DownloadStarting` 在**所有触发路径下都不触发**,也不落默认 Downloads 目录:

- 跨源 sandbox iframe 的 anchor 下载 / 帧导航到 attachment:不触发
- 无 sandbox iframe:不触发
- 顶层 anchor(脚本 click + **CDP 可信手势点击**,导航确已发生):不触发
- 顶层 **blob 下载**(无 HTTP/无服务器/无跨协议):不触发

结论:本 WebView2 环境(SDK 1.0.2903.40 + 系统 Evergreen runtime)不落下载管线。
**Phase 3 的 `DownloadStarting + ResultFilePath + 保存对话框` 路线必须放弃**,现已改为
**宿主中介导出**:注入脚本拦截 DSH 导出链接 → 把 URL 与建议文件名 `postMessage`
给 shell → `dsh_export` 发给 C# → C# 校验当前 DSH origin 与固定导出路径、主动拉取
受限大小的 ZIP，再弹 Windows 保存对话框原子写盘。这不依赖 WebView2 下载管线，
且字节不会经 WebView Bridge 跨界传输。

### 次要观测

- `FrameNavigationStarting` 在渲染层 CSP 强制**之前**触发(legacy `frame-src 'none'`
  下导航事件仍发生,但帧内容被拦,错误页 title 为 hostname)。Phase 3 白名单是主防线,CSP 是纵深。
- WebSocket 检查偶发 'idle'(离屏窗口后台节流嫌疑),已加 `--disable-background-timer-throttling`
  等参数但未完全消除;功能已两次验证通过,归手动清单兜底。
- 探针工程踩坑(供 Phase 3 参考):`async Main` 与 `[STAThread]` 组合不可靠,需显式同步入口;
  `Application.Run` 返回后 UI 泵停止,任何 WinForms-context 续体都会死锁;
  `ExecuteScriptAsync("location.reload()")` 永不返回(脚本销毁自身文档);
  `form.Close()` 在 UI 线程同步做 WebView2 销毁会死锁(用 `Application.ExitThread()`)。

## 裁决

P0 核心否决项(混合内容、iframe 可行性、原生脚本、焦点、注入)全部通过;
唯一否决级发现是下载管线,可通过**宿主中介导出**重设计解决,不构成放弃 iframe 的理由。
建议进入 Phase 1,并把下载通道改为宿主中介导出。
