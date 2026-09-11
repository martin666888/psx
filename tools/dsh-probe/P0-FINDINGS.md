# DSH WebView2 覆盖层探针结论

当前生产架构不再把 DeepSeek Harness 放入 shell iframe。PSX 保持一个
`https://psx.local` shell WebView2，并以第二个顶层 WebView2 覆盖 DSH 工作区的
内容矩形。该覆盖层直接打开一次性 `?token=` Ready URL，使
`SameSite=Strict` 会话 Cookie 在第一方上下文中建立；token 不经过 shell bridge。

探针工程位于 `tests/PSX.DshProbe/`，已加入 `PSX.slnx`，并由 Full 门禁以
mock 模式运行。报告全部写入 `TestResults/dsh-probe/`。

## Full 门禁自动检查

mock 模式使用与生产相同的共享 WebView2 environment 和
`DshSurfaceHostPolicy`，验证以下合同：

| 检查 | 通过条件 |
|---|---|
| shell-has-no-iframe | shell 保持 `psx.local`，且没有 DSH iframe |
| overlay-token-first-party | 覆盖层恰好打开一次 token URL，随后落到无 token 根路径并持有 Strict Cookie |
| health-root-unauthenticated | 无 token 根路径返回 401，且健康检查不消耗 token |
| surface-script-focus | 覆盖层 pointerdown 经宿主消息通道上报 |
| surface-script-export | 固定导出路径由宿主中介，不进入浏览器下载管线 |
| unicode-input | 中文与 emoji 能在覆盖层输入控件中往返 |
| websocket-echo | WebView2 必须实际完成 WebSocket upgrade，并收到 `echo:ping`；失败不可豁免 |
| window/popup/navigation | 新窗口和跨 origin 导航被宿主策略收口 |
| hide-restore | 隐藏、恢复后文档与会话仍存活，token 不被再次消费 |

Mock HTTP 响应必须保持严格的头部终止位置。空的可选头不能多写一个空行，否则
后续 `Connection` 头会落入正文并按 `Content-Length` 截断 HTML 尾部脚本。
WebSocket 检查同时记录 upgrade 和消息帧计数，便于区分页面脚本、握手和数据帧故障。

所有自动检查失败都会让 Probe 返回非零退出码；不再存在 WebSocket
`idle` 的 known-finding 放行路径。

## 真实 DSH 与安装探针

### 2026-09-10：0.1.5-rc.2 首装 seed 提升

- [隔离生成 workflow 34494859152](https://github.com/martin666888/psx/actions/runs/34494859152)
  在 Windows Server 2022、Portable Node 22.23.1 / npm 10.9.8 上通过官方 SRI 核对、
  `npm ci` 和真实 `dsh web` Ready URL HTTP 200 检查。
- `tools/dsh-seed/package.json` 与 `package-lock.json` 直接复用该次审核通过的
  `tools/dsh-locks/locks/0.1.5-rc.2/` 字节，首次安装不会先落到旧版再更新。

### 2026-09-10：0.1.5-rc.1 复核

- [隔离生成 workflow 34442139499](https://github.com/martin666888/psx/actions/runs/34442139499)
  在 Windows Server 2022、Portable Node 22.23.1 / npm 10.9.8 上通过官方 SRI 核对、
  `npm ci`（13 秒）和真实 `dsh web` Ready URL HTTP 200 检查。
- 检查 npm 已发布的 `@deepseek-ai/dsh-client-connection@0.1.5-rc.1/lib/index.js`：
  `sessionCookie` 仍设置 `HttpOnly; SameSite=Strict`；`isTrustedApiRequest`
  仍拒绝 `Sec-Fetch-Site: cross-site` 并核对 Origin。因此不能恢复到 `psx.local`
  页面内的跨站 iframe，继续使用第一方顶层 WebView2。
- tab 菜单的圆角排除区域用于解决原生 WebView2 HWND 遮挡 shell 菜单的问题，
  不是 DSH 页面样式补丁。上游版本更新没有消除这一宿主层约束，保留现有挖槽。
- 上述真实启动检查不等于已验证真实 WebView2 中的完整交互；Full 的宿主探针使用 mock，
  长对话、IME、附件和导出仍按下方人工检查范围执行。

- `tools/dsh-probe/install-probe.ps1` 和 `launch-probe.mjs` 用于隔离环境中的安装、
  启动与 Ready URL 验证。
- `PSX.DshProbe --dsh-origin <ready-url>` 可用第二个顶层 WebView2 对真实 DSH
  做加载 smoke；传入和输出日志只展示 origin，不记录一次性 token。
- 新 DSH 版本进入锁目录前，仍必须经过 `tools/generate-dsh-lock.ps1` 的隔离安装、
  官方 SRI 交叉校验和启动 smoke。

## 导出结论

WebView2 的浏览器下载管线在既有实验中不稳定，因此生产架构继续使用宿主中介导出：
注入脚本拦截固定导出链接，向 C# 发送 URL 与建议文件名；C# 校验当前 DSH origin、
固定路径、重定向、内容类型和大小后，再通过 Windows 保存对话框原子写盘。

## 保留的人工检查

- 覆盖层内可信鼠标点击能切换列焦点与 tab 高亮
- 真实 CJK IME composition
- 图片拖放、附件与剪贴板粘贴
- 原生目录选择器
- 真实 DSH 长对话、WebSocket 稳定性和会话 ZIP 导出
