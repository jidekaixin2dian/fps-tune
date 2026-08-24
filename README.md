# 三角洲行动 · 系统层帧率优化（delta-force-tune）

面向《三角洲行动》玩家的 **Windows 系统层**帧率优化技能：一个原创的 PowerShell 引擎
（`delta-optimizer.ps1`）+ 一份通用 Agent 技能说明（`SKILL.md`），任何能执行 PowerShell
的 AI 助手（Claude Code / Codex / WorkBuddy / 豆包等）都可以按流程调用：
**检测 → 解释 → 确认 → 执行 → 汇报**。

- ✅ **系统层，可还原**：只改 Windows 设置（注册表 / 电源计划 / 服务 / 启动配置），
  每次写入前自动备份原值，支持一键还原。
- ✅ **不碰游戏**：不修改游戏目录文件、不注入进程、不与反作弊交互、不关引导虚拟化、
  不做显卡伪装。
- ✅ **纯 PowerShell 5.1**：Windows 10/11 自带，零依赖、零安装。
- ✅ **JSON 输出**：`-Json` 模式输出结构化结果，Agent 友好。
- ✅ **MIT 开源**：代码完全原创（Clean-room），可自由使用、修改、商用。

> 项目背景：市面上的同类工具（如 DeltaForceBooster）采用专有 EULA，禁止修改与再分发。
> 本项目以**公开的功能清单**为参考，代码与文档完全自写，采用宽松许可证开源，
> 并刻意做减法：无遥测、无更新器；GUI 功能版可选，核心仍是一个脚本即插即用。

## 快速开始（AI Agent）

项目地址：<https://github.com/jiaxindeyang-a11y/delta-force-tune>

如果 Agent 已经在仓库目录里，直接发：

```text
读取当前目录下的 SKILL.md，严格按其中的流程帮我优化《三角洲行动》的帧率。
```

如果 Agent 还没有本项目，先让它克隆仓库，再读 SKILL.md：

```text
先执行 git clone https://github.com/jiaxindeyang-a11y/delta-force-tune.git，
然后读取克隆目录里的 SKILL.md，严格按其中的流程帮我优化《三角洲行动》的帧率。
```

> 不要只发“帮我优化帧率”：AI 不知道项目在哪，很容易凭空发挥。必须让 Agent
> 先拿到本项目，再以 SKILL.md 作为唯一操作流程。

## 快速开始（命令行）

```powershell
# 1. 检测（只读，安全）
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Detect -Json

# 2. 应用（先向用户说明并征得同意，再加 -Force）
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Apply -Preset balanced -Force -Json

# 3. 还原
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Restore -Json
```

## 快速开始（GUI 功能版）

- 推荐下载安装包：<https://github.com/jiaxindeyang-a11y/delta-force-tune/releases>
- 便携版：解压 `DeltaForceTune-Portable.zip` 后运行 `DeltaForceTune.exe`

或本地构建：

```powershell
cd <项目目录>
.uild-wpf.ps1 -Mode Build
.\DeltaForceTune.Wpfin\Release
et8.0-windows\DeltaForceTune.exe
```

当前 WPF GUI 已完成：检测、优化、A/B 实验、朋友测试、备份/日志、设置、
深色/亮色/跟随系统主题、现代化自定义弹窗、首次启动“检测并优化”引导。

## 命令一览

| 命令 | 说明 |
|---|---|
| `-Detect [-Json]` | 只读检测：硬件、游戏路径、22 项当前状态、3 项体检、驱动手动清单 |
| `-Apply [-Items id1,id2 \| -Preset full\|balanced\|safe-only] [-Force] [-Json]` | 应用优化；必须带 `-Force`（表示已获用户同意） |
| `-Restore [-Items id1,id2] [-Json]` | 还原（全部或按项） |
| `-ListRestoreItems [-Json]` | 列出备份中可还原的项 |
| `-GamePath "主程序路径"` | 手动指定游戏主程序（fso-off / gpu-pref / game-priority 依赖） |

## 预设

| 预设 | 内容 | 适用 |
|---|---|---|
| `balanced` | 14 项，副作用小，不含服务禁用/休眠 | 默认推荐 |
| `full` | 全部 22 项（含 sysmain-off / wsearch-off / hibernate-off 等） | 追求极致 |
| `safe-only` | 5 项纯当前用户设置，通常无需管理员 | 保守 |

## 安全与风险

- 所有可还原改动写入前先备份到 `%LocalAppData%\DeltaOptimizer\backup\backup-<时间戳>.json`，
  还原是逐项按原值恢复（包括删除"原本不存在"的值）。
- 需要管理员权限的项，在非管理员会话下会明确报错，不会静默失败。
- 效果因硬件/驱动/系统版本而异，**不承诺固定帧数**；争议项默认不勾选。
- 本工具不包含：显卡型号伪装、游戏文件修改、进程注入、虚拟化关闭、反作弊交互。
  这些是反作弊红线，做它们与自毁无异。
- 没有代码签名证书，SmartScreen 可能提示未知发布者——这是个人开源项目的正常现象。

## 目录

```
delta-skill/
├── delta-optimizer.ps1   # 核心引擎（PowerShell 5.1，单文件，含中文注释）
├── delta-gui.ps1         # GUI 功能版（WinForms，视觉后续交给 V4 flash version）
├── tuning-experiment.ps1 # A/B 自动调优实验（基线 + 候选组 + 规则决策 + CSV 导出）
├── friend-test.ps1       # 朋友测试：一键生成测试记录表（Markdown + CSV）
├── SKILL.md              # AI Agent 调用说明（流程 + 红线）
├── TESTING.md            # 朋友测试指南（完整 A/B 或只有帧率/游戏加加数据）
├── GUI_PLAN.md           # GUI 阶段合规功能规划
├── GUI_DESIGN_PROMPT.md   # 给 V4 flash vision 的视觉设计提示词
├── README.md             # 中文说明
├── README.en.md          # English readme
└── LICENSE               # MIT
```

## 开发状态

- [x] `-Detect` 冒烟测试通过（真实机器：i9-13900HX + RTX 5070 Ti Laptop GPU 笔记本 + 三角洲行动已安装）
- [x] 22 项优化 + 3 项体检全部实现
- [x] `-Apply` / `-Restore` 真实往返测试通过（transparency-off 往返，备份→消费→还原闭环）
- [x] A/B 自动调优（tuning-experiment.ps1）：基线稳定性判定 + 3 候选组 + 规则决策 + 自动还原 + CSV 导出（dry-run 全链路验证通过）
- [ ] A/B 真实采样（待更多机器 / 朋友数据）
- [ ] 更多游戏路径检测兜底（WeGame / Steam 变体）
- [x] GUI 功能版（`delta-gui.ps1`，视觉待 V4 flash version；规划见 [GUI_PLAN.md](GUI_PLAN.md)）

## 许可

MIT License。代码完全原创，与任何现有工具的代码/文档无衍生关系。
