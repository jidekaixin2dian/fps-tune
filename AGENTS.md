# AGENTS.md — 给接手本项目的 AI 代理

> 这份文件面向**开发本项目**的代理。仓库里另一个 `SKILL.md` 面向**使用本工具调优帧率**的代理，
> 两者不要混用：改代码读这份，帮用户优化电脑读那份。

## 30 秒现状

| 项 | 值 |
|---|---|
| 项目 | FPS 帧律 / fps-tune —— Windows 系统层帧率调校台（33 个可还原优化项） |
| 技术栈 | C# WPF · `net10.0-windows` · 单一 C# 引擎同时驱动 GUI 与无头 CLI |
| 版本 | `VersionPrefix=0.1.3` + `VersionSuffix=beta`；发版中 `v0.1.3-beta`。上一版 `v0.1.2-beta` |
| 版本线背景 | 1.x 线因 .NET 8 将于 2026-11-10 EOL，**已停止维护**（冻结点 `legacy/1.x` = `v1.6.2`）；现行线是 .NET 10 的 **0.1 Beta** |
| 开发分支 | **`main` 是技术主线**（0.1 Beta，用户 2026-09-22 决定）。`beta` 内容已并入 `main`，仅作历史分支保留，不再单独演进 |
| 测试基线 | **270 / 270 通过**，`dotnet test -c Release`（含 M3 / 一键优化 / i18n / catalog 英译） |
| 权威交接 | `AGENTS.md` + `docs/HANDOFF.md`（入库；**每轮收工必须两者都更新并提交**）+ 根目录 `HANDOFF_PROMPT_*.md`（不入库，单轮提示） |

## 第一步：确认基线，不要先改代码

```bash
cd /c/Users/Aether/Documents/fpstune/review-3a060d1   # 主开发工作区
git status --short && git log --oneline -3
dotnet test FpsTune.Wpf.Tests/FpsTune.Wpf.Tests.csproj -c Release   # 期望 270 全绿
```

基线不绿就先查为什么，别把别人的红灯算到自己头上。然后向用户确认本轮优先级，再动手。

## 硬纪律（不可协商）

来自 `docs/dev/AI-WORKFLOW.md`，摘要：

1. **任何已验证的改动立刻提交**，不允许把验证过的东西长期留在工作区。
2. 一个改动一个提交；大动作前先 `chore: checkpoint before <下一步>` 存档。
3. 提交信息用中文三段式：标题 + 为什么 + 怎么验的。
4. 跑全量测试并**贴原始输出**，不要只说"测试通过"。
5. 行尾：仓库统一 LF（`.gitattributes` 强制）。`git diff --stat` 出现大量"增删对称"的文件
   说明行尾被翻成 CRLF —— 丢弃重来，别提交。
6. **每轮收工必须更新 `AGENTS.md` 与 `docs/HANDOFF.md`，并立刻 `git commit`**（用户 2026-09-22 明确要求）。
   两份文档是下一个代理的唯一入口，不允许只改代码不改交接。
