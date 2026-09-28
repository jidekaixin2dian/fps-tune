# HANDOFF · 项目交接现状

> 最后核对：2026-09-29（本轮：**`v0.1.14-beta` 已发布**——构建/部署/push/Release 四步全完成）
> 本文是**入库的长期交接文档**。单轮工作的临时提示词写进根目录 `HANDOFF_PROMPT_YYYY-MM-DD.md`（当前：`HANDOFF_PROMPT_2026-09-28.md`）
> （已被 `.gitignore` 排除），那种文件只活一轮，不要往这里抄。
> 接手请先读 `AGENTS.md`，再读本文。

## 1. 一句话现状

主线是 0.1 Beta 线（现落在 **`main`**）。**最新已发布 `v0.1.14-beta`**（2026-09-29，标签 `41540c3`）；**0.1.16-beta 候选（P3-4 检测页状态条 + 概览卡片化 + 字体一致性）已按 `11398ea` 构建、部署 `D:\FpsTune` 并启动目检**，发版等用户拍板。
`main` **已 push 并与 origin 同步**。审计报告在 `review-output/AUDIT-2026-09-28.md`（不入库）。
1.x 线**已停止维护**，冻结点 `legacy/1.x`（= `v1.6.2`）。

## 2. 版本与分支

| 项 | 值 |
|---|---|
| `Directory.Build.props` | `VersionPrefix=0.1.16` / `VersionSuffix=beta` |
| 已发布 Release | **`v0.1.14-beta`**（2026-09-29 发布，标签 peeled SHA = `41540c3e9d5a2d51f88ffe0570895448388b7395` ✓，pre-release；三资产字节数与本地逐项一致：Setup 60650713 / Portable 978499 / 清单 266；Notes 中文渲染正常、无占位符）。上一版 `v0.1.13-beta`（`0b23141`） |
| 发布候选（未发布） | **`0.1.16-beta`**：P3-4 检测页状态条 + 概览卡片化（用户批准布局）+ 字体一致性（21 处中文文本去等宽）。已按 `11398ea` 构建（三资产哈希已核）、已部署 `D:\FpsTune` 供目检；若发布，标签打 `11398ea` |
| `main` | origin 同步至 `8b48519`（0.1.14 收尾）；本地领先 **4 个提交**（P3-4 + 两轮文档 + 卡片化），push 随发版拍板进行 |
| `beta` | 原开发线；内容已并入 `main`（merge `1d9777c`），不再单独演进 |
| `legacy/1.x` | **1.x 冻结分支** = tag `v1.6.2`；停止维护，不修不发 |
| 本机安装位 | `D:\FpsTune` = **`0.1.16-beta` 内嵌 SHA `11398ea`**（本轮已部署并启动目检）。文件清单与 `dist/folder-0.1.16/` 双向一致。备份链已于 2026-09-29 经用户拍板清理：**仅存 `-2f97`（0.1.15 候选，当前版本的回滚点）**，其余 15 个旧备份（0.1.14 及更早）已删除 |
| 测试基线 | 317 / 317（`dotnet test -c Release`；2026-09-28 复核，含审计修复新增 9 条用例） |
| 编译警告 | **0**（2026-09-28 `--no-incremental` 全量重编译复核；**增量构建会假报 0**，核对必须全量，见 `AI-WORKFLOW.md` §二） |
| CI | GitHub Actions **可用**（`build` + `smoke`，push 到 main 与 PR 触发） |
| catalog | 33 项；22 项需管理员、13 项需重启；预设 balanced(27) / safe-only |


> **历史核对记录**：0.1.7 及更早的版本/哈希/同步逐项核对（含 0.1.4~0.1.7 三资产哈希、CRLF 清单坑、升级做法）已随 §2.1 整体归档至 [docs/archive/HANDOFF-轮次存档-20260925.md](archive/HANDOFF-轮次存档-20260925.md)（2026-09-29；该节曾停留在 0.1.7 时点、与现状冲突）。

## 3. 0.1.2 里程碑（依据 `docs/dev/PLAN-0.1.2-features.md`）

**M1 / M2 / M3 全部完成并已发布**（M1 NVAPI+DLSS+数字振动、M2 ICC 滤镜与生成器、
M3 DRS 二期五项——均随 0.1.3 及此前版本上线）。细节见 PLAN 文档与 git 历史，本节不再维护表格。

## 4. 最近几轮做了什么（㉒ = 2026-09-29；⑯及更早见归档）

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

**完整待办总表见 `docs/dev/PLAN-backlog.md`（P0–P2 已全部收口，后续排期在其「P3 · 后续改动清单」）。** 摘要：

1. **发版待拍板**：0.1.16 候选（`11398ea`，概览卡片化 + 字体一致性 + P3-4 状态条）等用户拍板 → push + `gh release create v0.1.16-beta`（标签 `11398ea`）。
2. **P3 清单实质清空**（P3-3/4/5/8/9 完成，P3-6/7 暂停/暂缓）；概览卡片化已随 0.1.16 落地。
3. ~~**收尾杂项**：`dist` 里旧资产与旧备份可清理~~ **已完成（2026-09-29，用户拍板）**：`dist/` 只留 0.1.16 三资产+清单（旧版已发 GitHub Release 或被取代，740M→124M，0.1.16 哈希已复核）；`D:\` 备份只留 `-2f97`；仓库 `bin/obj` 已清（下次构建自动重建）。`work/` 探针按纪律保留 |
4. **概览页（ConsoleView）是否一并卡片化**：有意保留仪表盘形态，等用户表态。
5. **P2-7 国际化**：已按用户决定**暂停**（剩余 1000 处记在 `I18nBaseline.txt`，重启需用户点名）。
6. **P2-2 / P2-4**：依赖真实 A/B 数据（用户已取消 P0-2）→ **暂缓**。

> **发版状态**：`v0.1.14-beta` 已发布；**0.1.16 候选（`11398ea`）已构建部署待拍板**。

## 6. 维护本文的规则

- 每完成一轮工作，**同时更新 `AGENTS.md` 与本文**，并 `git commit`（用户 2026-09-22 明确要求）。
- **发布必须停在「已构建待拍板」**：本地构建 + git 后把效果交给用户，等拍板再发（用户 2026-09-22）。
- 更新 §1、§2、§5，并在 §4 追加本轮要点；**§4 历史轮次每积 ~3 轮就整体归档到
  `docs/archive/`**（2026-09-25 首次归档，见 §4 末尾指针），保持本文轻量。
- 凡是写进本文的数字（测试数、版本号、提交号）必须当场核实，不接受"上次记录的是"。
- 与代码冲突时以代码为准，并立刻修正本文 —— 过时的交接文档比没有交接文档更危险。
