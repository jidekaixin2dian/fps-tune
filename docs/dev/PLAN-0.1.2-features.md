# 0.1.2 功能开发计划 · 显示与画质（N 卡驱动设置 / DLSS / ICC 滤镜）

> 状态：**进行中** —— M2（ICC 滤镜 + 内置预设）已完成并全绿；M1 的 DLSS 模型覆盖代码在但未启用；M3 未开始。
> 里程碑明细见 `docs/HANDOFF.md` §3（现状以那里为准，本文只保留开工依据与设计细节）。
> 前置阅读：`../ROADMAP.md`（五条红线）、`../../AGENTS.md`（代理协作纪律）、`../HANDOFF.md`（仓库现状）
> 原基线：beta @ `ce62f90`，221/221 测试，.NET 10，v0.1.1-beta 已发布；当前基线已推进到 248/248
> **发版流程铁律：先本机构建 + 部署 D:\FpsTune 给用户过目，用户认可后才发 GitHub Release。**

---

## 0. 需求依据（2026-09-13 平台检索）

> 小红书/抖音无直连路由（无登录态，按技能规则不自动登录），采用等效路径：
> Exa 命中的**抖音高播教学完整字幕** + B 站/攻略站同类内容交叉验证。同一批玩家群体。

玩家最常求的"必改设置"高频清单（按出现频次）：

| 玩家需求 | 内容原话/依据 | 落点 |
|---|---|---|
| **N 卡 DLSS 模型预设** | "大力水手优设模型预设里边用 K 模型……K 会更稳定，画质提升特别多"（两个独立信源点名 K 预设） | 功能 A |
| **按游戏 exe 的驱动级 3D 设置** | "程序设置→添加三角洲主程序 exe→纹理过滤选性能、电源管理选最高性能优先、平滑处理透明度 4x（防激光马赛克）" | 功能 A（同一 DRS 基建） |
| **数字振动（色彩鲜艳度拉 70-80）** | "拉到七十到八十……画面会更鲜艳，很多主播都拉的这里" | 功能 A（DRS 数字振动） |
| **输出颜色 10bpc / RGB 完整** | "输出颜色里有十 bpc 就选十 bpc" | 功能 A（DRS 输出项，二期） |
| **N 卡锐化 / 游戏内锐化** | "锐化拉个六十，画面不那么模糊" | 功能 A 二期（驱动锐化） |
| 禁用全屏优化 / HAGS / 游戏模式 | 教学必提 | ✅ 已有（fso-off/hags/game-mode） |
| 游戏内画质档位（纹理极致、其余最低等） | 教学主线 | ❌ 不做——改游戏配置文件违反红线二，且游戏内可自行设置 |

色彩类需求的补充说明：数字振动（N 卡驱动层、仅游戏内生效）与 ICC 滤镜（系统层、全桌面生效）
是互补关系；A 卡/Intel 用户只能走 ICC。功能 B 因此保留。

刷新率一键拉满：**用户明确移除**，本期不做。

---

## 0.5 红线预审（每个功能先过这一关）

| 功能 | 红线二（零侵入） | 红线一（可还原） | 红线三（数据说话） | 结论 |
|---|---|---|---|---|
| N 卡驱动设置/DLSS 切换 | **DLL 替换方案违规，排除**；驱动配置文件覆盖不碰游戏文件，合规 | 记录原 profile 设置，可整体还原 | 画质/性能差异由用户游戏内自验，界面只陈述机制 | ✅ 走驱动覆盖 |
| ICC 滤镜 | 安装 .icc + 关联显示器设备，纯系统层 | 切换前备份当前关联 profile，一键还原 | 说明是色彩曲线变换，不承诺"提升战绩" | ✅ |

**明确不做**（违规或超范围）：替换游戏目录 nvngx_dlss.dll（改游戏文件）、关闭引导虚拟化、注册表清理类"优化"。

---

## 1. 功能 A：N 卡驱动级游戏画质设置（DLSS 预设 / 数字振动 / 纹理过滤 / 电源 / 低延迟）

### 背景
DLSS Super Resolution 有 CNN 与 Transformer 两代模型（及不同 preset，社区点名 K 预设）；
数字振动、纹理过滤、电源管理、平滑处理透明度、低延迟模式是"按游戏 exe 配 N 卡"教学的核心内容。
目标：不改游戏文件、不注入进程，在驱动层做应用级覆盖——这些设置全部走同一个 NVAPI DRS 基建，
一次互操作层投入，覆盖整组高频需求。

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
- 选项模型（分期交付）：
  - 一期：DLSS 预设（自动/CNN/Transformer/K）+ **数字振动**（0-100 滑条，社区档位 70-80）——需求最硬的两项；
  - 二期：纹理过滤质量（性能）、电源管理（最高性能优先）、平滑处理-透明度（4x）、低延迟模式；
  - 三期（调查后定）：输出色彩 10bpc/RGB、驱动锐化（属输出/显示通道，DRS 之外可能要走别的 API，实现时核实）。
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

