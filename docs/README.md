# 文档索引

> 本仓库所有文档的总入口。**如果只有 5 分钟**，按下面的「先读什么」走。
>
> 文档分两类：**现状文档**（描述当前代码与流程，与事实不符即修）与
> **历史存档**（`docs/archive/`，只作考古，不代表现状）。

## 先读什么

| 你是谁 | 按这个顺序读 |
|---|---|
| **接手开发的 AI 代理** | `AGENTS.md` → `docs/HANDOFF.md` → `docs/dev/PLAN-backlog.md`，再按需查下表 |
| **人类贡献者** | `CONTRIBUTING.md` → `docs/ROADMAP.md` → `docs/dev/AI-WORKFLOW.md` |
| **只想用工具优化帧率** | `SKILL.md`（给 AI 代理的操作流程）或 `README.md`（手动使用） |
| **来帮忙测试的朋友** | `TESTING.md` |

## 现状文档

### 根目录

| 文档 | 用途 | 什么时候读 |
|---|---|---|
| `README.md` / `README.en.md` | 面向使用者的产品说明、下载表、架构概览 | 想知道这是什么、怎么下载 |
| `AGENTS.md` | **开发本项目的 AI 代理入口**：30 秒现状、硬纪律、危险操作清单、工作区地图 | 动手改代码前第一份 |
| `CONTRIBUTING.md` | 人类贡献者最短路径：环境、改动规则、提 PR 前检查 | 想提 PR |
| `RELEASE.md` | 发版流程：版本号单源、从干净提交构建、产物校验 | 要发版 |
| `TESTING.md` | 朋友测试流程与数据回传模板 | 组织外部测试 |
| `SKILL.md` | **使用本工具优化帧率的 AI 代理入口**（不是开发文档，别混用） | 帮用户调优电脑 |

### `docs/`

| 文档 | 用途 |
|---|---|
| `HANDOFF.md` | **项目现状与交接**：版本/分支、测试基线、未决事项、历轮要点。**现状数字以此为准** |
| `ROADMAP.md` | 产品定位、五条红线、版本节奏。**方向看这里，现状看 HANDOFF** |

### `docs/dev/`

| 文档 | 用途 |
|---|---|
| `AI-WORKFLOW.md` | 开发纪律（提交、验证、环境变量、写法约定）—— **硬要求** |
| `PLAN-backlog.md` | **待办总表（P0–P2）** —— 开工先看这份 |
| `PLAN-0.1.2-features.md` | 0.1.x 功能设计依据：M1/M2/M3 的开工依据与设计细节 |
| `PLAN-P2-1-catalog-i18n.md` | P2-1（catalog 说明文本英译）的架构调研与推荐改法 |
| `PLAN-P2-6-hot-options.md` | P2-6（热门优化项）的调研结论与红线预审：哪几项已落地、哪几项不做及理由 |
| `GUI_PLAN.md` | GUI 阶段规划（P2-3 的盘点对象） |
| `GUI_DESIGN_PROMPT.md` / `UI_VISION_CHECKLIST.md` | 界面设计提示词与视觉检查清单 |

## 历史存档 `docs/archive/`

1.x 版本线（**已停止维护**，冻结于 `legacy/1.x` = `v1.6.2`）的工作记录与评审。
**不代表现状**——其中的文件路径、版本号、依赖要求（例如"需要 .NET 8"）都可能已过期。
清单与说明见 `docs/archive/README.md`。

## 维护规则

- **文档与代码冲突时以代码为准**，并立刻修正文档——过时的交接文档比没有交接文档更危险。
- 每轮收工必须更新 `AGENTS.md` 与 `docs/HANDOFF.md` 并 `git commit`（见 `docs/dev/AI-WORKFLOW.md`）。
- 新增文档请放进上面对应的位置。一轮性的临时提示词写进根目录
  `HANDOFF_PROMPT_YYYY-MM-DD.md`（`.gitignore` 已排除，不入库）。
