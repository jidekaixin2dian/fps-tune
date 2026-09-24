# 代码与工作区健康度（现状底数）

> 最后核对：2026-09-25（全量实测，命令与数字都可复现）
> 用途：接手时**先看这一份**，不要重新量一遍。数字过期就改这份，别另起文档。
> 相关：`docs/dev/AI-WORKFLOW.md`（纪律）、`docs/dev/PLAN-backlog.md`（待办）

## 0. 一句话结论

**工作区干净，代码不是屎山。** 主要债务是 i18n 文案（已知、已暂停）与几个偏大的
View code-behind；**没有** TODO/HACK 堆积、**没有**吞异常、**编译 0 警告**。
**不要为"看起来整洁"做大重构**——本文件列出的是有证据的问题，其余保持现状。

## 1. 工作区体积（`du -sh`，含未跟踪）

| 目录 | 体积 | 说明 |
|---|---|---|
| `dist/` | **370 M** | 构建产物，**已在 `.gitignore`**（0.1.2/0.1.3/0.1.4 三版） |
| `FpsTune.Wpf/` | 20 M | 含 `bin/`+`obj/`（已忽略）；源码本身很小 |
| `FpsTune.Wpf.Tests/` | 7.7 M | 同上 |
| `work/` | 6.0 M | 驱动层探针与一次性脚本，**已忽略**（AGENTS.md 禁止删） |
| 其余 | < 400 K | `assets` / `docs` / `catalog` / 根脚本 |

- **tracked 文件仅 168 个**；`.git` 16 M。
- **未跟踪且未被忽略的文件 = 0**（`git status --porcelain -uall` 为空）——
  即"工作区干净"是字面意义上的干净，没有游离文件。
- 结论：**体积不构成问题**。`dist/` 占 370 M 是历史发布产物，可随时重生成；
  真要瘦身只需删旧版本目录（属可删范围），但保留 0.1.3 资产有核对价值，暂不删。

## 2. 代码度量（`FpsTune.Wpf`，排除 `obj`/`bin`）

| 指标 | 值 | 判读 |
|---|---|---|
| 生产代码 | **22,985 行**（`.cs` + `.xaml`） | 规模小，单人可通读 |
| 测试代码 | 4,797 行 / 279 条 | 比例健康 |
| 方法总数 | 590 | — |
| **方法长度中位数** | **14 行** | 健康（面条代码的中位数会很高） |
| 方法 ≥ 60 行 | 29 个（4.9%） | 可接受 |
| 方法 ≥ 100 行 | 7 个 | 见 §3.2 |
| 最长方法 | 180 行（`ExperimentRunner.RunTestGroupAsync`） | 线性编排，见 §3.2 |
| 编译警告 | **0**（2026-09-25 由 6 个清零） | 底线：**新增代码必须 0 警告** |
| `TODO` / `HACK` / `FIXME` | **0** | — |
| 空 `catch` 吞异常 | **0** | 唯一的能力探测 `catch` 已注明理由 |

最大文件（前 6）：`App.xaml` 822（资源字典，非逻辑）、`PerformanceSessionStore.cs` 804、
`DisplayQualityView.xaml.cs` 769、`ExperimentRunner.cs` 763、`OptimizeView.xaml.cs` 744、
`AbExperimentView.xaml.cs` 725。

## 3. 已知债务（按值得做的顺序）

### 3.1 i18n：985 处硬编码中文 / 51 个文件 —— **用户已决定暂停**

- 基线 `FpsTune.Wpf.Tests/I18nBaseline.txt` 就是待办清单（按数量降序看）。
- **不要主动重启**；重启条件与做法见 `PLAN-backlog.md` 的 P2-7。
- 底线：**新代码不要新增硬编码中文**（棘轮会直接失败）。
  > 例外：把既有文案**搬到新文件**时按"新文件补行、旧文件减掉"改基线，总数只许降不许升。

### 3.2 30 个 ≥60 行的方法 —— **多数不必拆**

逐个人工看过最长的几个（`RunTestGroupAsync` 180、`BackupService.Capture.CreateBackupRecords`
167、`DiagnosticReportExporter.AddMarkdownSummary` 154、`CliHost.ParseOptions` 125、
`AutoProfileService.PollAsync` 115、`AbExperimentView.ApplyStepResult` 108、
`PerformanceSessionService.Stop` 103），形态都是
**"线性编排 + 守卫式早返回"**（编号步骤 / 参数分派 / 表格化输出），不是嵌套面条。
**为拆而拆只会制造更多跨方法状态传递。** 只有将来某段真的长出多层嵌套再动。

### 3.3 6 个 View code-behind 偏大，其中混有非 UI 逻辑 —— **可做，但需单独一轮**

| 文件 | 行数 | 里面混着的非 UI 逻辑 |
|---|---|---|
| `DisplayQualityView.xaml.cs` | 769 | 4 张卡片各自的 Refresh/Apply/Restore 编排（本身较薄，但重复的"确认框 + busy 守卫"模式出现 8 次） |
| `OptimizeView.xaml.cs` | 744 | **FlowDocument 构建约 200 行**（`NewPara`/`R`/`SetPlain`/`SetItemListDoc`/`SetItemDetail`/`SetApplyResult`/`SetRawMonospace`） |
| `AbExperimentView.xaml.cs` | 725 | **JSON 解析与报告组装**（`TryReadRequiredMetric`/`TryComposeReportFile`/`TryGet`）+ 图表绘制约 120 行（绘制属视图职责，可不动） |

- 可提取的：`OptimizeView` 的文档渲染 → 独立渲染类；`AbExperimentView` 的 JSON/报告组装 → Service。
- **为什么没直接做**：这会改动 3 个页面的结构，属于设计变更而非机械重构；
  且按仓库纪律必须"真切到该页验证渲染"（见 `AI-WORKFLOW.md` §四 第 2 条）。
  建议单独立一轮，先确认要不要做、做到什么程度。

### 3.4 DRS / NVAPI 写入路径无法在本机自动化验证

- 写入（Apply/Restore）需要管理员且会改真实驱动配置，属 AGENTS.md 危险操作清单。
- 现有保障：只读真机路径（DVC 读、DRS 读）可自动验证；写入逻辑由单测覆盖
  （`DisplayQualityTests` 用 `FakeNvdrsApi`）。**改动互操作层时，务必两条只读路径都跑一遍。**

## 4. 底线（新增代码请守这几条）

1. **0 编译警告**（`dotnet build -c Release` 应报 `0 个警告 0 个错误`）。
2. **不新增硬编码中文**；界面文案走 `{DynamicResource Str.Xxx}` / `Str.T("Str.Xxx")`。
3. **业务逻辑不进 code-behind**（新逻辑放 `Core/` 或 `Services/`）。
4. 互操作（NVAPI / P/Invoke）**只有一处实现**：NVAPI 引导统一在 `Services/NvapiNative.cs`，
   不要再在别的服务里自己 `LoadLibrary`。
5. 改 XAML 后**真切到那一页**验证渲染（页面懒加载，只看主窗口存活不算）。
