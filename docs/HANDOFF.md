# HANDOFF · 项目交接现状

> 最后核对：2026-09-27（本轮：**P2-12 PresentMon 前置探测 + 显示页居中 + 空闲预热重页
> + splash 重设计**（用户 /goal 轮，含 P3 清单落盘与 README/ROADMAP 公开文档更新））
> 本文是**入库的长期交接文档**。单轮工作的临时提示词写进根目录 `HANDOFF_PROMPT_YYYY-MM-DD.md`（当前：`HANDOFF_PROMPT_2026-09-27.md`）
> （已被 `.gitignore` 排除），那种文件只活一轮，不要往这里抄。
> 接手请先读 `AGENTS.md`，再读本文。

## 1. 一句话现状

主线是 0.1 Beta 线（现落在 **`main`**）。**最新已发布 `v0.1.7-beta`**（2026-09-25，标签 `fab34e7`，Setup + Portable + SHA256SUMS）；
**0.1.11-beta 候选（P2-11 + P2-10 + 二级页签 + P2-12 + 居中 + 预热 + splash 重设计）
已按 `712a1a3` 构建、部署到 `D:\FpsTune` 并启动给用户目检**，发 Release 等用户点名；`main` 已 push。
**GUI_PLAN 五缺口（G1–G5）与全部 P2 待办收口**；后续排期见 backlog「P3 · 后续改动清单」。
1.x 线**已停止维护**，冻结点 `legacy/1.x`（= `v1.6.2`）。

## 2. 版本与分支

| 项 | 值 |
|---|---|
| `Directory.Build.props` | `VersionPrefix=0.1.11` / `VersionSuffix=beta` |
| 已发布 Release | **`v0.1.7-beta`**（2026-09-27，标签打在 `fab34e7`，pre-release；资产 = Setup + Portable + SHA256SUMS，**不含单文件**） |
| 未发布的内容 | **`0.1.11-beta` 候选**：P2-11 + P2-10 + 二级页签 + P2-12 PresentMon 前置探测 + 显示页居中 + 空闲预热 + splash 重设计。**已按 `712a1a3` 构建（三资产齐全、哈希已核）、已部署 `D:\FpsTune` 供目检**；若发布，标签打 `712a1a3`。0.1.8/0.1.9/0.1.10 旧候选在 `dist/`，内容均被 0.1.11 包含 |
| `main` | **技术主线（0.1 Beta）**。**已 push**（用户因"更新 GitHub 上的 README"授权）；push 后又产 1 个收尾文档提交随收尾再推 |
| `beta` | 原开发线；内容已并入 `main`（merge `1d9777c`），不再单独演进 |
| `legacy/1.x` | **1.x 冻结分支** = tag `v1.6.2`；停止维护，不修不发 |
| 本机安装位 | `D:\FpsTune` = **`0.1.11-beta` 内嵌 SHA `712a1a3`**（本轮已部署并启动目检）。文件 6 个，与 `dist/folder-0.1.11/` 清单一致。备份链（按部署步命名）：`-2df6`（0.1.10 快照）、`-4439`（0.1.9）、`-647f-2`（0.1.8）、`-647f`、`-fab3`、`-46a3`、`-0627`、`-20260925`（= 已发布 0.1.3）、`-20260922`（1.x 时代遗留） |
| 测试基线 | 308 / 308（`dotnet test -c Release`；2026-09-25 复核） |
| 编译警告 | **0**（2026-09-25 由 6 个清零；新增代码请守住这条，见 `CODE-HEALTH.md`） |
| CI | GitHub Actions **可用**（`build` + `smoke`，push 到 main 与 PR 触发） |
| catalog | 33 项；22 项需管理员、13 项需重启；预设 balanced(27) / safe-only |

### 2.1 版本与同步状态核对（2026-09-25 实测，0.1.11 轮更新）

