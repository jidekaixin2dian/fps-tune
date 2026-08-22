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
> 并刻意做减法：无 GUI、无遥测、无更新器，一个脚本即插即用。

## 快速开始（AI Agent）

将下面的指令发送给支持执行 PowerShell 的 Agent：

```text
读取 <delta-skill 目录>/SKILL.md 并按其中的流程帮我优化《三角洲行动》的帧率
```

## 快速开始（命令行）

```powershell
# 1. 检测（只读，安全）
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Detect -Json

# 2. 应用（先向用户说明并征得同意，再加 -Force）
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Apply -Preset balanced -Force -Json

# 3. 还原
powershell -NoProfile -ExecutionPolicy Bypass -File delta-optimizer.ps1 -Restore -Json
```

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
├── tuning-experiment.ps1 # A/B 自动调优实验（基线 + 候选组 + 规则决策 + CSV 导出）
├── SKILL.md              # AI Agent 调用说明（流程 + 红线）
├── README.md             # 中文说明
├── README.en.md          # English readme
└── LICENSE               # MIT
```

## A/B 真实采样结果（靶场）

测试条件：i9-13900HX + GTX 1050 Ti 笔记本，Windows 11，官方 PresentMon（`winget install Intel.PresentMon.Console`），
固定靶场场景，每组 3 次 × 90 秒采样；基线稳定性 CV 1.18%（阈值 ≤0.05，达标）。

| 组 | 平均 FPS | 1% low | P99 (ms) | 卡顿 | 结论 |
|---|---|---|---|---|---|
| 基线 | 268.29 | 205.24 | 4.88 | 0 | — |
| group-1 调度组 | 267.57 | 207.19 | 4.83 | 0 | 无明显收益（平均 -0.3%，1% low +1.0%），已自动还原 |
| group-2 后台组 | 267.40 | 191.58 | 5.30 | 0 | 无明显收益（平均 -0.3%，1% low -6.7%），已自动还原 |
| group-3 电源组 | 265.30 | 196.69 | 5.09 | 0 | 无明显收益（平均 -1.1%，1% low -4.2%），已自动还原 |

结论：三组低风险候选在本机靶场均未达到保留规则（平均 ≥2% 或 1% low ≥5%），
全部按规则自动还原，系统设置已恢复到采样前状态。该结果仅代表本机/本场景。

## 开发状态

- [x] `-Detect` 冒烟测试通过（真实机器：i9-13900HX + GTX 1050 Ti 笔记本 + 三角洲行动已安装）
- [x] 22 项优化 + 3 项体检全部实现
- [x] `-Apply` / `-Restore` 真实往返测试通过（transparency-off 往返，备份→消费→还原闭环）
- [x] A/B 自动调优（tuning-experiment.ps1）：基线稳定性判定 + 3 候选组 + 规则决策 + 自动还原 + CSV 导出（dry-run 全链路验证通过）
- [x] A/B 真实采样（靶场实测：基线 268.29 FPS / CV 1.18%，三组候选均无明显收益并自动还原）
- [ ] 更多游戏路径检测兜底（WeGame / Steam 变体）
- [ ] GUI（下一阶段，视反馈而定）

## 许可

MIT License。代码完全原创，与任何现有工具的代码/文档无衍生关系。
