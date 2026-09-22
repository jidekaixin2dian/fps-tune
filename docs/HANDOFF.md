# HANDOFF · 项目交接现状

> 最后核对：2026-09-22（本轮：0.1 Beta 升 `main` 主线 + 待办总表 `PLAN-backlog.md`）
> 本文是**入库的长期交接文档**。单轮工作的临时提示词写进根目录 `HANDOFF_PROMPT_YYYY-MM-DD.md`
> （已被 `.gitignore` 排除），那种文件只活一轮，不要往这里抄。
> 接手请先读 `AGENTS.md`，再读本文。

## 1. 一句话现状

主线是 0.1 Beta 线（现落在 **`main`**）。**M1 已完成**（DLSS 真机闭环 + 数字振动 DVC 读写/还原）；M2 ICC 完成；M3 未开始。
`0.1.2` 仍预留给 M1 收尾后的发布，`VersionPrefix` 停在 0.1.1。
1.x 线**已停止维护**，冻结点 `legacy/1.x`（= `v1.6.2`）。

## 2. 版本与分支

| 项 | 值 |
|---|---|
| `Directory.Build.props` | `VersionPrefix=0.1.1` / `VersionSuffix=beta`（`0.1.2` 预留，见 §1） |
| 已发布 Release | `v0.1.1-beta`（2026-09-13）；资产 = Setup + Portable + SHA256SUMS |
| 未发布的内容 | ICC 滤镜、显示与画质页 —— 已进 beta 分支，但版本号仍停在 0.1.1 |
| `main` | **技术主线（0.1 Beta）**。树 = 原 `beta` 全部内容（含 ICC / DVC / 脚本校验根因修复） |
| `beta` | 原开发线；内容已并入 `main`（merge `1d9777c`），不再单独演进 |
| `legacy/1.x` | **1.x 冻结分支** = tag `v1.6.2`；停止维护，不修不发 |
| 本机安装位 | `D:\FpsTune` = `0.1.2-beta+6d0699e`（2026-09-14 构建，当时版本号已提前 bump） |
| 测试基线 | 255 / 255（`dotnet test -c Release`；含 DLSS 与数字振动回归） |
| CI | GitHub Actions **可用**（`build` + `smoke`，push 到 main 与 PR 触发） |
| catalog | 33 项；22 项需管理员、13 项需重启；预设 balanced(27) / safe-only |

**版本号回退带来的一个坑，必须知道**：本机重建后 `-Version` 会自报 `0.1.1-beta`，与已发布的
`v0.1.1-beta` **同号但内容更多**。区分两者只能看 `InformationalVersion` 里的完整 SHA，不要拿版本号当依据。
`D:\FpsTune` 那份 0.1.2-beta 安装位保持原样，不回滚。

**未决（需要用户拍板，别自作主张）**

1. 单文件 `FpsTune.exe` 没有上传到最近两个 Release，而 `RELEASE.md` §4 明确要求上传它。
   要么下次发版补上，要么改 `RELEASE.md` 删掉这个资产 —— 二者必须一致，README 已按"不提供单文件"改写。

**已决**

- **0.1 Beta 是主线，落在 `main`；1.x（1.6.X）停止维护**，冻结于 `legacy/1.x`（用户 2026-09-22）。
- **每轮收工必须更新 `AGENTS.md` + `docs/HANDOFF.md` 并 `git commit`**（用户 2026-09-22）。
- DLSS（M1）做完才发 0.1.2；期间不发版、不占版本号（用户 2026-09-21）。
- 主开发工作区 = `C:\Users\Aether\Documents\fpstune\review-3a060d1`（用户 2026-09-21 确认）。

## 3. 0.1.2 里程碑（依据 `docs/dev/PLAN-0.1.2-features.md`）

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | NVAPI DRS 基建 + DLSS 预设切换 + 数字振动 | **完成**：DLSS 真机闭环；数字振动显示级 DVC 真机读写/还原通过 |
| M2 | ICC 滤镜 + 内置预设生成器（A 卡 / Intel 兜底） | **完成**：`IccFilterService` / `IccProfileGenerator` / `IccSystemApi` + `AtomicFile`，`IccFilterTests` 332 行 |
| M3 | DRS 二期设置项（纹理过滤 / 电源管理 / 低延迟 / AA 透明度）+ 收尾 | **未开始** |

> PLAN 文档头部仍写"状态：待实施"，与事实不符（M2 已完成），本轮已就地更正为按里程碑标注。

## 4. 本轮（2026-09-22）做了什么

**开发计划总表**

- 新增 `docs/dev/PLAN-backlog.md`：全部待办按 P0–P2 排序（P0 = 目检 / 真机 A/B / 单文件口径 / 发版门禁）。
- `AI-WORKFLOW.md` 里 `.gitattributes（待办）` 已落地，改为「已用根 .gitattributes」。

**分支策略切换（用户拍板）**

- 0.1 Beta 提升为仓库主线：`git merge -X theirs beta` 进 `main`（`1d9777c`），树与 `beta` 一致。
- 1.x 停止维护：`legacy/1.x` @ `v1.6.2` 已推送；不修 bug、不发版。
- `AGENTS.md` / 本文同步改写：开发分支、硬纪律（收工必更两份交接文档）、危险操作清单。

