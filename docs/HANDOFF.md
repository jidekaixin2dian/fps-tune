# HANDOFF · 项目交接现状

> 最后核对：2026-09-25（本轮：**启动画面 + 启动预热 + 「我的方案」弹窗化**（用户反馈体验轮）
> ；此前：P2-8 驱动内手动设置清单 + 0.1.5 候选）
> 本文是**入库的长期交接文档**。单轮工作的临时提示词写进根目录 `HANDOFF_PROMPT_YYYY-MM-DD.md`
> （已被 `.gitignore` 排除），那种文件只活一轮，不要往这里抄。
> 接手请先读 `AGENTS.md`，再读本文。

## 1. 一句话现状

主线是 0.1 Beta 线（现落在 **`main`**）。M1/M2/M3 功能与一键优化、i18n 已发 **`v0.1.3-beta`**；
**0.1.6-beta 候选（P2-8 + 启动体验/方案弹窗）已按 `8eaec90` 构建、部署到 `D:\FpsTune` 并启动给用户目检，等拍板后再 push / 发 Release**；
`dist/` 里另有 0.1.5 / 0.1.4 两套旧候选完整保留。**发 0.1.6 即包含前两套全部内容**。
1.x 线**已停止维护**，冻结点 `legacy/1.x`（= `v1.6.2`）。

## 2. 版本与分支

| 项 | 值 |
|---|---|
| `Directory.Build.props` | `VersionPrefix=0.1.6` / `VersionSuffix=beta` |
| 已发布 Release | **`v0.1.3-beta`**（2026-09-22，`29ed0ea`）；资产 = Setup + Portable + SHA256SUMS。上一版 `v0.1.2-beta` |
| 未发布的内容 | **`0.1.6-beta` 候选（最新）**：P2-8 手动设置清单 + 启动画面/启动预热 + 「我的方案」弹窗化。**已按 `8eaec90` 构建（三资产齐全、哈希已核）、已部署 `D:\FpsTune` 供目检；未发 Release**<br>旧候选 **0.1.5-beta（`46a3776`，= P2-8）** 与 **0.1.4-beta（`e3d0041`）** 三资产仍完整保留在 `dist/`。**0.1.6 ⊇ 0.1.5 ⊇ 0.1.4 内容**，发 0.1.6 即可，标签打 `8eaec90` |
| `main` | **技术主线（0.1 Beta）**。树 = 原 `beta` 全部内容（含 ICC / DVC / 脚本校验根因修复） |
| `beta` | 原开发线；内容已并入 `main`（merge `1d9777c`），不再单独演进 |
| `legacy/1.x` | **1.x 冻结分支** = tag `v1.6.2`；停止维护，不修不发 |
| 本机安装位 | `D:\FpsTune` = **`0.1.6-beta` 内嵌 SHA `8eaec90`**（本轮已部署并启动目检）。文件 6 个，与 `dist/folder-0.1.6/` 清单一致。备份链：`-20260925-46a3`（= 0.1.5 旧装）、`-20260925-0627`（= `627740f`）、`-20260925`（= 已发布 0.1.3） |
| 测试基线 | 301 / 301（`dotnet test -c Release`；2026-09-25 复核，含 P2-8 厂商识别 20 用例与资源键守卫） |
| 编译警告 | **0**（2026-09-25 由 6 个清零；新增代码请守住这条，见 `CODE-HEALTH.md`） |
| CI | GitHub Actions **可用**（`build` + `smoke`，push 到 main 与 PR 触发） |
| catalog | 33 项；22 项需管理员、13 项需重启；预设 balanced(27) / safe-only |

### 2.1 版本与同步状态核对（2026-09-25 实测，0.1.6 轮更新）

