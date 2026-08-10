# PSX Interface Design System

本文档是 PSX 桌面界面的项目级设计约束。新增或修改 WPF、WebView2、Terminal、Agent 界面时，应优先复用这里定义的原则和模式。正式的组件级设计规范见根目录 `design.md`，本文档作为辅助设计记忆与其保持一致。

## 1. 产品方向

### 使用者与场景

PSX 面向正在编写代码、运行命令或与 Agent 协作的开发者。用户通常处于连续工作流中，需要快速判断状态、执行操作并回到终端，不应被装饰、复杂设置流程或不可撤销的界面变化打断。

### 界面感受

- 紧凑、安静、技术化。
- 信息密度较高，但层级必须一眼可辨。
- 操作反馈直接，危险或持久化操作必须明确。
- 临时预览应当可撤销，持久化必须由用户确认。

### 产品领域

终端、Shell、光标、代码、会话、色板、运行状态、工具输出和 Agent 决策构成 PSX 的设计语境。

### 色彩世界

主题颜色来自 INI，而不是组件中的任意色值。默认方向来自显示器黑、终端暖灰、磷光绿、光标青、纸张米白、警告黄和错误红。色彩用于表达状态和操作，不用于纯装饰。

### 标志性交互

PSX 的标志模式是“即时反馈、确认持久化”：例如 Theme 条目点击后立即预览，只有当前预览条目出现 Confirm，关闭弹层则恢复原状态。

### 明确拒绝的默认方案

- 不使用占满窗口的大型设置页解决单一选择任务，改用锚定在全局工具栏的紧凑弹层。
- 不使用只有名称、没有状态反馈的原生下拉框，改用带 Current、Updated、Invalid 和确认状态的列表。
- 不在功能组件里硬编码颜色，全部映射到 INI、WPF DynamicResource 或 CSS Variables。

## 2. 视觉基础

### 深度策略：边框与表面色差

PSX 使用 borders-only 与轻微表面色差建立层级，不混入明显投影；例外是五类浮起层——Agent 分层壳层的 canvas、无边框软面的 Composer 卡片、context 卡片（如 Plan）、浮动菜单/弹层（popover）和模态媒体（dialog）——允许使用 token 化的克制柔和阴影表达上下层关系（见 Agent 壳层布局）。Tool、Decision、Runtime、Recovery 等内容卡只用 surface + hairline，不加阴影。

表面顺序：

1. `background`：窗口和主画布。
2. `surface`：常规控件和输入区域。
3. `surfaceRaised`：弹层、工具卡片和需要抬高的区域。
4. `surfaceMuted`：选中状态、内嵌提示和弱分组。

规则：

- 普通分隔使用 `border`，弹层边界使用 `borderStrong`。
- 边框用于说明结构，不应成为画面中最醒目的元素。
- 不使用厚装饰边框、强投影或跨层级的大幅明度跳变。

### 间距

基础单位为 `4px` / `4 WPF device-independent pixels`。

- 微间距：4px。
- 控件内部和相邻操作：8px。
- 分组间距：12px。
- 主要区段间距：16px 或 24px。
- 同一控件内优先保持对称 padding。

例外值只用于对齐既有 36px 顶栏、文本基线或系统控件，不应形成新的间距体系。

### 圆角

圆角使用五级语义阶梯（Soft Workbench），组件不得定义阶梯外的任意值；WPF 侧对应 `Themes/Dark.xaml` 的 `ControlCornerRadius` / `InputCornerRadius` / `CardCornerRadius`：

- `--agent-radius-control`（8px）：按钮、图标按钮、chip、列表行、菜单项、行内代码。
- `--agent-radius-input`（10px）：输入框、搜索框、下拉触发器。
- `--agent-radius-card`（14px）：Tool / Decision / Runtime / Recovery / Plan 卡片、弹层、菜单、tooltip、图片预览、代码块。
- `--agent-radius-structure`（24px，结构 token）：Workspace 面板 / History dock / Composer 卡片共享的结构圆角；Composer 发送按钮为正圆，圆心与卡片右下角圆弧圆心重合（footer 右/下 padding = 24 − 17 = 7px）。
- 胶囊/正圆（50%、999px）只用于天然圆形或胶囊元素：发送按钮、开关、状态点、滚动条滑块。

