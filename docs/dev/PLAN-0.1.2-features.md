# 0.1.2 功能开发计划 · 显示与画质（DLSS / ICC 滤镜 / 刷新率）

> 状态：待实施（用户已定题，本文档为开工依据）
> 前置阅读：`../ROADMAP.md`（五条红线）、`HANDOFF_PROMPT_2026-09-13.md`（仓库现状与协作纪律）
> 基线：beta @ `ce62f90`，221/221 测试，.NET 10，v0.1.1-beta 已发布
> **发版流程铁律：先本机构建 + 部署 D:\FpsTune 给用户过目，用户认可后才发 GitHub Release。**

---

## 0. 红线预审（每个功能先过这一关）

| 功能 | 红线二（零侵入） | 红线一（可还原） | 红线三（数据说话） | 结论 |
|---|---|---|---|---|
| DLSS 模型切换 | **DLL 替换方案违规，排除**；驱动配置文件覆盖不碰游戏文件，合规 | 记录原 profile 设置，可整体还原 | 画质/性能差异由用户游戏内自验，界面只陈述机制 | ✅ 走驱动覆盖 |
| ICC 滤镜 | 安装 .icc + 关联显示器设备，纯系统层 | 切换前备份当前关联 profile，一键还原 | 说明是色彩曲线变换，不承诺"提升战绩" | ✅ |
| 刷新率拉满 | ChangeDisplaySettingsEx，纯系统层 | 备份当前显示模式 | 检测页已能读到当前/最高刷新率，差距即收益依据 | ✅ |

**明确不做**（违规或超范围）：替换游戏目录 nvngx_dlss.dll（改游戏文件）、关闭引导虚拟化、注册表清理类"优化"。

---

## 1. 功能 A：DLSS 模型切换（三角洲优先，机制全游戏通用）

### 背景
DLSS Super Resolution 有 CNN 与 Transformer 两代模型（及不同 preset），画质与性能开销不同，
三角洲玩家社区常用切换。目标：不改游戏文件、不注入进程，在驱动层做应用级覆盖。

### 机制（按优先级）
- **方案 A（主选）：NVAPI DRS 驱动配置覆盖。**
  P/Invoke `nvapi64.dll`：`NvAPI_Initialize` → `NvAPI_DRS_CreateProfile/Application`（按游戏 exe 名，如
  `DeltaForceClient-Win64-Shipping.exe`）→ `NvAPI_DRS_SetSetting` 写 DLSS 覆盖项 → `NvAPI_DRS_SaveSettings`。
  还原 = 删除本工具创建的 profile / 恢复备份的设置值。不碰游戏文件，完全可逆。
  ⚠️ **诚实标注**：具体 SettingID（DLSS 模型/preset 覆盖）在社区资料中版本敏感，实现时必须以
  NVIDIA 官方 `nvapi.h` 头文件核对，禁止凭记忆硬编码；找不到可靠 SettingID 时如实回退到方案 B 调查，
  不得假装支持。
- **方案 B（调查项，不承诺）**：`HKLM\SOFTWARE\NVIDIA Corporation\Global\NGXCore` 注册表键——社区有流传
  但与驱动版本强相关。仅在方案 A 受阻时调查，结论写回本文档。

### 设计要点
- 仅 NVIDIA RTX 显示：用 `HardwareInfoService` 的 GPU 厂商判断；非 N 卡用户界面隐藏并说明。
- 选项模型：模型（自动 / CNN / Transformer）+ 档位提示由游戏内设置（不在驱动层强行改 quality）。
- 作用范围：按当前定位的游戏 exe 建 profile（复用 AppState.GamePath）；换游戏=换 profile。
- 备份：进入功能时读取并持久化该 exe 现有 DRS 设置（可能为空），"还原"删除覆盖并恢复原值。
- UI 落点：新"显示与画质"页（见 §4）。

### 验收
真机 RTX 5070 Ti：设置 → 游戏内视觉可辨 → 还原 → 游戏内回到默认。NVIDIA App/Inspector 里能看到
profile 出现与消失（旁观证据）。游戏内主观画质对比由用户验收。

---

## 2. 功能 B：ICC 滤镜 + 内置预设