7. **发布流程（用户 2026-09-25 明确；本文是唯一权威版本，其它文档一律指向这里）**：

   | 步 | 动作 | 是否等用户拍板 |
   |---|---|---|
   | 1 | **做好**：实现 + 自测，跑全量测试并贴原始输出 | — |
   | 2 | **`git commit`**（已验证的改动立刻入库，**不必等拍板**） | — |
   | 3 | **产出发布候选构建**：跑 `publish-release.ps1`（只写 `dist/`，**不联网、不上传**） | — |
   | 4 | **部署到本机安装位**：把 `dist/folder-<版本>/` 覆盖到 `D:\FpsTune`（**不跑安装器、不碰注册表**） | — |
   | 5 | **启动 GUI 给用户看**：让用户在真机上看效果 | — |
   | 6 | **等用户明确确认**（用户说"可以"之前，禁止第 7–8 步） | ← 等 |
   | 7 | **`git push`** | ← 拍板后 |
   | 8 | **上传 Release**：`gh release create`（把第 3 步已产出的资产传上去） | ← 拍板后 |

   - **第 3–5 步是"拍板前允许"的动作**：构建产物 + 部署到 `D:\FpsTune` + 启动，就是
     "把效果给用户看"的手段。这与"未拍板不得发版"**不冲突**。
   - **`publish-release.ps1` / `build-installer.ps1` 是构建脚本，不是发布动作**——它们只写 `dist/`，
     不联网、不上传，**拍板前可以跑**。真正需要拍板的是 **`gh release create` 与 `git push`**。
   - `publish-release.ps1` 要求 **tracked tree 干净**，并把 HEAD 的完整 SHA 嵌进
     `InformationalVersion`——所以**必须先 commit 再构建**；早于最终提交的产物不能当发布资产。
   - **改版本号是"做好"的一部分**：`VersionPrefix` 若与已发布版本相同，重建会**覆盖 `dist/` 里
     同名已发布资产**，造成"同号不同内容"（本仓库踩过，见 `docs/HANDOFF.md` §2.1）。
     所以有实质改动要发版时，先 bump 版本号并 commit，再构建。
   - 部署前确认 `D:\FpsTune` 无进程占用；覆盖失败要如实报告，不要静默跳过。
   - 用户如明确说"不用备份"就整目录覆盖；否则先备份为 `D:\FpsTune-backup-YYYYMMDD`。
8. 推送 `main` 前向用户确认节奏；禁止 force push / 改写 `main` 已推送历史。`legacy/1.x` 只读冻结，不接受修复。

产品红线在 `docs/ROADMAP.md` §产品定位（安全闭环 / 零侵入 / 数据说话 / 全 FPS 通用 / 可信透明）。
**注意**：ROADMAP 的现状基线段落停在 v1.5.0，是历史存档；当前状态看 `docs/HANDOFF.md`。

## 哪些文件是权威

> **不确定某份文档是"现状"还是"历史存档"，先查 `docs/README.md`（文档总索引）。**

| 想查 | 看这里 | 陷阱 |
|---|---|---|
| **文档总索引** | `docs/README.md` | 按角色给出「先读什么」；现状 vs 存档分界 |
| 优化项与预设 | `catalog/catalog.json` | 唯一数据源；GUI/CLI 都读它 |
| 版本号 | `Directory.Build.props` | 不是 csproj，也不是 README |
| 发版流程 | `RELEASE.md` | 必须从干净最终提交构建 |
| 当前状态 / 待办 | `docs/HANDOFF.md` | 比 `docs/ROADMAP.md` 新；**现状数字以它为准** |
| 产品方向 / 五条红线 | `docs/ROADMAP.md` | 现状基线段落已校准，但数字仍以 HANDOFF 为准 |
| 0.1.x 功能设计依据 | `docs/dev/PLAN-0.1.2-features.md` | 里程碑 M1/M2/M3（**均已发布**） |
| 待办总表（P0–P2） | `docs/dev/PLAN-backlog.md` | **开工先看这份**；HANDOFF §5 与它对齐 |
| P2-1 英译改法 | `docs/dev/PLAN-P2-1-catalog-i18n.md` | 已调研未实施；含硬约束与踩坑点 |
| 代理开发纪律 | `docs/dev/AI-WORKFLOW.md` | 硬要求 |
| 工具使用者流程 | `SKILL.md` | 面向用户，不是开发者 |
| 1.x 历史存档 | `docs/archive/` | **不代表现状**；路径/版本/依赖都可能过期 |

`docs/` 根下只放**现状文档**（`HANDOFF.md` / `ROADMAP.md` / `README.md` 索引）。

`docs/` 根下只放**现状文档**（`HANDOFF.md` / `ROADMAP.md` / `README.md` 索引）。
1.x 版本线的存档（`RELEASE-v1.6.2.md`、`REVIEW-1.6.1.md`、`OPTIMIZE-CONSOLE-REVIEW.md` 等）
已移入 **`docs/archive/`**，其中"需要 .NET 8"之类的表述已过时，不要当作现状。
1.x 代码在分支 **`legacy/1.x`**（= tag `v1.6.2`），**停止维护**；不要往那条分支修 bug 或发版。
**文档总索引见 `docs/README.md`**——不确定某份文档是现状还是存档，先查那里。

## 环境事实

