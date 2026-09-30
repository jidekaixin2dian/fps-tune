# HANDOFF · 项目交接现状

> 最后核对：2026-09-29（本轮 ㉔ 收官：**`v0.1.18-beta` 已发布**——P3-10 catalog 扩展三项 + backlog 清理，push/Release/核验全链路完成）
> 本文是**入库的长期交接文档**。单轮工作的临时提示词写进根目录 `HANDOFF_PROMPT_YYYY-MM-DD.md`（当前：`HANDOFF_PROMPT_2026-09-29.md`）
> （已被 `.gitignore` 排除），那种文件只活一轮，不要往这里抄。
> 接手请先读 `AGENTS.md`，再读本文。

## 1. 一句话现状

主线是 0.2 Beta 线（现落在 **`main`**）。**最新已发布 `v0.2.1-beta`**（2026-09-30，标签 `86f84f4`，内容 = M2 多游戏上下文，已设 Latest）。上一版 `v0.2.0-beta.1`（`66c0ab3`）。
**下一阶段：0.2.0 大版本迭代（beta.1 进行中）**。范围已拍板 = M1 + M2 + M4 + 候选池六项（M3 HUD 顺延 0.2.1），见 `docs/dev/PLAN-0.2.0-迭代规划.md`（v3）。
**beta.1 进度（2026-09-30 收官）**：C-C / C-E / C-A / M1 后端+UI 全部完成、C-D 审计确认已存在；**0.2.0-beta.1 候选已按 `66c0ab3` 构建（哈希核验）并部署 `D:\FpsTune`（0.1.18 快照存 `-1139`，注：原 0.1.16 快照被本轮复用顶掉，0.1.16 已发布可随时重建）**，UIA 三页目检 PASS；发版等用户拍板。
1.x 线**已停止维护**，冻结点 `legacy/1.x`（= `v1.6.2`）。

## 2. 版本与分支

| 项 | 值 |
|---|---|
| `Directory.Build.props` | `VersionPrefix=0.1.18` / `VersionSuffix=beta` |
| 已发布 Release | **`v0.1.18-beta`**（2026-09-29 发布，标签 = `1f840db62a1bfaf295103f2aaf09f1336dd469a9` ✓，pre-release；三资产字节数与本地逐项一致：Setup 60655338 / Portable 983969 / 清单 266 ✓，清单哈希复核一致 ✓）。上一版 `v0.1.17-beta`（`00f6b23`） |
| 发布候选（未发布） | （无） |
| `main` | origin 同步至 `dcd5e28`（backlog 清理收尾）；本地与 origin 一致 |
| `beta` | 原开发线；内容已并入 `main`（merge `1d9777c`），不再单独演进 |
| `legacy/1.x` | **1.x 冻结分支** = tag `v1.6.2`；停止维护，不修不发 |
| 本机安装位 | `D:\FpsTune` = **`0.1.18-beta` 内嵌 SHA `1f840db`**（= 已发布版本；UIA 优化页断言 + CLI `-Detect -Json` 真机验证通过）。回滚点备份：`-00f6`（0.1.17）、`-1139`（0.1.16）、`-2f97`（0.1.15） |
| 测试基线 | 320 / 320（`dotnet test -c Release`；2026-09-29 复核，含 DetectMeta 3 条 + catalog 36 项计数/重启快照更新） |
| 编译警告 | **0**（2026-09-28 `--no-incremental` 全量重编译复核；**增量构建会假报 0**，核对必须全量，见 `AI-WORKFLOW.md` §二） |
| CI | GitHub Actions **可用**（`build` + `smoke`，push 到 main 与 PR 触发） |
| catalog | 36 项（P3-10 扩展 3 项）；25 项需管理员、16 项需重启；预设 balanced(29) / safe-only |


> **历史核对记录**：0.1.7 及更早的版本/哈希/同步逐项核对（含 0.1.4~0.1.7 三资产哈希、CRLF 清单坑、升级做法）已随 §2.1 整体归档至 [docs/archive/HANDOFF-轮次存档-20260925.md](archive/HANDOFF-轮次存档-20260925.md)（2026-09-29；该节曾停留在 0.1.7 时点、与现状冲突）。

## 3. 0.1.2 里程碑（依据 `docs/dev/PLAN-0.1.2-features.md`）