### 机制（mscms P/Invoke，全部有 Win32 文档）
1. 生成/装载 .icc 到系统色彩目录：`InstallColorProfileW`；
2. 取主显示器设备名：`EnumDisplayMonitors` → `GetMonitorInfo`（`MONITORINFOEX.szDevice`）；
3. 切换：`AssociateColorProfileWithDeviceW` 关联目标 .icc；
4. 还原：关联回备份的原始 profile（进入功能时用 DetectionService 既有读取逻辑取当前 profile 并持久化）。
   现有代码锚点：`Core/DetectionService.cs` 颜色配置检查（只读部分直接复用）。

### 内置预设（程序化生成最小 ICC v2 profile：RGB 矩阵 + gamma/曲线 tag，运行时生成、不捆绑第三方文件）
| 预设 | 曲线特征 | 适用 |
|---|---|---|
| 标准 sRGB | 原始关联（还原项） | 校色党/日常 |
| FPS 鲜艳 | 轻微提饱和 + 提对比 | 通用竞技 |
| 暗部增强 | 阴影段 gamma 上抬 | 死角优先 |
| 去雾 | S 型对比曲线 + 微降蓝 | 雾天/灰蒙场景 |

诚实边界（写进 UI）：程序生成的 ICC 是简单曲线变换，效果弱于专业校色；对色准敏感的用户应保留
原 profile。**切换是系统全局的（影响桌面观感）**——确认弹窗必须说明，且任何页面一键可回"标准"。

### 设计要点
- 第一版只作用主显示器；多显示器支持放后续。
- 预设文件生成器做成纯函数（输入参数 → byte[] ICC），走 `AtomicFile` 落盘到
  `%LOCALAPPDATA%\FpsTune\icc\`，便于测试断言字节结构与合法性（可用 Windows API `IsValidColorProfileW` 验证）。

---

## 3. 功能 C：刷新率一键拉满

检测页已能读当前/最高刷新率（`EnumDisplaySettings`），补"设置"动作：
枚举主显示器支持的最高刷新率 → `ChangeDisplaySettingsExW` 应用 → 备份当前 DEVMODE 关键字段
（分辨率/刷新率/位深）供还原。当前已是最高则显示"已达上限"。UI 放"显示与画质"页顶部（最简单、最稳）。

---

## 4. UI 落点：新页"显示与画质"

导航在"检测"与"优化"之间插入。布局沿用控制台卡片风（纯平、发丝分割线）：

```
显示与画质
├─ 刷新率：当前 320Hz / 最高 320Hz [一键拉满] [还原]
├─ DLSS 模型（仅 N 卡）：当前模型 · [自动|CNN|Transformer] [还原]（N/A 卡显示不支持说明）
└─ ICC 滤镜：当前关联 xxx.icc · 预设四卡 [应用] [还原原始]
```

每块独立备份/还原，状态栏如实显示"未应用/已应用/还原失败"。

---

## 5. 里程碑（每个可独立发布，逐步来）

| 里程碑 | 内容 | 风险 |
|---|---|---|
| M1 | 显示与画质页框架 + 刷新率拉满 | 低（纯 Win32，既有代码多） |
| M2 | ICC 滤镜 + 预设生成器 | 中（mscms 互操作 + ICC 二进制格式） |
| M3 | DLSS NVAPI DRS 覆盖 | 高（NVAPI 未公开文档部分，SettingID 核对；受阻则回退调查项并如实告知） |

每个里程碑：单测（互操作层用注入点隔离，不碰真实系统）→ 真机 → **部署 D:\FpsTune 给用户过目 → 认可后才发 Release**（0.1.2-beta 起版）。

## 6. 测试要点（开工前约定）

- ICC 生成器：纯函数测试（头字段/长度/tag 合法性 + `IsValidColorProfileW` 冒烟）。
- mscms/ChangeDisplaySettings/NVAPI 调用层：接口抽象 + 测试注入（沿用 `*Override` 约定 + 串行 Collection）；
  真实写入路径标"未自动化，需真机验收"。
- DLSS profile：只允许作用于"本工具创建的 profile"（命名加 FpsTune 前缀），还原绝不删除用户自建 profile。

## 7. 本文档的边界

- SettingID、NGXCore 键值等未核实细节一律标注"实现时核对"，不作为承诺。
- 功能范围以用户后续指派为准；本文档只是可执行依据，不是授权书。
