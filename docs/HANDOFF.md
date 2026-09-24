# HANDOFF · 项目交接现状

> 最后核对：2026-09-24（本轮：环境变量"缺失"根因复核 + 环境事实文档化 P2-5 + 版本同步状态核对）
> 本文是**入库的长期交接文档**。单轮工作的临时提示词写进根目录 `HANDOFF_PROMPT_YYYY-MM-DD.md`
> （已被 `.gitignore` 排除），那种文件只活一轮，不要往这里抄。
> 接手请先读 `AGENTS.md`，再读本文。

## 1. 一句话现状

主线是 0.1 Beta 线（现落在 **`main`**）。M1/M2/M3 功能与一键优化、i18n 已发 **`v0.1.3-beta`**。
1.x 线**已停止维护**，冻结点 `legacy/1.x`（= `v1.6.2`）。

## 2. 版本与分支

| 项 | 值 |
|---|---|
| `Directory.Build.props` | `VersionPrefix=0.1.3` / `VersionSuffix=beta` |
| 已发布 Release | **`v0.1.3-beta`**（2026-09-22，`29ed0ea`）；资产 = Setup + Portable + SHA256SUMS。上一版 `v0.1.2-beta` |
| 未发布的内容 | 无（`v0.1.3-beta` 已含 M1/M2/M3 + 一键优化 + i18n 框架文案；catalog 说明翻译见 P2-1） |
| `main` | **技术主线（0.1 Beta）**。树 = 原 `beta` 全部内容（含 ICC / DVC / 脚本校验根因修复） |
| `beta` | 原开发线；内容已并入 `main`（merge `1d9777c`），不再单独演进 |
| `legacy/1.x` | **1.x 冻结分支** = tag `v1.6.2`；停止维护，不修不发 |
| 本机安装位 | `D:\FpsTune` = **`0.1.2-beta`**（2026-09-22 构建，实测 `deps.json`）；**落后已发布的 `v0.1.3-beta` 一版**，升级需用户点头（P1-4） |
| 测试基线 | 266 / 266（`dotnet test -c Release`；2026-09-24 复核） |
| CI | GitHub Actions **可用**（`build` + `smoke`，push 到 main 与 PR 触发） |
| catalog | 33 项；22 项需管理员、13 项需重启；预设 balanced(27) / safe-only |

### 2.1 版本与同步状态核对（2026-09-24 实测，取代旧的"版本号回退坑"记载）

| 位置 | 版本 | 同步状态 |
|---|---|---|
| GitHub 已发布 Release | `v0.1.3-beta` | **已是最新，无需同步** |
| 本机安装位 `D:\FpsTune` | `0.1.2-beta` | **落后一版**（`-Version` 自报 `0.1.2-beta`）；升级需用户点头 → P1-4 |
| 本机 `dist/` 里的 0.1.3 产物 | `0.1.3-beta` | 与发布产物**逐字节一致**：`FpsTune-Portable-0.1.3.zip` 实测 SHA256 = `4E055D2E9AF56F1A7995662764B881F0CB289FCAA66BD8B3CA1F14B4B47DEE6A`，与发布页 `SHA256SUMS-v0.1.3.txt` 相同 |
| 源码 `main` | `0.1.3-beta` | 树 = `v0.1.3-beta` 的代码，**无代码分歧**；另有纯文档/杂项提交未 push |

- **本机只有一份可运行副本**：开始菜单 `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\FPS 帧律.lnk`
  解析后指向 `D:\FpsTune\FpsTune.exe`（用 Python 解析 `.lnk` 得到；`WScript.Shell` COM 被安全策略拦）。
  `%LOCALAPPDATA%\FpsTune` 是**配置/备份目录**（`lang.txt` / `settings.json` / `backup` / `logs` / `sessions`），
  **不是安装**。另有更早的备份 `D:\FpsTune-backup-20260922`（exe 日期 2026-09-14）。
- **本地源码与 GitHub 无分歧**（用户 2026-09-24 追问后实测）：本轮改动前本地 `HEAD` 与 `origin/main`
  同为 `c28e94f`；`git rev-list --left-right --count origin/main...HEAD` = `0 1`（远端无本地缺失的提交）；
  `git fetch` 后亦无新提交（只带回两个 tag）。本轮改动均叠加在此之上。
- 旧的"本机重建后 `-Version` 自报 `0.1.1-beta`、与已发布版同号不同内容"记载**已失效**
  （`VersionPrefix` 现为 `0.1.3`）。保留其教训：**判断版本只看 `Directory.Build.props` 与
  `InformationalVersion` 的完整 SHA，不要拿版本号当依据。**
- `D:\FpsTune` 那份 0.1.2-beta 安装位**尚未回滚也未升级**，保持原样待拍板。

**未决（需要用户拍板，别自作主张）**

1. **是否 push `main`**：`main` 领先 `origin/main`（2 个纯文档/杂项提交 + 本轮 HANDOFF/AGENTS 更新），
   按纪律停在本机，等用户拍板。**push 不会产生新 Release**（`v0.1.3-beta` 已存在）。