**M1 / M2 / M3 全部完成并已发布**（M1 NVAPI+DLSS+数字振动、M2 ICC 滤镜与生成器、
M3 DRS 二期五项——均随 0.1.3 及此前版本上线）。细节见 PLAN 文档与 git 历史，本节不再维护表格。

## 4. 最近几轮做了什么（㉘ = 2026-09-30；⑯及更早见归档）

**㉘ M2 多游戏上下文·beta.2 实现（2026-09-30，本轮）**

用户指令「beta.2 = M2 多游戏上下文 开工」。设计轮先读透检测数据流
（MainWindow.RefreshDetectionAsync → DetectView.RunDetectionAsync → ApplyDetectData → AppState.Items）再动刀。

- **存储层（`5c6a34a`）**：StateStore 增 GameProfile（games.json）——首次访问把旧
  game-path.txt 迁移为单一档案（幂等）；AddGame 按路径去重；逐游戏快照
  last-detect-<id>.json + 逐游戏 meta（缺失回退全局）。**红线：CLI 契约文件
  last-detect.json 绝不被逐游戏写入**（哨兵测试）。测试隔离修复：三个用静态
  override 的测试类挂入 "BackupService serial" 串行集合（并行竞态假失败）。
- **切换链路（`c3387a8`）**：DetectionData 把 items 解析从 DetectView 抽出共用；
  ConsoleView 开局准备卡新增切换器（档位 + 「＋ 添加游戏…」文件选择建档，
  定位流程顺带建档）；SwitchToGame = 设当前游戏（game-path.txt 同步、CLI 对齐）
  → 灌快照或清空 → 全页重建 → NotifyGameSwitched 同步检测页（有快照灌快照、
  无快照清空并复用 Loaded 自动检测）；检测成功写当前游戏快照 + meta；
  实测徽标按当前游戏过滤（不同游戏结论不串台）。
- **如实记录**：AutoProfile 自动切上下文（切进程=切游戏）为 beta.2 第二步，
  本轮先交付手动切换全链路；HomeView（经典概览）未接切换器，切游戏后其
  游戏相关显示会随下次检测刷新。
- **验证**：342/342（+ GameContextStoreTests 4 条：迁移幂等/路径去重/快照隔离/
  meta 回退）；`--no-incremental` 0 警告；三资产哈希核验一致（内嵌 SHA `86f84f4`）；
  UIA 概览页 PASS（切换器渲染、迁移档案生效、回退语义正确——"三角洲行动 ·
  开局准备"）；error.log 350335 不变。部署备份：本轮直接覆盖（回滚点 -1139=0.1.18）。

**㉗ Latest 徽章校准 + 发布口径变更（2026-09-30）**

**㉗ Latest 徽章校准 + 发布口径变更（2026-09-30，本轮）**

用户发现 GitHub "Latest" 停在 v0.1.3-beta 并要求指到 0.2.0。排查：早期 v0.1.0~0.1.3 发布时
尚未确立 pre-release 纪律（pre=false），从 v0.1.7 起全标 pre-release，而 GitHub Latest 只认
非预发布版。**口径变更（用户拍板）**：`gh release edit v0.2.0-beta.1 --prerelease=false --latest`
——此后 beta 版发布也取消 pre-release 标记并设 latest（版本号与应用内 beta 后缀不变，
只是 GitHub 侧不再打 Pre-release 标签）。应用内更新检查用列表端点，不受影响。
另记录：v0.1.8~11 与 v0.1.15 从未单独发 Release（攒进后续版本），列表缺号正常。

**㉖ README 对齐 0.2.0 + 工作区清理（2026-09-30）**

**㉖ README 对齐 0.2.0 + 工作区清理（2026-09-30，本轮）**

用户三指令：README 更新、工作区清理、交接文档更新。

- **README（中英）**：版本线改为 0.2 Beta（实测闭环阶段）；界面表 A/B 描述改为
  "候选组 / 自定义组"；新增「实测闭环（0.2.0 起）」段（自定义实测 / 徽标 / 报告导出 /
  会话帧率 / 瓶颈判定）；设置列表补"诊断包导出与一键反馈"；架构图 Services 行
  去掉已删除的"脚本定位"（friend-test 于 P2-9 移除）改为"实验引擎与判定存储 / 采样"。
