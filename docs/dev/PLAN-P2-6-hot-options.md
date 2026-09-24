# P2-6 · 热门优化项逐项落地（调研 + 红线预审）

> 最后核对：2026-09-25
> 状态：**调研已落盘。6 项中：3 项已落地 / 2 项不做 / 1 项待落地**
> 依据：`work/NvApiDriverSettings.h`（NVIDIA 官方，SPDX MIT，Copyright 2019–2026，1459 行）
> 红线：`docs/ROADMAP.md` §产品定位 —— 只改系统层/驱动层配置，不碰游戏文件、不注入进程、
> 不读游戏内存、不与反作弊交互、不关引导虚拟化。
> 相关：`docs/dev/PLAN-backlog.md` P2-6；`docs/HANDOFF.md` §5

## 0. 为什么要有这份文档

`PLAN-backlog.md` 的 P2-6 长期写着"调研已做一轮"，但**调研结论从未落盘**——
2026-09-22 那轮的结论只散在 `HANDOFF.md` 的一句话和提交信息里。
后果是每个接手的人都要重新推导一遍"哪几项做了、为什么三重缓冲不做"。
本文把结论固定下来，后续只做"按表实施"，不再重复调研。

## 1. 六项现状总表

| 项 | 状态 | 官方设置（`work/NvApiDriverSettings.h`） | 落点 |
|---|---|---|---|
| 三重缓冲关 | **不做（无 D3D 项）** | 只有 `OGL_TRIPLE_BUFFER_ID = 0x20FDD1F9`（L179，字符串 L49）——**OpenGL 专用** | `ApplyVSyncMode` 注释 + UI ToolTip 已说明 |
| 垂直同步强制关 | **已落地** | `VSYNCMODE_ID = 0x00A879CF`（L285）；`VSYNCMODE_FORCEOFF = 0x08416747`（L1422） | `ApplyVSyncMode` + UI 第 5 行 + 一键 + 竞技预设 |
| 各向异性 16x | **已落地** | `ANISO_MODE_SELECTOR_ID = 0x10D2BB16` / `ANISO_MODE_LEVEL_ID = 0x101E61A9`（L187-188）；16x = `ANISO_MODE_LEVEL_MAX = 0x10`（L594） | `ApplyAnisoLevel` + UI 第 4 行 + 一键 + 竞技预设 |
| 着色器缓存 | **已落地** | `PS_SHADERDISKCACHE_ID = 0x00198FFF`（L273）；`..._ON = 0x1`（L1338） | `ApplyShaderDiskCache` + UI 第 6 行 + 一键 + 竞技预设 |
| 图像锐化 | **不做（DRS 路径不存在）** | 官方头文件**无任何** SHARPEN / SHARP / DENOISE / NIS 项（已对上游核实，见 §3） | —— |
| 数字振动 55–60 | **能力已有，缺"推荐档位"** | 非 DRS 项：显示级 DVC（`GetDVCInfoEx` / `SetDVCLevelEx`） | `DigitalVibranceService` + UI 滑块；见 §4 |

## 2. 已落地的三项：实现与边界（**勿重复做**）

- 三项常量都在 `FpsTune.Wpf/Services/DisplayQualityService.cs` 的「P2-6 热门面板项」块（L94–L103），
  SettingID / 枚举值均取自官方头文件。
- 三项都已接入三条路径：
  1. 显示页「驱动 3D 设置」卡（`Views/DisplayQualityView.xaml` 的 Grid.Row 4 / 5 / 6）；
  2. 一键优化 `OneClickOptimizer.ApplyGpu1070TiPack`（L28–L30）；
  3. 竞技预设 `DisplayQualityService.ApplyCompetitivePreset`（L230–L232）。
- 写入机制：走 NVIDIA DRS profile，**首次写入前备份全部受管项，`Save` 成功才落盘备份**
  （避免留下假备份），需要管理员权限。
- **诚实边界（不要写成"系统级开关"）**：这是**按游戏 exe 的驱动 profile 设置**，
  只在游戏已登记 profile、或被本工具登记 profile 时生效；UI 与文档都不要承诺帧数。

## 3. 图像锐化：为什么不做（附核实方法，避免下一轮重查）