| 位置 | 版本 | 状态 |
|---|---|---|
| GitHub 已发布 Release | `v0.1.3-beta` | 线上最新（**0.1.4 / 0.1.5 / 0.1.6 均未发布**） |
| **发布候选（未发布，最新）** | **`0.1.6-beta`** | 官方脚本从 HEAD `8eaec90` 产出：`dist/folder-0.1.6/`（6 个文件）、`dist/single-file-0.1.6/FpsTune.exe`(62.7M)、`dist/FpsTune-Portable-0.1.6.zip`(0.9M)、`dist/installer/FpsTune-Setup-0.1.6.exe`、`dist/SHA256SUMS-v0.1.6.txt`（3 行）。`ProductVersion` = `0.1.6-beta+8eaec90…`。已部署 `D:\FpsTune` 并启动目检 |
| 发布候选（未发布，旧） | `0.1.5-beta`（`46a3776`）/ `0.1.4-beta`（`e3d0041`） | 三资产完整保留在 `dist/`，未被 0.1.6 构建波及；内容均被 0.1.6 包含 |
| 本机安装位 `D:\FpsTune` | `0.1.6-beta`（内嵌 SHA **`8eaec90`**） | **本轮已部署**：整目录覆盖，文件清单双向一致（6/6），`catalog.json` 哈希一致；启动 15 秒存活、`error.log` 字节数不变 |
| 源码 `main` | `0.1.6-beta` | **本地领先 `origin/main` 8+ 个提交未 push**（上轮 3 个 + 本轮 splash `6d422dd`、方案弹窗 `b2feb23`、bump `8ea6ed7`、.gitignore `8eaec90` + 收尾文档）。**push 等用户拍板** |
| 备份 | — | `D:\FpsTune-backup-20260925-46a3` = 0.1.5（本轮部署前所备）；`-20260925-0627` = `627740f`；`-20260925` = 已发布 0.1.3 |

- **0.1.6 三资产实测哈希**（与 `SHA256SUMS-v0.1.6.txt` 逐项一致）：单文件
  `91889E46…EBF0` / Portable zip `1D06CE58…ECBF2` / Setup `14C06DB1…D4454`。
  脚本自带冒烟通过（`-Version` / `-Detect -Json`）。
- **打标签口径（若发布 0.1.6）**：候选由 HEAD `8eaec90` 产出，`v0.1.6-beta` 打在 **`8eaec90`**。
  判断方法仍是读产物 `ProductVersion` 的 SHA。
- **`8eaec90` 本身是 .gitignore 提交**（忽略宿主生成的 `.zcodeignore`，它曾让发布脚本的
  "tracked tree 干净"检查拒绝构建）；其前是 bump `8ea6ed7` 与两个功能提交。
- 0.1.5 候选哈希：单文件 `A3A1A423…0CA2A` / zip `4D90C816…E0618` / Setup `964791F9…58D6`。
- 0.1.4 候选哈希：单文件 `91FF00D7…46F93` / zip `D26E13C2…0FBE2A` / Setup `46A46598…7D51B9`。
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

1. **发版**：`dist/` 里现有多套候选，**0.1.6-beta（`8eaec90`）内容 ⊇ 0.1.5 ⊇ 0.1.4，推荐发 0.1.6**；
   旧两套资产仅作保留。**唯一剩下的动作是 `gh release create`，等用户明确确认**；
   Release Notes **不得含编造的 FPS/收益数字**。
2. **是否 push `main`**：本地领先 `origin/main` 8+ 个提交（见 §2.1），**等用户拍板后一起 push**。
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

## 4. 本轮（2026-09-25 续）做了什么

**⑧ 启动画面 + 启动预热 + 「我的方案」弹窗化（本轮，用户点名的体验轮）**

- **背景**：用户提出三件事——性能优化（"只做看得见的页面加载"）、完全加载前的加载动画、
  自定义方案界面改成弹窗式。**排查结论**：页面懒加载**早已存在**（`MainWindow._pageFactories`
  按导航创建并缓存，WMI 等重活也已在后台线程），真正的体验缺口是**启动期零反馈**和方案交互本身。