- **工作区清理**：dist 494M→124M——删除 0.1.16/0.1.17/0.1.18 的全部资产
  （均已发布在 GitHub Release，可随时取回），仅留 0.2.0（= 当前已发布版本）；
  仓库 bin/obj 清空（下次构建自动重建）。`work/` 探针与日志按纪律保留；
  D 盘备份链未动（-2f97 = 0.1.15 / -00f6 = 0.1.17 / -1139 = 0.1.18，如需清理另行拍板）。
- 验证：README 双语 grep 核对（版本线/实测闭环段/架构行）；git 工作树仅 README 两文件改动；
  dist 为 gitignored 目录，清理不入册。

**㉕ 0.2.0-beta.1 第一批实现（2026-09-30）**

**㉕ 0.2.0-beta.1 第一批实现（2026-09-30，本轮）**

用户拍板"全塞进去"（候选池六项全部纳入 0.2.0）后指令"动手做"。

- **规划 v2/v3**（`76a843d` 前后）：用户指出 v1 未自审即交付；源码级复核推翻两大前提——
  实验引擎已完整存在（M1 从"新建管线"改"泛化现有引擎"，工作量 大→中）、会话无 FPS
  （报告改存实验历史+判定存储）。审查记录固化在 PLAN §0，教训入长期记忆。
- **C-C 一键反馈流（`f11609a`）**：诊断包导出后 Confirm → 打开预填环境摘要的 GitHub issue
  （版本/系统/管理员）；新增 8 对资源键。
- **C-E 会话 FPS（`9fa795b`）**：FrameTimeStats 从 ExperimentRunner 抽出共用（结构化错误，
  CLI JSON 逐字节一致）；SessionFpsRecorder 随会话起停 PresentMon（stdout 排空防阻塞），
  三种降级原因资源键明示；PerformanceSession 增可空 Fps/FpsNote；SessionView 洞察置顶
  FPS 结论 + 运行状态提示 + 历史摘要。
- **C-A 瓶颈判定（`6c0dc42`）**：SessionInsights.ClassifyBottleneck 纯阈值分类器
  （GPU 95 位≥95 且平均≥85 / GPU≤60 且 CPU 95 位≥90 且平均≥65 / 其余未判定），
  洞察卡置顶渲染，文案自带"启发式不代表因果"。
- **C-D 审计**：更新器静默安装链路已存在（0.1.14 轮），无需开发——候选池已更正。
- **M1 后端（`7aebfa1`）**：ExperimentRunner 增 step "custom"（Options.Items，需重启项明确
  拒绝）+ VerdictStore（真实模式实验结论 upsert verdicts.json，Simulate 绝不入册）。
- **验证**：337/337 全绿（+10 用例：SessionFps 7 + VerdictStore 5 - 归并）、
  `--no-incremental` 0 警告；I18n 棘轮全程不增。
- **M1 UI 露出层（`8cdc341`）**：AppState.SelectedIds 跨页勾选同步；优化页/概览页项级实测徽标（无记录不占位）；A/B 页自定义实测卡（读勾选、运行 custom 步、状态反馈）+ 本机实测结论卡（Δ 染色列表）+ 报告导出（VerdictReport.Build，语言随界面，只含实测与采样条件）。
- **启动即崩事故与修复（`66c0ab3`，如实记录）**：0.2.0 首轮构建部署后无法启动——前台运行 exe 抓到 XamlParseException：新增报告键 `Str.ReportGenerated` 与诊断报告旧键重名（WPF 合并字典重复键不报编译错、启动即崩）。改名 `Str.VerdictReportGenerated` + StringResourceTests 增同字典重复键守卫（338 条用例）。**首轮构建（0440e10）已作废重建，产物内嵌 SHA = `66c0ab3`**。守卫缺口复盘：既有测试只查中英键集对齐，没查字典内重复。
- **验证**：338/338（+重复键守卫）；`--no-incremental` 0 警告；三资产哈希核验一致；UIA 目检——A/B 页五断言 PASS（自定义实测/本机实测结论/运行/导出/空态文案）、概览页 PASS、优化页 PASS（127ms）、MISSING_KEYS 全否、error.log 350335 不变。
  优化页两级徽标）→ bump 0.2.0-beta.1 → 构建/部署/GUI+UIA 目检（XAML 改动需真渲染）。

**㉔ v0.1.18-beta 发布 + P3-10 catalog 扩展三项（2026-09-29）**

**㉔ v0.1.17-beta 发布 + P3-10 catalog 扩展三项（2026-09-29，本轮）**

