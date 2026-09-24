# HANDOFF · 项目交接现状

> 最后核对：2026-09-25（本轮：文档与工作区整理 + P2-1 英译改法调研）
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
| 本机安装位 | `D:\FpsTune` = **`0.1.4-beta`（发布候选，未发布）**；2026-09-25 由 `dist/folder-0.1.4/` 覆盖，逐文件一致（7/7）。上一版备份 `D:\FpsTune-backup-20260925`（= 已发布的 `0.1.3-beta`） |
| 测试基线 | 273 / 273（`dotnet test -c Release`；2026-09-25 复核，含 i18n 棘轮守卫） |
| CI | GitHub Actions **可用**（`build` + `smoke`，push 到 main 与 PR 触发） |
| catalog | 33 项；22 项需管理员、13 项需重启；预设 balanced(27) / safe-only |

### 2.1 版本与同步状态核对（2026-09-24 实测，取代旧的"版本号回退坑"记载）

| 位置 | 版本 | 状态 |
|---|---|---|
| GitHub 已发布 Release | `v0.1.3-beta` | 线上最新（**0.1.4 尚未发布**） |
| **发布候选（未发布）** | **`0.1.4-beta`** | 由**官方脚本**从 HEAD `f09d944` 构建：`dist/folder-0.1.4/`、`dist/single-file-0.1.4/FpsTune.exe`(62.6MB)、`dist/FpsTune-Portable-0.1.4.zip`(0.9MB)、`dist/installer/FpsTune-Setup-0.1.4.exe`(60.6MB)、`dist/SHA256SUMS-v0.1.4.txt`（**3 行**）。`ProductVersion` 含 `0.1.4` + `f09d944…` |
| 本机安装位 `D:\FpsTune` | **`0.1.4-beta`（候选，未发布）** | 由最终 `dist/folder-0.1.4/` 覆盖，**逐文件一致（7/7）**；界面语言 `en-US`，GUI 已启动供目检 |
| 源码 `main` | `0.1.4-beta` | 已 push 至 `895e903`；**另有 5 个提交未 push**（`c4e191f` / `dde33d6` / `46d0d2e` / `ff58760` / `f09d944`） |
| 已发布 0.1.3 的本地资产 | — | **未被破坏**：`FpsTune-Portable-0.1.3.zip` 仍为 `4E055D2E…DEE6A`。bump 版本号正是为了避免"同号覆盖" |
| 备份 | — | `D:\FpsTune-backup-20260925` = 已发布的 `0.1.3-beta` |

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
- **`SHA256SUMS-*.txt` 的 `FpsTune.exe` 条目指的是「单文件版」**（`dist/single-file-<ver>/FpsTune.exe`），
  **不是** `dist/folder-<ver>/FpsTune.exe` 那个 apphost。两者哈希必然不同（0.1.3 实测：
  单文件 `F48781A4…` vs folder `8A87387B…`）。校验「安装位 / Portable 是否等于发布版」应比对
  **Portable zip 的哈希**，或解压后逐文件比对；不要拿 folder 版 exe 去对 `SHA256SUMS` 里那行，
  会误判成"不一致"（本轮已踩过一次）。
- **遗留备份**：`D:\FpsTune-backup-20260922`（exe 日期 2026-09-14，0.1.1 时代）仍在，未清理。

**未决（需要用户拍板，别自作主张）**

1. 无。原两项已于 2026-09-24 拍板并执行：①push `main`（用户："直接 push"）；
   ②升级 `D:\FpsTune` 到 `0.1.3-beta`（用户："不用备份"）。
   原「单文件 exe 口径」已决：公开 Release **不提供单文件**，`RELEASE.md` §4 已改为三资产
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

## 4. 本轮（2026-09-25）做了什么

**朋友测试模块整体移除（用户要求）**

- 删掉的**不只是界面**。`ScriptLocator`（脚本完整性/信任校验）与 `PowerShellRunner`
  经核实**只被朋友测试页使用**，删模块后即成死代码，一并移除；
  `tools/friend-test.ps1`、两个专测脚本信任边界的测试文件也一并删除。
- 共删 7 个文件 + 清 14 个只属于该页的资源键；`FpsTune.Wpf` 现在**不再有外部 PowerShell 脚本**。
- **保住一个共用类型**：`RunResult` 原先定义在 `PowerShellRunner.cs` 里，但引擎与多个视图都在用，
  删文件时被连带删掉导致编译失败——已抽成独立的 `Services/RunResult.cs`。