| 位置 | 版本 | 状态 |
|---|---|---|
| GitHub 已发布 Release | **`v0.1.7-beta`** | 线上最新（`gh release view` 实测；标签 peeled SHA = `fab34e7` ✓）。资产三件：`FpsTune-Setup-0.1.7.exe`(60642090 B) / `FpsTune-Portable-0.1.7.zip`(968804 B) / `SHA256SUMS-v0.1.7.txt`(264 B)，与本地产物**字节数逐项一致**；清单文件回读内容一致。**回读下载两次遇 unexpected EOF（本地网络），未做全量大文件回读哈希——以字节数 + 官方清单回读为准** |
| **发布候选（未发布，最新）** | **`0.1.7-beta`** | 官方脚本从 HEAD `fab34e7` 产出：`dist/folder-0.1.7/`（6 个文件）、`dist/single-file-0.1.7/FpsTune.exe`(62.7M)、`dist/FpsTune-Portable-0.1.7.zip`(0.9M)、`dist/installer/FpsTune-Setup-0.1.7.exe`、`dist/SHA256SUMS-v0.1.7.txt`（3 行）。`ProductVersion` = `0.1.7-beta+fab34e7…`。已部署 `D:\FpsTune` 并启动目检 |
| 发布候选（未发布，旧） | `0.1.6`（`8eaec90`）/ `0.1.5`（`46a3776`）/ `0.1.4`（`e3d0041`） | 三资产完整保留在 `dist/`，未被后续构建波及；内容均被 0.1.7 包含 |
| 本机安装位 `D:\FpsTune` | `0.1.7-beta`（内嵌 SHA **`fab34e7`**） | **本轮已部署**：整目录覆盖，文件清单双向一致（6/6）；启动 15 秒存活、`error.log` 字节数不变 |
| 源码 `main` | `0.1.7-beta` | **已 push**（用户 2026-09-25 授权"这轮做完可以 push"），本地与 `origin/main` 同步 |
| 备份 | — | `-20260925-fab3` = 0.1.6（本轮部署前）；`-46a3` = 0.1.5；`-0627` = `627740f`；`-20260925` = 已发布 0.1.3 |

- **0.1.7 三资产实测哈希**（与 `SHA256SUMS-v0.1.7.txt` 逐项一致）：单文件
  `69FE2D90…F60A` / Portable zip `B87B6B53…0BB3` / Setup `22DF3D79…AEAC`。
- **打标签口径（若发布 0.1.7）**：候选由 HEAD `fab34e7` 产出，`v0.1.7-beta` 打在 **`fab34e7`**。
  判断方法仍是读产物 `ProductVersion` 的 SHA。
- **`8eaec90` 是 .gitignore 提交**（忽略宿主生成的 `.zcodeignore`，它曾让发布脚本的
  "tracked tree 干净"检查拒绝构建）。
- 0.1.6 哈希：单文件 `91889E46…EBF0` / zip `1D06CE58…ECBF2` / Setup `14C06DB1…D4454`。
- 0.1.5 哈希：单文件 `A3A1A423…0CA2A` / zip `4D90C816…E0618` / Setup `964791F9…58D6`。
- 0.1.4 哈希：单文件 `91FF00D7…46F93` / zip `D26E13C2…0FBE2A` / Setup `46A46598…7D51B9`。
- **`SHA256SUMS-*.txt` 是 CRLF 行尾**（0.1.2 / 0.1.3 / 0.1.4 三份一致，脚本产物如此）：
  Git Bash 里 `sha256sum -c` 会因 `\r` 报 "No such file"；**逐项比对哈希值**即可，
  不要据此判定清单损坏（本轮已确认清单正确）。