- **启动画面（`6d422dd`）**：新增 `Views/SplashWindow`（应用名 + 标语 + 四点呼吸动画 + 版本号）。
  `App.OnStartup` 重排：设置/语言/**主题**先行 → splash 显示 → 后台 Task 预热
  `HardwareInfoService`（WMI 慢查询提前跑，首页 Loaded 命中缓存）→ **ApplicationIdle 队列**
  里再构建主窗（排在 Render 之后，splash 必先画出首帧）→ 主窗 `ContentRendered` 后 180ms
  淡出关闭（低配直接关），`Closed` 兜底。顺带删掉 MainWindow 构造里重复的
  `SettingsService.Load()` 与 `ThemeManager.Initialize()`。
- **方案弹窗（`b2feb23`）**：新增 `Views/ProfileManagerWindow`，替换右栏 Expander 旧交互
  （旧交互只有名称输入 + 5 个小按钮，载入/删除必须手输名字、看不到已有方案）。弹窗内：
  已保存方案列表（名称 + 项数）、每行载入/导出/删除一键完成、顶部保存当前勾选（回车提交）、
  底部导入；空态有引导文案；Esc 关闭；载入经 `LoadRequestedIds` 带回优化页。
  预设行末尾加「我的配置方案」入口按钮；右栏详情面板随之占满全高。
  删除 5 个旧 handler 后 i18n 棘轮基线 `Views/OptimizeView.xaml.cs` **40 → 22**；
  新文案全走资源键（中英各 +16）。
- **验证**：`dotnet test -c Release` **301/301**、0 警告；UIA 实测 splash 时序
  （t=300ms splash+主窗并存、t=450ms splash 淡出）并**截图目检**；方案弹窗**全流程**：
  打开 → 保存（行"测试方案 · 27 项"出现）→ 删除（危险确认文案正确、确认后空态回来）→
  载入（弹窗带结果关闭、"自定义"预设选中、详情区"方案已载入 27 项"）→ 关闭，
  全程 `error.log` 不变；测试数据已清理（`profiles.json` 删除还原）。
- **新经验（已录入 `AI-WORKFLOW.md` §四）**：`ShowInTaskbar=False` 的 WPF 工具窗
  （AppDialogWindow / ProfileManagerWindow）**不出现在 UIA Root 子查询里**，
  验证脚本要用 `EnumWindows + AutomationElement.FromHandle` 定位；物理鼠标点击
  不可靠，改用 `InvokePattern` / `SelectionItemPattern`；PS 脚本必须带 UTF-8 BOM。
- **候选与部署**：bump 0.1.6（`8ea6ed7`）；`.zcodeignore`（宿主生成）曾让发布脚本的
  干净检查拒绝构建，按 `.workbuddy-ai/` 先例加入 `.gitignore`（`8eaec90`）；重建后
  三哈希与清单逐项一致；部署 `D:\FpsTune`（备份 `-20260925-46a3`）并启动给用户。
- **未做（等拍板）**：`git push`（本地领先 8+ 提交）、`gh release create`（推荐发 0.1.6，
  内容包含 0.1.5 / 0.1.4）。

**⑦ P2-8 驱动内手动设置清单 + 0.1.5 候选构建与部署（上一轮）**

- **P2-8 实现（`74d4eea`）**：GUI_PLAN G1「按厂商生成驱动内手动设置清单」落地为显示页第 6 张卡：
  - `Services/GpuVendor.cs`：按 WMI 显卡名粗判厂商（NVIDIA/AMD/Intel/Unknown），
    **认出不猜**（与 `GpuDriverAdvisor` 同口径）；多显卡只看主卡，与驱动建议卡一致。
  - 卡片按厂商出内容：**N 卡** → 指向上方「驱动 3D 设置」卡（已自动化，无需手动）；
    **A 卡** → Adrenalin 七条（Anti-Lag / 等待垂直刷新 / FreeSync / Chill / 增强同步 /
    图像锐化 / AF·纹理过滤，含「游戏 → 图形」入口与每游戏 profile 优先级）；
    **Intel** → Graphics Software 四条（驱动更新 / Smooth Sync 取舍 / 电源 / 双通道内存）；
    **识别不出** → 明说并指向 ICC 兜底。全卡**只读指引：不代改设置、不承诺帧数**，
    功能名按 AMD 官方支持文档与 Intel Graphics Software 现状核实（2026-09 查证，来源写进卡片尾注）。
    注：AMD 的 Radeon Image Sharpening 与 P2-6「N 卡图像锐化不做」不冲突——那条是说
    NVIDIA DRS 头文件无此设置；AMD 驱动里真有此项，按口味项如实列出。
  - 文案全部走 `Str.*` 资源键（中英各 +21 键），**不动 i18n 棘轮基线**；
    `OneClickOptimizer` 非 N 卡提示语同步指向新清单。
  - **新增两道守卫**：`GpuVendorTests`（20 用例，含 VMware/Matrox 等负例）+
    `StringResourceTests`（中英键集必须一致 + 界面引用键必须存在于字典，
    防 `!key!` 运行时漏配——首跑就抓到 `Str.cs` 文档注释里的示例键误报，已按
    `I18nGuardTests` 同款 `StripCommentLines` 修掉）。
- **验证**：`dotnet test -c Release` **301/301**（279 基线 + 22 新增）；UI Automation
  **切页验证**（第 4 个页签）：新卡标题与 N 卡分支文案渲染、原 6 卡齐全、无 `!Str.*` 缺键、
  `error.log` 字节数不变。**如实说明**：本机是 N 卡，A 卡 / Intel 分支的**内容**由单测覆盖、
  渲染模板与 N 卡分支完全相同，但**没人真机看过**——有 A 卡 / Intel 机器时值得目检一次。
- **0.1.5 候选（`46a3776` bump 版本后构建）**：`publish-release.ps1` + `build-installer.ps1`
  两步齐三资产，哈希与清单逐项一致（见 §2.1）；脚本自带冒烟通过。0.1.4 三资产未被波及。
- **部署 `D:\FpsTune`**（按硬纪律第 7 条，拍板前允许）：先备份为
  `D:\FpsTune-backup-20260925-0627`（= 旧构建 `627740f`；今天已有 `-20260925` = 0.1.3，
  名字撞车所以带 SHA 后缀），再整目录覆盖；清单 6/6 双向一致、无多余项，
  `catalog.json` 哈希一致；从安装位启动 15 秒存活、`error.log` 无新增，**GUI 已留给用户目检**。
- **未做（等拍板）**：`git push`（本地领先 3+ 提交）、`gh release create`（发 0.1.5 还是 0.1.4 由用户定）。

**⑥ push + 0.1.4 候选重建（上轮收尾，无代码改动）**

- **push**：`7dbfc91..e3d0041` 已推 `origin/main`；`git rev-list --left-right --count` = `0 0`。
- **重建 0.1.4 候选**（**版本号不变** —— 0.1.4 从未发布，bump 反而错）：
  - `publish-release.ps1` 退出码 0；`ProductVersion` = `0.1.4-beta+e3d00415c55…`；
    脚本自带独立冒烟通过（两个 exe `-Version` → `0.1.4-beta`、单文件 `-Detect -Json` → 33 项）。
  - **`build-installer.ps1` 是单独一步**：`publish-release.ps1` **不产安装包**（本轮差点漏掉）。
    两步都跑完才有齐三资产：单文件 63M / Portable zip 948K / Setup 58M。
  - 三资产哈希与 `SHA256SUMS-v0.1.4.txt`（3 行）**逐项一致**；已发布 0.1.3 资产未被波及
    （`4E055D2E…DEE6A` 重建前后实测一致）。
  - 已知现象复现：`finally` 清理 `dist/publish-tmp-0.1.4`（424 项）被沙箱拦（
    `SAFE_DELETE_BULK_CONFIRM_REQUIRED`），需手动 `rm -rf`；**重跑前也要先手动清 0.1.4 输出**
    （脚本开头的清理没有 `-ErrorAction`，被拦会直接失败）。
- **修正一处过期数字**：folder 产物 0.1.3 = **7** 个文件、0.1.4 = **6** 个
  （`friend-test.ps1` 随朋友测试模块移除，不再进产物）。§2 与"升级做法"已按实测改写。
- **未做**：部署 `D:\FpsTune`（用户未要求）→ 安装位仍停在旧构建 `627740f`。

**⑤ 代码健康度评估 + NVAPI 引导重构（`13c74b2`）**

- **评估结论**：工作区干净（未跟踪未忽略文件 **0**、tracked 仅 168 个）；代码**不是屎山**——
  590 个方法长度**中位数 14 行**、仅 29 个 ≥60 行、无 `TODO`/`HACK`、无吞异常、编译 0 警告。
  底数与债务清单落盘 → **`docs/dev/CODE-HEALTH.md`**（接手先看这份，**别重新量**）。
- **修掉的真隐患**（都是顺着编译警告查出来的，不是"消警告"）：
  1. **半初始化**：`NvidiaDrs.TryInitialize` / `NvDvcApi.TryInitialize` 原先边解析边落字段，
     中途失败会留下"_initialize 非空、其余为 null"；下一次 `TryInitialize` 因
     `_initialize is not null` 误判为已初始化，随后 `OpenSession` 撞空引用。
     改为"全部解析到局部变量、`NvAPI_Initialize` 成功后才落字段"。
  2. `FindGameProcess` 收 `string?`：`GameName` 可空，而 `Process.GetProcessesByName(null)`
     会抛 `ArgumentNullException`。
  3. `ProfileImpl` 的 `session` 是死参数（已删，2 处调用点同步）。
- **去重**：NVAPI 引导原先在 DRS 与数字振动各写一遍，且**两套机制已经分歧**
  （`LoadLibrary`/`GetProcAddress` vs `NativeLibrary.Load`/`GetExport`），委托与
  `IdInitialize`/`StatusOk` 也各声明一份 → 统一到 **`Services/NvapiNative.cs`**。
- **警告 6 → 0**；测试仍 **279/279**；真机验证两条只读 NVAPI 路径
  （DVC 读 = 「当前 50%…」、DRS 读 = 「当前未覆盖…」）都正常，`error.log` 字节数未变。
- **未验证（如实说明）**：DRS 的**写入**路径需管理员且会改真实驱动配置（危险操作清单），
  未在本机执行；该路径只改了 owner 取法，由单测
  `Apply_creates_own_profile_when_game_is_not_registered_anywhere` 覆盖。
- 顺带把工作区里仅有的 2 个 CRLF 文件（`DisplayQualityService.cs`、`I18nBaseline.txt`）
  归一为 LF（全仓库 tracked 文件 `w/crlf` 计数 = 0）。
- i18n 棘轮基线按"文案搬家"调整：新增 `Services/NvapiNative.cs 2`，
  `NvidiaDrs 19→16`、`DigitalVibranceService 9→8`，**总数 987 → 985（净减少）**；
  基线头部已写明这条规则，免得下次误判成棘轮违规。

**① 红线口径同步：从「零侵入」移除「不伪装硬件」（`505e146`）**

- 用户拍板确认从产品红线移除该条（工作区里 `docs/ROADMAP.md` 已有该未提交改动）。
- 全仓库同步 **13 个文件**：`ROADMAP` / `CONTRIBUTING` / `PLAN-backlog` / `README`(中英) /
  `SKILL` / `TESTING` / `UI_VISION_CHECKLIST` / `GUI_PLAN`，以及**应用内 About 文案**
  （`DetectionService.cs` 里 `-Detect -Json` 的 `notes` + `Strings.zh-CN|en-US.xaml` 的
  `Str.AboutLine2`）与 `OneClickOptimizer.cs` 的注释。
- **有意保留 4 处**：`GUI_DESIGN_PROMPT` 的功能禁加项、`GUI_PLAN` 的"已拒绝"记录、
  本文 §4 的历史轮次（史实）、两处与红线无关的注释用法。
- `-Detect -Json` **只改了 `notes` 的值、键结构未动**；已确认无测试断言该文案。

**② 交接文档数字校准（`415fe15`）**

- 上一轮收工漏更：`AGENTS.md` 仍写 `0.1.3` / 270 测试；§2 写 273/273；
  §2.1 写 HEAD `f09d944` + 5 个提交未 push。**全部当场实测后改对。**
- **重要发现**：产物内嵌 SHA 是 **`627740f`**（不是旧记载的 `f09d944`）——
  `dist/folder-0.1.4/`、`dist/single-file-0.1.4/` 与 `D:\FpsTune` 三处 `ProductVersion` 一致，
  说明候选是在最后一批功能提交**之后**重建的。**若发 0.1.4，标签应打在 `627740f`。**
- 另：`D:\FpsTune` 与 `dist/` 的产物**都停在 `627740f`**，本轮源码改动尚未进产物。

**③ P2-6 收口（`f2c4fcc` + 本轮实施提交）**

- **调研首次落盘** → `docs/dev/PLAN-P2-6-hot-options.md`。以前只写"调研已做一轮"，
  结论无处可查、每轮重推。现按项固定：
  - **已落地 4 项**：VSync 强制关（`VSYNCMODE_ID=0x00A879CF`）/ AF16x
    （`ANISO_MODE_SELECTOR_ID` + `ANISO_MODE_LEVEL_ID`）/ 着色器缓存
    （`PS_SHADERDISKCACHE_ID=0x00198FFF`）—— 三项早已入显示页 3D 卡 + 一键优化 + 竞技预设；
    本轮新增**数字振动「推荐 55–60%」按钮**。
  - **不做 2 项**：**图像锐化** —— 官方 `NvApiDriverSettings.h` 里**没有任何**
    SHARPEN / SHARP / DENOISE / NIS 项（**已抓上游 `NVIDIA/nvapi` main 分支复核**）；
    最接近的 `NV_QUALITY_UPSCALING_ID` 是缩放开关，**不得冒充锐化**（红线「可信透明」）。
    **三重缓冲** —— 只有 `OGL_TRIPLE_BUFFER_ID`（OpenGL 专用），D3D 无独立项。
- **数字振动实现细节**：社区给的 55–60 是**区间**，所以做的是"快捷按钮 + 区间提示"
  而非一个具名开关；按钮把滑块置到中点 **57**，**不直接写入**（仍走「应用」确认对话框）。
  来源标清（社区文章 2026-02-28：竞技类 50–70% / 单机氛围类 60–80%），**非官方推荐**；
  原有的"主播常用 70–80%"是另一取向，保留。
- **验证**：`dotnet test -c Release` **279/279**；并**真启动 GUI 切到显示页**，
  UI 自动化树确认新按钮与说明文本已渲染、`error.log` 字节数未变。

**④ 补了一条开发纪律（`docs/dev/AI-WORKFLOW.md` §四 第 2 条）**

- **发现既有纪律有漏洞**："改完 XAML 必须真启动一次并观察存活"**对懒加载页面无效** ——
  `MainWindow._pageFactories` 是 `() => new XxxView()`，页面只在**导航到时**才实例化，
  默认停在"概览"。改显示页 / 设置页时，"启动后进程存活"根本没覆盖到你改的那一页。
- 已补"切页验证"的完整做法（UI Automation 按位置点页签 + 用该页独有文案确认渲染 +
  核对 `error.log`），并写明**进程会随启动它的 shell 被回收，所以启动/点击/复查必须在同一次调用内完成**。

### 历史轮次（2026-09-25 上半场）

**收尾：清干净对外文档里的朋友测试痕迹 + 部署 + 推送**

- README.md / README.en.md 的"另有：…"一行都还在提朋友测试（上一轮只改了中文版，
  英文版漏了）——两处已清；`docs/ROADMAP.md` 里"诊断报告导出"的说明原本挂了一句
  "（朋友测试流程的线上化）"，改为"（把结果发给开发者）"。
- 截图资源 `assets/screenshots/` 只有 01–05，**本来就没有朋友测试页截图**，无需处理。
- 历史条目（`ROADMAP.md` 的 v1.0.5 版本历史、`docs/archive/`）**不动**——那是史实。
- **发现并修掉一个部署陷阱**：部署是**覆盖**不是**镜像**。删模块后候选目录里已无
  `friend-test.ps1`，但 `D:\FpsTune` 里那份**旧脚本残留了下来**（逐文件校验只报了 6/7 才暴露）。
  已手动删除，安装位现在与候选目录**文件清单一致**。
  **结论：每次部署后要比对两边文件清单，清掉候选里没有的多余项。**
- 本轮部署验证：publish 退出码 0；启动后 15 秒进程存活、`error.log` 字节数不变（无新异常）。
- 已 push（`3ce84e9..627740f`），`main` 与 `origin/main` 同步。
- 生成新一轮的接手提示词 `HANDOFF_PROMPT_2026-09-25.md`（按仓库约定不入库）。

**新增「驱动版本建议」卡片 + ICC 预设扩到 8 个（用户要求）**

- **ICC 预设 3 → 7**（+「标准」共 8 张卡片）：夜战护眼 / 暖色 / 冷色清晰 / 柔和，
  用现有的曲线与矩阵原语实现。**诚实边界**：ICC profile 是显示器个体相关的，
  别人的 profile 搬不过来；社区流传的是"视觉取向"，所以做的是取向而不是移植文件。
- **驱动版本建议**（新 `Services/GpuDriverAdvisor.cs` + 显示页卡片）：
  按显卡名解析 N 卡系列（RTX/GTX + 四位数字取前两位），给出稳定首选与备选版本。
  - **数据来源必须标清**：社区共识（什么值得买 2026-01-23），**非官方推荐**，
    界面与代码都写明来源与日期，并提示版本会变、以官网为准。
  - **50 系如实留空**：社区没有公认稳定版，就不编一个版本号出来（有测试守着这条）。
  - **识别不出就不猜**：非 N 卡 / 未知代号返回 null（AMD、Intel、RTX 9070、GTX 980 都有测试）。
  - **只读信息**：本工具不下载、不安装驱动（红线）。卡片里只有版本号与来源。
- 测试 259 → **279**（新增 20 条，含解析边界与"不猜"的负例）。

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

**完整待办总表见 `docs/dev/PLAN-backlog.md`（P0–P2）。** P0、P1、P2-6、**P2-8 均已收口**，摘要：

1. **P2-11** 备份页展示 `.restored` 消费记录 —— 小改动、体验直接。
2. **P2-10 / P2-12** ICC 自选 `.icc` / `.icm`、A/B 页 PresentMon 前置检测。
3. **P2-2 / P2-4** 依赖真实 A/B 数据，而 P0-2 已被用户取消 → **无前置数据，暂缓**。
4. **P2-7 国际化**：已按用户决定**暂停**（剩余 1000 处记在 `I18nBaseline.txt`）。
5. **P2-9** 已作废（朋友测试模块已整体移除）。

> **候选与拍板**：`dist/` 里 0.1.6（`8eaec90`，内容最全）/ 0.1.5 / 0.1.4 三套候选都齐三资产；
> 0.1.6 已部署 `D:\FpsTune` 并启动给用户目检。**push 与 `gh release create` 都等用户拍板**
> （见 §2.1 与"未决"；0.1.6 ⊇ 0.1.5 ⊇ 0.1.4，发 0.1.6 即可）。

## 6. 维护本文的规则

- 每完成一轮工作，**同时更新 `AGENTS.md` 与本文**，并 `git commit`（用户 2026-09-22 明确要求）。
- **发布必须停在「已构建待拍板」**：本地构建 + git 后把效果交给用户，等拍板再发（用户 2026-09-22）。
- 更新 §1、§2、§5，并在 §4 追加本轮要点（旧的可删，保持在一屏内）。
- 凡是写进本文的数字（测试数、版本号、提交号）必须当场核实，不接受"上次记录的是"。
- 与代码冲突时以代码为准，并立刻修正本文 —— 过时的交接文档比没有交接文档更危险。