- 导航、首页卡片（并重排编号）、`csproj` 两处引用、README/README.en/WPF README、`TESTING.md`
  （保留方式 A/B 与回传模板，只删脚本那节）同步更新；backlog 的 P2-9 与 GUI_PLAN 的 G2 置**作废**。
- 未重跑基线前的构建：0 错误。

**P2-3 完成：GUI_PLAN 盘点，只留真缺口**

- 方法：对 `docs/dev/GUI_PLAN.md` 的每条承诺**逐条 grep 现界面与引擎**，确认"有没有真的做出来"，
  而不是看文档怎么说。
- 结论写入 `GUI_PLAN.md` 新增的「盘点结果」章节：
  - **已实现 9 条**（7 个模块里 6 个已落地，且多有超出——显示页远超原规划，
    多了 DLSS 预设覆盖 / 驱动 3D 设置 / 数字振动）。
  - **真缺口 5 个** → 已立为 **P2-8 ~ P2-12**：
    G1 按厂商生成驱动内手动设置清单（非 N 卡用户只拿到一句"用 ICC 兜底"）；
    G2 朋友测试缺 CSV 导出与复制到剪贴板；G3 ICC 不支持自选 `.icc`/`.icm` 文件；
    G4 备份页未展示 `.restored` 消费记录（引擎有机制、界面没露出）；
    G5 A/B 页缺 PresentMon 前置检测。
  - **已过期 4 条**：WinForms `delta-gui.ps1` 阶段、22 项/3 项体检/3 套预设等旧数字、
    「M4 ICC 滤镜页」。
- 结论：**不必按 GUI_PLAN 重做任何东西**，只做上面 5 个缺口。

**⚠️ 严重回归已修复：程序启动即闪退（`Run.Text` 默认双向绑定）**

- 用户报告"打开闪退"。根因：为让 `StringFormat=副作用：{0}` 能随语言切换，我把它拆成了
  `<Run Text="{DynamicResource ...}"/><Run Text="{Binding SideEffect}"/>`，而
  **`Run.Text` 是默认双向绑定的属性**，绑到只读的 `SideEffect` 上时模板实例化即抛
  `InvalidOperationException: 无法对只读属性进行 TwoWay 绑定` → 启动崩溃。
- **编译通过、273 条单测全绿，都发现不了**——只有真正渲染 UI 才触发。
- 修复：5 处 `Run.Text` 绑定显式加 `Mode=OneWay`（其中 SettingsView 的 2 处是**既有潜在 bug**，
  先前只在有数据时才暴露）。新增守卫 `FpsTune.Wpf.Tests/XamlGuardTests.cs`。
- **我的判断也错过一次**：先前把"进程启动后消失"当成"被环境回收"，实际是崩溃。
  已把"改 XAML 后必须真正启动一次并观察存活"写进 `AI-WORKFLOW.md`。
- 验证：启动后 **20 秒进程存活、`error.log` 字节数不变**（无新异常）。

**界面国际化根因修复（用户截图反馈"英文下大量中文仍在"）**

> **状态：已按用户决定暂停（2026-09-25）。** 界面语言已切回 `zh-CN`，中文界面完全正常
> （所有新增资源键的中文值就是原文）。英文侧已完成的 609 处（XAML 层全清零 + code-behind 首批）
> 全部保留，剩余 **1000 处**记在 `FpsTune.Wpf.Tests/I18nBaseline.txt`（即待办清单）。
> 若要重启，见 `PLAN-backlog.md` 的 P2-7。**不要把它当成"已完成"**——P1-2 当年就是这么留下的坑。

- **根因**（审计得出，**不是**字体或资源加载问题）：界面共 **1609 处硬编码中文、分布在 65 个文件**。
  XAML 13 个文件里 319 处只接了 35 个资源键；C# 55 个文件里 1340 处，而
  **code-behind 连取资源的辅助方法都没有** —— C# 文案根本无从本地化。
  P1-2 当年只接了 35 处，却在交接文档里记为"国际化完成"。
- **结构修复**：新增 `Services/Str.cs`（`Str.T` / `Str.T(key,args)` / `Str.Pick`；
  缺失键显示 `!key!` 让漏配显形）；新增 `FpsTune.Wpf.Tests/I18nGuardTests.cs` 三条守卫，
  其中 `No_new_hardcoded_chinese_in_ui` 是**棘轮**（基线 `I18nBaseline.txt`，**只许减少不许增加**）
  —— 这就是"不让后面再出现同样问题"的那道闸。写法与守卫见 `docs/dev/AI-WORKFLOW.md` §四。