> **⚠️ 2026-09-14 真机探针修订（Windows 11 26200.9445，RTX 5070 Ti Laptop）**：
> 上面第 3、4 步的原始机制在该系统上不可用，已按探针证据改为下述机制（实现见
> `Services/IccSystemApi.cs` 注释）：
> - `AssociateColorProfileWithDeviceW`：`\\.\DISPLAY1` 形式返回 FALSE；纯文件名 + `"DISPLAY1"`
>   返回 TRUE，但实际写入的是**捕获设备（Capture）关联列表**，显示器 profile 不变——接口行为与文档不符；
> - `WcsSetDefaultColorProfile`：各设备名形式均返回 TRUE 但静默无操作；
> - `SetICMProfile`：返回 TRUE 但无操作；
> - `ColorProfileGetDisplayList`：访问冲突（0xC0000005），不可用；
> - **可用机制**：`ColorProfileSetDisplayDefaultAssociation(CURRENT_USER, CPT_ICM, CPST_NONE, LUID, sourceID)`
>   —— 行为是把 profile 追加进 per-user 显示关联列表末尾，**默认 = 列表中最后一个有效 profile**
>   （已用 Windows 颜色管理 UI 对照验证：UI「设为默认」同样把所选 profile 移到列表末尾）；
> - **读端**：`GetICMProfile` 不反映真实默认（本机始终报 sRGB），生效 profile 以 per-user
>   关联列表（HKCU `ICM\ProfileAssociations\Display\{4d36e96e…}\<监视器驱动键>`，MULTI_SZ）
>   从末尾数第一个有效显示类（`mntr`/`RGB `）profile 为准；跳过的条目包括文件已删除的残留
>   与非显示类（如打印机类 RSWOP.icm）。
> - 主显示器 LUID/sourceID：`QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` 后用
>   `DisplayConfigGetDeviceInfo(GET_SOURCE_NAME)` 把路径映射回 GDI 设备名核对；
> - 探针全程自恢复；对照实验曾短暂把默认在 sRGB/BOE 间切换，实验后已按快照精确还原。

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

## 3. UI 落点：新页"显示与画质"

导航在"检测"与"优化"之间插入。布局沿用控制台卡片风（纯平、发丝分割线）：

```
显示与画质
├─ N 卡游戏设置（仅 N 卡）：DLSS 预设 [自动|CNN|Transformer|K] · 数字振动滑条 · 纹理过滤/电源/低延迟（二期）
│   按当前定位的游戏 exe 作用 · [还原此游戏默认]
└─ ICC 滤镜：当前关联 xxx.icc · 预设四卡 [应用] [还原原始]
```
非 N 卡用户：N 卡区块显示不支持说明，ICC 区块照常可用。

每块独立备份/还原，状态栏如实显示"未应用/已应用/还原失败"。

---

## 4. 里程碑（每个可独立发布，逐步来）

| 里程碑 | 内容 | 风险 |
|---|---|---|
| M1 | NVAPI DRS 基建（互操作层 + profile 备份/还原）+ DLSS 预设切换 + 数字振动 | 高（SettingID 须以 nvapi.h 核对；受阻如实回退） |
| M2 | ICC 滤镜 + 预设生成器（A 卡/Intel 用户与系统层滤镜的兜底） | 中（mscms 互操作 + ICC 二进制格式） |
| M3 | DRS 二期设置项（纹理过滤/电源/低延迟/AA 透明度）+ 收尾 | 中（SettingID 逐项核对） |

每个里程碑：单测（互操作层用注入点隔离，不碰真实系统）→ 真机 → **部署 D:\FpsTune 给用户过目 → 认可后才发 Release**（0.1.2-beta 起版）。

## 5. 测试要点（开工前约定）

- ICC 生成器：纯函数测试（头字段/长度/tag 合法性 + `IsValidColorProfileW` 冒烟）。
- mscms/NVAPI 调用层：接口抽象 + 测试注入（沿用 `*Override` 约定 + 串行 Collection）；
  真实写入路径标"未自动化，需真机验收"。
- DLSS profile：只允许作用于"本工具创建的 profile"（命名加 FpsTune 前缀），还原绝不删除用户自建 profile。

## 6. 本文档的边界

- SettingID、NGXCore 键值等未核实细节一律标注"实现时核对"，不作为承诺。
- 功能范围以用户后续指派为准；本文档只是可执行依据，不是授权书。