2. **是否把 `D:\FpsTune` 升到 `0.1.3-beta`**（即 P1-4）：覆盖前必须用户点头。
   建议做法：先整目录备份为 `D:\FpsTune-backup-20260924`，再用 `dist/folder-0.1.3/` 覆盖——
   **不跑安装器、不碰注册表**，可整目录回退。
3. 原「单文件 exe 口径」已决：公开 Release **不提供单文件**，`RELEASE.md` §4 已改为三资产
   （Setup + Portable + SHA256SUMS），与 README / README.en 下载表一致。

**已决**

- **发布流程（用户 2026-09-22）**：本地构建 → git 提交 → **给用户看效果** → **用户拍板** → 才发布。
  拍板前禁止 `gh release create`、禁止覆盖 `D:\FpsTune`。
- **跳过真机 A/B、直接发 0.1.2**（用户 2026-09-22）；**禁止编造收益数字**。
- **公开 Release 不提供单文件 exe**（2026-09-22）：只发 Setup + Portable + SHA256SUMS。
- **0.1 Beta 是主线，落在 `main`；1.x（1.6.X）停止维护**，冻结于 `legacy/1.x`（用户 2026-09-22）。
- **每轮收工必须更新 `AGENTS.md` + `docs/HANDOFF.md` 并 `git commit`**（用户 2026-09-22）。
- DLSS（M1）做完才发 0.1.2；期间不发版、不占版本号（用户 2026-09-21）。
- 主开发工作区 = `C:\Users\Aether\Documents\fpstune\review-3a060d1`（用户 2026-09-21 确认）。

## 3. 0.1.2 里程碑（依据 `docs/dev/PLAN-0.1.2-features.md`）

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | NVAPI DRS 基建 + DLSS 预设切换 + 数字振动 | **完成**：DLSS 真机闭环；数字振动显示级 DVC 真机读写/还原通过 |
| M2 | ICC 滤镜 + 内置预设生成器（A 卡 / Intel 兜底） | **完成**：`IccFilterService` / `IccProfileGenerator` / `IccSystemApi` + `AtomicFile`，`IccFilterTests` 332 行 |
| M3 | DRS 二期设置项（纹理过滤 / 电源管理 / 低延迟 / AA 透明度）+ 收尾 | **服务+UI+单测完成**（261/261）；SettingID 已对官方 NvApiDriverSettings.h；真机 apply→restore 待用户过目 |

> PLAN 文档头部仍写"状态：待实施"，与事实不符（M2 已完成），本轮已就地更正为按里程碑标注。

## 4. 本轮（2026-09-24）做了什么

**环境变量"缺失"根因复核 + 环境事实文档化（P2-5）**

- 用户反馈"每次环境变量都缺失"。复核结论：**变量一个都没丢**。MSYS2 运行时
  （`winsup/cygwin/environ.cc` 的 `renv_arr[]`）在进程启动时经 `win32env_to_cygenv()` → `ucenv()`
  把 `ProgramFiles` / `CommonProgramFiles` / `ComSpec` / `SystemRoot` / `SystemDrive` / `windir`
  这 6 个名字**改写成全大写**；bash 变量名区分大小写，`$ProgramFiles` 才取到空值。
  不在表里的 `ProgramFiles(x86)` / `ProgramW6432` / `ProgramData` 保持原样（实测吻合，因为不匹配
  表里带 `=` 的条目）。
- 实测**证伪旧记载**：不做任何 env 修补时 `dotnet restore --force` 退出码 0、
  `dotnet test -c Release` **266/266 通过**。仓库内仅 2 处引用该变量
  （`build-installer.ps1` 的 PowerShell、`NativeSystem.cs` 的 .NET API），两者均大小写不敏感。
- 落地 P2-5：`docs/dev/AI-WORKFLOW.md` 新增「环境变量」一节（含 `renv_arr[]` 清单与正确用法），
  `CONTRIBUTING.md` 加同一条目；`AGENTS.md` 环境事实段同步。
- 顺手修掉几处自相矛盾 / 过期：`AGENTS.md` 基线注释 255→266；本文 §2 的
  `VersionPrefix=0.1.2`→`0.1.3`、测试基线 255→266、`未发布的内容`；`CONTRIBUTING.md` 里
  "国际化全未做"改为"框架文案已完成、catalog 说明待译"。
- 追加提交 `c95ba7a`：`.gitignore` 忽略 `.workbuddy-ai/`（本地 AI 代理工作数据；按仓库
  "本地 AI 文档不入库"的既有约定）。
- **版本同步状态核对（用户追问后实测，结论写入 §2.1）**：GitHub 已发布的 `v0.1.3-beta` 就是最新，
  **无需同步**；落后的只有本机安装位 `D:\FpsTune`（`0.1.2-beta`）；本机 `dist/` 的 0.1.3 产物与
  发布产物逐字节一致（SHA256 相同）。并核实**本地源码与 GitHub 无分歧**。
- 为用户启动 `dist/folder-0.1.3/FpsTune.exe` 目检 0.1.3（独立便携版，**不碰 `D:\FpsTune`**），
  `-Version` 自报 `0.1.3-beta`。**用户尚未拍板**是否 push 与是否升级本机安装位。

