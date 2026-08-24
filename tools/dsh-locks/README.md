# DSH 更新 lock 目录（`tools/dsh-locks/`）

DSH（DeepSeek Harness）更新管道的可信预生成 lock 仓库。客户端（`DshWebRuntime.StageUpdateAsync`）
更新时**永不**在用户机器上求解依赖版本：它从这里取得每个版本随 PSX 发布、SHA 锚定的
`package.json + package-lock.json`，校验后直接 `npm ci`（镜像路径经
`--replace-registry-host=npmjs` 下载，每个 tarball 仍按 lock 内 SRI 校验字节）。

## 目录结构

```
tools/dsh-locks/
  catalog.json                    # 唯一清单：entries + blockedVersions
  locks/<version>/package.json    # 与 lock 同源生成的合成根清单，客户端原样复制
  locks/<version>/package-lock.json
```

- `entries`：经批准可安装的版本，**最新在前**，至多 5 条（`-Keep`），全部 ≥ seed（`0.1.1-rc.2`）。
  每条携带 `lockSha256`/`packageSha256`/`lockfileVersion`/`generatedByNpm`/`dshSri`/`smokePassed`。
- `blockedVersions`：人工审核决策，`reason` 为固定键
  （`smoke_failed | official_integrity_mismatch | sri_conflict`），至多 10 条，与 entries 不相交。
- `locks/` 目录集合必须恰好等于 entries 版本集合（无孤儿目录）。

## 生成流程（`tools/generate-dsh-lock.ps1`）

```powershell
# 干跑（求解 + 官方核对 + 初检；不写 catalog，任何机器可跑）
powershell -ExecutionPolicy Bypass -File tools/generate-dsh-lock.ps1 -Version 0.1.1-rc.2 -NoSmoke

# 正式生成（只能在隔离环境，见下方契约）
powershell -ExecutionPolicy Bypass -File tools/generate-dsh-lock.ps1 -Version 0.1.1-rc.2 -Isolated

# 对已收录条目整体重放启动 smoke
powershell -ExecutionPolicy Bypass -File tools/generate-dsh-lock.ps1 -Version ignore -Isolated -FullResmoke
```

生成 = 钉死 Portable Node v22.23.1（与 build-release 同 SHA）→
`npm install --package-lock-only --ignore-scripts`（官方源）→ 结构初检 →
官方 `dist.integrity` 与 lock 条目 SRI 相等核对 → `npm ci` + 启动
`dsh web --host 127.0.0.1 --port 0 --no-open` 等 Ready 的强制 smoke → 写入 catalog（降序、裁剪、SHA）。

## 隔离契约（供应链边界）

- 求解阶段带 `--ignore-scripts`，不执行第三方脚本，可在普通机器干跑计时。
- **smoke 的 `npm ci` 会执行未审第三方 lifecycle scripts**：任何会写入 catalog 的完整运行，
  必须整体在一次性环境（一次性 Windows x64 VM 或专用 CI runner）中执行——
  无签名密钥、无 npm/GitHub token 等秘密、普通权限账户、运行后销毁工作区；
  产物仅经 Git PR 审查进入仓库。
- 正式发布机只做静态校验（`tools/build-release.ps1`：catalog SHA 常量、逐条目 SHA/origin/SRI、
  新鲜度元数据查询），**永不执行新包脚本**。

## 不变式

1. **既有条目永不重生成**；同版本官方 `dist.integrity` 与已收录 `dshSri` 不同 = 安全事件
   （拒装、保留旧版本、人工调查），**任何路径都不得刷新 catalog 接受同版本新字节**。
2. `DshLockCatalogTests`（C#，复用 `DshLockValidator`）是仓库门禁；build-release 是发布门禁；
   两侧校验同一产物，客户端安装用的也是同一校验器。
3. 新鲜度门禁：> seed 的最新已发布版本必须命中 entries ∪ blockedVersions，
   否则发布失败（`-SkipDshFreshnessCheck` 是唯一逃生口）。
4. smoke 失败的版本不自动进 blocked：生成器只打印建议条目，人工确认后经 PR 提交。

## Spike 结论（go/no-go 记录）

环境：Windows x64 开发机（发布机等效），钉死 Portable Node v22.23.1 + 自带 npm 10.9.8，
`--package-lock-only --ignore-scripts`，官方源。**结论：go。**

| 版本 | 求解耗时（冷 cache） | lock SHA-256（前 16 位） | 官方 SRI 核对 |
|---|---|---|---|
| 0.1.0-rc.7 | 1,216s（≈20.3 分钟） | F48A4B23EA4AC896 | 一致 |
| 0.1.0-rc.8 | 1,347s（≈22.5 分钟） | E7D38F7B3209D7CD | 一致 |
| 0.1.1-rc.1 | 1,744s（≈29.1 分钟） | 85B83097C682ABA1 | 一致 |

- 最差实测 ≈29 分钟 → `-TimeoutMinutes` 默认取 60（最差 × 2）。
- 热缓存几乎不提速（rc.8 在 rc.7 之后仍 22.5 分钟）：持久 npm cache 只省网络请求，
  idealTree 每次完整重算——这正是该命令不能留在用户机器上的原因。
- 生产客户端的 npm 超时是 20 分钟：三版本在用户机器上全部必然超时，与线上反馈一致；
  集中到生成侧后单版本 <30 分钟、每次发版只为新版本付一次成本，可接受。
- 同版本重复生成的 SHA 一致性验证：0.1.0-rc.7 两次独立求解的 lock SHA-256 完全一致
  （1,216s vs 热缓存 1,044s），生成是确定性的。
- smoke（`npm ci` + `dsh web` 启动等 Ready）须在隔离环境执行，见上方隔离契约。

机器配置：Windows x64 开发机（发布机等效）；正式收录前请在发布等效环境复测并更新本表。