用户指令「push + Release 一下 + 继续开发」。v0.1.16 当天刚发布，新内容只有自查修复 → 发 `v0.1.17-beta`；
继续开发按交接提示默认方向做优化项扩展调研（`PLAN-P3-10`），用户从 4 候选批准 3 项落地。

- **v0.1.17 发版**：bump `00f6b23` → 两脚本产三资产（哈希逐项核）→ 部署 `D:\FpsTune`
  （0.1.16 快照存 `-1139`）→ push + `gh release create v0.1.17-beta --target 00f6b23… --prerelease`
  + 三资产 → 标签/资产字节数核验 ✓。内容 = 检测异常路径修复 + DetectMeta 测试。
- **P3-10 调研**（`5e7215c`）：agent-reach（Exa）核官方文档——4 候选（鼠标缓冲 / 全局定时器
  分辨率 / PCIe ASPM / 网卡节能）+ 6 项评估为不做（Spectre 缓解、VBS、HPET、Defender 排除、
  LargeSystemCache、MSI 模式），结论入册 `docs/dev/PLAN-P3-10-catalog-expansion.md` 防重查。
  用户拍板：**C1/C3/C4 落地，C2 不做**。
- **三项落地（`9f3e796`）**：catalog 33→36；引擎/备份/还原/校验/检测五个挂钩点全补——
  新备份 Kind `power-aspm`（只还原实际读到的原 AC 值，与 power-tuning 同口径）+ `OldAspmValue`
  字段（Validation 两处防线同步）；nic 逐物理网卡多条 registry 记录（`Characteristics` 位过滤
  虚拟网卡、`PnPCapabilities` 按位或 0x18、白名单校验自动通过）；检测状态函数与 Apply 过滤口径一致。
  新代码文案全部走 `Str.T` 新增 16 对中英键——**App.xaml 静态合并 zh-CN 字典，CLI 取值稳定**；
  I18n 棘轮不增反减（Restore 顺手 -1）。
- **0.1.18 候选与部署**：bump `1f840db` → 三资产哈希核 → 部署 `D:\FpsTune`（0.1.17 快照存 `-00f6`）
  → explorer 启动。**验证**：320/320；`--no-incremental` 0 警告；UIA 优化页——`鼠标缓冲区扩容`
  直接可见、PCIe/网卡两项经搜索框过滤后断言渲染 PASS（列表虚拟化，视口外行不进 UIA 树，
  直接断言会假阴性）、MISSING_KEYS 无；error.log 350335 不变。
- **真机发现（如实记录）**：CLI 非管理员下 `nic-power-save-off` 检测读 Class 子键被拒
  （"Requested registry access is not allowed."）——错误被优雅捕获如实显示，与 `gpu-pstate-lock`
  读 GPU 驱动键（可读）口径不同是 ACL 事实；GUI 提权运行下正常。`pcie-aspm-off` 本机实测
  ASPM 已为 0（报达标）。
- **文档口径同步**：README/README.en/ROADMAP/AGENTS/HANDOFF 的 33 项/22 管理员/13 重启/
  balanced(27) 全部改为 36/25/16/29。
- **backlog 清理（用户指令「P0 到 P2 做过的东西做下清理」，`3d12bb2`）**：P0–P2 原文
  （三张任务表 + 已完成表 + 依赖关系图）整体迁入
  `docs/archive/PLAN-backlog-P0-P2-存档-20260929.md`；backlog 119→52 行（现行排期 +
  暂停项单独成节 + 明确不做补"不要重查重提"指针）；HANDOFF §5 同步校准到 0.1.18 时点
  （原停留 0.1.16、含一条早已完成的"卡片化等表态"）；docs/README 与 AGENTS 权威表指针更新。
- **发版（用户拍板「可以 push 并发版了」）**：push `00f6b23..dcd5e28`（6 提交）→
  `gh release create v0.1.18-beta --target 1f840db… --prerelease` + 三资产 → 标签/资产字节数
  核验 ✓（Portable 983969 / Setup 60655338 / 清单 266）。**如实更正**：㉔ 轮记录与单轮提示词
  曾误把 0.1.17 的资产字节数（979856/60651342）当 0.1.18 的写入文档，发布核验时发现并已改。

**㉓ v0.1.16-beta 发布 + 深度自查（2026-09-29）**