### 历史轮次（2026-09-22 及更早）

**P1-2 国际化 + P2-6 热门项（2026-09-22）**

- `LangService` + `Resources/Strings.zh-CN|en-US.xaml`；设置页可选语言并持久化，默认中文。
- 框架文案（导航/设置分区/一键/显示页卡片标题与主按钮）已接 `DynamicResource`。
- P2-6：AF16x、VSync 强制关、着色器磁盘缓存开 —— 进 DRS 与一键/竞技包；三重缓冲 D3D 无独立项（关 VSync 即可，已在 ToolTip 说明）。
- 测试 266/266。**catalog 33 项说明仍中文**（issue #1 拆出后续）。

组合 = ① catalog **均衡档**（含 `power-ultimate` 电源卓越性能、`gpu-pref` 高性能 GPU）
+ ② **DLSS K 模型** + ③ 显卡 3D **1070 Ti 档**（纹理高质量 · 电源最高性能优先 · 透明度 **2x** · 预渲染 1）
+ ④ 电源方案随均衡档。实现：`OneClickOptimizer.ApplyAsync` / `ApplyGpu1070TiPack`；优化页「一键优化」。
**不伪装显卡型号**（红线二）——「1070 Ti」仅作 3D 档位标签。测试 264/264。

- 使用 agent-reach（Exa 网页搜索；小红书/OpenCLI 无登录态）交叉核对 NVIDIA 官方说明 + 攻略站/教学文。
- **推荐策略（用户拍板 2026-09-22）**：
  - 电源管理：**完整保留**，默认推「最高性能优先」
  - 纹理过滤·质量：**优先推荐「高质量」**（现代 N 卡帧率影响小、远景清晰）
  - 平滑处理·透明度：**默认推超级采样 2x**；**仅桌面非笔电高端卡（5070 Ti 级）才推 4x**
  - 低延迟·预渲染帧：**评估为 1 帧**（≈驱动低延迟模式「开启」）；游戏内有 Reflex 则「应用程序控制」
- 一键「竞技推荐」`ApplyCompetitivePreset`；`HardwareInfoService.IsDesktop` / `IsHighEndNvidia`。
- 测试 263/263。

- P1-3 完成：ROADMAP 基线改写为 0.1.2-beta。
- P1-1 M3 实现：`QUALITY_ENHANCEMENTS` / `PREFERRED_PSTATE` / `AA_MODE_ALPHATOCOVERAGE`+`AA_MODE_REPLAY` /
  `PRERENDERLIMIT`（全部取自 NVIDIA/nvapi `NvApiDriverSettings.h`）。
- 显示页新增「驱动 3D 设置」卡；与 DLSS 共用备份/还原。测试 261/261。
- 待用户目检 M3 卡后，再谈发版（按拍板后发流程）。

- P0-1 目检完成（`b7fe071`）；P0-2 用户取消；P0-3 口径统一（`8427593`）。
- **P0-4 完成**：`4e6b728` 构建 → `gh release create v0.1.2-beta`（Setup + Portable + SHA256SUMS）。
- Release Notes 无编造 FPS/收益数字。
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

**环境备忘（2026-09-24 复核，原记载有误）**：旧记载为"MiMo 代理 shell 可能缺 `ProgramFiles*` 变量，
会导致 NuGet `Path.Combine` 炸掉；命令里补上即可。换 git bash 绕不开"。实测**不复现**——
变量没丢，只是 MSYS2 把名字转成了大写（详见本文 §4 本轮要点与
`docs/dev/AI-WORKFLOW.md` §环境变量）。**不要再给命令补 env**，那是无效动作。

## 5. 下一步建议（按性价比排序）

**完整待办总表见 `docs/dev/PLAN-backlog.md`（P0–P2）。** 摘要：

1. **P1-4** 将 `D:\FpsTune` 从实测的 `0.1.2-beta` 升到 `v0.1.3-beta` 发布构建（覆盖前需你点头）。
2. **P2-1** catalog 33 项的 `name` / `description` / `sideEffect` 英文翻译 —— i18n 收尾；
   英文 locale 下目前会露出中文。
3. **P2-3** 盘点 `docs/dev/GUI_PLAN.md` 与现界面的差异，只留真缺口（先盘点再动手）。
4. **P2-2 / P2-4** A/B 报告可引用导出、M4+ 更多 DRS / 显示项（红线要求先证明收益）。
5. 若要可引用 A/B 数字，再跑真机 `-Experiment`（P0-2 已取消，可作 P2）。

## 6. 维护本文的规则

- 每完成一轮工作，**同时更新 `AGENTS.md` 与本文**，并 `git commit`（用户 2026-09-22 明确要求）。
- **发布必须停在「已构建待拍板」**：本地构建 + git 后把效果交给用户，等拍板再发（用户 2026-09-22）。
- 更新 §1、§2、§5，并在 §4 追加本轮要点（旧的可删，保持在一屏内）。
- 凡是写进本文的数字（测试数、版本号、提交号）必须当场核实，不接受"上次记录的是"。
- 与代码冲突时以代码为准，并立刻修正本文 —— 过时的交接文档比没有交接文档更危险。