### 字体

- WPF 工具栏和短标签：11–13px，使用系统 UI 字体。
- Agent 正文、代码和 Terminal 字体由 INI 控制。
- 状态、元数据和分组标题应通过字号、字重与颜色共同建立层级，不能只靠缩小字号。
- 数据、路径、代码和终端内容优先使用主题定义的等宽字体。

### 文本层级

- Primary：正文、标题和主要操作。
- Secondary：说明、状态和次要操作。
- Dim：元数据、空状态和低优先级提示。
- Semantic：仅用于错误、警告、允许、拒绝和确认状态。

## 3. 颜色与 Token 规则

`psx.ini` 是正式配置来源，主题文件是可选择的显示设置来源。

- WPF 使用 `DynamicResource`，不得用组件内固定十六进制颜色替代已有资源。
- WebView2 使用 CSS Variables，变量应与 `ThemeColors`、`AgentThemeColors` 和 `TerminalPalette` 对应。
- 八位颜色统一使用 CSS `#RRGGBBAA` 语义；进入 WPF 时才转换为 `#AARRGGBB`。
- 新增颜色时必须同步更新模型、核心配置、全部内置主题、WPF 应用层和对应 CSS token。
- 一个组件只使用一个主要 accent。错误、警告等语义颜色不能作为装饰色。
- 深色和浅色主题都必须保持清晰的文本、边框、hover 和 disabled 状态。

## 4. 组件模式

### 全局工具栏

- 跨 Terminal 和 Agent 生效的可见操作放在 WebView activity rail（History / Create / Theme）或对应的 WebView 全局浮层；WPF 顶栏仅保留隐藏的兼容投影，不得成为第二套导航。
- 按钮高度、字号和密度应与 Terminal / Agent 切换控件一致。
- 图标只有在能减少理解成本时使用；文字已经足够清楚时不添加装饰图标。
- WPF 按钮统一使用 `Themes/Dark.xaml` 的 `SoftWorkbenchButtonStyle` / `SoftWorkbenchToggleButtonStyle` / `SoftWorkbenchIconButtonStyle`；图标使用 XAML `Path` 几何，不使用字体字形。

### 锚定弹层

Theme 弹层是全局选择器的参考实现：

- 锚定触发按钮，在按钮下方展开。
- 使用 `surfaceRaised + borderStrong`，不使用明显投影。
- 推荐宽度约 300px；内容较多时限制高度并内部滚动。
- 顶部提供名称与当前上下文，主体按来源或语义分组，底部放刷新和管理操作。
- 点击外部、按 Esc 或再次点击触发按钮都应安全关闭。

### 可预览列表

- 点击整行的主要区域触发预览。
- 只有当前预览行显示 Confirm，避免多个主操作同时竞争注意力。
- `Current` 表示正式保存的状态；`Updated` 表示来源已变化；`Invalid` 表示不可操作。
- 无效项目保留可见，并提供具体诊断，不静默从列表消失。
- 关闭弹层但未确认时必须恢复打开前状态。

### 状态与错误

- 正常的临时状态使用 Secondary 文本。
- 错误使用 Error token，并说明对象、字段和恢复方法。
- 空状态应说明为什么为空以及用户下一步可以做什么。
- 长诊断放在 tooltip、详情或可滚动区域，不把主界面无限撑高。
- 全局未捕获错误只显示一条可关闭、已脱敏的恢复提示；不得把 JavaScript 堆栈、
  资源 URL、工作目录或会话内容直接打印到界面。Chromium 的两条已知
  ResizeObserver 交付通知属于非致命浏览器诊断，不进入用户错误表面。
- WebView 原生右键只服务于编辑和复制：可编辑区域保留撤销、剪切、复制、粘贴、
  全选等本地化命令，选中文本保留复制；页面另存、打印、查看源代码、刷新和浏览器
  下载不属于 PSX 产品界面。