用户指令「按 HANDOFF_PROMPT 开发」+ 拍板：批准发版、本轮做深度自查（dist 旧资产清理经查**已在
`e6959e9` 轮完成**，交接提示第 3 条已失效）。

- **发版**：push `8b48519..e6959e9`（7 提交）→ `gh release create v0.1.16-beta --target
  11398ea13bcea5fc2e8950b73c64c9986b865927 --prerelease` + 三资产（Portable / Setup / SHA256SUMS，
  与 0.1.14 同口径——单文件 exe 只进清单不上架）。**核验**：标签 target 完整 SHA ✓、pre-release ✓、
  三资产字节数与本地逐项一致（60650801 / 979803 / 266）✓、清单哈希对本地资产复核一致 ✓。
  Notes 格式对齐 0.1.14（存 `/tmp/release-notes-0.1.16.md`，未留 work/ 副本）。
- **深度自查**（聚焦 0.1.16 两个代码提交 `2f9712f` / `11398ea`）：
  - **修复（`2cc2af4`）**：检测抛异常时 catch 分支漏调 `RefreshLastDetectText()`，页头状态行
    卡「正在检测...」不恢复（成功/失败分支都恢复了，异常分支漏）。一行修复。
  - **补测试**：StateStore 增 `internal BaseDirOverride`（套 DiagnosticReportExporter 模式，
    生产不变）+ 新增 `DetectMetaTests` 3 条（存取往返含无 BOM 与不触碰 last-detect.json、
    无文件 null、损坏容错 null）——P3-4 的 DetectMeta 此前零测试覆盖。
  - **核实无需改**：DetectView 其余 5 处 FontMono 均纯 ASCII/数字；卡片化 6 个 XAML 小 diff
    均干净移除 FontFamily（含 DisplayQualityView 从 `TabBadgeTextStyle` 删 Setter——该样式
    渲染中文徽标，正确）；ConsoleView code-behind 未被卡片化改动。
  - **记录的既有债务（不动）**：`ConsoleView.UpdatePreparation` 等处硬编码中文（`e7af578` 起，
    P2-7 暂缓背景下已知）；`_ = main.ReviewSelectionWithIntroAsync(ids)` fire-and-forget 异常未观测。
- **验证**：320/320（+3）；`--no-incremental` 0 警告 0 错误。无 XAML 改动，无需重新渲染目检。
- **未做**：`2cc2af4` 未 push（发版拍板只覆盖当时 7 提交）、未构建未部署——一行防御路径修复
  不占版本号，随下个候选走；`D:\FpsTune` 仍是 `11398ea`（与已发布版本一致，无缺口）。

**㉑ P3-4 检测页前置状态条（2026-09-29，接手轮）**

用户指令「handoff 已更新，接手开发」；按 HANDOFF §5 排期，首项即 P3-4。

- **P3-4（`2f9712f`）**：检测页页头新增状态行（mono 小字，位于说明文案下方）——
  进入即可见「上次检测：YYYY-MM-DD HH:mm · 耗时 X.X 秒」，无记录时给动作指引。
  检测期间显示「正在检测...」；**失败不记录**、恢复显示上一次成功结果。
- **存储设计**：新增 `StateStore.SaveDetectMeta/LoadDetectMeta`（独立文件
  `last-detect.meta.json`，原子写、失败静默）——引擎输出 `last-detect.json` 是
  CLI `-Detect -Json` 的同一结构、有测试逐字节依赖，**不能往里加键**。
- 新资源键 `Str.LastDetectLabel` / `Str.NeverDetected` 中英各一条；bump 0.1.15。
- **验证**：`--no-incremental` 0 警告；317/317；UIA + 文件证据——删 meta 冷启动 →
  点「运行检测」→ meta 生成（at/elapsedMs 合理）且**与 last-detect.json 时间
  drift=0min（同步写入）**；状态行更新、离开再回来保持；无 !Str 缺键；error.log
  不变；截图目检。
- **如实标注**：「尚未检测过」分支在正常启动流程中不停留（概览页会为「已达标项目
  N/33」触发一次检测并写入 meta），该分支为防御性路径，未在 UI 上稳定复现。
- **0.1.15 候选与部署**：两脚本产三资产、哈希逐项核对一致 → 部署 `D:\FpsTune`
  （0.1.14 快照存 `-4154`）→ **按 ⑳ 教训用 `Start-Process explorer.exe -ArgumentList`
  借用户 shell 启动**（PID 13440）→ `error.log` 不变。