- 本机 SDK `10.0.401`，WindowsDesktop 运行时 `10.0.12`。
- **Git Bash 里 Windows 系统变量名是全大写的，这不是变量缺失**：`$ProgramFiles` 为空、
  `${PROGRAMFILES}` 才有值。MSYS2 运行时故意把一小批变量名改成大写（`renv_arr[]`，
  2026-09-24 复核确认）。**构建与测试不依赖这些变量**——`dotnet build` / `dotnet test`
  直接跑即可，不要给命令补 env。详见 `docs/dev/AI-WORKFLOW.md` §环境变量。
- **GitHub Actions 可用**（`build` + `smoke`，push 到 `main` / 开 PR 时触发）。但 CI 在 `windows-latest`
  runner 上跑，比本机更容易撞上文件占用类瞬时失败——改完先本机全量绿，再看 CI；不要把 CI 的一次红
  直接归因于自己的提交，先比对是不是同两条已知用例。
- **`publish-release.ps1` 在本代理沙箱里会在打包前被拦**：它 `finally` 清理
  `dist/publish-tmp-<版本>`（约 409 个文件）会触发沙箱的"批量删除需确认"策略（阈值 50），
  报 `SAFE_DELETE_BULK_CONFIRM_REQUIRED`。**两个 publish 输出（`single-file-<ver>/`、
  `folder-<ver>/`）在拦截前已正常产出**，缺的是 zip 与 `SHA256SUMS`——按
  `docs/HANDOFF.md`「环境备忘」的手动口径补齐即可（含无 BOM 的清单写法）。
- `gh` CLI 已登录，仓库为 `jidekaixin2dian/fps-tune`（公开）。
- Inno Setup 6 存在，路径由 `build-installer.ps1 -CheckOnly` 探测。

## 危险操作清单（动手前必须问用户）

| 操作 | 为什么危险 |
|---|---|
| 运行 `FpsTune.exe -Apply` / GUI 里点"应用所选" | 真的改本机注册表、电源计划、服务、启动配置 |
| 跑 `publish-release.ps1` / `build-installer.ps1` 后**未经用户拍板就发布** | 产物会公开出现在 GitHub Release；必须先本地构建 + git + 给用户看效果，拍板后才发 |
| 覆盖 `D:\FpsTune` | 那是**本机安装位**（实测 `0.1.3-beta`，2026-09-24 升级），不是源码；覆盖前必须用户点头 |
| force push / 重写 `main` 历史，或向 `legacy/1.x` 提交 | 破坏已推送历史或已冻结的 1.x |
| 删 `dist/` 以外的目录、`work/` 里的探针 | 探针是驱动层实验的原始依据 |

只读安全动作：`-Detect -Json`、`-Version`、`-ListRestore -Json`、`-Experiment -Simulate`、`dotnet test`。

## 工作区地图（这台机器上有三份，别搞混）

```
C:\Users\Aether\Documents\fpstune\review-3a060d1   ← 主开发工作区（用户 2026-09-21 确认，代码在这里）
C:\Users\Aether\Documents\GitHub\fps-tune          ← 次克隆，2026-09-01 建后停用；曾用来改 README 并推过 main
D:\FpsTune                                         ← 本机唯一安装位（实测 0.1.3-beta），开始菜单 .lnk 也指向它；只读，别当源码
C:\Users\Aether\Documents\fps-tune-promo           ← 推广物料与文案（不在仓库里）
```

改动只在主开发工作区进行。若发现次克隆或 `origin/main` 有你没做过的提交，先 `git fetch` 对比再合并，
不要直接覆盖。

## 完成一轮工作的标准动作

1. 全量测试贴输出；涉及界面则本机跑一次 GUI 目检（只读页面）。
2. **更新 `docs/HANDOFF.md`**（当前状态 / 下一步 / 本轮要点）。
3. **更新 `AGENTS.md`**（若分支策略、基线、纪律或权威文件表有变）。
4. **把上述文档改动 `git commit`**——不允许只改文档不提交，也不允许只改代码不改交接。
5. 按用户授权决定是否推送 `main`。
6. 若改了版本线、发布产物或公开口径，同步检查 `README.md` / `README.en.md` / `RELEASE.md` 是否跟着过期。