### 长内容

- Tool output、日志、历史记录和诊断必须支持折叠或内部滚动。
- 默认状态应保护主工作流的垂直空间。
- 运行中的内容可以展开，完成后应恢复为紧凑状态。

## 5. 交互与状态

每个交互控件必须考虑：

- default
- hover
- active / selected
- keyboard navigation（使用统一的 `:focus-visible` 焦点环）
- disabled
- loading（适用时）
- empty / error（数据组件适用时）

焦点规范：键盘导航、程序化 focus 与 Esc 关闭后的焦点恢复必须保持正常。WebView 交互控件仅在 `:focus-visible` 时显示统一的 `2px solid var(--agent-focus-ring)`，`outline-offset: 2px`；指针点击不显示焦点环，Composer 输入通过 `:has([data-role="input"]:focus-visible)`在卡片外框显示同一焦点环。菜单项可保留 focused surface，Pane 焦点只由 tab/nameplate 强度与下划线表达。WPF 隐藏兼容投影不得形成第二套可见焦点表面。

交互反馈应快速、平稳，不使用弹跳或夸张动画。普通 hover 和状态切换控制在短时微交互范围；尊重系统减少动画设置。

紧凑元数据行中，普通文本与文字操作按钮按字形基线对齐；纯图标操作组按几何中心对齐。不得用单项 margin、translate 或相对定位补偿视觉错位。

持久化操作遵循以下顺序：

```text
预览或编辑内存状态
→ 用户明确确认
→ 校验
→ 原子保存
→ 更新正式状态
→ 失败则保留原状态并显示诊断
```

### Terminal 会话生命周期

- 软件启动、点击 `+`、关闭最后一个 Tab 后补建，属于新建会话：创建新的 ConPTY/Shell，并允许 Shell 输出一次启动说明。
- Agent/Terminal 切换和 Terminal Tab 切换，属于恢复会话：不得重启 Shell、清空 buffer、发送回车或伪造提示符。
- Terminal 隐藏时继续接收后台输出，但不得测量 DOM、fit 或 resize ConPTY。
- Terminal 恢复显示后，等待布局稳定，只提交一个最终行列尺寸并恢复焦点。
- 主题颜色可在隐藏时更新；字体变化只标记延迟 fit，不能抢走 Agent 输入焦点。
- 长提示符、半条未执行命令、scrollback、cwd 和运行进程都属于必须保留的会话状态。

### Agent 斜杠命令

- 斜杠菜单是 PSX 对用户承诺的完整命令目录，由 PSX 内置命令和当前 ACP Session 动态命令组成。
- 只有消息开头的完整命令 token 才能触发命令；正文中出现的 `/xxx` 始终作为普通 Prompt。
- 未在目录中的命令必须在 Composer 就地提示，不得发送 ACP、写入历史或进入 busy。
- 不维护 Claude Code 命令黑名单；需要原生交互界面的命令统一引导用户通过 `/terminal` 使用。

### Agent 壳层布局