- **文档维护**：发现 §2.1「版本与同步状态核对」整节停留在 2026-09-25（0.1.7 时点、
  与现状冲突——会把 0.1.7 误当最新发布），已整体归档至 `docs/archive/`，本文留指针。
- **未做（等用户拍板）**：发 `v0.1.15-beta`（标签 `2f9712f`）。

**㉒ 概览卡片化 + 字体一致性（2026-09-29，本轮）**

用户三指令：①更新交接文档删过时内容（已完成，见上轮收尾 `6fa0d6e`）②国际化暂缓
（backlog P3-6 已注明 2026-09-29 重申）③概览页卡片化出预览。HTML 预览稿
（`review-output/overview-card-preview.html`，色值/组件全部取自真实浅色主题与
既有组件语言）交付后，用户**批准卡片式**并指出字体不一致（「需管理员」徽章
中文套等宽字体 Cascadia，回退渲染与正文不搭）。

- **概览卡片化（`11398ea`）**：ConsoleView 重写为三卡——①开局准备卡（游戏上下文 +
  定位游戏/基础建议/记录一局/备份还原四按钮同卡）②指标通栏卡（CPU/内存/GPU/已达标
  四格发丝分隔 + 3px 细进度条，与检测页硬件条同构）③优化项卡（方案段选 + 搜索 +
  发丝行列表 + **底部动作条收卡内**）。全部命名元素与事件处理器保留、行为不变；
  截图比对预览稿一致。
- **字体一致性（同 `11398ea`）**：立规则「**mono 只用于纯 ASCII/数字内容**（路径/
  版本/编号/百分比/1% LOW），凡渲染中文的文本一律正文字体」。移除 21 处
  （OptimizeView 需管理员/需重启徽章、DetectView 状态 chip 与笔记本/管理员徽章、
  SessionView 洞察行 + 比较表头、AbExperimentView PresentMon 状态/候选状态/平均帧率、
  DisplayQualityView 页签徽标 + 游戏胶囊、SettingsView 启动类型 chip、P3-4 状态行、
  微信号标签）；保留 29 处纯 ASCII/数字。**round-1 脚本教训**：FontFamily 常在
  续行上，按首行签名匹配会漏——第二轮按「行内容唯一签名」处理并 git diff 核实。
- **验证**：`--no-incremental` 0 警告；317/317；UIA 概览锚点 PASS + 八页巡检全 PASS
  （84–502ms）、无缺键；**PrintWindow 后台窗口截图**目检（三卡结构渲染正确、
  徽章为正文字体）——**SetForegroundWindow 在工具会话内会被前台锁拒绝，
  CopyFromScreen 只能抓到遮挡窗口；后台窗口用 PrintWindow(PW_RENDERFULLCONTENT)**。
- **0.1.16 候选与部署**：两脚本产三资产、哈希逐项核对一致 → 部署 `D:\FpsTune`
  （0.1.15 快照存 `-2f97`）→ explorer 借用户 shell 启动（PID 3496）→ `error.log` 不变。
  **测试残留的 `onboarding.done` 已从备份恢复**（用户已见过首次引导流程，不再重复提示）。
- **未做（等用户拍板）**：发 `v0.1.16-beta`（标签 `11398ea`）；push 随发版（本地领先 5 提交）。

**⑳ 0.1.14 候选构建 + 部署 + push（2026-09-29，用户指令「构建，部署，push」）**

- **bump `41540c3`**：VersionPrefix 0.1.14（内容 = ⑱ 审计修复 + ⑲ splash 门控）；顺带把
  `.workbuddy/`（本机代理工作数据）补进 `.gitignore`——它会挡 publish-release 的干净树检查。
- **构建**：`publish-release.ps1` + `build-installer.ps1` 从 HEAD `41540c3` 产出三资产；
  `ProductVersion = 0.1.14-beta+41540c3e…`；三哈希与 `SHA256SUMS-v0.1.14.txt` 逐项一致。
  构建日志存 `work/publish-0114.log` / `work/installer-0114.log`。
