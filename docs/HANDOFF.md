# HANDOFF · 项目交接现状

> 最后核对：2026-09-22（本轮由 MiMo 代理完成：根因修复 DLSS GetSetting AV 并真机验证通过）
> 本文是**入库的长期交接文档**。单轮工作的临时提示词写进根目录 `HANDOFF_PROMPT_YYYY-MM-DD.md`
> （已被 `.gitignore` 排除），那种文件只活一轮，不要往这里抄。
> 接手请先读 `AGENTS.md`，再读本文。

## 1. 一句话现状

主线是 0.1 Beta 线。ICC 滤镜（M2）已完成并全绿；**DLSS 模型覆盖（M1）的 GetSetting AV 已根因修复并真机闭环验证**（应用 K→M→还原），功能默认启用；数字振动仍未做。`0.1.2` 仍预留给 M1 做完后的那次发布，`VersionPrefix` 停在 0.1.1。

## 2. 版本与分支

| 项 | 值 |
|---|---|
| `Directory.Build.props` | `VersionPrefix=0.1.1` / `VersionSuffix=beta`（`0.1.2` 预留，见 §1） |
| 已发布 Release | `v0.1.1-beta`（2026-09-13）；资产 = Setup + Portable + SHA256SUMS |
| 未发布的内容 | ICC 滤镜、显示与画质页 —— 已进 beta 分支，但版本号仍停在 0.1.1 |
| `beta` | 开发线；已备份到 `origin/beta`（ICC 工作曾只存在于本机 3 个提交里，现已在远端） |
| `main` | 门面线，**落后 beta**：缺 ICC、缺显示与画质页、缺本文件与 AGENTS.md |
| 本机安装位 | `D:\FpsTune` = `0.1.2-beta+6d0699e`（2026-09-14 构建，当时版本号已提前 bump） |
| 测试基线 | 251 / 251（`dotnet test -c Release`；含 DLSS Save 失败不写假备份等 2 条新回归） |
| CI | GitHub Actions **可用**（`build` + `smoke`，push 到 main 与 PR 触发） |
| catalog | 33 项；22 项需管理员、13 项需重启；预设 balanced(27) / safe-only |

**版本号回退带来的一个坑，必须知道**：本机重建后 `-Version` 会自报 `0.1.1-beta`，与已发布的
`v0.1.1-beta` **同号但内容更多**。区分两者只能看 `InformationalVersion` 里的完整 SHA，不要拿版本号当依据。
`D:\FpsTune` 那份 0.1.2-beta 安装位保持原样，不回滚。

**未决（需要用户拍板，别自作主张）**

1. `main` 是否快进到 `beta`？文档层已合流（`origin/main` 的 README 已 merge 进 beta），代码层 beta 领先。
   正常路径是等 DLSS 做完、发 0.1.2 时一起合。
2. 单文件 `FpsTune.exe` 没有上传到最近两个 Release，而 `RELEASE.md` §4 明确要求上传它。
   要么下次发版补上，要么改 `RELEASE.md` 删掉这个资产 —— 二者必须一致，README 已按"不提供单文件"改写。

**已决**

- DLSS（M1）做完才发 0.1.2；期间不发版、不占版本号（用户 2026-09-21）。
- 主开发工作区 = `C:\Users\Aether\Documents\fpstune\review-3a060d1`（用户 2026-09-21 确认）。

## 3. 0.1.2 里程碑（依据 `docs/dev/PLAN-0.1.2-features.md`）

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | NVAPI DRS 基建 + DLSS 预设切换 + 数字振动 | **DLSS 已通**：GetSetting AV 根因修复（句柄二次解引用），真机应用/还原闭环；数字振动未做 |
| M2 | ICC 滤镜 + 内置预设生成器（A 卡 / Intel 兜底） | **完成**：`IccFilterService` / `IccProfileGenerator` / `IccSystemApi` + `AtomicFile`，`IccFilterTests` 332 行 |
| M3 | DRS 二期设置项（纹理过滤 / 电源管理 / 低延迟 / AA 透明度）+ 收尾 | **未开始** |

> PLAN 文档头部仍写"状态：待实施"，与事实不符（M2 已完成），本轮已就地更正为按里程碑标注。

## 4. 本轮（2026-09-22）做了什么

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

**测试**：251/251（新增 `Failed_save_does_not_leave_fake_backup`、
`Save_denied_error_mentions_admin_rights`）。

**环境备忘（非仓库缺陷）**：MiMo 代理 shell 可能缺 `ProgramFiles*` 变量，会导致
NuGet `Path.Combine` 炸掉；命令里补上即可。换 git bash 绕不开。

## 5. 下一步建议（按性价比排序）

1. **数字振动**（M1 剩余）：N 卡 `NvAPI_Disp_ColorControl`（不是 DRS 按游戏项）；
   计划里的「DRS 数字振动」表述可能不准，实现时以官方头为准，做完再发 0.1.2。
2. 真实 A/B 数据：目前 README 截图里的实验数字是历史/模拟状态，**不能用于宣传**。
   跑一轮真机 `-Experiment`（需 `winget install Intel.PresentMon.Console` + 真实对局）拿到可引用的收益。
3. 统一"单文件 exe"口径：`README.md` / `RELEASE.md` §4 / 实际 Release 资产三处现在不一致，见 §2 未决 2。
4. 界面文案国际化（issue #1）——公开口径里已经承诺了这件事。
5. `docs/ROADMAP.md` 的现状基线段落仍停在 v1.5.0，下次动 ROADMAP 时一并刷新；
   在它刷新之前，本文是唯一的现状来源。

## 6. 维护本文的规则

- 每完成一轮工作，更新 §1、§2、§5，并在 §4 追加本轮要点（旧的可删，保持在一屏内）。
- 凡是写进本文的数字（测试数、版本号、提交号）必须当场核实，不接受"上次记录的是"。
- 与代码冲突时以代码为准，并立刻修正本文 —— 过时的交接文档比没有交接文档更危险。