- **旧记载的候选 SHA（`f09d944`、`627740f`）均已过期**，以本表为准。
  判断版本只看 `Directory.Build.props` 与产物 `ProductVersion` 的完整 SHA，不要拿版本号当依据。

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
- **升级做法与结果**：`cp -f dist/folder-0.1.3/* → D:\FpsTune\`（整目录覆盖，**不跑安装器、不碰注册表**）。
  覆盖前确认无进程占用（唯一在跑的 FpsTune 进程来自 `dist\folder-0.1.3`，不锁 `D:\FpsTune`）。
  覆盖后逐文件比对发布 Portable zip：**一致 7/7**，且无多余文件。用户目录 `%LOCALAPPDATA%\FpsTune`
  （配置 / 备份 / 日志）未受影响。
- **folder 产物文件数会随版本变**：0.1.2 / 0.1.3 = **7 个**（含 `friend-test.ps1`），
  0.1.4 = **6 个** —— 朋友测试模块整体移除后该脚本不再进产物。比对时**别把"少一个文件"当异常**，
  要对照当时的产物清单（`dist/folder-<版本>/`）判断。
- **`SHA256SUMS-*.txt` 的 `FpsTune.exe` 条目指的是「单文件版」**（`dist/single-file-<ver>/FpsTune.exe`），
  **不是** `dist/folder-<ver>/FpsTune.exe` 那个 apphost。两者哈希必然不同（0.1.3 实测：
  单文件 `F48781A4…` vs folder `8A87387B…`）。校验「安装位 / Portable 是否等于发布版」应比对
  **Portable zip 的哈希**，或解压后逐文件比对；不要拿 folder 版 exe 去对 `SHA256SUMS` 里那行，
  会误判成"不一致"（本轮已踩过一次）。
- **遗留备份**：`D:\FpsTune-backup-20260922`（exe 日期 2026-09-14，0.1.1 时代）仍在，未清理。

**未决（需要用户拍板，别自作主张）**

1. （已清空）0.1.7 已发布；后续发版按发布流程走新一轮。
2. `main` **已 push**（2026-09-25 两次授权），本地与远端同步；后续 push 仍需按节奏确认。
3. 原「单文件 exe 口径」已决：公开 Release **不提供单文件**，`RELEASE.md` §4 已改为三资产
   （Setup + Portable + SHA256SUMS），与 README / README.en 下载表一致。

**已决**

- **发布流程（用户 2026-09-25 明确）**：**唯一权威版本在 `AGENTS.md` 硬纪律第 7 条**，此处不重复。
  要点：`做好 → git commit → 部署到 D:\FpsTune → 启动 GUI 给用户看 → 等确认 → push → release`。
  **拍板前禁止 push 与发 Release，但 commit 与"部署到本机给用户看"是允许的。**
  > 历史注记：本条曾与 `AGENTS.md` 表述不一致（一处写"拍板后才允许 commit"，一处写"commit 在给看之前"），
  > 2026-09-25 已统一到 `AGENTS.md`。
- **同步决策（用户 2026-09-24）**：直接 push `main`；`D:\FpsTune` 升到 `0.1.3-beta`，
  用户**明确"不用备份"**（整目录覆盖，不跑安装器、不碰注册表）。
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

## 4. 最近几轮做了什么（⑭ = 2026-09-27；⑧⑦⑥及更早见归档）

**⑭ 用户 /goal 轮：P2-12 + 居中 + 预热 + splash 重设计 + 文档全量更新（本轮）**

用户一个 /goal 提了三块：①界面与性能优化 + bug 巡检 ②把 P2-12 做掉
③先设计后续改动清单，然后全量文档 + GitHub README 更新。全部按计划完成：

- **后续改动清单（3a）**：backlog 新增「**P3 · 后续改动清单**」8 项（发版节奏 / 验证脚本
  固化 / classic 回归目检 / 检测页前置状态条 / 启动基线测量 / i18n 重启条件 /
  P2-2·P2-4 前置条件 / CODE-HEALTH 复测），本轮执行了其中 P2-12 与布局、性能三项。
- **布局居中（1a，`36fca02`）**：显示页是全仓库唯一 MaxWidth+左对齐的页面——宽窗口
  右侧大片留白。改为居中（其余页面均满宽，不受影响）。UIA 实测卡片中心 1286 vs
  窗口中心 1280。
- **空闲预热（1b，`56f4545`）**：主窗 ContentRendered 后在 ApplicationIdle 档
  `WarmHeavyPages()`——只构建"优化/显示"实例（未 attach，零数据读取），
  XAML 解析成本挪出点击关键路径；失败静默回退懒加载。说明：桌面高负载下
  毫秒计时噪声大，不再引用具体数字，收益是结构性的。
- **P2-12（`a90692e`）**：`ExperimentRunner.ProbePresentMon()`（与自动采样同一套查找
  口径）+ A/B 页头 PresentMon 状态条——就绪显示文件名，缺失显示官方安装命令
  （`winget install Intel.PresentMon.Console`）+ 复制按钮，**不代为安装**。
  本机实测显示"已就绪：PresentMon_x64.exe"（FrameViewSDK 路径命中）。
- **splash 重设计（用户插话"加载动画不好看"，`a35d5bb`）**：56px 图标 + 24 号标题 +
  **accent 弧线圆环 spinner**（0.9s 匀速旋转 + 弱化轨道）替换四点呼吸；高度 280→300。
  实机截图确认渲染。
- **bug 巡检（1c）**：GitHub issues 无新增（仅暂停中的 i18n）；`error.log` 全部历史异常
  均为已修复的 Run.Text 崩溃（修复前记录），修复后零异常；重点代码巡查
  （DetectView/ConsoleView/OptimizeView/备份/ICC 路径 + `AppState.Items` 换引用语义
  与预热的安全性）未发现可修的新 bug——**如实记录：本轮没有修任何 bug，因为没有**。
- **文档全量（3b/3c）**：README 中英「界面」段 + ROADMAP 现状基线（0.1.7 发布 /
  0.1.11 候选）+ backlog（P2-12 完成、P3 清单）+ 本文与 AGENTS 同步。
- **验证**：308/308；UIA（居中 diff 6px、A/B 状态条、无 `!Str.*`）；`error.log` 不变。
- **0.1.11 候选与部署**：bump `0793a9b`（第一次构建被自己未提交的文档改动挡下——
  **先提交再构建**这条又验证了一遍）→ 文档提交后重建成功（HEAD `712a1a3`）→
  三哈希与清单逐项一致 → 部署（0.1.10 快照存 `-2df6`）。**部署教训**：上一轮的
  0.1.10 GUI 实例还在跑，`cp` 以改名方式绕过了文件锁——磁盘正确但旧实例残留，
  已杀掉；下次部署前先杀进程（0.1.7 轮就这么做的，这轮忘了）。
- **未做（等用户点名）**：`gh release create v0.1.11-beta`。

**⑬ 显示页二级页签（上一轮）**

- 显示页逐轮长到 6 张卡，全靠滚动。按用户建议改为**二级页签**（`95d35bf`）：
  页头下一排子页签——**DLSS 预设 / 数字振动 / ICC 滤镜 / 驱动 3D / 建议与清单**
  （两张信息卡合并一签），一次只显示一组；页面实例缓存使会话内页签选择自然保持。
- **实现克制**：卡片本体一行未动，只在外层 Border 加名字与初始 Visibility，
  切换仅改外层可见性——异步状态刷新照常作用于全部卡片，切回来状态就是新的。
  新增 Str.* 资源键 5 对（中英），code-behind 无新增硬编码中文。
- **验证**：308/308；UIA 逐签断言（默认 DLSS 可见其余隐藏；四签依次切换，每次
  目标组可见、其它组隐藏）；`error.log` 不变；截图目检页签条与「建议与清单」排版。
- **0.1.10 候选与部署**：bump `2df66b3` → 三哈希与清单逐项一致 → 部署 `D:\FpsTune`
  （0.1.9 快照存 `-4439`）→ 启动 GUI 给用户。
- **未做（等用户点名）**：`git push`（本地领先 7 提交）、`gh release create v0.1.10-beta`。

**⑫ P2-10 ICC 自选 .icc/.icm 文件（上一轮，GUI_PLAN G3 收口）**

- 用户先问了版本号（本机 0.1.8 vs 线上 0.1.7）——已解释：公开 Release 停在已发布版本，
  本机装的是**下一轮候选**（发布流程每轮 bump + 部署目检），两者本就差一截。
- **引擎（`c84c8e5`）**：`IccFilterService.ApplyFromFile(sourcePath)` 与预设同一套安全语义
  （首次备份 / 漂移拒绝 / 主显示器校验 / 关联后读回验证）。校验链：存在 → 扩展名
  .icc/.icm → `IsDisplayProfile`（mntr/RGB，与"生效判定"同一把尺子）→ 内容 SHA256 前 8 位
  命名 `FpsTune-Custom-*.icc/icm` → 留档 `display-icc/custom/` → 安装。
  **关键扩展**：漂移检查从"备份列表+预设名"改为"+本工具安装的 profile"（自定义前缀放行），
  否则应用过自定义文件后，下一次切换会被误判为外部修改而拒绝。
  共用逻辑抽为 `RequireDisplayAndColorDir` / `AssociateWithBackup` / `InstallIntoColorDir`，
  消除了与预设路径的重复。
- **界面**：ICC 卡新增「从文件应用…」（文件对话框 → 与预设相同的全局生效确认）；
  两个应用入口共用 `IccApplyCore`。服务层新错误文案全走资源键（服务文件在 i18n
  棘轮基线里，不能新增硬编码中文；测试断言 key 名，运行时显示中文）；
  DisplayQualityView 基线 56 → 54（两条旧文案顺带转资源键）。
- **验证**：308/308（IccFilterTests 新增 4 条：安装+备份+还原、同内容幂等、
  漂移容忍自定义、无效来源三种拒绝且不触发切换/备份）；UIA 真渲染：新按钮 +
  文件对话框弹出/关闭 + `error.log` 不变 + 截图目检。
- **0.1.9 候选与部署**：bump `443ad5d` → 三哈希与清单逐项一致 → 部署 `D:\FpsTune`
  （0.1.8 安装位快照存为 `-647f-2`）→ 启动 GUI 给用户。
- **未做（等用户点名）**：`git push`（本地领先 5 提交）、`gh release create v0.1.9-beta`。

> **更早的轮次流水账**（⑪ P2-11 与发版 v0.1.7、⑩～①、2026-09-25 上半场、09-24、09-22 及更早）
> 已整体移至 [docs/archive/HANDOFF-轮次存档-20260925.md](archive/HANDOFF-轮次存档-20260925.md)；
> 原始记录另见 git 历史（`git show 65a910b:docs/HANDOFF.md`）。本文只保留最近三轮与现状，
> 历史教训的"活版本"在 `docs/dev/AI-WORKFLOW.md` 与各守卫测试。

## 5. 下一步建议（按性价比排序）

**完整待办总表见 `docs/dev/PLAN-backlog.md`（P0–P2 已全部收口，后续排期在其「P3 · 后续改动清单」）。** 摘要：

1. **发版**：0.1.11 候选（`712a1a3`）等用户拍板 → `gh release create v0.1.11-beta`（标签 `712a1a3`）。
2. **P3-2 验证脚本固化**：UIA 切页/弹窗/页签脚本目前是一次性 %TEMP% 文件，固化进 `tools/`。
3. **P3-3 classic 界面回归目检**：二级页签、方案弹窗等新 UI 只在控制台风格目检过。
4. **P3-4 检测页前置状态条** / **P3-5 启动基线测量** / **P3-8 CODE-HEALTH 复测**。
5. **P2-7 国际化**：已按用户决定**暂停**（剩余 1000 处记在 `I18nBaseline.txt`，重启需用户点名）。
6. **P2-2 / P2-4**：依赖真实 A/B 数据（用户已取消 P0-2）→ **暂缓**。

> **发版状态**：`v0.1.7-beta` 已发布；**0.1.11 候选（`712a1a3`，P2-11 + P2-10 + 二级页签 + P2-12 + 居中 + 预热 + splash 重设计）已构建部署待拍板**。
> `main` 已 push（README 更新授权）；发 Release 等用户点名。**GUI_PLAN 五缺口（G1–G5）与全部 P2 待办收口**。

## 6. 维护本文的规则

- 每完成一轮工作，**同时更新 `AGENTS.md` 与本文**，并 `git commit`（用户 2026-09-22 明确要求）。
- **发布必须停在「已构建待拍板」**：本地构建 + git 后把效果交给用户，等拍板再发（用户 2026-09-22）。
- 更新 §1、§2、§5，并在 §4 追加本轮要点；**§4 历史轮次每积 ~3 轮就整体归档到
  `docs/archive/`**（2026-09-25 首次归档，见 §4 末尾指针），保持本文轻量。
- 凡是写进本文的数字（测试数、版本号、提交号）必须当场核实，不接受"上次记录的是"。
- 与代码冲突时以代码为准，并立刻修正本文 —— 过时的交接文档比没有交接文档更危险。