- **部署**：备份 `D:\FpsTune`（0.1.13 @ 0b23141）→ `D:\FpsTune-backup-0b23`（robocopy /E）；
  `dist/folder-0.1.14/*` 覆盖到 `D:\FpsTune`，文件清单双向一致、exe 哈希与 dist 逐字节同、
  `ProductVersion` 复读一致。**启动 GUI 实测坑**：工具会话内 Start-Process 的子进程会随会话被回收
  （AI-WORKFLOW 已有此教训，本轮再次踩中）——**改用 `Start-Process explorer.exe -ArgumentList <exe>`
  借用户 shell 启动即跨会话存活**（pid 30592 实测两次复查存活）。
- **push**：用户明确指令，`6e14413..41540c3`（10 提交）已上行，本地与 origin 同步。
- **已发布（用户拍板「release」）**：`gh release create v0.1.14-beta --target 41540c3e… --prerelease`
  + 三资产（Notes 文件 `work/release-notes-0.1.14.md`，格式对齐 0.1.13）。**核验**：标签 peeled SHA =
  `41540c3e9d5a2d51f88ffe0570895448388b7395` ✓、三资产字节数与本地逐项一致（Setup 60650713 /
  Portable 978499 / 清单 266——174 是 build-installer 重写加 Setup 行之前的旧读数）✓、pre-release 标记 ✓。
  **人工验证点（留给用户）**：设置页「检查更新」应报"已是最新"（0.1.14 是最新预发布，更新检查修复的首个真机验证）。
- **用户确认（2026-09-29 00:40）**：用户实测后明确「成功了」——**发布闭环完成**：真机目检通过、
  更新检查修复真机验证通过（此前的"人工验证点"清零）、无残留问题反馈。
- **未做**：（无——构建/部署/push/Release/真机确认全链路完成。）

**⑱⑲ = 2026-09-28**（审计修复轮 + splash 门控，见下）

**⑲ splash 放行门控（2026-09-28，用户反馈当日修复）**

用户反馈：进入软件时 splash 在主页数据未加载完就关了，不符合逻辑。根因：`App.xaml.cs` 用
`mainWindow.ContentRendered`（首帧渲染）关闭 splash，而控制台概览首启 `AppState.Items.Count==0`
必跑后台检测（数秒）、经典概览硬件摘要要等 WMI——首帧页面全是占位（指标 "—"、空列表）。
修法：关闭条件改为「首帧 + 首页数据就绪」双门控 + 15s 超时兜底；新增 `MainWindow.HomeDataReady`
事件，ConsoleView「首样本 + 行列表/检测落定」双标志汇合上报，HomeView 硬件摘要填充后上报
（顺带补 WMI 无守卫 catch）；等待期进度 92%（新键 `Str.SplashPhaseData`）。提交 `8071829`。
验证：317/317、0 警告；**UIA WatchSplash 实测 splash 覆盖 t≈1.8s→3.5s（数据就绪后才淡出）**；
首页锚点 PASS、error.log 不变。经典概览分支对称未真机切换验证。

**⑱ 系统性代码审计 + 逐项修复（2026-09-28，本轮）**

用户点名：先读交接文档全面理解，再系统性审查，先出审计报告（易用性/逻辑/美观三类，按严重度排序），
然后按「逻辑缺陷→易用性→视觉」顺序逐项修复。

- **审计报告**：`review-output/AUDIT-2026-09-28.md`（不入库；gitignore 本轮补上 `review-output/` 排除）。
  结论：**L1 高严重度 1 项**（更新检查死功能）+ 逻辑中低 7 项 + 易用 4 项 + 视觉 3 项（去重后 12 项全部修复）。
- **L1（高）`52b79f7`**：应用内更新检查对本项目永久失效，三层叠加——`/releases/latest`
  只认非 prerelease（本项目全为 pre-release → 恒 404）、`TryNormalizeVersion` 拒绝 `-beta` 后缀、
  `IsNewer` 对含后缀版本 `Version.Parse` 抛异常恒 false。改 `/releases?per_page=10` 列表端点 +
  归一化剥后缀 + 比较前归一化；`DownloadAsync` 改无总时限（60MB 慢网会被默认 100s 截断）。
  新增 9 条防回归用例。**教训：跨端点/协议语义不能凭直觉——「latest」在 GitHub 语境下排除 prerelease。**
- **L2（中）`4c4ecb1`**：还原入口（优化页/备份页）补管理员预检，与「应用」路径一致——
  新增 `BackupService.RestoreNeedsAdmin()`（Kind 非 registry 或 Hive=LocalMachine 即需管理员）；
  新资源键 `Str.RestoreNeedsAdminBody` 等 8 键（中英双语）。
