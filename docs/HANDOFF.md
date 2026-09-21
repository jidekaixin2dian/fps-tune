# HANDOFF · 项目交接现状

> 最后核对：2026-09-21（本轮由 Qoder 代理完成，全部结论来自本机实测与 `gh api` 实查，非引用旧文档）
> 本文是**入库的长期交接文档**。单轮工作的临时提示词写进根目录 `HANDOFF_PROMPT_YYYY-MM-DD.md`
> （已被 `.gitignore` 排除），那种文件只活一轮，不要往这里抄。
> 接手请先读 `AGENTS.md`，再读本文。

## 1. 一句话现状

主线是 0.1 Beta 线（1.x 线因 .NET 8 将于 2026-11-10 EOL 已整体切到 .NET 10 并重开版本号）。
ICC 滤镜（M2）已完成并全绿；DLSS 模型覆盖（M1）代码在但未启用，**`0.1.2` 这个版本号预留给它做完后的
那次发布**，所以 `VersionPrefix` 回退在 0.1.1。公开门面（README / 贡献者入口 / 交接文档）刚重写过。

## 2. 版本与分支

| 项 | 值 |
|---|---|
| `Directory.Build.props` | `VersionPrefix=0.1.1` / `VersionSuffix=beta`（`0.1.2` 预留，见 §1） |
| 已发布 Release | `v0.1.1-beta`（2026-09-13）；资产 = Setup + Portable + SHA256SUMS |
| 未发布的内容 | ICC 滤镜、显示与画质页 —— 已进 beta 分支，但版本号仍停在 0.1.1 |
| `beta` | 开发线；已备份到 `origin/beta`（ICC 工作曾只存在于本机 3 个提交里，现已在远端） |
| `main` | 门面线，**落后 beta**：缺 ICC、缺显示与画质页、缺本文件与 AGENTS.md |
| 本机安装位 | `D:\FpsTune` = `0.1.2-beta+6d0699e`（2026-09-14 构建，当时版本号已提前 bump） |
| 测试基线 | 248 / 248（`dotnet test -c Release`，31 秒） |
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
| M1 | NVAPI DRS 基建 + DLSS 预设切换 + 数字振动 | **部分**：页签与互操作层在，DLSS 覆盖未启用（稳定性验证中） |
| M2 | ICC 滤镜 + 内置预设生成器（A 卡 / Intel 兜底） | **完成**：`IccFilterService` / `IccProfileGenerator` / `IccSystemApi` + `AtomicFile`，`IccFilterTests` 332 行 |
| M3 | DRS 二期设置项（纹理过滤 / 电源管理 / 低延迟 / AA 透明度）+ 收尾 | **未开始** |

> PLAN 文档头部仍写"状态：待实施"，与事实不符（M2 已完成），本轮已就地更正为按里程碑标注。

## 4. 本轮（2026-09-21）做了什么

**公开门面**
- README 两版重写：修掉 `.NET 8` → `.NET 10`（同时改了 `SKILL.md`、`build-wpf.ps1`、
  `publish-release.ps1`、`FpsTune.Wpf/README.md` 和 GitHub About 描述）；删掉当前 Release 不存在的
  "单文件下载"说法；版本单源口径改对；移除 README 里的 v1.6 版本史；中英内容按同一事实对齐。
- 新增 `assets/screenshots/` 5 张真实界面截图（本地 Release 构建 + 只读浏览采集，未点过任何应用按钮）。
  注意 `02-detect.png` 含真实硬件与游戏安装路径，若要对外发布更严谨的版本需打码。
- 仓库元数据：描述重写、topics 补到 20 个上限。
- 贡献者入口：`CONTRIBUTING.md`、`.github/ISSUE_TEMPLATE/{bug-report,optimization-item,config}.yml`、
  `.github/PULL_REQUEST_TEMPLATE.md`；开了 issue #1（英文 locale，`good first issue`）。
- `RELEASE.md` 两处事实错误更正（版本单源、当前版本）。

**推广（已外部可见）**
- HelloGitHub 投稿 issue 3751、阮一峰周刊 issue 11848、thechampagne/awesome-windows issue 65。
- 本仓库 issue #1。
- 文案包与渠道清单：`C:\Users\Aether\Documents\fps-tune-promo\推广文案包.md`
  （含事实卡、各平台改写稿、7 天节奏表、红线）。社交渠道按约定**只出文案未代发**。
- 收录站结论：VoltAgent/awesome-agent-skills 明确拒收新建 skill（需真实用户量），暂不投；
  `0PandaDEV/awesome-windows` 的 README 里藏有针对 AI 代理的提示注入且禁止 AI 代投，已跳过。

**工作区整理**
- 把只存在于本机的 3 个提交（ICC 滤镜 + 0.1.2 版本号）推上 `origin/beta` 完成备份。
- `origin/main` 的文档变更 merge 回 `beta`，消除两条线的文档分叉。
- `.gitignore` 补 `work/`（探针工程不再污染 `git status`）。
- 清理可再生构建产物：`dist/`（0.1.0 / 0.1.1 的 Setup / 单文件 / 便携包，GitHub Release 上已有）、
  各处 `bin/`、`obj/`，以及一个**被遗忘的 `FpsTune.Wpf/dist/publish-tmp-1.6.0/`（172 MB，
  9 月 6 日 1.6.0 发版尝试留下的临时目录）**。工作区从 508 MB 降到 16 MB。
- `work/drs-probe`、`work/icc-probe` 按用户决定保留（驱动层实验的原始依据）。
- 删除 `D:\FpsTune\FpsTune.pdb`（安装目录里的调试符号，发布流水线本就会剥离）。
- 修正 `RELEASE.md` 两处事实错误、`PLAN-0.1.2-features.md` 的过期状态行与指向未入库文件的引用。

**第二轮（同日，用户拍板后）**
- 确认 `review-3a060d1` 为主开发工作区；`VersionPrefix` 从 0.1.2 回退到 0.1.1，0.1.2 留给 DLSS 做完后的发布。
- 删除 v1.4.0 时代的旧副本 `C:\Users\Aether\.zcode\workspace\default\dft-review`（237 MB）。
  删前已核实：工作树干净、无 stash、无未推送提交；`git fsck` 报出的 1 个 dangling commit
  （`5f5128d`）与已并入 `origin/main` 的 `fd931e3` 是同一份工作（rebase 前的重复对象），无独有内容。

## 5. 下一步建议（按性价比排序）

1. **M1 的 DLSS 模型覆盖**：这是 0.1.2 的发版门槛。`DisplayQualityView.xaml.cs` 目前把应用/还原按钮
   锁死并提示"稳定性验证中"，需要拿真机（RTX + 支持 DLSS 预设的游戏）验证 SettingID 与回滚是否可靠。
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
