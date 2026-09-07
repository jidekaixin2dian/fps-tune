# FPS 帧律 · fps-tune

[English](README.en.md) · [下载最新版](https://github.com/jidekaixin2dian/fps-tune/releases/latest)

面向 Windows 玩家的**系统层**帧率优化工具，核心采用 C# WPF + .NET 8，
`FpsTune.exe` 同时提供无头 CLI 模式（命令行 / AI Agent 入口）。

它只调整 Windows 系统设置（注册表 / 电源计划 / 服务 / 启动配置），
不针对特定游戏——因此不仅适用于《三角洲行动》，也适用于
《CS2》《无畏契约》《APEX》《PUBG》《使命召唤》《守望先锋》等绝大多数 FPS 游戏。

能力闭环：**检测 → 解释 → 确认 → 执行 → 还原**。

- **系统层，可还原**：只改 Windows 设置，每次写入前自动备份原值（含"原本不存在"状态），支持一键还原。
- **不碰游戏**：不修改游戏目录文件、不注入进程、不与反作弊交互、不关引导虚拟化、不做显卡伪装。
- **游戏定位**：从卸载注册表和常见安装目录查找已支持的 FPS 游戏，不读取运行中进程的路径；未识别的游戏可用 `-Game` 手动指定 EXE。
- **单一数据源**：33 个优化项与预设统一定义在 `catalog/catalog.json`，
  GUI 与 CLI 共用同一份 C# 引擎与数据，加载即校验。
- **性能可验证**：内置性能会话（CPU/内存/GPU/显存本地采样、摘要与启发式洞察、
  会话对比与导出）和 A/B 实验向导（基线 + 三候选组 + 报告，可关联会话）；
  结论只来自本机实测，洞察仅标注相关性、不承诺 FPS 提升。
- **版本可追溯**：程序集版本 / 安装器版本 / CLI 自报版本来自 `Directory.Build.props` 的 `<Version>`；
  正式发布脚本从最终干净提交读取完整 SHA，嵌入 `InformationalVersion` 并用本机文件版本信息核验。
- **MIT 开源**：代码完全原创（Clean-room），无遥测。

![release](https://img.shields.io/github/v/release/jidekaixin2dian/fps-tune)
![license](https://img.shields.io/github/license/jidekaixin2dian/fps-tune)

> 项目背景：市面上的同类工具采用专有 EULA，禁止修改与再分发。
> 本项目以公开的功能清单为参考，代码与文档完全自写，宽松许可证开源。

## 快速开始（AI Agent）

```text
先执行 git clone https://github.com/jidekaixin2dian/fps-tune.git，
然后读取克隆目录里的 SKILL.md，严格按其中的流程帮我优化帧率。
```

SKILL.md 是唯一的操作流程入口，不要凭空发挥。

## 快速开始（命令行）

`FpsTune.exe` 带命令行参数时进入无头 CLI 模式（不弹 GUI 窗口，输出到 stdout，退出码 0/1）：

```powershell
# 1. 检测（只读，安全）
.\FpsTune.exe -Detect -Json

# 2. 应用（先向用户说明并征得同意再执行；需要管理员权限的项会明确报错）
.\FpsTune.exe -Apply -Preset balanced -Json

# 3. 还原
.\FpsTune.exe -Restore -Json

# 其他：-Version / -ListRestore -Json / -Apply -Items id1,id2 / -Restore -Items id1,id2
```

需要管理员权限的项请在已提权的终端里运行（exe 本身以 asInvoker 运行，不会自动弹 UAC）。

## 快速开始（GUI）

- 适用环境：Windows 10/11 x64。
- 单文件：下载 `FpsTune.exe` 直接运行，自带 .NET 运行时。
- 安装包：从 [Releases](https://github.com/jidekaixin2dian/fps-tune/releases) 下载 `FpsTune-Setup-*.exe`
- 便携版：解压 `FpsTune-Portable-*.zip` 后运行 `FpsTune.exe`；需已安装 [.NET 8 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

v1.6 使用顶部中文页签的控制台布局：概览 / 检测 / 优化 / 性能会话 / A/B 实验 / 朋友测试 / 备份日志 / 设置。
支持深色、浅色及跟随系统主题。v1.6.1 修复执行路径安全边界、配置导入校验和安装器探测。
当前源码新增两种界面模式：标题栏可切换紧凑中文控制台与经典概览，并记住选择。控制台检测页显示数字和细进度条；经典检测页保留随窗口缩放即时重绘的折线图。此项属于后续源码改动，已发布的 v1.6.1 安装包不包含。
每个 Release 提供 `SHA256SUMS-v<版本>.txt`，可用 `Get-FileHash -Algorithm SHA256` 核对下载文件。
程序尚未代码签名，Windows 可能提示未知发布者。应用内更新会先征求同意，再下载和核验安装器 SHA256。

本地构建：

```powershell
.\build-wpf.ps1 -Mode Build
.\FpsTune.Wpf\bin\Release\net8.0-windows\FpsTune.exe
```

GUI 已完成：检测（含实时 CPU/内存/GPU/显存监控）、优化、性能会话（本地采样 + 摘要 + 启发式洞察 + 会话对比 + JSON/CSV 导出）、A/B 实验向导（可关联性能会话、中断恢复）、按游戏自动应用 Profile（活动中心可查事件）、朋友测试、备份/日志、设置、
深色/亮色/跟随系统主题、自定义无边框窗口与标题栏、现代化弹窗、首次启动引导。

## 架构

```
catalog/catalog.json          优化项与预设的唯一数据源
FpsTune.Wpf/
  Core/                       OptimizationCatalog / NativeOptimizationEngine /
                              BackupService / DetectionService 等
  Services/                   主题 / 设置 / 脚本定位 / 进程封装 / CLI 宿主
  Views/                      WPF 视图
FpsTune.Wpf.Tests/            单元测试（含 catalog 一致性守卫）
tuning-experiment.ps1         A/B 实验框架（模拟态可安全跑 CI，实际采样调用 FpsTune.exe CLI）
tools/friend-test.ps1         朋友测试记录脚本（GUI 调用）
installer/                    Inno Setup 打包
```

新增或修改一个优化项的流程：编辑 `catalog/catalog.json` 中对应条目，
再在 `NativeOptimizationEngine`（apply/revert）与 `DetectionService`（状态读取）
各加一个 case 分支——catalog 一致性测试会强制条目与实现同步。

## 测试

```powershell
dotnet test FpsTune.Wpf.Tests/FpsTune.Wpf.Tests.csproj -c Release
# 以及无副作用的 CLI 冒烟（CI 已集成）
.\FpsTune.Wpf\bin\Release\net8.0-windows\FpsTune.exe -Detect -Json
```

## 许可

MIT，详见 LICENSE。