- Agent 界面是分层壳层：History 是页面唯一的底层 dock（左侧），Conversation canvas 位于上层并连接窗口顶/右/底边缘，Plan 是浮在 canvas 右上角（工具栏下方）的内容高度小卡片；层级观感 canvas 在上、dock 在下、Plan 最高，靠 background < canvas-surface < surface-raised 的明度阶梯加左缘圆角/细边/阴影表达。
- 结构 token：`--agent-history-width`（默认 280px，可拖拽，控制器约束在 220–420px）、`--agent-plan-width`（固定 320px，不可调）、`--agent-reading-max-width`（920px）、`--agent-reading-min-width`（320px）、`--agent-toolbar-height`（44px）、`--agent-composer-bottom-space`（24px，所有 Pane 和响应式档位共享）、`--agent-radius-context-card`（映射到 `--agent-radius-card`，14px）、`--agent-radius-structure`（24px，Workspace 面板 / History dock / Composer 卡片共享；`--agent-workspace-radius` 与 `--agent-radius-composer` 映射到它）、`--agent-shadow-canvas`、`--agent-shadow-context-card`、`--agent-shadow-composer`、`--agent-shadow-popover`、`--agent-shadow-dialog`、`--agent-canvas-surface`（由 surface/surface-raised color-mix 推导）；阴影从 `--agent-shadow` 推导，不硬编码颜色。
- 阅读列在 History 推挤后的每个 Pane 实际内容矩形中居中，并保持 `--agent-reading-max-width`；空间不足时各列继续按纯比例压缩，History 不因阅读列碰撞而自动收起。Plan overlay 不参与布局计算；消息条目不卡片化。
- 响应式只按 Agent Pane 宽度改变 Pane 内部呈现：Plan 在 ≥520px 为内容高度卡片，<520px 为 Pane 内 overlay；Composer 与工具栏使用各自的 container query。Plan / Update 在 ≥520px 直接显示，<520px 移入受控的 ⋯ 弹层；不得用关闭状态的原生 disclosure 承载宽档必须可见的操作。History 是进程级全局 dock，不复用 Pane narrow 状态控制宽度、可见性或拖拽能力。
- 多 Pane 的 Composer 底边始终锚定到面板底部上方 24px 的同一基线；窄 Pane 的 placeholder、草稿、附件或决策提示增加固有高度时只向上生长，响应式档位不得改写底部 inset，也不通过 JS 同步不同 Workspace 的内容高度。
- History 在任何窗口和 Pane 宽度下都保持持久化宽度并可拖拽（220–420px）；用户保存的 `--agent-history-width` 不被任一响应式状态改写。空间不足时右侧列缩窄，History 不回落固定宽度也不覆盖列内容。
- Plan 卡片两态：visible/hidden，workspace 运行时偏好不持久化且默认显示；窄模式以临时覆盖收起，离开窄模式时恢复偏好。工具栏图标切换，隐藏期间新计划在图标上显示未读点。runtime 安装卡等内容卡片使用与对话、Composer 相同的阅读列宽度和位置规则。
- History 只负责全局 Thread 导航：列表、加载状态和错误只在 dock 内展示，不得写入主对话流；行高亮使用整行圆角背景（`--agent-radius-control`），不使用左侧强调条。线程行是单行结构：标题承担截断，`Current`/`Open` 文字徽标与右侧短时间不收缩，完整的 provider|时间放 tooltip；分组折叠 affordance 用文件夹开合两态图标。
- 持久化键：`psx.agent.historyDockOpen`、`psx.agent.historyDockWidth`；`psx.agent.planWidth`、`psx.agent.inspectorWidth`、`psx.agent.planPanelWidth` 均已退役（不再读取）。
- 动画限制在 120–160ms，只用 opacity 和小距离 translate，并尊重 prefers-reduced-motion。

## 6. WPF 与 WebView2 一致性

- WPF 和 WebView2 是同一个产品表面，不能各自发展独立的颜色、间距和状态语言。
- 主题切换必须同时覆盖 WPF 外壳、Agent、已有 xterm 和 Windows 标题栏。
- C# / JavaScript bridge 新增显示消息时，双方类型和处理分支必须同步更新。
- 字体或终端主题变化后，对可见 xterm 重新执行 fit，避免尺寸与渲染错位。
- WebView 中的弹层和卡片应复用 Agent token，不引入与 WPF 冲突的固定颜色。

## 7. 设计评审清单

提交界面修改前检查：

- 是否解决了开发者当前任务，而不是增加无关装饰？
- 是否使用现有 token、4px 间距和边框层级？
- 模糊观察时，主次层级是否仍清楚且没有突兀边界？
- 是否具备 hover、disabled、empty、error 状态和正常的键盘导航行为？
- 临时操作是否可撤销，持久化操作是否明确确认？
- 长内容是否会无限拉高主界面？
- 深色、浅色主题和字体变化下是否仍可用？
- WPF 与 WebView2 是否同步？
- 是否避免了厚边框、强投影、阶梯外任意圆角、无意义图标和装饰渐变？

当新的组件模式被复用两次以上，或形成稳定尺寸与状态规则时，应更新本文档。
