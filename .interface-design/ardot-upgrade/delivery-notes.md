# PSX UI 升级交付说明（codex/ui-ardot-upgrade）

基点：`dev` @ `587d5f3`。设计基准：Ardot 文件 `723296751630711`，参数快照见同目录 `design-tokens.md`。

## 提交序列

| SHA | 内容 |
|---|---|
| a7cb6fd | `.gitignore` 增加 `/.playwright-mcp/`（原有未提交改动） |
| 8e2cc8e | C#：`ShellThemeColors` 可选模型、`[shellTheme]` INI 解析/校验/指纹、旧预设幂等迁移、五套新预设 ini |
| c88f763 | 前端：`shellTheme.js`（14 字段、渐变合成、终端激活 Tab 墨色派生）、main.js/WorkspaceHost 双路径、tokens.css 默认值、TerminalManager 终端底色 |
| 190219d | 扁平几何：历史面板全高贴栏、列平铺去 gutter/圆角/阴影、1px 列分隔线、阅读列 760px、Composer 16px 圆角与阴影/边框按节点 |
| 4158962 | Tab 固定 175×32px（圆角 5、间距 4、标题 11px、激活 Medium）、滚动区+右侧固定「全部标签」入口（1×20 分隔、+N 徽章、列内菜单）、激活隐藏标签自动滚动可见、重渲染保持滚动位置 |
| 6f2ff7e | Tab 图标改中性灰、关闭 × 12px、AGENTS.md 视觉合同与测试最低数更新 |
| 2d35a58 | 视觉 harness 几何契约适配扁平布局、历史面板元数据对比度修复、基线重录 |

## 五套内置主题

`base-light`（默认）、`meadow-green`（草原绿，侧栏/历史 180° 渐变 #F3F8EC→#E5F8D1，首色标位置 9.39% 由 `sidebarGradientFromStop` 承载，`background-attachment: fixed` 与 rail 共享坐标）、`parchment`（羊皮纸黄纯色）、`vercel-black`、`charcoal`（炭黑）。旧预设 dark / vercel-neutral-dark / terminal-green / warm-light / vercel-neutral-light 从内置列表移除，启动时幂等迁移：实际加载继任预设的颜色组（保留用户字体与非主题设置）并写入真实指纹；用户自定义主题文件不受影响，缺 `[shellTheme]` 段时全部回退到 tokens.css 默认值。新装无配置时由 base-light 预设播种，首次启动与手动应用「基础浅色」效果一致。

深色主题下终端列激活 Tab 采用 `#E8E8E8` 浅灰板（用户指定，覆盖画板 #3D3D3D），终端背景 `#141414`；浅色主题终端列激活 Tab 为白色板。

## 设计决策与冲突处理（详见 design-tokens.md 第 7 节）

- 变量集 `tabHeight=36` 与全部画板实测 40px 冲突 → 以画板为准（40px 栏 / 32px Tab）。
- 草原绿 Composer 锚点 `#ECF4DC` 与实测 `#E6F8D2` 不符 → 用实测值（复审重读节点 `8:830` 纠正了早前的换算错误 `#ECF4D2`）。
- 单列稿有 760px Reading Column、多列稿无包裹 → 统一 `min(760px, 100% − 48px)` 居中，多列天然铺满。
- Composer 阴影五稿一致：`rgba(0,0,0,0.06)`，0/5/8/1（用户后期在画板统一加上的版本，非早期"去阴影"方案）。

## 已知边界 / 未覆盖

- 画板未定义的交互态（hover/pressed、焦点环）沿用现有行为。
- ANSI 色、错误/警告等功能色沿用现有语义字段，不来自画板。
- DSH / Kimi Web 内部界面不在本次范围（PSX 只改宿主边界，gutter 已归零）。
- 视觉基线截图由 Full 套件的 locked-Chromium 检查覆盖；本次为有意重设计，基线已通过 `npm run update:visual` 重录（30 张，axe 0 违规），差异逐项对应：扁平几何（gutter/圆角/阴影移除）、175px 固定 Tab + 溢出入口、阅读列 760px、历史面板头部 padding 16px。
- 视觉 harness 几何契约同步更新：responsive 场景视口宽度 `paneWidth + 66` → `+ 40`（面板不再内缩 12px×2+边框）；History/列区间距 12px → 0px；220px 搜索框全宽断言容差按设计 16px 侧 padding 调整。
- axe 修复：设计稿 `#6E737A` 元数据灰在 `#F7F7FA` 历史面板底上对比度 4.46:1（低于 4.5:1），历史面板内 small 元数据统一向主文字色混合 8%（color-mix 92%），视觉无感知、全主题合规（设计偏差已在此记录）。

## 复审修复（第 1 轮）

复审基线 `587d5f3..2d35a58`，6 项问题全部确认并修复：

1. **旧主题迁移只改名称（P1）** → `SettingsService.MigrateLegacyThemeKey` 现在实际加载继任预设并应用全部颜色组（`[theme]`/`[agentTheme]`/`[terminalColors]`/`[shellTheme]`），写入真实指纹；用户字体与非主题设置保留；预设文件缺失时退化为仅改键+空指纹。测试逐行核对预设背景、代码块、终端前景、shell 颜色与指纹。
2. **视觉 harness 未传 shellTheme（P1）** → `tools/screenshot-baseline.mjs` 的 `appearanceFromPreset` 补上 `shellTheme`；五套预设全部纳入门禁：base-light / vercel-black 跑全场景，meadow-green 加跑单列+双列+History 推移（验证渐变与色标位置），parchment / charcoal 跑单列+双列。
3. **新装默认主题不完整（P2）** → 无 psx.ini 时 `GetSettings` 由 base-light 预设播种（含 shellTheme 与真实指纹）；仓库根 `psx.ini` 模板配色全量对齐 base-light 并补 `[shellTheme]` 段。
4. **Tab 重渲染丢焦点（P2）** → `fillTabStrip` 重建前记录聚焦的 Tab 与部件（target/close），重建后还原到同 workspaceId 的新节点；新增回归测试覆盖 target、close 与已删除 Tab 三种情形。
5. **草原绿 Composer 色值换算错误（P2）** → 重读节点 `8:830`，实为 `#E6F8D2`；`meadow-green.ini` 与本文档一并纠正。
6. **渐变色标位置丢失（P2）** → 新增可选字段 `sidebarGradientFromStop`（0–100 百分比，C# 模型/INI 读写/校验/指纹/桥接全链路），meadow-green 取历史面板值 9.39%（统一决策见 design-tokens.md §7.6）；前端 `shellTheme.js` 生成 `linear-gradient(180deg, from 9.39%, to)`。

设计参数快照与交付说明从被忽略的 `TestResults/` 移入版本控制：`.interface-design/ardot-upgrade/`。

## 最终验证记录

- `npm run test:web`（完整 Web 门禁，含 typecheck / lint / 单测 / 视觉）：50 文件 420 测试全过。
- `powershell -File tools/test.ps1 -Suite Fast`：通过。
- `powershell -File tools/test.ps1 -Suite Full`（桌面探测、便携包冒烟、锁定 Chromium 视觉/axe、依赖审计）：通过（exit 0）。npm audit 报的 3 条 moderate（vitest 2.x 路径穿越，GHSA-82fw-gwwq-j7x9）为既有 dev 依赖问题，非本次改动引入，升级需 `npm audit fix --force` 跨大版本，留给后续独立决策。
- 最终提交 SHA：`2d35a58`（基点 `587d5f3`，共 7 个提交，工作区干净）。