- **已转换**：**检测页与优化页 XAML 全量清零**（55 处，两文件已移出基线），棘轮基线 1609 → 1554。
  两处 `StringFormat=副作用：{0}` 改为内联 `<Run>`：`Binding.StringFormat` **不是依赖属性**
  （`Binding` 派生自 `MarkupExtension`），用不了 `DynamicResource`。
- **未完成（重要）**：其余 **1554 处**，含 3 个截图页里的**设置页**、以及**全部 code-behind 文案**。
  基线文件 `FpsTune.Wpf.Tests/I18nBaseline.txt` 就是**待办清单**（按数量降序看）。
  **建议按页推进**：设置页 → 概览页 → 控制台 → 其余页面。

**0.1.4-beta 候选改用官方脚本重建 + 根因更正（同日续）**

- 用户放开沙箱权限后重跑 `publish-release.ps1`，**仍然失败**。排查发现真正根因**不在沙箱策略**，
  而在**脚本自身的管道式 `Remove-Item`**：本环境的 `Remove-Item` 包装不支持管道参数绑定，
  **空集合也抛错**（最小实验证明与文件数无关）。`DebugType=none` 下那段清理本就是空操作。
- 修脚本（`f09d944`：改 `foreach` + `-LiteralPath`）后，**`publish-release.ps1` 与
  `build-installer.ps1` 都完整跑通**，产物改由官方路径产出——含脚本自带的 zip 内容校验、
  单文件纯净性检查、独立冒烟（`-Version` / `-Detect -Json`）、清单格式校验。
- 最终产物（HEAD `f09d944`）：单文件 exe / Portable zip / Setup exe + **3 行** SHA256SUMS；
  三项实测哈希与清单逐项一致。
- 因嵌入的 SHA 变化，**重新部署** `D:\FpsTune`（逐文件 7/7 一致）并重启供目检；
  确认 `%LOCALAPPDATA%\FpsTune\logs\error.log` **无新崩溃**（最近一条是 2026-09-07 的 1.x 旧记录）。
- **自我更正**：本轮早些时候把根因误判为"沙箱批量删除策略"，并据此写了一段"手动补齐 zip/清单"
  的口径。已在「环境备忘」改正——留这段记录是为了说明**误判也会被写进交接文档**，
  下一个人别照抄旧结论。

**0.1.4-beta 发布候选：构建、部署到本机、启动供目检（未 push、未发布）**

- **统一发布流程口径**（用户指出原表述会误导）：原 `AGENTS.md` 与本文对"commit 在拍板前还是
  拍板后"说法**相反**，且都写"拍板前禁止覆盖 `D:\FpsTune`"——而用户口径里"部署到本地"
  恰恰是第 3 步。已改为 `AGENTS.md` 硬纪律第 7 条的 **8 步表格**为唯一权威，其它文档指向它
  （`c4e191f`）。随后发现该表又把 `publish-release.ps1` 误列为"拍板后"——它其实是**构建脚本**
  （只写 `dist/`，不联网、不上传），已修正并补两条实打实的约束（`dde33d6`）。
- **版本号 0.1.3 → 0.1.4**（`46d0d2e`）：不 bump 就重建会覆盖 `dist/` 里同名的**已发布 0.1.3 资产**，
  造成"同号不同内容"。已验证 `FpsTune-Portable-0.1.3.zip` 哈希未变（仍为 `4E055D2E…`）。
- **构建**：`publish-release.ps1` 产出 `single-file-0.1.4/` 与 `folder-0.1.4/`，但**在打包前被
  沙箱批量删除策略拦住**（见「环境备忘」）。按脚本口径手动补齐 `FpsTune-Portable-0.1.4.zip`
  与 `SHA256SUMS-v0.1.4.txt`，并跑独立冒烟：单文件 `-Version` → `0.1.4-beta`，
  `-Detect -Json` → 33 项 OK。
- **部署**：先备份 `D:\FpsTune` → `D:\FpsTune-backup-20260925`（= 已发布的 `0.1.3-beta`），
  再把 `dist/folder-0.1.4/` 整目录覆盖过去，**逐文件一致（7/7）**。界面语言切 `en-US` 后启动供目检。