- **L3-L8 `252b1af`**：导出日志/导出方案 try/catch；会话删除收集失败项统一报告；explorer 打开
  三处统一 SettingsView 同款（UseShellExecute+守卫）；设置页路径不存在提示色 Ok→Warning；
  OpenGitHub 空 catch 改提示（与 CODE-HEALTH「空 catch 0」声明矛盾处收口）；ICC 启停集合补全 8 卡。
- **U1-U4/V1 `267d82c`**：设置页外观卡改「标签→主题三选一→语言」顺序（原标签下首组是语言，错位）；
  会话比较表加表头（新 4 键）；对话框 Enter=主按钮；优化页删 Width=0 死列；A/B 历史图
  缓存 runs + SizeChanged 重绘（原首入页在布局前绘制恒按 320px 兜底，窗口变化不跟随）。
- **验证**：`--no-incremental` 0 警告；**317/317**（+9 新用例）；UIA 四页走查 PASS
  （优化/会话/A·B/设置，新表头「差值 B−A」渲染确认；error.log 350335→350335 不变）；
  设置页/会话页截图目检。UIA 输出存档 `work/audit-uia-round.txt`。
- **未做（如实标注）**：未构建未部署未发版；更新检查的真机端点行为待部署后人工点一次；
  还原预检交互（需非管理员会话）未真机验证；守卫失败分支未逐一构造触发场景。
  **下一轮若要发版：先 bump `VersionPrefix` 再构建**（本轮刻意未动版本号——不发版不占号）。




> **更早的轮次流水账**（⑰⑯⑮ 见本归档附录二；⑭ /goal 轮、⑬ 显示页二级页签、⑫ P2-10、⑪ P2-11 与发版 v0.1.7、⑩～①、2026-09-25 上半场、09-24、09-22 及更早）
> 已整体移至 [docs/archive/HANDOFF-轮次存档-20260925.md](archive/HANDOFF-轮次存档-20260925.md)；
> 原始记录另见 git 历史（`git show 49aec36:docs/HANDOFF.md`）。本文只保留最近三轮与现状，
> 历史教训的"活版本"在 `docs/dev/AI-WORKFLOW.md` 与各守卫测试。

## 5. 下一步建议（按性价比排序）

**现行排期见 `docs/dev/PLAN-backlog.md`（P3 清单 + 暂停项；P0–P2 已全部收口，原文归档 `docs/archive/PLAN-backlog-P0-P2-存档-20260929.md`）。** 摘要：

1. ~~发版待拍板~~ **已完成**：`v0.1.18-beta` 已发布（标签 `1f840db`，三资产核验一致）。
2. **0.2.0 迭代**（P3-11）：**beta.1 候选已完成并部署待拍板**（标签将打 `66c0ab3`）；拍板后 push + `gh release create v0.2.0-beta.1 --prerelease`。下一批 = beta.2（M2 多游戏上下文）。
3. **P3 清单其余实质清空**（P3-6/7 暂停/暂缓不碰）；无新需求时默认方向 = 深度自查或优化项扩展调研（P3-10 模式）。
3. **P2-7 国际化**：已按用户决定**暂停**（剩余约 1000 处记在 `I18nBaseline.txt`，重启需用户点名）。
4. **P2-2 / P2-4**：依赖真实 A/B 数据（用户已取消 P0-2）→ **暂缓**。
5. 收尾杂项（dist 旧资产、D 盘旧备份、bin/obj）已于 2026-09-29 经用户拍板清理完毕。

> **发版状态**：**`v0.1.18-beta` 已发布**（`1f840db`）；无未发布候选。

## 6. 维护本文的规则

- 每完成一轮工作，**同时更新 `AGENTS.md` 与本文**，并 `git commit`（用户 2026-09-22 明确要求）。
- **发布必须停在「已构建待拍板」**：本地构建 + git 后把效果交给用户，等拍板再发（用户 2026-09-22）。
- 更新 §1、§2、§5，并在 §4 追加本轮要点；**§4 历史轮次每积 ~3 轮就整体归档到
  `docs/archive/`**（2026-09-25 首次归档，见 §4 末尾指针），保持本文轻量。
- 凡是写进本文的数字（测试数、版本号、提交号）必须当场核实，不接受"上次记录的是"。
- 与代码冲突时以代码为准，并立刻修正本文 —— 过时的交接文档比没有交接文档更危险。
