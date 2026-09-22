# AGENTS.md — 给接手本项目的 AI 代理

> 这份文件面向**开发本项目**的代理。仓库里另一个 `SKILL.md` 面向**使用本工具调优帧率**的代理，
> 两者不要混用：改代码读这份，帮用户优化电脑读那份。

## 30 秒现状

| 项 | 值 |
|---|---|
| 项目 | FPS 帧律 / fps-tune —— Windows 系统层帧率调校台（33 个可还原优化项） |
| 技术栈 | C# WPF · `net10.0-windows` · 单一 C# 引擎同时驱动 GUI 与无头 CLI |
| 版本 | `VersionPrefix=0.1.2` + `VersionSuffix=beta`；**已发布 `v0.1.2-beta`**。下一版本号 `0.1.3` 留给 M3 |
| 版本线背景 | 1.x 线因 .NET 8 将于 2026-11-10 EOL，**已停止维护**（冻结点 `legacy/1.x` = `v1.6.2`）；现行线是 .NET 10 的 **0.1 Beta** |
| 开发分支 | **`main` 是技术主线**（0.1 Beta，用户 2026-09-22 决定）。`beta` 内容已并入 `main`，仅作历史分支保留，不再单独演进 |
| 测试基线 | **255 / 255 通过**，`dotnet test -c Release` |
| 权威交接 | `AGENTS.md` + `docs/HANDOFF.md`（入库；**每轮收工必须两者都更新并提交**）+ 根目录 `HANDOFF_PROMPT_*.md`（不入库，单轮提示） |

## 第一步：确认基线，不要先改代码

```bash
cd /c/Users/Aether/Documents/fpstune/review-3a060d1   # 主开发工作区
git status --short && git log --oneline -3
dotnet test FpsTune.Wpf.Tests/FpsTune.Wpf.Tests.csproj -c Release   # 期望 255 全绿
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
7. 推送 `main` 前向用户确认节奏；禁止 force push / 改写 `main` 已推送历史。`legacy/1.x` 只读冻结，不接受修复。

产品红线在 `docs/ROADMAP.md` §产品定位（安全闭环 / 零侵入 / 数据说话 / 全 FPS 通用 / 可信透明）。
**注意**：ROADMAP 的现状基线段落停在 v1.5.0，是历史存档；当前状态看 `docs/HANDOFF.md`。

## 哪些文件是权威

| 想查 | 看这里 | 陷阱 |
|---|---|---|
| 优化项与预设 | `catalog/catalog.json` | 唯一数据源；GUI/CLI 都读它 |
| 版本号 | `Directory.Build.props` | 不是 csproj，也不是 README |
| 发版流程 | `RELEASE.md` | 必须从干净最终提交构建 |
| 当前状态 / 待办 | `docs/HANDOFF.md` | 比 `docs/ROADMAP.md` 新 |
| 0.1.2 功能计划 | `docs/dev/PLAN-0.1.2-features.md` | 里程碑 M1/M2/M3 |
| 待办总表（P0–P2） | `docs/dev/PLAN-backlog.md` | **开工先看这份**；HANDOFF §5 与它对齐 |
| 代理开发纪律 | `docs/dev/AI-WORKFLOW.md` | 硬要求 |
| 工具使用者流程 | `SKILL.md` | 面向用户，不是开发者 |

`docs/` 下带版本号的文档（`RELEASE-v1.6.2.md`、`REVIEW-1.6.1.md`、`OPTIMIZE-CONSOLE-REVIEW.md` 等）
是 **1.x 版本线的存档**，其中"需要 .NET 8"之类的表述已过时，不要当作现状。
1.x 代码在分支 **`legacy/1.x`**（= tag `v1.6.2`），**停止维护**；不要往那条分支修 bug 或发版。

## 环境事实

- 本机 SDK `10.0.401`，WindowsDesktop 运行时 `10.0.12`。
- **GitHub Actions 可用**（`build` + `smoke`，push 到 `main` / 开 PR 时触发）。但 CI 在 `windows-latest`
  runner 上跑，比本机更容易撞上文件占用类瞬时失败——改完先本机全量绿，再看 CI；不要把 CI 的一次红
  直接归因于自己的提交，先比对是不是同两条已知用例。
- `gh` CLI 已登录，仓库为 `jidekaixin2dian/fps-tune`（公开）。
- Inno Setup 6 存在，路径由 `build-installer.ps1 -CheckOnly` 探测。

## 危险操作清单（动手前必须问用户）

| 操作 | 为什么危险 |
|---|---|
| 运行 `FpsTune.exe -Apply` / GUI 里点"应用所选" | 真的改本机注册表、电源计划、服务、启动配置 |
| 碰 `D:\FpsTune` | 那是**本机安装位**（当前 0.1.2-beta），不是源码；覆盖它等于换掉用户在用的程序 |
| 跑 `publish-release.ps1` / `build-installer.ps1` 后发布 | 产物会公开出现在 GitHub Release |
| force push / 重写 `main` 历史，或向 `legacy/1.x` 提交 | 破坏已推送历史或已冻结的 1.x |
| 删 `dist/` 以外的目录、`work/` 里的探针 | 探针是驱动层实验的原始依据 |

只读安全动作：`-Detect -Json`、`-Version`、`-ListRestore -Json`、`-Experiment -Simulate`、`dotnet test`。

## 工作区地图（这台机器上有三份，别搞混）

```
C:\Users\Aether\Documents\fpstune\review-3a060d1   ← 主开发工作区（用户 2026-09-21 确认，代码在这里）
C:\Users\Aether\Documents\GitHub\fps-tune          ← 次克隆，2026-09-01 建后停用；曾用来改 README 并推过 main
D:\FpsTune                                         ← 本机安装位（0.1.2-beta 构建），只读，别当源码
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