- **待用户拍板**：push 那 3 个提交 → `gh release create v0.1.4-beta`。

**P2-1 实施：catalog 33 项说明英译（+ 优化页分组标签）**

- `catalog/catalog.json` 33 项各加 `nameEn` / `descriptionEn` / `sideEffectEn`，键顺序统一为
  id → name → description → sideEffect → admin → default → reboot → kind → group。
  数据自检：字段完整、英文无中日韩字符、空白项一致、**无 BOM**；
  admin 22 / reboot 13 / balanced 27 / safe-only 5 —— 与 README 口径一致。
- `OptimizationItemDefinition` 加 3 个可选参数与 `Display*` 解析属性
  （英文缺失**回退中文**，绝不显示空白）；`BuildDetectJson` 只改**值**、不改**键**。
- **CLI 机器协议不变**（端到端验证）：把 `lang.txt` 临时设为 `en-US` 后跑 `-Detect -Json`，
  输出**仍是中文**；item 键结构逐项与改动前一致。
- 分组**显示标签**走新的 `Core/CatalogGroups.cs` + `Views/CatalogGroupLabelConverter.cs`；
  筛选 chip 的 `Tag` 仍是中文键 → **分类筛选行为不变**。
- 新增 4 条守卫测试（英文完整性/无中日韩字符、语言跟随、缺失回退、CLI 启动路径不加载语言）。
  其中架构守卫**首次运行误报了自己**（正则匹配到文档注释里提到的 API 名），已加
  `StripCommentLines` 剥注释行修掉。
- 测试基线 266 → **270**。
- **偏差（有意分步）**：`OptimizeView` 的页面 chrome（标题「系统优化」、预设名等）**从未本地化**，
  属 P1-2 遗留，已拆成新待办 **P2-7**；故英文界面下该页仍是中英混排。
- **未验证**：英文界面下的 GUI 人工目检（需人在场看窗口）。

**文档与工作区整理（同日早些时候）**

- **文档分层**：`docs/` 根下只留现状文档（`HANDOFF.md` / `ROADMAP.md` / 新增 `README.md` 索引）；
  9 份 1.x 期工作记录用 `git mv` 移入 **`docs/archive/`**（历史保留），并新增
  `docs/archive/README.md` 说明"只作考古、不代表现状"。引用路径同步修正
  （`AGENTS.md` / `docs/dev/AI-WORKFLOW.md` / `docs/archive/COMFORT-FIX.md`）。
- **新增 `docs/README.md`（文档总索引）**：按角色（接手代理 / 人类贡献者 / 工具使用者 / 测试朋友）
  给出"先读什么"，并逐份列出用途与"什么时候读"；明确**现状 vs 存档**的分界。
- **新增 `docs/dev/PLAN-P2-1-catalog-i18n.md`**：把 P2-1 的架构调研落盘，避免下一个代理重新推导。
  关键结论：
  1. catalog 只有 **`DetectionService.BuildDetectJson` 一个数据出口**，GUI 解析它显示；
  2. `App.OnStartup` 的 CLI 分支早于 `LangService.Load()` → **CLI 天然恒为中文**，是天然隔离点；
  3. CLI `-Json` **只能改值、不能加键**（有测试逐字节依赖）；
  4. `group` 同时是**筛选键**（XAML `Tag` ↔ `vm.Group`），**不能翻译**，只能做显示层映射；
  5. 切语言**不会重刷**已有条目列表（`AppState.Items` 是快照）——易漏。
- **修正过期头部**：`PLAN-0.1.2-features.md` 原写"M3 未开始"（实际已随 `v0.1.3-beta` 发布）→
  改为 M1/M2/M3 全部完成，标题由 0.1.2 更正为 0.1.x；`ROADMAP.md` 的现状基线整块由
  0.1.2-beta / 255 项单测校准到 **0.1.3-beta / 266 项**，并补上"一键优化""界面语言"两行。
- 本轮**只改文档**，不含代码改动。

### 历史轮次

**2026-09-24 · 环境变量"缺失"根因复核 + 环境事实文档化（P2-5）+ 版本同步核对**

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
- 为用户启动 `dist/folder-0.1.3/FpsTune.exe` 目检 0.1.3（独立便携版），`-Version` 自报 `0.1.3-beta`。
- **用户拍板后收尾（同日）**：①`git push origin main` → `c28e94f..f9066db`，本地与 `origin/main`
  已同步；②把 `D:\FpsTune` 从 `0.1.2-beta` 覆盖升级到 `0.1.3-beta`（用户明确"不用备份"），
  逐文件比对发布 Portable **7/7 一致**、无多余文件，`-Version` 自报 `0.1.3-beta`。**至此 P1-4 完成。**

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