**结论：不能通过 DRS 落地，因此不做。**

1. 本地官方头文件 `work/NvApiDriverSettings.h`（NVIDIA SPDX MIT，2019–2026，1459 行）
   **不含任何** `SHARP` / `SHARPEN` / `SHARPENING` / `DENOISE` / `IMAGE` / `NIS` / `FILM_GRAIN` 条目。
2. **已对上游核实**（2026-09-25）：抓取 `NVIDIA/nvapi` 仓库 `main` 分支的 `NvApiDriverSettings.h`
   复核，结论一致 —— 头文件里唯一含 "scaling" 的是 DLSS 的缩放比率
   （`NGX_DLSS_RR_OVERRIDE_SCALING_RATIO_ID` / `NGX_DLSS_SR_OVERRIDE_SCALING_RATIO_ID`），与锐化无关。
3. 最接近的条目是 `NV_QUALITY_UPSCALING_ID = 0x10444444`（L233，字符串 "NVIDIA Quality upscaling"），
   取值只有 `OFF(0)` / `ON(1)`（L963–967）—— **这是"图像缩放"开关，不是锐化强度**。
   **不要拿它冒充"图像锐化"**（红线「可信透明」）。
4. 驱动控制面板里的"图像锐化"滑杆、以及 NIS 的 `NVSharpen` / `NVScaler`，
   属于**游戏内 SDK 集成**或控制面板私有路径；公开 DRS 接口没有，本工具不碰。

**处置**：P2-6 的该项标为**"不做（DRS 无此设置）"**并保留本节作为理由。
若用户另有要求，只能另立新条目，且必须先证明存在公开可用接口（当前无）。

## 4. 数字振动 55–60：可做，且是小改动

- **现状**：`DigitalVibranceService.GetState()` / `SetPercent(int)` / `Restore()` 已完整；
  显示页有滑块（`Views/DisplayQualityView.xaml.cs` 的 `RefreshVibrance` / `ApplyVibrance`）。
  本机实测量纲 **0–100，默认 50**。
- **关键判断**：社区说的"55–60"**不是一个可写入的模式**，而是**推荐数值区间**。
  所以正确做法是**在滑块旁给出推荐区间提示 / 一个"推荐 55–60%"快捷按钮**，
  **不是**新增一个名为"数字振动 55–60"的开关（那样会把区间硬编成一个魔数）。
- **诚实边界（必须保留现有文案）**：DVC 是**整屏全局**生效（桌面 / 网页 / 游戏都变），
  不是只在游戏内；且与 ICC 滤镜可能叠加，UI 需说明。
- **红线预审：通过**。DVC 是显示级 NVAPI 调用，写前备份当前档位、可一键还原；
  不碰游戏文件、不注入进程、不读游戏内存、不与反作弊交互。

## 5. 红线预审（统一结论）

| 红线 | DRS 项（VSync / AF / 着色器缓存） | DVC（数字振动） |
|---|---|---|
| 不碰游戏文件 | ✅ 改的是驱动 profile，不动游戏目录 | ✅ |
| 不注入进程 / 不读游戏内存 | ✅ | ✅ |
| 不与反作弊交互 | ✅ | ✅ |
| 不关引导虚拟化 | ✅ | ✅ |
| 写前备份 / 可还原 | ✅ 引擎已实现（受管项快照） | ✅ 已实现（档位备份） |
| 数据说话（不编收益数字） | ✅ UI 只写机制，不写 "+N FPS" | ✅ |

图像锐化不进入预审表 —— 它没有可落地的接口，不是"是否合规"的问题。

## 6. 本文件之后的动作

1. `PLAN-backlog.md` 的 P2-6 改为**按项标注**（3 已落地 / 2 不做 / 1 待落地），并指向本文。
2. 实施「数字振动推荐档位」（§4）：改 UI + 资源键。
   **改完必须真启动一次 GUI 并观察存活**（进程活着 + `error.log` 字节数不变）——
   XAML 绑定错误编译与单测都抓不到，本仓库已因此闪退过一次。
3. 图像锐化：按 §3 标为**不做**；如用户另有要求，另立条目。
4. 三重缓冲：维持不做（只有 OpenGL 项），在 UI ToolTip 继续说明"关 VSync 即可"。