**CI 根因修复落到 main（run 35582873412 闭环）**

- 现象：`Script_runner_uses_system_powershell_and_preserves_quoted_data` 在 main 报
  「FPS 帧律：脚本内容校验失败，已拒绝执行。」
- 根因：子进程完整性校验用 `Get-FileHash`（按需加载模块，windows-latest 上未注册）
  → catch 后一律拒绝合法脚本；旧文案又把「读不到」和「哈希不符」压成同一句。
- 修复已在 beta（`9245ac1`→`6f5c9bb`），本轮按用户选择只把这 4 个提交的补丁 cherry-pick 到 main
  （跳过 beta 独有的 AGENTS.md / HANDOFF / IccFilterTests），新提交：
  `cb821ba` / `1a7e446` / `b7e5803` / `5d1d917`，已推送 `origin/main`。
- 验证：main 上 `dotnet test -c Release` **228/228**（227 + 回归 `Load_releases_the_events_file`）。
- 根因修复要点：`[IO.File]` + `[Security.Cryptography.SHA256]`，不再依赖 PowerShell 模块。

**M1 DLSS：GetSetting AV 根因修复（阻断项已除）**

- 根因：`FindApplicationOwner` / `CreateProfile` 把 `ref IntPtr` 已写入的 profile 句柄
  再 `Marshal.ReadIntPtr` 二次解引用，得到垃圾句柄；定位「成功」但后续 `GetSetting` 必 AV。
  对照 nvidiaProfileInspector `NvapiDrsWrapper`：句柄应经 `ref IntPtr` 直接使用。
- 同步对齐 NPI 封送：设置/应用结构走 `ref struct`；`NVDRS_SETTING_UNION` 用 `Size=4100`
  原始字节，避开 union + ByValTStr 重叠。SettingID / 预设枚举值与 NPI / NVIDIA 头一致，未改。
- `FeatureEnabled` 默认改为启用（`FPS_ENABLE_DLSS=0` 可关掉）。
- 另修：备份改到 `Save()` 成功后才落盘——原先 Save 被拒会留下假备份（真机已踩到）。
- `NVAPI_ACCESS_DENIED(-175)` 如实提示需管理员，并提供「以管理员重启」（与优化页同模式）。
  DRS 持久化需要管理员，exe 仍是 `asInvoker`，不自动弹 UAC。

**真机验证（RTX 5070 Ti，`work/drs-probe`）**

- 只读：`GetDlssState` 正常（此前必 AV）。
- 提权写路径：`Apply K(0xB) → Apply M(0xD) → Remove` 全通，还原回到原值 `preset=0x0`，
  备份文件正确创建与删除。结果见 `%TEMP%\drs-probe-result.txt`。

**数字振动（M1 剩余项，同日）**

- 计划里的「DRS 数字振动」表述不准：官方 DRS 设置表**没有**该项；实际是显示级
  `NvAPI_GetDVCInfoEx` / `SetDVCLevelEx`（ID 对齐 falahati/NvAPIWrapper）。
- 诚实边界：整屏全局生效（桌面/网页/游戏），不是只在游戏内。UI 已写明。
- 读写必须同一对接口：GetDVCInfoEx ↔ SetDVCLevelEx（本机 0–100，默认 50）。
  早先混用旧 `SetDVCLevel` 会量纲漂移（set 75 读回 110），已按根因修掉。
- 真机：读 min=0/max=100/default=50；set 75%→75；还原回原档；桌面已恢复 50。

**测试**：主线 `main`（`1d9777c` + 文档改动前）**255/255**，`dotnet test -c Release`。

**环境备忘（非仓库缺陷）**：MiMo 代理 shell 可能缺 `ProgramFiles*` 变量，会导致
NuGet `Path.Combine` 炸掉；命令里补上即可。换 git bash 绕不开。

## 5. 下一步建议（按性价比排序）

**完整待办总表见 `docs/dev/PLAN-backlog.md`（P0–P2）。** 摘要：

1. **P0-1** M1 收尾目检：GUI「显示与画质」三张卡本机打开看一眼。
2. **P0-2** 真实 A/B 数据（README 截图数字**不能用于宣传**）。
3. **P0-3** 统一"单文件 exe"口径（README / RELEASE.md §4 / 实际资产）。
4. **P0-4** 0.1.2 发版门禁（依赖 P0-1/2/3）→ 部署 `D:\FpsTune` 过目。
5. **P1-1** M3（DRS 二期）与 **P1-2** 界面文案国际化可并行。
6. **P1-3** 刷新 `docs/ROADMAP.md` 现状基线（仍停在 v1.5.0）。

## 6. 维护本文的规则

- 每完成一轮工作，**同时更新 `AGENTS.md` 与本文**，并 `git commit`（用户 2026-09-22 明确要求）。
- 更新 §1、§2、§5，并在 §4 追加本轮要点（旧的可删，保持在一屏内）。
- 凡是写进本文的数字（测试数、版本号、提交号）必须当场核实，不接受"上次记录的是"。
- 与代码冲突时以代码为准，并立刻修正本文 —— 过时的交接文档比没有交接文档更危险。