**环境备忘（2026-09-25，含一次自我更正）**：上一版本文记录说"`publish-release.ps1` 因沙箱
批量删除策略被拦、需手动补齐 zip/清单"——**那是误判**。真正的根因在**脚本自身**：

- 脚本用管道式 `Get-ChildItem ... | Where-Object {...} | Remove-Item -Force` 清理 folder 输出里
  残留的 pdb；而本环境的 `Remove-Item` 包装**不支持管道参数绑定**——最小实验证明：
  喂**空集合**或喂 **1 个文件**都抛 `Remove-Item: missing path operand`（与文件数无关）。
- `DebugType=none` 之后该管道本来就是**空操作**，却把整个发布构建打断在打包之前
  （zip 与 `SHA256SUMS` 恰好都在这一步之后，所以永远产不出来）。
- 已修脚本：改成 `foreach` + `Remove-Item -LiteralPath $pdb.FullName`（行为等价，不走管道），
  提交 `f09d944`。修后 `publish-release.ps1` 与 `build-installer.ps1` **都能完整跑通**。
- 我第一轮把 `[safe-delete][...BULK_CONFIRM...]` 当成主因，其实它只是 `finally` 清理
  `dist/publish-tmp-<版本>` 时被跳过——**带 `-ErrorAction SilentlyContinue`，不影响退出码**，
  但会在 `dist/` 留下临时目录（约 430 个文件），需要手动 `rm -rf dist/publish-tmp-<版本>`。
- 教训：`publish-release.ps1` 开头（第 80–84 行）也会删这些输出目录，**没有** `-ErrorAction`，
  所以重跑前最好先用 bash `rm -rf` 清掉 `dist/{single-file,folder,publish-tmp}-<版本>` 与
  zip/清单，让它的清理变成空操作。

**环境备忘（2026-09-24 复核，原记载有误）**：旧记载为"MiMo 代理 shell 可能缺 `ProgramFiles*` 变量，
会导致 NuGet `Path.Combine` 炸掉；命令里补上即可。换 git bash 绕不开"。实测**不复现**——
变量没丢，只是 MSYS2 把名字转成了大写（详见本文 §4 本轮要点与
`docs/dev/AI-WORKFLOW.md` §环境变量）。**不要再给命令补 env**，那是无效动作。

## 5. 下一步建议（按性价比排序）

**完整待办总表见 `docs/dev/PLAN-backlog.md`（P0–P2）。** P0 与 P1 已全部收口，摘要：

1. **P2-6** 热门优化项逐项落地（三重缓冲关 / 垂直同步强制关 / 各向异性 16x / 着色器缓存 /
   图像锐化 / 数字振动 55–60）—— 调研已做一轮，**玩家收益最直接，建议优先**；每项须先过红线预审。
2. **P2-8** 按厂商生成驱动内手动设置清单（A 卡 / Intel）—— **覆盖面最大**（非 N 卡用户目前无指引）。
3. **P2-9 / P2-11** 朋友测试 CSV 与剪贴板、备份页展示 `.restored` —— 小改动、体验直接。
4. **P2-10 / P2-12** ICC 自选文件、PresentMon 前置检测。
5. **P2-2 / P2-4** 依赖真实 A/B 数据，而 P0-2 已被用户取消 → **无前置数据，暂缓**。
6. **P2-7 国际化**：已按用户决定**暂停**（剩余 1000 处记在 `I18nBaseline.txt`）。

## 6. 维护本文的规则

- 每完成一轮工作，**同时更新 `AGENTS.md` 与本文**，并 `git commit`（用户 2026-09-22 明确要求）。
- **发布必须停在「已构建待拍板」**：本地构建 + git 后把效果交给用户，等拍板再发（用户 2026-09-22）。
- 更新 §1、§2、§5，并在 §4 追加本轮要点（旧的可删，保持在一屏内）。
- 凡是写进本文的数字（测试数、版本号、提交号）必须当场核实，不接受"上次记录的是"。
- 与代码冲突时以代码为准，并立刻修正本文 —— 过时的交接文档比没有交接文档更危险。
